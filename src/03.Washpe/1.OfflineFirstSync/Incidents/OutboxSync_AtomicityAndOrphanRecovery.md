# 인시던트: 전송 완료 후에도 "전송 대기" 배너가 사라지지 않음

> [담당업무 1] "Outbox 성공 경로의 비원자성과 orphan 상태를 원인까지 추적해 복구 경로를 설계"

## 증상
- 게시물 사진은 R2에 올라갔고 서버 INSERT도 성공했는데 "전송 대기 게시물" 배너가 계속 떠 있다.
- 앱을 재시작해도 그대로다. 사용자가 할 수 있는 조치가 없다.

## 원인
1. **성공 경로가 원자적이지 않았다.** outbox 삭제와 draft `synced` 마킹이 서로 다른 쓰기로 실행됐다.
   그 사이에 앱이 종료되면 outbox 는 없는데 draft 는 `syncing` 에 머문다.
   `getSnapshot()` 은 `syncing` 을 pending 으로 세기 때문에 배너가 계속 뜬다.
2. **이 상태를 벗어날 경로가 없었다.** 재시도는 outbox 행을 기준으로 돈다. outbox 가 사라진 draft 는 다시 처리되지 않는다.
3. **복구 경로를 만들자 다른 문제가 드러났다.** orphan 을 재전송하면 사진 메타데이터가 서버에 이미 있어 23505(`DuplicateFailure`)가 난다.
   `DuplicateFailure` 는 Fatal 로 분류돼 있어서 재시도가 곧바로 중단됐다.

## 해결
1. 성공 정리를 하나의 트랜잭션으로 묶었다. → [`workers/post_sync_worker.dart`](../workers/post_sync_worker.dart) 5단계
   ```dart
   await _db.transaction(() async {
     await _deleteOutboxEntry(task.id);
     await _updateDraftStatus(task.entityId, 'synced');
   });
   ```
2. 시작 시 orphan 을 복구한다. → [`sync_engine.dart`](../sync_engine.dart) `_recoverOrphanedDrafts()`
   `syncing` 이면서 outbox 가 없는 draft 에 outbox 를 다시 만들고 **기존 idempotency key 를 재사용**한다.
   서버 쪽 [`create_post_idempotent`](../Sql/create_post_idempotent.sql) 가 같은 키에 최초 응답을 돌려주므로 중복 게시가 생기지 않는다.
   이 복구는 `processPendingTasks()` 보다 **먼저 await** 해야 한다. 그렇지 않으면 복구가 끝나기 전에 폴링이 돌아 orphan 을 놓친다.
3. 사진 메타 저장에서 `DuplicateFailure` 는 "이미 반영됨"으로 보고 성공 처리한다. 삭제 워커의 `NotFoundFailure` 도 같은 이유로 성공 처리한다.
4. 그 밖의 수정:
   - PostListBloc 이 SyncEngine 상태 스트림을 구독한다. 전송이 끝나면 optimistic 카드를 서버 데이터로 바꾼다.
   - injectable 은 optional 파라미터를 의존성 그래프에서 추적하지 않는다. 그래서 등록 순서가 어긋나 GetIt 런타임 에러가 났다. 해당 BLoC 은 모듈에서 수동 등록했다.

## 결과
- 배너 고착이 해소됐다. 이미 고착돼 있던 draft 도 다음 앱 시작 때 자동으로 재전송된다.
- 교훈: Outbox 는 "로컬 저장과 전송 예약"만 원자적으로 만들어서는 부족하다. **완료 정리까지 원자적**이어야 한다.
  재전송이 안전하려면 클라이언트 키 재사용과 서버 멱등 처리가 짝을 이뤄야 한다.
