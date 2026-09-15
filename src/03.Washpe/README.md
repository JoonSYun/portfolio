# [개인 프로젝트] Washpe — 세차 날씨·커뮤니티 앱

| | |
|---|---|
| 기간 | 2026.02 ~ 2026.04 |
| 규모 | 1명 (기획·설계·백엔드·앱·배포 단독) |
| 기술 스택 | Supabase (PostgreSQL, Edge Functions / Deno·TypeScript, RLS), Cloudflare R2·Pages, Google Cloud Run, Flutter (BLoC, Drift, get_it/injectable), Google Play·App Store 결제 |

**개요** — 날씨로 세차 적합도(DWI, 0-100)를 계산하고, 보유 약품 재고와 날씨 조건을 함께 봐서 맞춤 조언을 주는 앱.
세차 커뮤니티와 구독 결제도 들어 있다.
데이터 접근은 **모두 Edge Function 을 거친다**. 클라이언트는 PostgREST 를 직접 호출하지 않고, 서버가 service_role 로 접근하면서 소유권 검증·감사 로그·요청 로그를 맡는다.
Edge Function 14종, DB 마이그레이션 30개, 앱과 웹을 같은 백엔드로 운영한다(웹은 Cloudflare Pages).

> 회사 코드가 아니라 개인 프로젝트라서 이 폴더의 코드는 **원문 그대로**입니다(파일 상단 `[담당업무 N]` 주석만 추가).
> 비밀키는 모두 `Deno.env` 로 주입되며 저장소에 포함되어 있지 않습니다.

## 담당업무 ↔ 코드

### 1) Edge Function 공통 플랫폼과 시작 API 통합 → [`1.EdgeFunctionPlatform/`](1.EdgeFunctionPlatform/)
> 인증·CORS·입력 파싱·역할 검사·요청 로그를 공통 라우터 한 곳으로 모아, 각 함수는 `action → handler` 등록만 하도록 표준화.
> 앱 시작 시 흩어져 있던 호출을 집계 액션으로 통합해 Edge Function 호출을 10-12회에서 4-5회로, JWT 검증을 4-5회에서 1회로 줄임

| 파일 | 내용 |
|---|---|
| [`_shared/router.ts`](1.EdgeFunctionPlatform/_shared/router.ts) | **공통 라우터**. OPTIONS → safe JSON 파싱(빈 body·배열·깨진 JSON 방어) → action 조회 → 필수/선택 인증(`publicActions`) → `adminOnly` 역할 검사 → 핸들러 → `finally` 에서 요청 로그(지연·요청/응답 크기·에러 코드·플랫폼)를 fire-and-forget |
| [`_shared/auth.ts`](1.EdgeFunctionPlatform/_shared/auth.ts) | `--no-verify-jwt` 배포 전제에서 함수 내부 JWT 검증. 선택 인증은 `no_token`(게스트)과 `invalid_token`(만료·변조)을 구분해 보안 로그에 남김 |
| [`_shared/cors.ts`](1.EdgeFunctionPlatform/_shared/cors.ts) · [`_shared/response.ts`](1.EdgeFunctionPlatform/_shared/response.ts) | Origin 허용 목록 + `Vary: Origin`, 모바일은 UA 보조 검증. 500 응답은 내부 메시지를 숨기고 `x-request-id` 로 로그 매칭 |
| [`_shared/audit.ts`](1.EdgeFunctionPlatform/_shared/audit.ts) · [`_shared/request_logger.ts`](1.EdgeFunctionPlatform/_shared/request_logger.ts) · [`_shared/supabase.ts`](1.EdgeFunctionPlatform/_shared/supabase.ts) | 감사 로그·요청 로그. 로그 실패가 본 요청을 실패시키지 않도록 격리. service_role 공유 클라이언트 |
| [`profile/index.ts`](1.EdgeFunctionPlatform/profile/index.ts) | 라우터 위 파생 함수 예시. `bootstrap` = `verifyAuth` 1회 + 4개 조회 `Promise.all` |
| [`StartupApiOptimization.md`](1.EdgeFunctionPlatform/StartupApiOptimization.md) | 설계 기록 — 화면 단위 호출을 시작 시점 기준 2-Phase(비인증 즉시 → 인증 집계)로 재설계 |

