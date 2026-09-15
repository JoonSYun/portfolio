// [담당업무 4] Google RTDN(OIDC) / Apple SNv2(JWS) 웹훅 — 페이로드를 믿지 않고 스토어에서 최신 상태 재조회, 항상 200.

import { supabase } from '../_shared/supabase.ts';
import { getCorsHeaders } from '../_shared/cors.ts';
import { jsonResponse } from '../_shared/response.ts';
import { writeAuditLog } from '../_shared/audit.ts';
import { verifyGoogleOIDCToken } from '../_shared/google_oidc.ts';
import { writeRequestLog, detectPlatform } from '../_shared/request_logger.ts';
import {
  validateGooglePlayReceipt,
  upsertSubscription,
  findSubscriptionByTransactionId,
  verifyAppleJWS,
  StoreVerificationError,
  type AppleNotificationPayload,
  type AppleTransactionPayload,
} from '../_shared/store_validation.ts';

Deno.serve(async (req: Request) => {
  // ── CORS preflight ──────────────────────────────────────────────
  if (req.method === 'OPTIONS') {
    return new Response('ok', { headers: getCorsHeaders(req) });
  }

  // POST만 허용
  if (req.method !== 'POST') {
    return jsonResponse(req, { error: 'Method not allowed' }, 405);
  }

  const start = performance.now();
  let webhookAction = 'unknown';
  let statusCode = 200;

  try {
    // ── 플랫폼 라우팅 ─────────────────────────────────────────────
    const url = new URL(req.url);
    const platform = url.searchParams.get('platform');

    if (platform === 'google_play') {
      webhookAction = 'google_rtdn';
      return await handleGoogleRTDN(req);
    } else if (platform === 'app_store') {
      webhookAction = 'apple_snv2';
      return await handleAppleSNv2(req);
    }

    // 플랫폼 미지정 시 body 형태로 자동 감지
    const body = await req.json();
    if (body.signedPayload) {
      webhookAction = 'apple_snv2';
      return await processAppleNotification(req, body);
    } else if (body.message?.data) {
      webhookAction = 'google_rtdn';
      return await processGoogleNotification(req, body);
    }

    statusCode = 400;
    return jsonResponse(req, { error: 'Unknown webhook format' }, 400);
  } catch (err) {
    console.error('store-webhook error:', err);
    // 웹훅은 항상 200 반환 (재시도 방지, 에러는 로그에만)
    statusCode = 200;
    return jsonResponse(req, { received: true, error: 'internal' });
  } finally {
    writeRequestLog({
      function_name: 'store-webhook',
      action: webhookAction,
      duration_ms: Math.round(performance.now() - start),
      status_code: statusCode,
      success: statusCode < 400,
      platform: detectPlatform(req),
    }).catch(() => {});
  }
});

// ── Google Play RTDN ────────────────────────────────────────────────

/** Google RTDN notification types that affect subscription state. */
const GOOGLE_ACTIONABLE_TYPES = new Set([
  1,  // SUBSCRIPTION_RECOVERED
  2,  // SUBSCRIPTION_RENEWED
  3,  // SUBSCRIPTION_CANCELED
  5,  // SUBSCRIPTION_ON_HOLD
  6,  // SUBSCRIPTION_IN_GRACE_PERIOD
  7,  // SUBSCRIPTION_RESTARTED
  12, // SUBSCRIPTION_REVOKED
  13, // SUBSCRIPTION_EXPIRED
]);

async function handleGoogleRTDN(req: Request): Promise<Response> {
  // OIDC Bearer 토큰 검증 (Pub/Sub push 인증)
  const audience = Deno.env.get('GOOGLE_RTDN_AUDIENCE');
  if (audience) {
    const authHeader = req.headers.get('authorization') ?? '';
    const token = authHeader.replace(/^Bearer\s+/i, '');
    if (!token) {
      console.warn('Google RTDN: missing Authorization header');
      return jsonResponse(req, { error: 'Unauthorized' }, 401);
    }
    const payload = await verifyGoogleOIDCToken(token, audience);
    if (!payload) {
      console.warn('Google RTDN: OIDC token verification failed');
      return jsonResponse(req, { error: 'Unauthorized' }, 401);
    }
  }

  const body = await req.json();
  return await processGoogleNotification(req, body);
}

async function processGoogleNotification(
  req: Request,
  body: Record<string, unknown>,
): Promise<Response> {
  // Pub/Sub 메시지 디코딩
  const message = body.message as { data?: string; messageId?: string } | undefined;
  if (!message?.data) {
    console.warn('Google RTDN: missing message.data');
    return jsonResponse(req, { received: true });
  }

  const decoded = JSON.parse(atob(message.data));
  const notification = decoded.subscriptionNotification;

  if (!notification) {
    // 테스트 알림 또는 기타 타입 (voided purchase 등)
    console.log('Google RTDN: non-subscription notification', decoded);
    return jsonResponse(req, { received: true });
  }

  const { notificationType, purchaseToken, subscriptionId } = notification;

  // 처리 대상 notification type인지 확인
  if (!GOOGLE_ACTIONABLE_TYPES.has(notificationType)) {
    console.log(`Google RTDN: skipping notification type ${notificationType}`);
    return jsonResponse(req, { received: true });
  }

  try {
    // Google Play API로 최신 구독 상태 조회
    const result = await validateGooglePlayReceipt(purchaseToken, subscriptionId);

    // 기존 구독에서 user_id 조회
    const existing = await findSubscriptionByTransactionId(
      supabase,
      result.store_transaction_id,
    );

    if (!existing) {
      // validate-receipt가 아직 호출되지 않은 경우
      // purchaseToken으로 한번 더 검색 시도
      const byToken = await findSubscriptionByTransactionId(supabase, purchaseToken);
      if (!byToken) {
        console.warn(
          `Google RTDN: no subscription found for transaction ${result.store_transaction_id}`,
        );
        return jsonResponse(req, { received: true });
      }
      // byToken으로 진행
      const oldStatus = byToken.status;
      const row = await upsertSubscription(supabase, {
        user_id: byToken.user_id as string,
        platform: 'google_play',
        ...result,
      });
      await writeWebhookAudit(row, 'google_play', notificationType, oldStatus);
      return jsonResponse(req, { received: true });
    }

    const oldStatus = existing.status;
    const row = await upsertSubscription(supabase, {
      user_id: existing.user_id as string,
      platform: 'google_play',
      ...result,
    });
    await writeWebhookAudit(row, 'google_play', notificationType, oldStatus);
  } catch (err) {
    console.error('Google RTDN processing error:', err);
    // 에러 발생해도 200 반환 (Pub/Sub 무한 재시도 방지)
  }

  return jsonResponse(req, { received: true });
}

