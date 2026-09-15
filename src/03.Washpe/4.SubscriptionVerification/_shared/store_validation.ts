// [담당업무 4] Apple x5c 체인→Root CA G3 검증 + JWS 서명, Google 서비스 계정 JWT(RS256) → subscriptionsv2 상태 매핑.

import { createClient, type SupabaseClient } from 'jsr:@supabase/supabase-js@2';
import * as jose from 'npm:jose@5';
import forge from 'npm:node-forge@1';

// ── Types ───────────────────────────────────────────────────────────

export interface StoreVerificationResult {
  status: 'active' | 'grace_period' | 'expired' | 'cancelled' | 'account_hold' | 'paused';
  product_id: string;
  store_transaction_id: string;
  current_period_start: string; // ISO 8601
  expires_at: string;
  cancelled_at?: string;
  resume_date?: string;
  paused_at?: string;
}

export interface SubscriptionUpsertData {
  user_id: string;
  platform: 'google_play' | 'app_store';
  status: string;
  product_id: string;
  store_transaction_id: string;
  current_period_start: string;
  expires_at: string;
  cancelled_at?: string;
  resume_date?: string;
  paused_at?: string;
}

/** 스토어 검증 실패 에러. */
export class StoreVerificationError extends Error {
  constructor(message: string, public readonly statusCode: number = 502) {
    super(message);
    this.name = 'StoreVerificationError';
  }
}

// ── Apple JWS Verification ──────────────────────────────────────────

const APPLE_ROOT_CA_URL = 'https://www.apple.com/certificateauthority/AppleRootCA-G3.cer';
let cachedRootCA: { pem: string; fetchedAt: number } | null = null;
const ROOT_CA_TTL_MS = 24 * 60 * 60 * 1000; // 24 hours

/** Apple Root CA G3 PEM을 가져온다 (24h 인메모리 캐시). */
async function getAppleRootCAPem(): Promise<string> {
  const now = Date.now();
  if (cachedRootCA && (now - cachedRootCA.fetchedAt) < ROOT_CA_TTL_MS) {
    return cachedRootCA.pem;
  }
  const res = await fetch(APPLE_ROOT_CA_URL);
  if (!res.ok) {
    throw new StoreVerificationError('Failed to fetch Apple Root CA', 502);
  }
  const der = new Uint8Array(await res.arrayBuffer());
  const b64 = btoa(String.fromCharCode(...der));
  const pem = `-----BEGIN CERTIFICATE-----\n${b64.match(/.{1,64}/g)!.join('\n')}\n-----END CERTIFICATE-----`;
  cachedRootCA = { pem, fetchedAt: now };
  return pem;
}

/** x5c Base64 DER → PEM 변환. */
function x5cToPem(b64Der: string): string {
  const lines = b64Der.match(/.{1,64}/g) ?? [];
  return `-----BEGIN CERTIFICATE-----\n${lines.join('\n')}\n-----END CERTIFICATE-----`;
}

/**
 * Apple JWS를 x5c 인증서 체인 검증 + 서명 검증 후 디코딩한다.
 *
 * 1. JWS 헤더에서 x5c 체인 추출
 * 2. node-forge로 인증서 체인 검증 (validity + issuer + Apple Root CA)
 * 3. jose로 리프 인증서 공개키 기반 JWS 서명 검증
 */
export async function verifyAppleJWS<T>(jws: string): Promise<T> {
  const header = jose.decodeProtectedHeader(jws);
  const x5c = header.x5c;
  if (!x5c || x5c.length < 2) {
    throw new StoreVerificationError('Missing or insufficient x5c chain', 400);
  }
  const alg = header.alg ?? 'ES256';

  // 인증서 체인 검증 (leaf → intermediate → Apple Root CA G3)
  await verifyCertificateChain(x5c);

  // 리프 인증서 공개키로 JWS 서명 검증
  const leafPem = x5cToPem(x5c[0]);
  const leafKey = await jose.importX509(leafPem, alg);
  const { payload } = await jose.compactVerify(jws, leafKey);

  return JSON.parse(new TextDecoder().decode(payload)) as T;
}

