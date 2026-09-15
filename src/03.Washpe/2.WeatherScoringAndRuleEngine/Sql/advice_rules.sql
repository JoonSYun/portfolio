-- [담당업무 2] 규칙 테이블 + RLS(관리자만 쓰기) + 기존 하드코딩 규칙 27개 이관 seed.

-- ============================================================================
-- advice_rules: DB-driven Rule Engine for chemical advice
-- 날씨 조건 + 약품 재고 기반 맞춤 추천 규칙을 DB에서 관리
-- ============================================================================

create table public.advice_rules (
  id               uuid        primary key default gen_random_uuid(),
  layer            text        not null check (layer in ('warning', 'positive', 'catalog', 'fallback')),
  sort_order       int         not null,
  is_active        boolean     not null default true,

  -- 조건: JSONB (variable__operator 형식, 모든 절 AND 결합)
  -- 변수: humidity, temperature, dust, wind_speed, visibility, dwi_score, available_count
  -- 연산자: __gt, __gte, __lt, __lte, __eq, __in (없으면 __eq)
  condition        jsonb       not null default '{}',

  -- 대상 케미컬 카테고리 (null = 카테고리 무관)
  target_categories text[]     default null,

  -- 보유 모드: owned(보유 시) / unowned(미보유 시) / any(무관)
  ownership_mode   text        not null default 'any'
                   check (ownership_mode in ('owned', 'unowned', 'any')),

  -- 메시지 템플릿 ({product_name}, {product_brand}, {humidity}, {temperature}, {wind_speed}, {visibility}, {dwi_score})
  message_template text        not null,
  emoji            text        not null default '',

  -- 레이어 내 다른 규칙이 이미 발동했으면 스킵 (범용 폴백용)
  is_layer_fallback boolean    not null default false,

  description      text,
  created_at       timestamptz not null default now(),
  updated_at       timestamptz not null default now()
);

-- 인덱스: 활성 규칙 조회 최적화
create index idx_advice_rules_active
  on public.advice_rules (is_active, layer, sort_order);

-- RLS
alter table public.advice_rules enable row level security;

create policy "advice_rules: 누구나 읽기"
  on public.advice_rules for select using (true);

create policy "advice_rules: 관리자 추가"
  on public.advice_rules for insert with check (
    exists (select 1 from public.profiles where profiles.id = auth.uid() and profiles.is_admin = true)
  );

create policy "advice_rules: 관리자 수정"
  on public.advice_rules for update using (
    exists (select 1 from public.profiles where profiles.id = auth.uid() and profiles.is_admin = true)
  );

create policy "advice_rules: 관리자 삭제"
  on public.advice_rules for delete using (
    exists (select 1 from public.profiles where profiles.id = auth.uid() and profiles.is_admin = true)
  );

-- updated_at 트리거 (기존 set_updated_at 함수 재사용)
create trigger advice_rules_updated_at
  before update on public.advice_rules
  for each row execute function public.set_updated_at();

-- ============================================================================
-- Seed: 기존 하드코딩 규칙 전량 이관 (27개)
-- ============================================================================

-- ── Layer 1: Warning ────────────────────────────────────────────────────────

-- W1: 고습 — detailer 보유
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 10, '{"humidity__gt": 70}', '{detailer}', 'owned',
  '보유한 "{product_name}" 사용을 권장합니다 — 고습에서도 안정적으로 작업 가능합니다.',
  '✅', '고습 — detailer 보유 시 추천');

-- W2: 고습 — detailer 미보유
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 11, '{"humidity__gt": 70}', '{detailer}', 'unowned',
  '스프레이 실런트 / 퀵 디테일러(QD) — 고습에서도 빠르게 경화됩니다.',
  '✅', '고습 — detailer 미보유 시 일반 안내');

-- W3: 고습 — wax 보유 주의
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 20, '{"humidity__gt": 70}', '{waxAndSealant}', 'owned',
  '보유한 "{product_name}" 사용 주의 — 습도 {humidity}%에서 왁스가 밀착되지 않고 얼룩질 수 있습니다.',
  '⚠️', '고습 — wax 보유 시 경고');

-- W4: 고습 — wax 미보유 경고
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 21, '{"humidity__gt": 70}', '{waxAndSealant}', 'unowned',
  '카나우바 페이스트 왁스 — 습도 70% 이상에서는 밀착이 어렵고 얼룩집니다.',
  '⚠️', '고습 — wax 미보유 시 일반 경고');

-- W5: 고온 — 패널별 세차 (공통)
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 30, '{"temperature__gt": 28}', null, 'any',
  '패널별 세차 기법 — 고온에서 제품이 급속 건조되지 않도록 한 패널씩 작업하세요.',
  '✅', '고온 — 패널별 기법 안내');