### 2) 날씨 스코어링과 DB 기반 규칙 엔진 → [`2.WeatherScoringAndRuleEngine/`](2.WeatherScoringAndRuleEngine/)
> 기상청 단기·중기예보와 Open-Meteo 를 서버에서 병합해 DWI 점수를 계산하고, 5km 격자 단위 PostgreSQL 캐시를 사용자 간에 공유해 외부 API 호출을 줄임.
> 코드에 하드코딩돼 있던 약품 조언 분기 27개를 `advice_rules` 테이블로 옮겨, 조언 규칙은 배포 없이 데이터만 바꿔 조정

| 파일 | 내용 |
|---|---|
| [`weather-index/index.ts`](2.WeatherScoringAndRuleEngine/weather-index/index.ts) | 요청 1회로 current·hourly·daily 점수와 조언을 반환. 위경도 → 격자 → `weather_cache` 조회(hourly 30분, daily 3시간 TTL). KMA 는 필수, Open-Meteo 는 `allSettled` 로 실패해도 진행(graceful degradation). 위치별 `allSettled` 로 부분 실패 허용. 약품은 사용자별 5분 LRU, 카탈로그·규칙은 isolate 메모리 캐시 |
| [`_shared/weather_scoring.ts`](2.WeatherScoringAndRuleEngine/_shared/weather_scoring.ts) | 스코어링 엔진. Kill-switch 5종(강수확률>60%·영하·강우·강설·뇌우) + 감점 7종을 구간별 선형 보간해 경계에서 점수가 튀지 않게 함. 시간별 → 클라이언트 시간대 기준 일별 집계, 단기와 중기 병합(단기 우선) |
| [`_shared/rule_engine.ts`](2.WeatherScoringAndRuleEngine/_shared/rule_engine.ts) · [`_shared/chemical_advice.ts`](2.WeatherScoringAndRuleEngine/_shared/chemical_advice.ts) | **규칙 엔진**. JSONB 조건 `humidity__gt`·`dust__in` 등을 AND 평가. `warning → positive → catalog(최대 2) → fallback` 4계층, 보유 여부(`owned/unowned/any`)로 재고·만료를 반영, 메시지 템플릿 치환. DB 실패 시 stale 캐시, 규칙이 비어도 최소 1개 보장 |
| [`_shared/kma_grid.ts`](2.WeatherScoringAndRuleEngine/_shared/kma_grid.ts) | 위경도 → 기상청 격자(LCC 투영) 포팅. 캐시 키이자 KMA 입력 |
| [`Sql/advice_rules.sql`](2.WeatherScoringAndRuleEngine/Sql/advice_rules.sql) · [`Sql/weather_cache.sql`](2.WeatherScoringAndRuleEngine/Sql/weather_cache.sql) | 규칙 스키마 + RLS(관리자만 쓰기) + 기존 27개 규칙 seed / `(grid_nx, grid_ny, forecast_type)` PK 캐시 |

### 3) 오프라인 우선 동기화 (Transactional Outbox) → [`3.OfflineFirstSync/`](3.OfflineFirstSync/)
> 게시물 작성을 로컬 트랜잭션(draft·사진·outbox)으로 먼저 확정하고, 동기화 엔진이 네트워크 상태에 맞춰 서버로 전송.
> 클라이언트 idempotency key 와 서버 멱등 RPC 를 짝지어 재전송해도 중복이 생기지 않게 하고, 실패는 Fatal/Transient 로 나눠 재시도 여부를 결정