/**
 * node-forge caStore로 x5c 체인을 Apple Root CA G3까지 검증한다.
 *
 * `forge.pki.verifyCertificateChain()`이 내부적으로 다음을 수행:
 * - validity (notBefore/notAfter) 검사
 * - issuer ↔ subject 매칭
 * - 서명 체인 검증
 * - 루트 CA 일치 확인
 */
async function verifyCertificateChain(x5c: string[]): Promise<void> {
  const rootPem = await getAppleRootCAPem();
  const caStore = forge.pki.createCaStore([rootPem]);
  const chain = x5c.map((b64: string) =>
    forge.pki.certificateFromPem(x5cToPem(b64)),
  );

  try {
    forge.pki.verifyCertificateChain(caStore, chain);
  } catch (err) {
    throw new StoreVerificationError(
      `Certificate chain verification failed: ${(err as Error).message}`,
      400,
    );
  }
}

// ── Apple Types ──────────────────────────────────────────────────────

/** Apple signed transaction 페이로드 (StoreKit 2). */
export interface AppleTransactionPayload {
  transactionId: string;
  originalTransactionId: string;
  productId: string;
  purchaseDate: number;       // ms since epoch
  expiresDate?: number;       // ms since epoch
  revocationDate?: number;    // ms since epoch
  type: string;               // 'Auto-Renewable Subscription'
  inAppOwnershipType: string; // 'PURCHASED' | 'FAMILY_SHARED'
}

/** Apple Server Notification V2 페이로드. */
export interface AppleNotificationPayload {
  notificationType: string;
  subtype?: string;
  data: {
    signedTransactionInfo: string;
    signedRenewalInfo?: string;
    bundleId: string;
    environment: string;
  };
  signedDate: number;
}

// ── Google Play ─────────────────────────────────────────────────────

/** Google 서비스 계정 JSON에서 추출하는 필드. */
interface GoogleServiceAccount {
  client_email: string;
  private_key: string;
}

/**
 * PEM 형식의 private key를 ArrayBuffer로 변환한다.
 * `crypto.subtle.importKey("pkcs8", ...)` 입력용.
 */
function pemToArrayBuffer(pem: string): ArrayBuffer {
  const base64 = pem
    .replace(/-----BEGIN PRIVATE KEY-----/, '')
    .replace(/-----END PRIVATE KEY-----/, '')
    .replace(/\n/g, '');
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes.buffer;
}

