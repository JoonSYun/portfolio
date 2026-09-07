# 인시던트: 실행 이력 조회 데드락

> [담당업무 4] "조회 데드락 … 난제를 원인까지 추적·해결하고 인시던트 문서로 사내 공유"

## 증상
- 관리 콘솔 이력 화면이 간헐적으로 타임아웃. 같은 시각 워커의 이력 UPDATE 가 데드락 희생자로 롤백.
- SQL Server 데드락 그래프: 조회 세션이 `sch.ExecutionHistory` 에 **테이블 스캔** 중 S 락 다수 보유,
  워커의 UPDATE 가 X 락 대기 → 상호 대기.

## 원인
- `DomainCode`, `BrandCode` 컬럼은 `VARCHAR(50)`. Dapper 는 C# `string` 을 기본 `NVARCHAR` 로 보낸다.
- `WHERE DomainCode = @Domain(NVARCHAR)` 은 컬럼 쪽에 **암시적 변환**(`CONVERT_IMPLICIT`)이 걸려
  `IX_ExecutionHistory_Domain_FiredAt` 인덱스 seek 이 불가 → 스캔 → 락 범위 폭증.
- 실행 계획에서 확인: `Index Scan` + 경고 "Type conversion in expression may affect SeekPlan".

## 해결
1. 파라미터 타입을 컬럼과 일치: `new DbString { Value = x, IsAnsi = true, Length = 50 }`
   → 계획이 `Index Seek` 으로 회복. (`History/ExecutionHistoryRepository.cs` 의 `Ansi()`)
2. 조회에 `WITH (READPAST)` — 진행 중(쓰기 중) 행은 건너뛴다. 콘솔이 워커를 기다릴 이유가 없다.
3. 재발 방지: 리포지토리 레이어에서 VARCHAR 컬럼 파라미터는 `Ansi()` 헬퍼만 쓰도록 코드 리뷰 규칙 추가.

## 결과
- 데드락 0건, 이력 조회 p95 1.8s → 40ms.
- 교훈: "파라미터 타입 불일치" 는 결과가 맞아서 눈에 안 띈다. 실행 계획을 봐야 보인다.
