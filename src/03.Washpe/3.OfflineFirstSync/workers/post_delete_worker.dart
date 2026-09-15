// [담당업무 3] 삭제 워커 — DB hard-delete 후 R2 정리, NotFound 는 성공으로 간주(재시도 멱등).

import 'dart:convert';
import 'dart:math';

import 'package:drift/drift.dart';
import 'package:injectable/injectable.dart';

import '../../../features/community/data/data_sources/community_remote_data_source.dart';
import '../../../features/community/data/data_sources/r2_image_data_source.dart';
import '../../../features/garage/data/data_sources/chemical_database.dart';
import '../../logging/app_logger.dart';
import '../../utils/result.dart';

/// 게시물 삭제 워커 — Outbox 작업을 실행하여 Supabase hard-delete + R2 사진 정리.
///
/// 실행 순서:
/// 1. 중복 실행 방지 (isProcessing 플래그)
/// 2. Supabase hard-delete (CASCADE로 post_photos 등 자동 정리)
/// 3. R2 오브젝트 삭제 (payload에 저장된 photo keys)
/// 4. 성공: outbox 삭제
/// 5. 실패: Fatal/Transient 분류 → 재시도 또는 중단
@injectable
class PostDeleteWorker {
  PostDeleteWorker(this._db, this._remote, this._r2);

  final ChemicalDatabase _db;
  final CommunityRemoteDataSource _remote;
  final R2ImageDataSource _r2;

  static const _tag = 'PostDeleteWorker';

  /// Outbox 작업을 실행한다.
  Future<void> execute(SyncOutboxDto task) async {
    // 1. 중복 실행 방지
    await (_db.update(_db.syncOutboxTable)
          ..where((t) => t.id.equals(task.id)))
        .write(const SyncOutboxTableCompanion(
      isProcessing: Value(true),
    ));

    try {
      // 2. Supabase hard-delete (CASCADE로 관련 데이터 자동 정리)
      final deleteResult = await _remote.deletePost(task.entityId);
      if (deleteResult is Failure<Unit>) {
        // NotFoundFailure는 이미 삭제된 상태 → 성공으로 간주
        if (deleteResult is NotFoundFailure<Unit>) {
          log.d(
            '게시물이 이미 삭제됨 (not found): ${task.entityId}',
            tag: _tag,
          );
        } else {
          await _markFailed(task, deleteResult as Failure, 'Supabase 삭제 실패');
          return;
        }
      }

      // 3. R2 오브젝트 삭제 (payload에서 photo keys 추출)
      final objectKeys = _parsePayload(task.payload);
      if (objectKeys.isEmpty) {
        await _deleteOutboxEntry(task.id);
        log.d('✔ Post deleted (no photos): ${task.entityId}', tag: _tag);
        return;
      }

      final r2Result = await _r2.deleteObjects(objectKeys);
      if (r2Result is Failure<Unit>) {
        await _markFailed(task, r2Result as Failure, 'R2 삭제 실패');
        return;
      }

      // 4. 성공 정리
      await _deleteOutboxEntry(task.id);
      log.d(
        '✔ Post deleted + ${objectKeys.length} R2 objects: ${task.entityId}',
        tag: _tag,
      );
    } on Exception catch (e) {
      log.e('✘ Unexpected error: $e', tag: _tag);
      await _markFailedWithError(task, e.toString());
    }
  }

  // ── 헬퍼 ──────────────────────────────────────────────────────────────────

  /// payload JSON을 파싱하여 R2 object key 목록을 반환한다.
  List<String> _parsePayload(String? payload) {
    if (payload == null || payload.isEmpty) return [];
    try {
      final decoded = jsonDecode(payload);
      if (decoded is List) return decoded.cast<String>();
      return [];
    } on Exception catch (_) {
      log.w('payload 파싱 실패, R2 정리 건너뜀', tag: _tag);
      return [];
    }
  }

  /// Fatal vs Transient 에러를 분류하여 재시도 정책을 결정한다.
  Future<void> _markFailed(
    SyncOutboxDto task,
    Failure failure,
    String context,
  ) async {
    final isFatal = failure is AuthFailure ||
        failure is DuplicateFailure ||
        failure is StorageFailure;

    if (isFatal) {
      log.w('✘ Fatal failure ($context): ${failure.message}', tag: _tag);
      await (_db.update(_db.syncOutboxTable)
            ..where((t) => t.id.equals(task.id)))
          .write(SyncOutboxTableCompanion(
        isProcessing: const Value(false),
        retryCount: Value(task.maxRetries), // 즉시 중단
        lastError: Value('[수정 필요] $context: ${failure.message}'),
      ));
      return;
    }

    // Transient: 지수 백오프 (5s, 10s, 20s, 40s, 80s)
    await _markFailedWithError(task, '$context: ${failure.message}');
  }

  Future<void> _markFailedWithError(SyncOutboxDto task, String error) async {
    final nextCount = task.retryCount + 1;
    final backoffSeconds = pow(2, nextCount).toInt() * 5;
    final nextRetryAt = DateTime.now().add(Duration(seconds: backoffSeconds));

    log.w(
      '✘ Transient failure (attempt $nextCount/${task.maxRetries}): $error\n'
      '  Next retry at: $nextRetryAt',
      tag: _tag,
    );

    await (_db.update(_db.syncOutboxTable)
          ..where((t) => t.id.equals(task.id)))
        .write(SyncOutboxTableCompanion(
      isProcessing: const Value(false),
      retryCount: Value(nextCount),
      lastError: Value(error),
      nextRetryAt: Value(nextRetryAt),
    ));
  }

  Future<void> _deleteOutboxEntry(String outboxId) async {
    await (_db.delete(_db.syncOutboxTable)
          ..where((t) => t.id.equals(outboxId)))
        .go();
  }
}
