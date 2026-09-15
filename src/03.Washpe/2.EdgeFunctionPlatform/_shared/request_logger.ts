// [담당업무 2] API 요청 로그 (함수·액션·지연·크기·상태·플랫폼) — 라우터 finally 에서 fire-and-forget.

import { supabase } from './supabase.ts';

export interface RequestLog {
  user_id?: string;
  function_name: string;
  action: string;
  duration_ms: number;
  request_body_size?: number;
  response_body_size?: number;
  status_code: number;
  success: boolean;
  error_code?: string;
  error_message?: string;
  request_id?: string;
  metadata?: Record<string, unknown>;
  platform?: string;
}

/** 요청 로그 INSERT. 실패 시 console.error만 출력 (호출자 에러 전파 없음). */
export async function writeRequestLog(entry: RequestLog): Promise<void> {
  const { error } = await supabase.from('api_request_logs').insert(entry);
  if (error) {
    console.error('request_log insert failed:', error.message);
  }
}

/** User-Agent에서 플랫폼 감지. */
export function detectPlatform(req: Request): string {
  const ua = req.headers.get('user-agent') ?? '';
  if (ua.startsWith('Washpe-App/') || ua.startsWith('supabase-flutter/')) {
    return 'mobile';
  }
  return 'web';
}
