// [담당업무 3] Transactional Outbox 테이블 (Drift) — idempotencyKey·retryCount·nextRetryAt.

import 'package:drift/drift.dart';

/// 전송 대기열 (Transactional Outbox) Drift 테이블.
///
/// draft 저장과 동일 트랜잭션에서 등록되어 원자성을 보장한다.
/// [SyncEngine]이 폴링하여 pending/failed 작업을 처리한다.
@DataClassName('SyncOutboxDto')
class SyncOutboxTable extends Table {
  @override
  String get tableName => 'sync_outbox';

  /// UUID.
  TextColumn get id => text()();

  /// 대상 엔티티 타입: 'post', 'comment'
  TextColumn get entityType => text()();

  /// 대상 엔티티 ID (draft ID).
  TextColumn get entityId => text()();

  /// 수행할 작업: 'create_post', 'delete_post'
  TextColumn get operation => text()();

  /// 서버 중복 방지 키 (폼 열릴 때 생성된 UUID).
  TextColumn get idempotencyKey => text()();

  /// 현재까지 재시도 횟수.
  IntColumn get retryCount =>
      integer().withDefault(const Constant(0))();

  /// 최대 재시도 횟수.
  IntColumn get maxRetries =>
      integer().withDefault(const Constant(5))();

  /// 중복 실행 방지 플래그.
  BoolColumn get isProcessing =>
      boolean().withDefault(const Constant(false))();

  /// 마지막 실패 에러 메시지.
  TextColumn get lastError => text().nullable()();

  DateTimeColumn get createdAt => dateTime()();

  /// 작업별 추가 데이터 (JSON). 예: delete_post의 R2 object keys.
  TextColumn get payload => text().nullable()();

  /// 지수 백오프 스케줄 (다음 재시도 시각).
  DateTimeColumn get nextRetryAt => dateTime().nullable()();

  @override
  Set<Column> get primaryKey => {id};
}
