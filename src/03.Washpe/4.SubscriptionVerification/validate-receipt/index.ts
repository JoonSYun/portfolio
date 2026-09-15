// [담당업무 4] 클라이언트 영수증을 서버에서 스토어 API 로 재검증 → UPSERT, 23505 충돌은 멱등 응답.

import { supabase } from '../_shared/supabase.ts';
import { getCorsHeaders } from '../_shared/cors.ts';
import { jsonResponse, errorResponse } from '../_shared/response.ts';
import { writeAuditLog } from '../_shared/audit.ts';
import { verifyAuth, AuthError } from '../_shared/auth.ts';
import { writeRequestLog, detectPlatform } from '../_shared/request_logger.ts';
import {
  validateGooglePlayReceipt,
  validateAppStoreReceipt,
  upsertSubscription,
  findSubscriptionByUserId,
  StoreVerificationError,
} from '../_shared/store_validation.ts';

Deno.serve(async (req: Request) => {
  // ── CORS preflight ──────────────────────────────────────────────
  if (req.method === 'OPTIONS') {
    return new Response('ok', { headers: getCorsHeaders(req) });
  }

  const start = performance.now();
  let userId: string | undefined;
  let statusCode = 500;

  try {
    // ── 0. Auth verification ────────────────────────────────────
    ({ userId } = await verifyAuth(req));

    // ── 1. Input validation ─────────────────────────────────────
    const body = await req.json();
    const { verification_data, product_id, platform } = body;

    if (!verification_data || typeof verification_data !== 'string') {
      return jsonResponse(req, { error: 'verification_data is required' }, 400);
    }
    if (!product_id || typeof product_id !== 'string') {
      return jsonResponse(req, { error: 'product_id is required' }, 400);
    }
    if (platform !== 'google_play' && platform !== 'app_store') {
      return jsonResponse(
        req,
        { error: 'platform must be "google_play" or "app_store"' },
        400,
      );
    }

    // ── 2. Store verification ───────────────────────────────────
    const result =
      platform === 'google_play'
        ? await validateGooglePlayReceipt(verification_data, product_id)
        : await validateAppStoreReceipt(verification_data, product_id);

    // ── 3. DB upsert ────────────────────────────────────────────
    let row: Record<string, unknown>;
    try {
      row = await upsertSubscription(supabase, {
        user_id: userId,
        platform,
        ...result,
      });
    } catch (dbError: unknown) {
      // store_transaction_id 중복 (partial unique index 충돌)
      const pgError = dbError as { code?: string };
      if (pgError.code === '23505') {
        // 멱등 응답: 기존 구독 반환
        const existing = await findSubscriptionByUserId(supabase, userId);
        if (existing) {
          return jsonResponse(req, existing);
        }
        return jsonResponse(req, { error: 'Receipt already verified' }, 409);
      }
      throw dbError;
    }

    // ── 4. Audit log ────────────────────────────────────────────
    await writeAuditLog({
      user_id: userId,
      operation: 'SUBSCRIPTION_VERIFY',
      source: 'validate-receipt',
      record_id: row.id as string,
      metadata: {
        platform,
        product_id,
        store_transaction_id: result.store_transaction_id,
      },
    });

    // ── 5. Return result ────────────────────────────────────────
    statusCode = 200;
    return jsonResponse(req, row);
  } catch (err) {
    if (err instanceof AuthError) {
      statusCode = 401;
      return jsonResponse(req, { code: 401, message: err.message }, 401);
    }
    if (err instanceof StoreVerificationError) {
      statusCode = err.statusCode;
      return jsonResponse(req, { error: err.message }, err.statusCode);
    }
    statusCode = 500;
    return errorResponse(req, err, 'validate-receipt');
  } finally {
    writeRequestLog({
      user_id: userId,
      function_name: 'validate-receipt',
      action: 'verify',
      duration_ms: Math.round(performance.now() - start),
      status_code: statusCode,
      success: statusCode < 400,
      platform: detectPlatform(req),
    }).catch(() => {});
  }
});