/** base64url 인코딩. */
function base64url(data: ArrayBuffer | Uint8Array | string): string {
  let bytes: Uint8Array;
  if (typeof data === 'string') {
    bytes = new TextEncoder().encode(data);
  } else if (data instanceof ArrayBuffer) {
    bytes = new Uint8Array(data);
  } else {
    bytes = data;
  }

  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/**
 * Google 서비스 계정 JWT를 생성하고 access token을 교환한다.
 */
async function getGoogleAccessToken(sa: GoogleServiceAccount): Promise<string> {
  const now = Math.floor(Date.now() / 1000);
  const header = { alg: 'RS256', typ: 'JWT' };
  const payload = {
    iss: sa.client_email,
    scope: 'https://www.googleapis.com/auth/androidpublisher',
    aud: 'https://oauth2.googleapis.com/token',
    iat: now,
    exp: now + 3600,
  };

  const headerB64 = base64url(JSON.stringify(header));
  const payloadB64 = base64url(JSON.stringify(payload));
  const unsignedToken = `${headerB64}.${payloadB64}`;

  // RS256 서명
  const keyData = pemToArrayBuffer(sa.private_key);
  const key = await crypto.subtle.importKey(
    'pkcs8',
    keyData,
    { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' },
    false,
    ['sign'],
  );
  const signature = await crypto.subtle.sign(
    'RSASSA-PKCS1-v1_5',
    key,
    new TextEncoder().encode(unsignedToken),
  );
  const jwt = `${unsignedToken}.${base64url(signature)}`;

  // Access token 교환
  const tokenRes = await fetch('https://oauth2.googleapis.com/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer&assertion=${jwt}`,
  });
  if (!tokenRes.ok) {
    const body = await tokenRes.text();
    throw new StoreVerificationError(`Google OAuth token exchange failed: ${body}`);
  }
  const { access_token } = await tokenRes.json();
  return access_token;
}

/**
 * Google Play subscriptionsv2 API로 구독 영수증을 검증한다.
 *
 * @param purchaseToken - IAP에서 받은 purchaseToken
 * @param productId - 구독 상품 ID
 */
export async function validateGooglePlayReceipt(
  purchaseToken: string,
  productId: string,
): Promise<StoreVerificationResult> {
  // Base64 인코딩된 서비스 계정 JSON 우선, 없으면 plain JSON fallback
  const saB64 = Deno.env.get('GOOGLE_PLAY_SERVICE_ACCOUNT_B64');
  const saJsonRaw = saB64 ? atob(saB64) : Deno.env.get('GOOGLE_PLAY_SERVICE_ACCOUNT_JSON');
  if (!saJsonRaw) {
    throw new StoreVerificationError(
      'GOOGLE_PLAY_SERVICE_ACCOUNT_B64 (or GOOGLE_PLAY_SERVICE_ACCOUNT_JSON) not configured',
      500,
    );
  }
  const saJson = saJsonRaw;
  const packageName = Deno.env.get('GOOGLE_PLAY_PACKAGE_NAME');
  if (!packageName) {
    throw new StoreVerificationError('GOOGLE_PLAY_PACKAGE_NAME not configured', 500);
  }

  const sa: GoogleServiceAccount = JSON.parse(saJson);
  const accessToken = await getGoogleAccessToken(sa);

  // subscriptionsv2.get API 호출
  const apiUrl =
    `https://androidpublisher.googleapis.com/androidpublisher/v3/applications/${packageName}/purchases/subscriptionsv2/tokens/${purchaseToken}`;

  const res = await fetch(apiUrl, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!res.ok) {
    const body = await res.text();
    throw new StoreVerificationError(`Google Play API error (${res.status}): ${body}`);
  }

  const data = await res.json();

  // subscriptionState 매핑
  const stateMap: Record<string, StoreVerificationResult['status']> = {
    SUBSCRIPTION_STATE_ACTIVE: 'active',
    SUBSCRIPTION_STATE_IN_GRACE_PERIOD: 'grace_period',
    SUBSCRIPTION_STATE_EXPIRED: 'expired',
    SUBSCRIPTION_STATE_CANCELED: 'cancelled',
    SUBSCRIPTION_STATE_ON_HOLD: 'account_hold',
    SUBSCRIPTION_STATE_REVOKED: 'expired',
    SUBSCRIPTION_STATE_PAUSED: 'paused',
  };
  const status = stateMap[data.subscriptionState] ?? 'expired';

  // lineItems에서 만료 시간 추출
  const lineItem = data.lineItems?.[0];
  const expiryTime = lineItem?.expiryTime ?? data.expiryTime;
  const startTime = data.startTime ?? new Date().toISOString();

  // cancelled 상태인 경우 canceledStateContext 확인
  const cancelledAt =
    status === 'cancelled' && data.canceledStateContext
      ? new Date().toISOString()
      : undefined;

  // paused 상태인 경우 autoResumeTime 추출
  const resumeDate =
    status === 'paused' && data.pausedStateContext?.autoResumeTime
      ? new Date(data.pausedStateContext.autoResumeTime).toISOString()
      : undefined;

  const pausedAt =
    status === 'paused'
      ? new Date().toISOString()
      : undefined;

  return {
    status,
    product_id: productId,
    store_transaction_id: data.latestOrderId ?? purchaseToken,
    current_period_start: startTime,
    expires_at: expiryTime,
    cancelled_at: cancelledAt,
    resume_date: resumeDate,
    paused_at: pausedAt,
  };
}

// ── Apple App Store ─────────────────────────────────────────────────

/**
 * Apple App Store의 signedTransaction(JWS)을 검증한다.
 *
 * StoreKit 2에서 받은 JWS 포맷의 signed transaction을
 * x5c 인증서 체인 검증 + 서명 검증 후 구독 상태를 추출한다.
 */
export async function validateAppStoreReceipt(
  signedTransaction: string,
  productId: string,
): Promise<StoreVerificationResult> {
  const bundleId = Deno.env.get('APPLE_BUNDLE_ID');
  if (!bundleId) {
    throw new StoreVerificationError('APPLE_BUNDLE_ID not configured', 500);
  }

  // JWS 서명 검증 + 페이로드 디코딩
  const txn = await verifyAppleJWS<AppleTransactionPayload>(signedTransaction);

  // productId 검증
  if (txn.productId !== productId) {
    throw new StoreVerificationError(
      `Product ID mismatch: expected ${productId}, got ${txn.productId}`,
      400,
    );
  }

  // 상태 결정
  const now = Date.now();
  let status: StoreVerificationResult['status'];
  let cancelledAt: string | undefined;

  if (txn.revocationDate) {
    // 환불됨
    status = 'expired';
    cancelledAt = new Date(txn.revocationDate).toISOString();
  } else if (txn.expiresDate && txn.expiresDate < now) {
    // 만료됨
    status = 'expired';
  } else {
    // 활성
    status = 'active';
  }

  return {
    status,
    product_id: productId,
    store_transaction_id: txn.originalTransactionId,
    current_period_start: new Date(txn.purchaseDate).toISOString(),
    expires_at: txn.expiresDate
      ? new Date(txn.expiresDate).toISOString()
      : new Date(now + 30 * 24 * 60 * 60 * 1000).toISOString(), // fallback 30일
    cancelled_at: cancelledAt,
  };
}

// ── DB Upsert ───────────────────────────────────────────────────────

/**
 * subscriptions 테이블에 구독을 UPSERT한다.
 * user_id UNIQUE 제약으로 ON CONFLICT 처리 (1 user = 1 subscription).
 * service_role 클라이언트 사용 필수 (RLS bypass).
 *
 * @returns upserted row (SubscriptionDto.fromJson() 호환 형식)
 */
export async function upsertSubscription(
  supabase: SupabaseClient,
  data: SubscriptionUpsertData,
): Promise<Record<string, unknown>> {
  const { data: row, error } = await supabase
    .from('subscriptions')
    .upsert(
      {
        user_id: data.user_id,
        platform: data.platform,
        status: data.status,
        product_id: data.product_id,
        store_transaction_id: data.store_transaction_id,
        current_period_start: data.current_period_start,
        expires_at: data.expires_at,
        cancelled_at: data.cancelled_at ?? null,
        resume_date: data.resume_date ?? null,
        paused_at: data.paused_at ?? null,
        updated_at: new Date().toISOString(),
      },
      { onConflict: 'user_id' },
    )
    .select()
    .single();

  if (error) throw error;
  return row;
}

/**
 * store_transaction_id로 기존 구독을 조회한다.
 * 웹훅에서 user_id를 모를 때 사용.
 */
export async function findSubscriptionByTransactionId(
  supabase: SupabaseClient,
  storeTransactionId: string,
): Promise<Record<string, unknown> | null> {
  const { data, error } = await supabase
    .from('subscriptions')
    .select()
    .eq('store_transaction_id', storeTransactionId)
    .maybeSingle();

  if (error) throw error;
  return data;
}

/**
 * user_id로 기존 구독을 조회한다.
 * 멱등성 처리 시 기존 구독 반환용.
 */
export async function findSubscriptionByUserId(
  supabase: SupabaseClient,
  userId: string,
): Promise<Record<string, unknown> | null> {
  const { data, error } = await supabase
    .from('subscriptions')
    .select()
    .eq('user_id', userId)
    .maybeSingle();

  if (error) throw error;
  return data;
}
