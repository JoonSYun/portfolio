-- [담당업무 4] store_transaction_id partial unique index — 중복 영수증·웹훅 재시도의 DB 레벨 멱등성.

-- store_transaction_id에 UNIQUE 인덱스 추가 (NULL 허용, 멱등성 보장)
-- 중복 영수증/웹훅 재시도 시 동일 트랜잭션 중복 처리 방지
CREATE UNIQUE INDEX IF NOT EXISTS uq_subscriptions_store_txn
  ON public.subscriptions (store_transaction_id)
  WHERE store_transaction_id IS NOT NULL;
