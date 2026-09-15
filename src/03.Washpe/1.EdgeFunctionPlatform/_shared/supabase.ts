// [담당업무 1] service_role 공유 클라이언트 — RLS 우회이므로 소유권 검증은 핸들러 책임.

import { createClient } from 'jsr:@supabase/supabase-js@2';

/**
 * 전체 Edge Function에서 공유하는 Supabase 서비스 역할 클라이언트.
 *
 * `service_role` 키를 사용하므로 RLS를 우회한다.
 * 모든 쿼리에서 `user_id = userId` 소유권 검증을 명시적으로 수행해야 한다.
 */
export const supabase = createClient(
  Deno.env.get('SUPABASE_URL')!,
  Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!,
);