-- W6: 고온 — preWash 보유
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 31, '{"temperature__gt": 28}', '{preWash}', 'owned',
  '보유한 "{product_name}" — 고온에서 폼 건으로 충분히 불린 후 작업하세요.',
  '✅', '고온 — preWash 보유 시 팁');

-- W7: 고온 — ironRemover 보유 주의
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 40, '{"temperature__gt": 28}', '{ironRemover}', 'owned',
  '보유한 "{product_name}" — 고온 도장면에 에칭(부식) 위험이 있으니 주의하세요.',
  '⚠️', '고온 — ironRemover 보유 시 경고');

-- W8: 고온 — ironRemover 미보유 경고
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 41, '{"temperature__gt": 28}', '{ironRemover}', 'unowned',
  '강산성·강알칼리 케미컬 — 뜨거운 도장면에 에칭(부식) 위험이 매우 높습니다.',
  '⚠️', '고온 — ironRemover 미보유 시 일반 경고');

-- W9: 미세먼지 — preWash 보유
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 50, '{"dust__in": ["bad", "veryBad", "hazardous"]}', '{preWash}', 'owned',
  '보유한 "{product_name}" — 미세먼지가 많은 날 충분한 프리워시로 스크래치를 방지하세요.',
  '✅', '미세먼지 — preWash 보유 시 추천');

-- W10: 미세먼지 — preWash 미보유
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 51, '{"dust__in": ["bad", "veryBad", "hazardous"]}', '{preWash}', 'unowned',
  '스노우 폼 프리워시 — 미세먼지 입자를 접촉 없이 제거하세요.',
  '✅', '미세먼지 — preWash 미보유 시 일반 안내');

-- W11: 미세먼지 — clayBar 보유 주의
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 52, '{"dust__in": ["bad", "veryBad", "hazardous"]}', '{clayBar}', 'owned',
  '보유한 "{product_name}" — 미세먼지가 심한 날 클레이바 사용은 스크래치 위험이 높습니다.',
  '⚠️', '미세먼지 — clayBar 보유 시 경고');

-- W12: 강풍
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 60, '{"wind_speed__gt": 6}', null, 'any',
  '바람이 강합니다 ({wind_speed}m/s) — 스프레이 제품은 바람에 날리므로 직접 도포 방식을 사용하세요.',
  '⚠️', '강풍 경고');

-- W13: 저온 — 공통
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 70, '{"temperature__lt": 5, "temperature__gte": 0}', null, 'any',
  '저온 주의 — 세제 희석 농도를 평소보다 높이고, 따뜻한 물을 사용하세요.',
  '⚠️', '저온 공통 안내');

-- W14: 저온 — shampoo 보유
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 71, '{"temperature__lt": 5, "temperature__gte": 0}', '{shampoo}', 'owned',
  '보유한 "{product_name}" — 따뜻한 물에 희석하여 사용하세요.',
  '✅', '저온 — shampoo 보유 시 팁');

-- W15: 저시정 — coating 보유 주의
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 80, '{"visibility__lt": 3000}', '{glassCoat,paintProtection}', 'owned',
  '보유한 "{product_name}" — 안개/스모그 상태에서 코팅 작업은 경화 불량 위험이 있습니다.',
  '⚠️', '저시정 — coating 보유 시 경고');

-- W16: 저시정 — coating 미보유 경고
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('warning', 81, '{"visibility__lt": 3000}', '{glassCoat,paintProtection}', 'unowned',
  '가시거리가 낮습니다 ({visibility}) — 안개/스모그 상태에서 코팅 작업은 피하세요.',
  '⚠️', '저시정 — coating 미보유 시 일반 경고');

-- ── Layer 2: Positive ───────────────────────────────────────────────────────

-- P1: wax 작업 최적
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('positive', 10, '{"dwi_score__gte": 60}', '{waxAndSealant}', 'owned',
  '보유한 "{product_name}" — 오늘 날씨는 왁스 작업에 최적입니다!',
  '✨', 'DWI 60+ — wax 보유 시 긍정 추천');

-- P2: coating 작업 적합
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('positive', 20, '{"dwi_score__gte": 60, "humidity__lte": 70}', '{glassCoat,paintProtection}', 'owned',
  '보유한 "{product_name}" — 코팅 작업에 좋은 날씨입니다.',
  '✨', 'DWI 60+ 저습 — coating 보유 시 긍정 추천');

-- P3: 폴리싱 이상적
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('positive', 30, '{"dwi_score__gte": 80}', '{compound,polish}', 'owned',
  '보유한 "{product_name}" — 폴리싱 작업에 이상적인 조건입니다.',
  '✨', 'DWI 80+ — compound/polish 보유 시 긍정 추천');

