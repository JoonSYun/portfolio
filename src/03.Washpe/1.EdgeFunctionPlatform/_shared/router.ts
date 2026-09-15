// [담당업무 1] Action 기반 공통 라우터 — CORS·JSON 파싱·JWT·역할 검사·요청 로그를 한 곳에서 처리하고, 각 Edge Function 은 핸들러만 등록한다.

import { supabase } from './supabase.ts';
import { getCorsHeaders } from './cors.ts';
import { jsonResponse, errorResponse } from './response.ts';
import { verifyAuth, tryVerifyAuth, AuthError } from './auth.ts';
import { writeRequestLog, detectPlatform } from './request_logger.ts';

// ── Types ────────────────────────────────────────────────────────────────────

/** 액션 핸들러 함수 시그니처. public action에서는 userId가 null일 수 있다. */
type HandlerFn = (
  req: Request,
  userId: string | null,
  params: Record<string, unknown>,
) => Promise<Response>;

/** 액션 정의 (핸들러 + 메타데이터). */
export interface ActionDef {
  handler: HandlerFn;
  /** true면 is_admin 체크 (역할 기반 접근 제어). */
  adminOnly?: boolean;
}

/**
 * 핸들러 맵 — 함수만 넘기면 자동 래핑, ActionDef로 넘기면 그대로 사용.
 *
 * ```typescript
 * Deno.serve(createRouter('chemicals', {
 *   get_all: async (req, userId) => { ... },              // 간편 등록
 *   admin_reset: { handler: ..., adminOnly: true },       // 역할 제어
 * }));
 * ```
 */
type HandlerMap = Record<string, ActionDef | HandlerFn>;

// ── Internal helpers ─────────────────────────────────────────────────────────

function normalizeHandlers(map: HandlerMap): Record<string, ActionDef> {
  const result: Record<string, ActionDef> = {};
  for (const [key, val] of Object.entries(map)) {
    result[key] = typeof val === 'function' ? { handler: val } : val;
  }
  return result;
}

// ── createRouter ─────────────────────────────────────────────────────────────

/**
 * Action-based 라우터 생성.
 *
 * 자동 처리 항목:
 * 1. CORS preflight (OPTIONS)
 * 2. JWT 인증 (`verifyAuth`)
 * 3. Safe JSON parsing (빈 body / 배열 / 잘못된 JSON 방어)
 * 4. 역할 기반 접근 제어 (`adminOnly`)
 * 5. 요청 로그 (`api_request_logs`) — finally 블록에서 fire-and-forget
 * 6. body size 추적 (request + response)
 */
export function createRouter(
  functionName: string,
  rawHandlers: HandlerMap,
  options?: { publicActions?: string[] },
) {
  const handlers = normalizeHandlers(rawHandlers);
  const publicSet = new Set(options?.publicActions ?? []);

  return async (req: Request): Promise<Response> => {
    // ── CORS preflight ───────────────────────────────────────────────────
    if (req.method === 'OPTIONS') {
      return new Response('ok', { headers: getCorsHeaders(req) });
    }

    // ── Tracking variables ───────────────────────────────────────────────
    const start = performance.now();
    let userId: string | null | undefined;
    let action = 'unknown';
    let statusCode = 500;
    let success = false;
    let errorCode: string | undefined;
    let errorMessage: string | undefined;
    let requestId: string | undefined;
    let requestBodySize = 0;
    let responseBodySize = 0;
    let authReason: string | undefined;

    try {
      // ── 1. Safe JSON parsing (auth 전에 action 추출) ───────────────────
      let body: Record<string, unknown>;
      try {
        const raw = await req.text();
        requestBodySize = new TextEncoder().encode(raw).byteLength;
        const parsed = JSON.parse(raw);
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
          statusCode = 400;
          errorCode = 'INVALID_BODY';
          errorMessage = 'Request body must be a JSON object';
          return jsonResponse(req, { error: errorMessage, code: errorCode }, 400);
        }
        body = parsed;
      } catch {
        statusCode = 400;
        errorCode = 'INVALID_JSON';
        errorMessage = 'Invalid JSON in request body';
        return jsonResponse(req, { error: errorMessage, code: errorCode }, 400);
      }

      // ── 2. Action 추출 ─────────────────────────────────────────────────
      action = (body.action as string) ?? 'unknown';
      const { action: _, ...params } = body;

      const def = handlers[action];
      if (!def) {
        statusCode = 400;
        errorCode = 'UNKNOWN_ACTION';
        errorMessage = `Unknown action: ${action}`;
        return jsonResponse(req, { error: errorMessage, code: errorCode }, 400);
      }

      // ── 3. Auth (public action이면 선택적 인증) ─────────────────────────
      const isPublic = publicSet.has(action);
      if (isPublic) {
        const result = await tryVerifyAuth(req);
        userId = result.userId;
        authReason = result.reason;
      } else {
        ({ userId } = await verifyAuth(req));
      }

      // ── 4. Role check (adminOnly) ──────────────────────────────────────
      if (def.adminOnly) {
        if (!userId) {
          statusCode = 401;
          errorCode = 'AUTH_REQUIRED';
          errorMessage = 'Authentication required for admin actions';
          return jsonResponse(req, { error: errorMessage, code: errorCode }, 401);
        }
        const { data: profile } = await supabase
          .from('profiles')
          .select('is_admin')
          .eq('id', userId)
          .single();
        if (!profile?.is_admin) {
          statusCode = 403;
          errorCode = 'ADMIN_REQUIRED';
          errorMessage = 'Admin access required';
          return jsonResponse(req, { error: errorMessage, code: errorCode }, 403);
        }
      }

      // ── 5. Execute handler ─────────────────────────────────────────────
      const response = await def.handler(req, userId, params);
      statusCode = response.status;
      success = statusCode >= 200 && statusCode < 400;

      // 응답 body 크기 측정
      try {
        const cloned = response.clone();
        const resText = await cloned.text();
        responseBodySize = new TextEncoder().encode(resText).byteLength;
      } catch { /* ignore */ }

      return response;
    } catch (err) {
      // ── Auth error ─────────────────────────────────────────────────────
      if (err instanceof AuthError) {
        statusCode = 401;
        errorCode = 'AUTH_FAILED';
        errorMessage = err.message;
        return jsonResponse(req, { error: err.message, code: errorCode }, 401);
      }

      // ── Internal error ─────────────────────────────────────────────────
      requestId = crypto.randomUUID();
      errorCode = 'INTERNAL_ERROR';
      errorMessage = err instanceof Error ? err.message : String(err);
      return errorResponse(req, err, functionName);
    } finally {
      // ── 6. Request log (fire-and-forget) ───────────────────────────────
      const durationMs = Math.round(performance.now() - start);
      writeRequestLog({
        user_id: userId ?? undefined,
        function_name: functionName,
        action,
        duration_ms: durationMs,
        request_body_size: requestBodySize || undefined,
        response_body_size: responseBodySize || undefined,
        status_code: statusCode,
        success,
        error_code: errorCode,
        error_message: authReason ? `[${authReason}] ${errorMessage ?? ''}`.trim() : errorMessage,
        request_id: requestId,
        platform: detectPlatform(req),
      }).catch(() => {});
    }
  };
}
