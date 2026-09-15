// [담당업무 1] Outbox 폴링 엔진 — 시작·네트워크 복귀·포그라운드 3개 트리거, 세마포어 동시성 3, 세션 선갱신, orphan 복구.

import 'dart:async';
import 'dart:io';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:drift/drift.dart';
import 'package:flutter/widgets.dart';
import 'package:injectable/injectable.dart';
import 'package:supabase_flutter/supabase_flutter.dart';
import 'package:uuid/uuid.dart';

import '../../features/garage/data/data_sources/chemical_database.dart';
import '../logging/app_logger.dart';
import '../utils/path_resolver.dart';
import '../utils/semaphore.dart';
import 'workers/post_delete_worker.dart';
import 'workers/post_sync_worker.dart';

/// Sync 상태 스냅샷 — UI에 전파하기 위한 값 객체.
class SyncSnapshot {
  const SyncSnapshot({
    required this.pendingCount,
    required this.failedCount,
    required this.isSyncing,
  });

  static const empty = SyncSnapshot(
    pendingCount: 0,
    failedCount: 0,
    isSyncing: false,
  );

  final int pendingCount;
  final int failedCount;
  final bool isSyncing;

  bool get hasIssues => pendingCount > 0 || failedCount > 0;
}

/// Outbox 폴링 + 작업 디스패치 엔진.
///
/// 세 가지 트리거로 pending/failed 작업을 처리한다:
/// 1. **앱 시작** — `initialize()` 호출 시 즉시 실행
/// 2. **네트워크 복귀** — `connectivity_plus` 리스너
/// 3. **앱 포그라운드 복귀** — `WidgetsBindingObserver`
///
/// workmanager는 iOS에서 최소 15분 간격이므로,
/// 앱이 포그라운드일 때는 이 엔진이 즉시 동기화를 담당한다.
@singleton
class SyncEngine with WidgetsBindingObserver {
  SyncEngine(
    this._db,
    this._postWorker,
    this._postDeleteWorker,
    this._supabaseClient,
  );

  final ChemicalDatabase _db;
  final PostSyncWorker _postWorker;
  final PostDeleteWorker _postDeleteWorker;
  final SupabaseClient _supabaseClient;

  final _uploadSemaphore = Semaphore(3);
  StreamSubscription<List<ConnectivityResult>>? _connectivitySub;
  bool _isProcessing = false;

  final _statusController = StreamController<SyncSnapshot>.broadcast();

  /// Sync 상태 변경 스트림 — UI에서 구독하여 배너 표시.
  Stream<SyncSnapshot> get status => _statusController.stream;

  static const _tag = 'SyncEngine';

