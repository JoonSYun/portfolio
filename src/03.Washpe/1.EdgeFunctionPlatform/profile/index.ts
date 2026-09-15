// [담당업무 1] 라우터 위의 파생 함수 예시 + 시작 API 통합(bootstrap: verifyAuth 1회로 4개 조회 병렬).

import { createRouter } from '../_shared/router.ts';
import { supabase } from '../_shared/supabase.ts';
import { jsonResponse } from '../_shared/response.ts';
import { writeAuditLog } from '../_shared/audit.ts';

Deno.serve(createRouter('profile', {

  /**
   * 프로필 수정 (닉네임/아바타).
   */
  async update_profile(req, userId, params) {
    const { nickname, avatar_url } = params as {
      nickname?: string;
      avatar_url?: string;
    };

    const updates: Record<string, unknown> = {
      updated_at: new Date().toISOString(),
    };
    if (nickname !== undefined) updates.nickname = nickname;
    if (avatar_url !== undefined) updates.avatar_url = avatar_url;

    const { error } = await supabase
      .from('profiles')
      .update(updates)
      .eq('id', userId);

    if (error) {
      return jsonResponse(req, { error: error.message, code: 'DB_ERROR' }, 500);
    }

    await writeAuditLog({
      user_id: userId,
      operation: 'UPDATE',
      source: 'profiles',
      record_id: userId,
      metadata: { nickname, avatar_url },
    });

    // 업데이트된 프로필 반환
    const { data } = await supabase
      .from('profiles')
      .select()
      .eq('id', userId)
      .single();

    return jsonResponse(req, data ?? {});
  },

  /**
   * 구독 상태 단독 조회.
   * SubscriptionCubit._silentRefresh(), checkExistingSubscription() 등에서 사용.
   */
  async get_subscription(req, userId) {
    const { data } = await supabase.rpc('get_user_subscription', {
      p_user_id: userId,
    });

    return jsonResponse(req, { subscription: data });
  },

  /**
   * 앱 시작 Bootstrap — profile + subscription + cities + chemicals 통합 조회.
   *
   * 단일 verifyAuth로 4개 DB 쿼리를 병렬 실행하여
   * 기존 hydrate_user + cities/get_all + chemicals/get_all 3개 호출을 1개로 통합.
   */
  async bootstrap(req, userId) {
    const [profileResult, subResult, citiesResult, chemicalsResult] =
      await Promise.all([
        supabase.from('profiles').select().eq('id', userId).single(),
        supabase.rpc('get_user_subscription', { p_user_id: userId }),
        supabase
          .from('user_cities')
          .select()
          .eq('user_id', userId)
          .order('created_at'),
        supabase
          .from('chemicals')
          .select()
          .eq('user_id', userId)
          .order('purchase_date', { ascending: false })
          .order('name'),
      ]);

    if (profileResult.error) {
      return jsonResponse(
        req,
        { error: 'Profile not found', code: 'NOT_FOUND' },
        404,
      );
    }

    return jsonResponse(req, {
      profile: profileResult.data,
      subscription: subResult.data ?? null,
      cities: citiesResult.data ?? [],
      chemicals: chemicalsResult.data ?? [],
    });
  },

}));
