// [담당업무 1] 함수 내부 JWT 검증 — 필수 인증(verifyAuth) / 선택 인증(tryVerifyAuth, Guest·Expired 구분).

import { supabase } from './supabase.ts';

export interface AuthResult {
  userId: string;
}

/**
 * Authorization 헤더에서 JWT를 추출하고 Supabase Auth로 검증한다.
 *
 * --no-verify-jwt 배포 시 인프라 레벨 검증이 비활성화되므로,
 * 이 함수로 직접 인증을 처리한다.
 * supabase.auth.getUser()는 알고리즘(HS256/ES256)과 무관하게 동작한다.
 */
export async function verifyAuth(req: Request): Promise<AuthResult> {
  const authHeader = req.headers.get('authorization');
  if (!authHeader?.startsWith('Bearer ')) {
    throw new AuthError('Missing or invalid Authorization header');
  }

  const token = authHeader.slice(7);
  const { data, error } = await supabase.auth.getUser(token);

  if (error || !data.user) {
    throw new AuthError(error?.message ?? 'Invalid token');
  }

  return { userId: data.user.id };
}

/** 인증 실패 에러. */
export class AuthError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'AuthError';
  }
}

// ── Optional Auth (public actions) ──────────────────────────────────────────

export interface OptionalAuthResult {
  userId: string | null;
  /** Guest(토큰 없음) vs Expired(토큰 만료/변조) 구분 — 보안 로그용. */
  reason?: 'no_token' | 'invalid_token';
}

/**
 * 선택적 인증 — public action용.
 *
 * 토큰이 없거나 유효하지 않아도 예외를 던지지 않고 `userId: null`을 반환한다.
 * `reason` 필드로 Guest / Expired를 구분하여 로깅에 활용할 수 있다.
 */
export async function tryVerifyAuth(req: Request): Promise<OptionalAuthResult> {
  const authHeader = req.headers.get('authorization');
  if (!authHeader?.startsWith('Bearer ')) {
    return { userId: null, reason: 'no_token' };
  }

  const token = authHeader.slice(7);
  const { data, error } = await supabase.auth.getUser(token);

  if (error || !data.user) {
    return { userId: null, reason: 'invalid_token' };
  }

  return { userId: data.user.id };
}
