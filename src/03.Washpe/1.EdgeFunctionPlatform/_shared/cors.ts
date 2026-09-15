// [담당업무 1] Origin 허용 목록 기반 CORS + 모바일(Origin 없음) UA 보조 검증.

// ── Allowed origins ──────────────────────────────────────────────────────────

const ALLOWED_ORIGINS = new Set([
  'https://washpe.pages.dev',
  'https://washpe-supervise.pages.dev',
]);

// 로컬 개발 환경 동적 추가
if (Deno.env.get('DENO_ENV') !== 'production') {
  ALLOWED_ORIGINS.add('http://localhost:3000');
  ALLOWED_ORIGINS.add('http://localhost:5173');
  ALLOWED_ORIGINS.add('http://localhost:54321');
}

/** 모바일 앱 커스텀 User-Agent 프리픽스. */
const APP_UA_PREFIX = 'Washpe-App/';

/** Supabase Flutter SDK의 User-Agent 프리픽스. */
const SUPABASE_UA_PREFIX = 'supabase-flutter/';

/**
 * 요청의 Origin을 검사하여 적절한 CORS 헤더를 반환한다.
 *
 * - 모바일 앱: Origin 없음 → `*` (UA 보조 검증 + 경고 로깅)
 * - 허용된 웹 Origin: 해당 origin 반환 + `Vary: Origin`
 * - 비허용 Origin: ACAO 헤더 없이 반환 → 브라우저가 차단
 */
export function getCorsHeaders(req: Request): Record<string, string> {
  const origin = req.headers.get('origin');

  let allowOrigin: string;

  if (!origin) {
    // Origin 없음 — 모바일 앱 또는 서버-to-서버 (webhook 등)
    const ua = req.headers.get('user-agent') ?? '';
    if (!ua.startsWith(APP_UA_PREFIX) && !ua.startsWith(SUPABASE_UA_PREFIX)) {
      console.warn(`No-origin request without app UA: ua="${ua.slice(0, 80)}"`);
    }
    allowOrigin = '*';
  } else if (ALLOWED_ORIGINS.has(origin)) {
    allowOrigin = origin;
  } else {
    console.warn(`CORS blocked: origin="${origin}"`);
    return { 'Access-Control-Allow-Methods': 'POST, OPTIONS' };
  }

  return {
    'Access-Control-Allow-Origin': allowOrigin,
    'Access-Control-Allow-Headers':
      'authorization, x-client-info, apikey, content-type',
    'Access-Control-Allow-Methods': 'POST, OPTIONS',
    ...(origin ? { Vary: 'Origin' } : {}),
  };
}
