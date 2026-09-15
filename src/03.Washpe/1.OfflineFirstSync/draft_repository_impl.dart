// [담당업무 1] draft + 사진 + outbox 를 하나의 로컬 트랜잭션으로 저장 — 사용자 저장과 전송 예약의 원자성.

import 'dart:io';

import 'package:drift/drift.dart';
import 'package:injectable/injectable.dart';
import 'package:uuid/uuid.dart';

import '../../../../core/utils/path_resolver.dart';
import '../../../../core/utils/result.dart';
import '../../../garage/data/data_sources/chemical_database.dart';
import '../data_sources/r2_image_data_source.dart';
import '../../domain/repositories/i_draft_repository.dart';

/// [IDraftRepository] 구현 — Drift 트랜잭션으로 draft + outbox 원자 저장.
@Singleton(as: IDraftRepository)
class DraftRepositoryImpl implements IDraftRepository {
  DraftRepositoryImpl(this._db, this._r2);

  final ChemicalDatabase _db;
  final R2ImageDataSource _r2;

  static const _uuid = Uuid();

  @override
  Future<Result<Unit>> createDraftWithOutbox({
    required DraftPostData post,
    required List<DraftPhotoData> photos,
  }) async {
    try {
      await _db.transaction(() async {
        final now = DateTime.now();

        // 1. 게시물 초안
        await _db.into(_db.draftPostsTable).insert(
              DraftPostsTableCompanion.insert(
                id: post.id,
                authorId: post.authorId,
                category: post.category,
                title: post.title,
                body: post.body,
                idempotencyKey: post.idempotencyKey,
                createdAt: now,
              ),
            );

        // 2. 사진 메타 (R2 업로드 결과 포함)
        for (final photo in photos) {
          await _db.into(_db.draftPostPhotosTable).insert(
                DraftPostPhotosTableCompanion.insert(
                  id: photo.id,
                  postId: photo.postId,
                  localPath: photo.localPath,
                  sequence: photo.sequence,
                  uploadStatus: Value(photo.uploadStatus),
                  imageKey: Value(photo.imageKey),
                  thumbnailKey: Value(photo.thumbnailKey),
                  width: Value(photo.width),
                  height: Value(photo.height),
                  fileSizeBytes: Value(photo.fileSizeBytes),
                  createdAt: now,
                ),
              );
        }

        // 3. Outbox 등록
        await _db.into(_db.syncOutboxTable).insert(
              SyncOutboxTableCompanion.insert(
                id: _uuid.v4(),
                entityType: 'post',
                entityId: post.id,
                operation: 'create_post',
                idempotencyKey: post.idempotencyKey,
                createdAt: now,
              ),
            );
      });
      return const Success(Unit.instance);
    } on Exception catch (e) {
      return StorageFailure('초안 저장 실패', cause: e);
    }
  }

  @override
  Future<List<DraftPostData>> getSyncedDrafts() async {
    final rows = await (_db.select(_db.draftPostsTable)
          ..where((t) => t.syncStatus.equals('synced')))
        .get();
    return rows.map(_toDraftData).toList();
  }

  @override
  Future<List<DraftPostData>> getPendingDrafts() async {
    final rows = await (_db.select(_db.draftPostsTable)
          ..where((t) =>
              t.syncStatus.equals('pending') |
              t.syncStatus.equals('failed') |
              t.syncStatus.equals('syncing')))
        .get();
    return rows.map(_toDraftData).toList();
  }

  @override
  Future<void> updateSyncStatus(
    String postId,
    String status, {
    String? error,
  }) async {
    await (_db.update(_db.draftPostsTable)
          ..where((t) => t.id.equals(postId)))
        .write(DraftPostsTableCompanion(
      syncStatus: Value(status),
      lastError: Value(error),
      syncAttempts: status == 'syncing'
          ? const Value.absent()
          : const Value.absent(),
    ));
  }

  @override
  Future<List<DraftPostDetail>> getPendingDraftsWithDetail() async {
    try {
      final photoCount = _db.draftPostPhotosTable.id.count();

      final query = _db.select(_db.draftPostsTable).join([
        leftOuterJoin(
          _db.draftPostPhotosTable,
          _db.draftPostPhotosTable.postId
              .equalsExp(_db.draftPostsTable.id),
        ),
      ]);

      query
        ..addColumns([photoCount])
        ..where(_db.draftPostsTable.syncStatus.equals('pending') |
            _db.draftPostsTable.syncStatus.equals('failed') |
            _db.draftPostsTable.syncStatus.equals('syncing'))
        ..groupBy([_db.draftPostsTable.id])
        ..orderBy([
          OrderingTerm.desc(_db.draftPostsTable.createdAt),
        ]);

      final rows = await query.get();

      return rows.map((row) {
        final draft = row.readTable(_db.draftPostsTable);
        final count = row.read(photoCount) ?? 0;
        return DraftPostDetail(
          id: draft.id,
          title: draft.title,
          category: draft.category,
          syncStatus: draft.syncStatus,
          lastError: draft.lastError,
          createdAt: draft.createdAt,
          photoCount: count,
        );
      }).toList();
    } on Exception catch (_) {
      return [];
    }
  }