  /// 앱 시작 시 호출 — orphan 복구 + connectivity + lifecycle 리스너 등록.
  Future<void> initialize() async {
    // 0. Orphan 복구 (앱 시작 1회) — outbox 없이 'syncing' 고착된 draft 재큐잉
    await _recoverOrphanedDrafts();

    // 1. 네트워크 복귀 시 즉시 동기화
    _connectivitySub = Connectivity().onConnectivityChanged.listen((results) {
      if (results.any((r) => r != ConnectivityResult.none)) {
        log.d('Network restored → processing pending tasks', tag: _tag);
        processPendingTasks();
      }
    });

    // 2. 앱 포그라운드 복귀 시 즉시 동기화
    WidgetsBinding.instance.addObserver(this);

    // 3. 시작 시 한 번 실행 (orphan이 pending으로 복원된 후)
    processPendingTasks();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) {
      log.d('App resumed → processing pending tasks', tag: _tag);
      processPendingTasks();
    }
  }

  /// 대기 중인 Outbox 작업을 순차 처리한다.
  ///
  /// 중복 실행 방지: [_isProcessing] 플래그로 보호.
  Future<void> processPendingTasks() async {
    if (_isProcessing) return;
    _isProcessing = true;

    try {
      final now = DateTime.now();
      final tasks = await (_db.select(_db.syncOutboxTable)
            ..where((t) => t.isProcessing.equals(false))
            ..where((t) => t.retryCount.isSmallerThan(t.maxRetries))
            ..where((t) =>
                t.nextRetryAt.isNull() |
                t.nextRetryAt.isSmallerOrEqualValue(now))
            ..orderBy([(t) => OrderingTerm.asc(t.createdAt)]))
          .get();

      if (tasks.isEmpty) return;

      log.d('Found ${tasks.length} pending task(s)', tag: _tag);

      // 세션 유효성 확인 후 동기화 진행
      if (!await _ensureValidSession()) return;

      await Future.wait(
        tasks.map((task) => _uploadSemaphore.run(() async {
              try {
                switch (task.operation) {
                  case 'create_post':
                    await _postWorker.execute(task);
                  case 'delete_post':
                    await _postDeleteWorker.execute(task);
                  default:
                    log.w('Unknown operation: ${task.operation}', tag: _tag);
                }
              } on Exception catch (e) {
                log.e('Task ${task.id} failed: $e', tag: _tag);
              }
              await _emitSnapshot();
            })),
      );
    } on Exception catch (e) {
      log.e('SyncEngine error: $e', tag: _tag);
    } finally {
      _isProcessing = false;
      await _emitSnapshot();
      await _cleanupSyncedDrafts();
    }
  }

  /// 현재 Outbox 상태를 DB에서 조회하여 스냅샷으로 반환한다.
  Future<SyncSnapshot> getSnapshot() async {
    try {
      final pending = await (_db.select(_db.draftPostsTable)
            ..where((t) =>
                t.syncStatus.equals('pending') |
                t.syncStatus.equals('syncing')))
          .get();
      final failed = await (_db.select(_db.draftPostsTable)
            ..where((t) => t.syncStatus.equals('failed')))
          .get();
      return SyncSnapshot(
        pendingCount: pending.length,
        failedCount: failed.length,
        isSyncing: _isProcessing,
      );
    } on Exception catch (_) {
      return SyncSnapshot.empty;
    }
  }

  /// 세션 유효성 확인 및 필요 시 갱신.
  ///
  /// 만료 임박(60초 이내)도 갱신 대상으로 처리하여
  /// 체크~호출 사이의 Race Condition을 방지한다.
  Future<bool> _ensureValidSession() async {
    final session = _supabaseClient.auth.currentSession;
    if (session == null) {
      log.w('No active session — skipping sync', tag: _tag);
      return false;
    }

    // 디버그: 현재 세션 상태 출력
    final expiresAt =
        DateTime.fromMillisecondsSinceEpoch(session.expiresAt! * 1000);
    final buffer = expiresAt.difference(DateTime.now()).inSeconds;
    final token = session.accessToken;
    log.d(
      'Session check — user: ${session.user.id}, '
      'expires in ${buffer}s, '
      'token prefix: ${token.substring(0, token.length.clamp(0, 20))}..., '
      'token length: ${token.length}',
      tag: _tag,
    );

    // 만료되었거나 60초 이내 만료 예정이면 갱신
    if (session.isExpired || buffer < 60) {
      log.d(
        'Session expired or expiring soon (${buffer}s) — refreshing',
        tag: _tag,
      );
      try {
        final response = await _supabaseClient.auth.refreshSession();
        if (response.session == null) {
          log.w('Session refresh failed — skipping sync', tag: _tag);
          return false;
        }
        log.d('Session refreshed successfully', tag: _tag);
      } on AuthException catch (e) {
        log.e('Session refresh auth error: ${e.message}', tag: _tag);
        return false;
      } on Exception catch (e) {
        log.e('Session refresh exception: $e', tag: _tag);
        return false;
      }
    }

    return true;
  }

  Future<void> _emitSnapshot() async {
    if (_statusController.isClosed) return;
    final snapshot = await getSnapshot();
    _statusController.add(snapshot);
  }

  static const _uuid = Uuid();

  /// outbox 없이 'syncing' 고착된 draft를 감지하여 재큐잉한다.
  ///
  /// 원인: 이전 sync에서 outbox 삭제 후 draft 상태 갱신 전 앱 중단.
  /// idempotency key 재사용으로 서버 중복 방지.
  Future<void> _recoverOrphanedDrafts() async {
    try {
      final syncingDrafts = await (_db.select(_db.draftPostsTable)
            ..where((t) => t.syncStatus.equals('syncing')))
          .get();

      for (final draft in syncingDrafts) {
        final outbox = await (_db.select(_db.syncOutboxTable)
              ..where((t) => t.entityId.equals(draft.id)))
            .getSingleOrNull();

        if (outbox == null) {
          log.w('Orphaned syncing draft → re-enqueue: ${draft.id}', tag: _tag);
          await _db.transaction(() async {
            await _db.into(_db.syncOutboxTable).insert(
                  SyncOutboxTableCompanion.insert(
                    id: _uuid.v4(),
                    entityType: 'post',
                    entityId: draft.id,
                    operation: 'create_post',
                    idempotencyKey: draft.idempotencyKey,
                    createdAt: DateTime.now(),
                  ),
                );
            await (_db.update(_db.draftPostsTable)
                  ..where((t) => t.id.equals(draft.id)))
                .write(const DraftPostsTableCompanion(
              syncStatus: Value('pending'),
              lastError: Value(null),
            ));
          });
        }
      }
    } on Exception catch (e) {
      log.w('Orphan recovery failed (non-fatal): $e', tag: _tag);
    }
  }

  /// synced 상태이고 24시간 이상 지난 draft를 배치 삭제한다.
  Future<void> _cleanupSyncedDrafts() async {
    try {
      final cutoff = DateTime.now().subtract(const Duration(hours: 24));
      final syncedDrafts = await (_db.select(_db.draftPostsTable)
            ..where((t) => t.syncStatus.equals('synced'))
            ..where((t) => t.createdAt.isSmallerOrEqualValue(cutoff)))
          .get();

      for (final draft in syncedDrafts) {
        // 로컬 사진 파일 삭제 (파일 먼저, DB 나중 → orphan 방지)
        final photos = await (_db.select(_db.draftPostPhotosTable)
              ..where((t) => t.postId.equals(draft.id)))
            .get();
        for (final photo in photos) {
          try {
            final file = File(PathResolver.toAbsolute(photo.localPath));
            if (await file.exists()) await file.delete();
          } on Exception catch (_) {
            // best-effort: 파일 삭제 실패해도 DB 정리 계속 진행
          }
        }

        await (_db.delete(_db.draftPostPhotosTable)
              ..where((t) => t.postId.equals(draft.id)))
            .go();
        await (_db.delete(_db.draftPostsTable)
              ..where((t) => t.id.equals(draft.id)))
            .go();
      }

      if (syncedDrafts.isNotEmpty) {
        log.d(
          'Cleaned up ${syncedDrafts.length} synced draft(s)',
          tag: _tag,
        );
      }
    } on Exception catch (e) {
      log.w('Synced draft cleanup failed (non-fatal): $e', tag: _tag);
    }
  }

  /// 리소스 정리.
  void dispose() {
    _connectivitySub?.cancel();
    _statusController.close();
    WidgetsBinding.instance.removeObserver(this);
  }
}