| 파일 | 내용 |
|---|---|
| [`draft_repository_impl.dart`](3.OfflineFirstSync/draft_repository_impl.dart) · [`sync_outbox_table.dart`](3.OfflineFirstSync/sync_outbox_table.dart) | 작성 = draft + 사진 메타 + outbox 한 트랜잭션. outbox 에 `idempotencyKey`·`retryCount/maxRetries`·`nextRetryAt`·`payload` |
| [`sync_engine.dart`](3.OfflineFirstSync/sync_engine.dart) · [`semaphore.dart`](3.OfflineFirstSync/semaphore.dart) | 앱 시작·네트워크 복귀·포그라운드 복귀 3개 트리거. 백오프 시각이 지난 작업만 조회, 동시성 3, 만료 60초 전 세션 선갱신, 시작 시 orphan 복구, 24시간 지난 synced draft 정리 |
| [`workers/post_sync_worker.dart`](3.OfflineFirstSync/workers/post_sync_worker.dart) | 사진 압축(원본·썸네일) → **배치 presign 1회** → R2 병렬 PUT → 멱등 INSERT → 사진 메타. 인증·중복·로컬 저장 실패·파일 없음 = Fatal(즉시 중단), 5xx·네트워크 = Transient(5·10·20·40·80초 백오프) |
| [`workers/post_delete_worker.dart`](3.OfflineFirstSync/workers/post_delete_worker.dart) | DB hard-delete(CASCADE) → payload 의 R2 키 정리. `NotFound` 는 이미 반영된 것으로 보고 성공 처리 |
| [`Sql/create_post_idempotent.sql`](3.OfflineFirstSync/Sql/create_post_idempotent.sql) | 서버 멱등 RPC — 같은 키면 최초 응답 반환(`idempotency_keys`, 24시간 만료) |
| [`Incidents/OutboxSync_AtomicityAndOrphanRecovery.md`](3.OfflineFirstSync/Incidents/OutboxSync_AtomicityAndOrphanRecovery.md) | 인시던트 — 성공 정리의 비원자성으로 draft 가 `syncing` 에 고착, 트랜잭션화 + orphan 복구 + 중복 응답 성공 처리 |

### 4) 구독 결제 서버 검증과 스토어 웹훅 → [`4.SubscriptionVerification/`](4.SubscriptionVerification/)
> 클라이언트가 보낸 결제 결과를 그대로 믿지 않고 서버에서 스토어 API·서명 체인으로 다시 검증.
> 갱신·취소·환불 웹훅도 페이로드 대신 스토어의 최신 상태를 재조회해 반영하고, DB unique 제약으로 중복 영수증과 웹훅 재시도를 멱등 처리

| 파일 | 내용 |
|---|---|
| [`validate-receipt/index.ts`](4.SubscriptionVerification/validate-receipt/index.ts) | JWT 인증 → 입력 검증 → 플랫폼별 검증 → `subscriptions` UPSERT → 감사 로그. `23505`(중복 거래)는 기존 구독을 돌려주는 멱등 응답 |
| [`store-webhook/index.ts`](4.SubscriptionVerification/store-webhook/index.ts) | Google RTDN: OIDC 인증 → Pub/Sub 디코딩 → 처리 대상 타입만 → `subscriptionsv2` 재조회. Apple SNv2: 알림 JWS와 거래 JWS **이중 검증**. 스토어 무한 재시도를 막으려 항상 200, 상태 전이(old → new)는 감사 로그로 남김 |
| [`_shared/store_validation.ts`](4.SubscriptionVerification/_shared/store_validation.ts) | Apple: x5c 체인 → Apple Root CA G3 검증(24시간 캐시) 후 리프 공개키로 서명 검증, productId 일치 확인. Google: 서비스 계정 RS256 JWT → access token → 구독 상태 7종 매핑 |
| [`_shared/google_oidc.ts`](4.SubscriptionVerification/_shared/google_oidc.ts) | JWKS 를 `Cache-Control max-age` 만큼 캐시, 모르는 `kid` 면 1회 갱신 후 재시도. iss·aud·exp 검증 |
| [`Sql/subscription_store_txn_unique.sql`](4.SubscriptionVerification/Sql/subscription_store_txn_unique.sql) | `store_transaction_id` partial unique index |

---

**관련 저장소 (비공개)** — 규칙·카탈로그·앱 버전·사용량을 관리하는 운영자용 관리 콘솔(Flutter Web, 관리자 RLS 정책 기반 접근).
