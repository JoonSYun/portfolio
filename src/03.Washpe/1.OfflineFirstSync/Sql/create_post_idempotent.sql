-- [담당업무 1] 서버 측 멱등 RPC — 같은 키의 재전송은 최초 응답을 그대로 반환.

-- ============================================================================
-- create_post_idempotent RPC 재생성
-- ============================================================================
-- 기존 함수가 잘못된 컬럼명을 참조하는 문제 수정.
-- idempotency_keys.key 컬럼을 올바르게 참조하도록 재정의.

CREATE OR REPLACE FUNCTION create_post_idempotent(
  p_idempotency_key UUID,
  p_id              UUID,
  p_author_id       UUID,
  p_category        TEXT,
  p_title           TEXT,
  p_body            TEXT,
  p_status          TEXT DEFAULT 'published',
  p_wash_log_id     UUID DEFAULT NULL
)
RETURNS JSON
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_existing JSONB;
  v_result   JSONB;
BEGIN
  -- 중복 확인
  SELECT response INTO v_existing
    FROM idempotency_keys
   WHERE key = p_idempotency_key;

  IF v_existing IS NOT NULL THEN
    RETURN v_existing;
  END IF;

  -- 게시물 생성
  INSERT INTO posts (id, author_id, category, title, body, status, wash_log_id, published_at)
  VALUES (p_id, p_author_id, p_category, p_title, p_body, p_status, p_wash_log_id, NOW())
  RETURNING row_to_json(posts.*) INTO v_result;

  -- Idempotency key 저장
  INSERT INTO idempotency_keys (key, response)
  VALUES (p_idempotency_key, v_result);

  RETURN v_result;
END;
$$;