// ── Apple Server Notifications V2 ───────────────────────────────────

/** Apple notification types → 구독 상태 매핑. */
const APPLE_STATUS_MAP: Record<string, string> = {
  DID_RENEW: 'active',
  SUBSCRIBED: 'active',
  DID_FAIL_TO_RENEW: 'grace_period',
  EXPIRED: 'expired',
  GRACE_PERIOD_EXPIRED: 'expired',
  REVOKE: 'expired',
};

async function handleAppleSNv2(req: Request): Promise<Response> {
  const body = await req.json();
  return await processAppleNotification(req, body);
}

async function processAppleNotification(
  req: Request,
  body: Record<string, unknown>,
): Promise<Response> {
  const signedPayload = body.signedPayload as string | undefined;
  if (!signedPayload) {
    console.warn('Apple SNv2: missing signedPayload');
    return jsonResponse(req, { received: true });
  }

  // JWS 서명 검증 + 페이로드 디코딩 (x5c 체인 → Apple Root CA 검증)
  let notification: AppleNotificationPayload;
  try {
    notification = await verifyAppleJWS<AppleNotificationPayload>(signedPayload);
  } catch (err) {
    console.error('Apple SNv2: JWS verification failed:', err);
    return jsonResponse(req, { received: true });
  }

  const { notificationType, subtype, data } = notification;

  if (!data?.signedTransactionInfo) {
    console.warn('Apple SNv2: missing signedTransactionInfo');
    return jsonResponse(req, { received: true });
  }

  // signedTransactionInfo JWS 검증 + 디코딩
  let txn: AppleTransactionPayload;
  try {
    txn = await verifyAppleJWS<AppleTransactionPayload>(data.signedTransactionInfo);
  } catch (err) {
    console.error('Apple SNv2: transaction JWS verification failed:', err);
    return jsonResponse(req, { received: true });
  }

  const originalTransactionId = txn.originalTransactionId;
  const productId = txn.productId;

  try {
    // 기존 구독에서 user_id 조회
    const existing = await findSubscriptionByTransactionId(
      supabase,
      originalTransactionId,
    );

    if (!existing) {
      console.warn(
        `Apple SNv2: no subscription found for transaction ${originalTransactionId}`,
      );
      return jsonResponse(req, { received: true });
    }

    // 상태 결정
    let newStatus: string;

    // DID_CHANGE_RENEWAL_STATUS + AUTO_RENEW_DISABLED → cancelled
    if (
      notificationType === 'DID_CHANGE_RENEWAL_STATUS' &&
      subtype === 'AUTO_RENEW_DISABLED'
    ) {
      newStatus = 'cancelled';
    } else {
      newStatus = APPLE_STATUS_MAP[notificationType] ?? (existing.status as string);
    }

    // 만료/갱신 시간 결정
    const expiresAt = txn.expiresDate
      ? new Date(txn.expiresDate).toISOString()
      : (existing.expires_at as string);

    const cancelledAt =
      newStatus === 'cancelled' || newStatus === 'expired'
        ? new Date().toISOString()
        : null;

    const oldStatus = existing.status;
    const row = await upsertSubscription(supabase, {
      user_id: existing.user_id as string,
      platform: 'app_store',
      status: newStatus,
      product_id: productId,
      store_transaction_id: originalTransactionId,
      current_period_start: txn.purchaseDate
        ? new Date(txn.purchaseDate).toISOString()
        : (existing.current_period_start as string),
      expires_at: expiresAt,
      cancelled_at: cancelledAt ?? undefined,
    });

    await writeWebhookAudit(row, 'app_store', notificationType, oldStatus);
  } catch (err) {
    console.error('Apple SNv2 processing error:', err);
  }

  return jsonResponse(req, { received: true });
}

// ── Helpers ─────────────────────────────────────────────────────────

async function writeWebhookAudit(
  row: Record<string, unknown>,
  platform: string,
  notificationType: string | number,
  oldStatus: unknown,
): Promise<void> {
  await writeAuditLog({
    user_id: row.user_id as string,
    operation: 'SUBSCRIPTION_WEBHOOK',
    source: 'store-webhook',
    record_id: row.id as string,
    metadata: {
      platform,
      notification_type: String(notificationType),
      old_status: oldStatus,
      new_status: row.status,
    },
  });
}