  @override
  Future<Result<Unit>> deleteDraft(String draftId) async {
    try {
      // 로컬 사진 파일 삭제 (파일 먼저, DB 나중 → orphan 방지)
      final photos = await (_db.select(_db.draftPostPhotosTable)
            ..where((t) => t.postId.equals(draftId)))
          .get();
      for (final photo in photos) {
        try {
          final file = File(PathResolver.toAbsolute(photo.localPath));
          if (await file.exists()) await file.delete();
        } on Exception catch (_) {
          // best-effort: 파일 삭제 실패해도 DB 정리 계속 진행
        }
      }

      await _db.transaction(() async {
        await (_db.delete(_db.draftPostPhotosTable)
              ..where((t) => t.postId.equals(draftId)))
            .go();
        await (_db.delete(_db.syncOutboxTable)
              ..where((t) => t.entityId.equals(draftId)))
            .go();
        await (_db.delete(_db.draftPostsTable)
              ..where((t) => t.id.equals(draftId)))
            .go();
      });
      return const Success(Unit.instance);
    } on Exception catch (e) {
      return StorageFailure('초안 삭제 실패', cause: e);
    }
  }

  @override
  Future<Result<Unit>> resetDraftForRetry(String draftId) async {
    try {
      await _db.transaction(() async {
        await (_db.update(_db.draftPostsTable)
              ..where((t) => t.id.equals(draftId)))
            .write(const DraftPostsTableCompanion(
          syncStatus: Value('pending'),
          lastError: Value(null),
        ));
        await (_db.update(_db.syncOutboxTable)
              ..where((t) => t.entityId.equals(draftId)))
            .write(const SyncOutboxTableCompanion(
          retryCount: Value(0),
          isProcessing: Value(false),
          nextRetryAt: Value(null),
          lastError: Value(null),
        ));
      });
      return const Success(Unit.instance);
    } on Exception catch (e) {
      return StorageFailure('재시도 초기화 실패', cause: e);
    }
  }

  @override
  Future<List<DraftPhotoData>> getDraftPhotos(String draftId) async {
    final rows = await (_db.select(_db.draftPostPhotosTable)
          ..where((t) => t.postId.equals(draftId)))
        .get();
    return rows.map(_toPhotoData).toList();
  }

  @override
  Future<Result<Unit>> deleteDraftWithCleanup(String draftId) async {
    // 1. R2 key 수집 (DB 삭제 전에 조회)
    final photos = await getDraftPhotos(draftId);
    final r2Keys = <String>[];
    for (final photo in photos) {
      if (photo.imageKey != null) r2Keys.add(photo.imageKey!);
      if (photo.thumbnailKey != null) r2Keys.add(photo.thumbnailKey!);
    }

    // 2. DB 삭제 우선 (사용자 UX: 목록에서 즉시 사라짐)
    final deleteResult = await deleteDraft(draftId);
    if (deleteResult is Failure<Unit>) return deleteResult;

    // 3. R2 삭제 (best-effort, 실패해도 무시 — orphan은 추후 정리 가능)
    if (r2Keys.isNotEmpty) {
      await _r2.deleteObjects(r2Keys);
    }

    return const Success(Unit.instance);
  }

  DraftPostData _toDraftData(DraftPostDto row) {
    return DraftPostData(
      id: row.id,
      authorId: row.authorId,
      category: row.category,
      title: row.title,
      body: row.body,
      idempotencyKey: row.idempotencyKey,
    );
  }

  DraftPhotoData _toPhotoData(DraftPostPhotoDto row) {
    return DraftPhotoData(
      id: row.id,
      postId: row.postId,
      localPath: row.localPath,
      sequence: row.sequence,
      uploadStatus: row.uploadStatus,
      imageKey: row.imageKey,
      thumbnailKey: row.thumbnailKey,
      width: row.width,
      height: row.height,
      fileSizeBytes: row.fileSizeBytes,
    );
  }
}
