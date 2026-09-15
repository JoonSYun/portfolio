# 설계 기록: 앱 시작 API 통합 (10-12회 → 4-5회)

> [담당업무 2] "시작 시 흩어진 호출을 집계 액션으로 통합해 Edge Function 호출과 JWT 검증 횟수를 줄임"

## 문제
- 앱 시작 시 Edge Function 이 **10-12회** 호출되고 그중 JWT 검증(`verifyAuth`)이 **4-5회** 반복됐다. `verifyAuth` 한 번에 200-400ms가 걸린다.
- 인증 후 프로필 조회(`hydrate_user`)가 `authStateChanges.asyncMap` 안에서 실행돼 인증을 기다리는 BLoC 7개가 모두 300-500ms씩 대기했다.
- 같은 데이터를 여러 곳에서 따로 불렀다. 피드·인기글은 auth 구독과 `initState` 에서 각각 2회, 구독 정보는 hydrate 결과에 이미 있는데 1초 뒤 한 번 더, `weather-index` 는 생성자·도시 변경·GPS 변경마다 해서 최대 3회였다.

## 원인
호출이 **화면(BLoC) 단위**로 설계돼 있었다. BLoC 마다 필요한 데이터를 스스로 부르다 보니, 같은 사용자의 같은 시작 시점인데도 요청과 인증이 BLoC 수만큼 늘었다.

## 해결 — 시작 시점을 기준으로 호출 재설계
1. **비인증 우선 (Phase 1)** — 로그인 없이 볼 수 있는 데이터는 인증을 기다리지 않고 즉시 조회한다.
   `community/get_community_init` 이 피드와 인기글을 `Promise.all` 로 묶어 1회에 돌려준다. 날씨는 GPS가 없으면 서울을 기본값으로 쓴다.
2. **인증 데이터 집계 (Phase 2)** — [`profile/index.ts`](profile/index.ts) 의 `bootstrap` 액션은
   `verifyAuth` 1회 후 profile·subscription·cities·chemicals 4개를 병렬 조회한다. 기존 개별 액션은 CRUD·새로고침 용도로만 남겼다.
3. **인증 상태는 즉시, 데이터는 백그라운드로** — `asyncMap` 대신 JWT 메타데이터로 `AuthAuthenticated` 를 바로 발행한다(0ms).
   `bootstrap` 응답은 AuthBloc 이 각 BLoC 에 이벤트로 나눠 전달한다.
4. **중복 트리거 제거** — BLoC 의 auth 구독을 없앴다. `weather-index` 는 in-flight guard 와 디바운스를 두고, GPS 결과가 도착했을 때만 호출한다.

## 결과 (프로젝트 내부 측정 문서 기준)

| 지표 | 개선 전 | 개선 후 |
|---|---|---|
| 시작 시 Edge Function 호출 | 10-12회 | 4-5회 |
| `verifyAuth` 실행 | 4-5회 | 1회 |
| 첫 콘텐츠 표시 (커뮤니티) | 300-500ms | ~100ms |
| 날씨 표시 (디스크 캐시 히트) | 1.5-3.5s | ~10ms |
| 인증 블로킹 | 300-500ms | 0ms |

- 교훈: 백엔드 API를 화면 단위로 쪼개면 호출 비용이 클라이언트 구조를 따라 불어난다. 앱 시작처럼 호출이 몰리는 시점에는 **집계 엔드포인트**를 따로 두는 것이 효과적이다.
