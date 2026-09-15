// [담당업무 2] 응답 헬퍼 — 500 은 내부 메시지를 숨기고 request_id 로만 로그와 매칭.

import { getCorsHeaders } from './cors.ts';

/**
 * JSON 응답 헬퍼 — CORS 헤더 + Content-Type 자동 포함.
 *
 * 모든 Edge Function에서 공통 사용한다.
 */
export function jsonResponse(
  req: Request,
  body: Record<string, unknown>,
  status = 200,
  extraHeaders?: Record<string, string>,
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      ...getCorsHeaders(req),
      'Content-Type': 'application/json',
      ...extraHeaders,
    },
  });
}

/**
 * 500 에러 응답 헬퍼 — Request ID를 생성하고 로그에 기록한 뒤 제네릭 메시지를 반환한다.
 *
 * CS 문의 시 로그 매칭을 위해 `x-request-id` 헤더와 `request_id` 필드를 포함한다.
 */
export function errorResponse(
  req: Request,
  err: unknown,
  tag: string,
): Response {
  const requestId = crypto.randomUUID();
  console.error(`[${requestId}] ${tag} error:`, err);
  return jsonResponse(
    req,
    { error: 'Internal server error', request_id: requestId },
    500,
    { 'x-request-id': requestId },
  );
}
