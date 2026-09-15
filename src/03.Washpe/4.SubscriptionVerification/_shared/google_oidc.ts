// [담당업무 4] Pub/Sub push OIDC 토큰 검증 — JWKS 캐시(Cache-Control), kid 미스 시 1회 갱신.

/**
 * Google OIDC 토큰 검증 모듈.
 *
 * Google Pub/Sub push 구독에서 OIDC 인증을 활성화하면
 * 요청의 Authorization 헤더에 Bearer JWT가 포함된다.
 * 이 모듈은 Google JWKS 공개키로 JWT 서명을 검증한다.
 */

// ── Types ───────────────────────────────────────────────────────────

interface JWK {
  kid: string;
  kty: string;
  alg: string;
  n: string;
  e: string;
  use?: string;
}

interface JWKS {
  keys: JWK[];
}

interface JWTHeader {
  alg: string;
  kid: string;
  typ?: string;
}

interface JWTPayload {
  iss: string;
  aud: string;
  exp: number;
  iat: number;
  sub?: string;
  email?: string;
}

// ── Constants ───────────────────────────────────────────────────────

const GOOGLE_JWKS_URL = 'https://www.googleapis.com/oauth2/v3/certs';
const VALID_ISSUERS = ['accounts.google.com', 'https://accounts.google.com'];

// ── JWKS Cache ──────────────────────────────────────────────────────

let cachedJWKS: JWKS | null = null;
let cacheExpiresAt = 0;

async function getGoogleJWKS(): Promise<JWKS> {
  const now = Date.now();
  if (cachedJWKS && now < cacheExpiresAt) {
    return cachedJWKS;
  }

  const res = await fetch(GOOGLE_JWKS_URL);
  if (!res.ok) {
    throw new Error(`Failed to fetch Google JWKS: ${res.status}`);
  }

  cachedJWKS = await res.json() as JWKS;

  // Cache-Control 헤더에서 max-age 추출, 없으면 1시간
  const cacheControl = res.headers.get('cache-control') ?? '';
  const maxAgeMatch = cacheControl.match(/max-age=(\d+)/);
  const maxAge = maxAgeMatch ? parseInt(maxAgeMatch[1], 10) : 3600;
  cacheExpiresAt = now + maxAge * 1000;

  return cachedJWKS;
}

// ── Base64url Helpers ───────────────────────────────────────────────

function base64urlDecode(input: string): Uint8Array {
  // base64url → base64
  let base64 = input.replace(/-/g, '+').replace(/_/g, '/');
  const pad = base64.length % 4;
  if (pad) base64 += '='.repeat(4 - pad);

  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

// ── JWT Verification ────────────────────────────────────────────────

/**
 * Google Pub/Sub OIDC Bearer 토큰을 검증한다.
 *
 * @param token - Authorization 헤더에서 추출한 JWT 문자열
 * @param expectedAudience - Pub/Sub push 구독에 설정된 audience (Edge Function URL)
 * @returns 검증 성공 시 JWT payload, 실패 시 null
 */
export async function verifyGoogleOIDCToken(
  token: string,
  expectedAudience: string,
): Promise<JWTPayload | null> {
  try {
    const parts = token.split('.');
    if (parts.length !== 3) return null;

    // 1. 헤더 파싱 → kid 추출
    const header: JWTHeader = JSON.parse(
      new TextDecoder().decode(base64urlDecode(parts[0])),
    );
    if (header.alg !== 'RS256') return null;

    // 2. JWKS에서 해당 kid의 공개키 찾기
    const jwks = await getGoogleJWKS();
    const jwk = jwks.keys.find((k) => k.kid === header.kid);
    if (!jwk) {
      // kid가 캐시에 없으면 JWKS 갱신 후 재시도
      cacheExpiresAt = 0;
      const refreshedJwks = await getGoogleJWKS();
      const refreshedJwk = refreshedJwks.keys.find((k) => k.kid === header.kid);
      if (!refreshedJwk) return null;
      return await verifyWithKey(refreshedJwk, parts, expectedAudience);
    }

    return await verifyWithKey(jwk, parts, expectedAudience);
  } catch (err) {
    console.error('Google OIDC verification error:', err);
    return null;
  }
}

async function verifyWithKey(
  jwk: JWK,
  parts: string[],
  expectedAudience: string,
): Promise<JWTPayload | null> {
  // JWK → CryptoKey
  const key = await crypto.subtle.importKey(
    'jwk',
    { kty: jwk.kty, n: jwk.n, e: jwk.e, alg: 'RS256', ext: true },
    { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' },
    false,
    ['verify'],
  );

  // 서명 검증
  const data = new TextEncoder().encode(`${parts[0]}.${parts[1]}`);
  const signature = base64urlDecode(parts[2]);

  const valid = await crypto.subtle.verify(
    'RSASSA-PKCS1-v1_5',
    key,
    signature,
    data,
  );
  if (!valid) return null;

  // Claims 검증
  const payload: JWTPayload = JSON.parse(
    new TextDecoder().decode(base64urlDecode(parts[1])),
  );

  const now = Math.floor(Date.now() / 1000);

  if (!VALID_ISSUERS.includes(payload.iss)) return null;
  if (payload.aud !== expectedAudience) return null;
  if (payload.exp < now) return null;

  return payload;
}