-- P4: 클레이바 적합
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('positive', 40, '{"dwi_score__gte": 60, "dust__in": ["good", "moderate"]}', '{clayBar}', 'owned',
  '보유한 "{product_name}" — 미세먼지가 적어 클레이바 작업에 적합합니다.',
  '✨', 'DWI 60+ 양호먼지 — clayBar 보유 시 긍정 추천');

-- P5: 샴푸 적합 기온
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('positive', 50, '{"dwi_score__gte": 60, "temperature__gte": 10, "temperature__lte": 25}', '{shampoo}', 'owned',
  '보유한 "{product_name}" — 샴푸 세차에 적합한 기온입니다.',
  '✨', 'DWI 60+ 적정기온 — shampoo 보유 시 긍정 추천');

-- P6: 범용 긍정 (layer fallback — 다른 positive 규칙이 없을 때만)
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, is_layer_fallback, description)
values ('positive', 60, '{"dwi_score__gte": 60, "available_count__gt": 0}', null, 'any',
  '오늘은 세차와 디테일링에 좋은 날씨입니다. 보유한 제품으로 멋지게 관리하세요!',
  '✨', true, '범용 긍정 폴백 — 다른 positive 규칙 미발동 시');

-- ── Layer 3: Catalog ────────────────────────────────────────────────────────

-- C1: DWI 80+ — wax 카탈로그
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('catalog', 10, '{"dwi_score__gte": 80}', '{waxAndSealant}', 'unowned',
  '"{product_name}" ({product_brand}) — 오늘 같은 날씨에 왁스 작업을 추천합니다',
  '🛒', 'DWI 80+ — wax 미보유 시 카탈로그 추천');

-- C2: DWI 80+ — coating 카탈로그
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('catalog', 20, '{"dwi_score__gte": 80}', '{glassCoat,paintProtection}', 'unowned',
  '"{product_name}" ({product_brand}) — 코팅 작업에 적합한 날씨입니다',
  '🛒', 'DWI 80+ — coating 미보유 시 카탈로그 추천');

-- C3: DWI 60+ — detailer 카탈로그
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('catalog', 30, '{"dwi_score__gte": 60}', '{detailer}', 'unowned',
  '"{product_name}" ({product_brand}) — 간단한 디테일링 관리를 추천합니다',
  '🛒', 'DWI 60+ — detailer 미보유 시 카탈로그 추천');

-- C4: 고습 — detailer 카탈로그
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('catalog', 40, '{"humidity__gt": 70}', '{detailer}', 'unowned',
  '"{product_name}" ({product_brand}) — 고습 환경에서 안정적인 제품입니다',
  '🛒', '고습 — detailer 미보유 시 카탈로그 추천');

-- C5: 미세먼지 — preWash 카탈로그
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('catalog', 50, '{"dust__in": ["bad", "veryBad", "hazardous"]}', '{preWash}', 'unowned',
  '"{product_name}" ({product_brand}) — 미세먼지 제거에 효과적인 프리워시입니다',
  '🛒', '미세먼지 — preWash 미보유 시 카탈로그 추천');

-- C6: 고온 — preWash 카탈로그
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('catalog', 60, '{"temperature__gt": 28}', '{preWash}', 'unowned',
  '"{product_name}" ({product_brand}) — 고온에서 폼 프리워시가 필수적입니다',
  '🛒', '고온 — preWash 미보유 시 카탈로그 추천');

-- C7: 저온 — shampoo 카탈로그
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('catalog', 70, '{"temperature__lt": 5, "temperature__gte": 0}', '{shampoo}', 'unowned',
  '"{product_name}" ({product_brand}) — 저온에서 따뜻한 물과 함께 사용하세요',
  '🛒', '저온 — shampoo 미보유 시 카탈로그 추천');

-- C8: 범용 — shampoo 카탈로그
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('catalog', 80, '{}', '{shampoo}', 'unowned',
  '"{product_name}" ({product_brand}) — 기본 세차 샴푸를 갖추면 좋은 결과를 얻을 수 있습니다',
  '🛒', '범용 — shampoo 미보유 시 카탈로그 추천');

-- C9: 범용 — preWash 카탈로그
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('catalog', 90, '{}', '{preWash}', 'unowned',
  '"{product_name}" ({product_brand}) — 프리워시로 스크래치 없는 세차를 시작하세요',
  '🛒', '범용 — preWash 미보유 시 카탈로그 추천');

-- ── Layer 4: Fallback ───────────────────────────────────────────────────────

-- F1: 점수 좋을 때 긍정 폴백
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('fallback', 10, '{"dwi_score__gte": 60}', null, 'any',
  '오늘은 세차하기 좋은 날씨입니다!',
  '✨', '폴백 — DWI 60+ 긍정');

-- F2: 범용 폴백
insert into public.advice_rules (layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, description)
values ('fallback', 20, '{}', null, 'any',
  '날씨 상황을 확인하고 세차 계획을 세우세요.',
  'ℹ️', '폴백 — 범용 안내');
