// [담당업무 3] 게시물 전송 워커 — 압축 → 배치 presign → 병렬 업로드 → 멱등 INSERT, Fatal/Transient 분류와 지수 백오프.

import 'dart:async';
import 'dart:io';
import 'dart:math';
import 'dart:ui' as ui;

import 'package:drift/drift.dart';
import 'package:flutter/painting.dart';
import 'package:flutter_image_compress/flutter_image_compress.dart';
import 'package:injectable/injectable.dart';
import 'package:uuid/uuid.dart';

import '../../../features/community/data/data_sources/community_remote_data_source.dart';
import '../../../features/community/data/data_sources/r2_image_data_source.dart';
import '../../../features/garage/data/data_sources/chemical_database.dart';
import '../../logging/app_logger.dart';
import '../../media/image_config.dart';
import '../../utils/path_resolver.dart';
import '../../utils/result.dart';

/// 게시물 전송 워커 — Outbox 작업을 실행하여 서버에 동기화한다.
///
/// 실행 순서:
/// 1. 중복 실행 방지 (isProcessing 플래그)
/// 2. 사진 3단계 파이프라인: 압축 → 배치 presign → 병렬 업로드
/// 3. 게시물 서버 INSERT (Idempotency Key로 중복 방지)
/// 4. 사진 메타데이터 서버 저장
/// 5. 성공: outbox 삭제 + draft synced 마킹
/// 6. 실패: Fatal/Transient 분류 → 재시도 또는 중단
@injectable
class PostSyncWorker {
  PostSyncWorker(this._db, this._remote, this._r2);

  final ChemicalDatabase _db;
  final CommunityRemoteDataSource _remote;
  final R2ImageDataSource _r2;

  static const _tag = 'PostSyncWorker';
  static const _uuid = Uuid();

  /// Outbox 작업을 실행한다.
  Future<void> execute(SyncOutboxDto task) async {
    // 1. 중복 실행 방지
    await (_db.update(_db.syncOutboxTable)
          ..where((t) => t.id.equals(task.id)))
        .write(const SyncOutboxTableCompanion(
      isProcessing: Value(true),
    ));
    await _updateDraftStatus(task.entityId, 'syncing');

    try {
      final draft = await _getDraft(task.entityId);
      if (draft == null) {
        log.w('Draft not found: ${task.entityId}', tag: _tag);
        await _deleteOutboxEntry(task.id);
        return;
      }

      // 2. 사진 3단계 파이프라인 (pending 사진만)
      final photos = await _getDraftPhotos(task.entityId);
      final pendingPhotos =
          photos.where((p) => p.uploadStatus != 'uploaded').toList();

      if (pendingPhotos.isNotEmpty) {
        // Phase 1: 전체 사진 압축 (병렬)
        final compressResults = await Future.wait(
          pendingPhotos.map((photo) => _compressPhoto(photo)),
        );

        // Fatal(파일 없음) 사진 필터링 — null은 이미 'failed' 마킹됨
        final compressed =
            compressResults.whereType<_CompressedPhoto>().toList();

        if (compressed.isNotEmpty) {
          // Phase 2: 배치 presign (1회 호출)
          final presignRequests = <PresignRequest>[];
          for (final c in compressed) {
            presignRequests.add(PresignRequest(
              postId: task.entityId,
              fileName: '${c.photoId}.jpg',
              contentType: 'image/jpeg',
            ));
            presignRequests.add(PresignRequest(
              postId: task.entityId,
              fileName: 'thumb_${c.photoId}.jpg',
              contentType: 'image/jpeg',
            ));
          }

          final presignResult = await _r2.getPresignedUrls(presignRequests);
          if (presignResult is Failure<List<PresignedUploadInfo>>) {
            throw Exception(
                'Presign 실패: ${(presignResult as Failure).message}');
          }
          final presignInfos =
              (presignResult as Success<List<PresignedUploadInfo>>).value;

          // Phase 3: R2 업로드 (병렬) + DB 업데이트
          final uploadFutures = <Future<void>>[];
          for (var i = 0; i < compressed.length; i++) {
            final displayInfo = presignInfos[i * 2];
            final thumbInfo = presignInfos[i * 2 + 1];
            final c = compressed[i];

            uploadFutures.add(_uploadToR2(displayInfo, c.displayBytes));
            uploadFutures.add(_uploadToR2(thumbInfo, c.thumbBytes));
          }
          await Future.wait(uploadFutures);

          // 성공: 개별 사진 상태 업데이트
          for (var i = 0; i < compressed.length; i++) {
            final c = compressed[i];
            await (_db.update(_db.draftPostPhotosTable)
                  ..where((t) => t.id.equals(c.original.id)))
                .write(DraftPostPhotosTableCompanion(
              uploadStatus: const Value('uploaded'),
              imageKey: Value(presignInfos[i * 2].objectKey),
              thumbnailKey: Value(presignInfos[i * 2 + 1].objectKey),
              width: Value(c.size?.width.toInt()),
              height: Value(c.size?.height.toInt()),
              fileSizeBytes: Value(c.displayBytes.length),
            ));
          }
        }

        // 업로드 후 재조회 — 파일 없음(Fatal) 사진은 제외하고 진행
        final refreshed = await _getDraftPhotos(task.entityId);
        final stillPending = refreshed
            .where(
                (p) => p.uploadStatus != 'uploaded' && p.uploadStatus != 'failed')
            .toList();
        if (stillPending.isNotEmpty) {
          await _markFailedWithError(
              task, '사진 ${stillPending.length}장 업로드 미완료');
          return;
        }
      }

      // 3. 게시물 서버 INSERT (Idempotency Key로 중복 방지)
      final postData = <String, dynamic>{
        'id': draft.id,
        'author_id': draft.authorId,
        'category': draft.category,
        'title': draft.title,
        'body': draft.body,
        'status': 'published',
      };

      final createResult = await _remote.createPostIdempotent(
        data: postData,
        idempotencyKey: task.idempotencyKey,
      );

      if (createResult is Failure) {
        await _markFailed(task, createResult as Failure, 'POST 전송 실패');
        return;
      }

      // 4. 사진 메타데이터 서버 저장 (uploaded 상태만)
      final allPhotos = await _getDraftPhotos(task.entityId);
      final uploadedPhotos =
          allPhotos.where((p) => p.uploadStatus == 'uploaded').toList();
      if (uploadedPhotos.isNotEmpty) {
        final photosData = uploadedPhotos.map((p) {
          return <String, dynamic>{
            'id': p.id,
            'post_id': p.postId,
            'image_key': p.imageKey,
            'thumbnail_key': p.thumbnailKey,
            'width': p.width ?? 0,
            'height': p.height ?? 0,
            'file_size_bytes': p.fileSizeBytes ?? 0,
            'sequence': p.sequence,
          };
        }).toList();

        final photoResult = await _remote.createPostPhotos(photosData);
        if (photoResult is Failure && photoResult is! DuplicateFailure) {
          await _markFailed(task, photoResult as Failure, '사진 메타 저장 실패');
          return;
        }
        // DuplicateFailure → orphan 재시도 시 이미 존재하는 사진, 성공으로 진행
      }

      // 5. 성공 정리 (원자적 — 부분 실패로 인한 orphan 방지)
      await _db.transaction(() async {
        await _deleteOutboxEntry(task.id);
        await _updateDraftStatus(task.entityId, 'synced');
      });
      log.d('✔ Post synced: ${task.entityId}', tag: _tag);
    } on Exception catch (e) {
      log.e('✘ Unexpected error: $e', tag: _tag);
      await _markFailedWithError(task, e.toString());
    }
  }

  // ── Phase 1: 사진 압축 ────────────────────────────────────────────────────

  /// 개별 사진을 압축한다.
  ///
  /// - **Fatal** (파일 없음): 개별 `failed` 마킹, null 반환.
  /// - **Transient** (네트워크): Exception throw → Worker 레벨 지수 백오프.
  Future<_CompressedPhoto?> _compressPhoto(DraftPostPhotoDto photo) async {
    final absPath = PathResolver.toAbsolute(photo.localPath);
    final file = File(absPath);

    // Fatal: 파일이 없으면 복구 불가 → 개별 failed 마킹
    if (!await file.exists()) {
      log.w('✘ Photo file not found: $absPath', tag: _tag);
      await _updatePhotoStatus(photo.id, 'failed');
      return null;
    }

    final bytes = await file.readAsBytes();
    final size = await _decodeImageSize(bytes);

    final displayBytes = await _compressImage(
      bytes,
      maxSize: ImageConfig.displayMaxSize,
      quality: ImageConfig.displayQuality,
    );
    final thumbBytes = await _compressImage(
      bytes,
      maxSize: ImageConfig.thumbMaxSize,
      quality: ImageConfig.thumbQuality,
    );

    log.d(
      'display: ${displayBytes.length} bytes, '
      'thumb: ${thumbBytes.length} bytes',
      tag: _tag,
    );

    return _CompressedPhoto(
      original: photo,
      photoId: _uuid.v4(),
      displayBytes: displayBytes,
      thumbBytes: thumbBytes,
      size: size,
    );
  }

  // ── Phase 3: R2 업로드 ────────────────────────────────────────────────────

  Future<void> _uploadToR2(
    PresignedUploadInfo info,
    Uint8List bytes,
  ) async {
    final uploadResult = await _r2.uploadBytes(
      presignedUrl: info.uploadUrl,
      bytes: bytes,
      contentType: 'image/jpeg',
    );

    if (uploadResult is Failure<Unit>) {
      throw Exception('R2 업로드 실패: ${(uploadResult as Failure).message}');
    }
  }

  // ── 유틸 ──────────────────────────────────────────────────────────────────

  /// 이미지를 [maxSize]px 이내로 리사이즈 + JPEG 압축.
  Future<Uint8List> _compressImage(
    Uint8List bytes, {
    required int maxSize,
    required int quality,
  }) async {
    final result = await FlutterImageCompress.compressWithList(
      bytes,
      minWidth: maxSize,
      minHeight: maxSize,
      quality: quality,
      format: CompressFormat.jpeg,
    );
    return result;
  }

  /// 바이트 배열에서 이미지 크기를 추출한다.
  Future<ui.Size?> _decodeImageSize(Uint8List bytes) async {
    final completer = Completer<ui.Size?>();
    final provider = MemoryImage(bytes);
    final stream = provider.resolve(ImageConfiguration.empty);
    late ImageStreamListener listener;
    listener = ImageStreamListener(
      (info, _) {
        completer.complete(ui.Size(
          info.image.width.toDouble(),
          info.image.height.toDouble(),
        ));
        stream.removeListener(listener);
      },
      onError: (error, _) {
        log.w('이미지 크기 추출 실패: $error', tag: _tag);
        completer.complete(null);
        stream.removeListener(listener);
      },
    );
    stream.addListener(listener);
    return completer.future;
  }

  // ── DB 헬퍼 ─────────────────────────────────────────────────────────────

  /// Fatal vs Transient 에러를 분류하여 재시도 정책을 결정한다.
  ///
  /// - **Fatal** (400, 403, Validation): 재시도 불가 → maxRetries로 밀어서 즉시 중단.
  /// - **Transient** (5xx, 408, Socket, Timeout): 지수 백오프 재시도.
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
      await _updateDraftStatus(task.entityId, 'failed',
          error: '[수정 필요] ${failure.message}');
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
    await _updateDraftStatus(task.entityId, 'failed', error: error);
  }

  Future<DraftPostDto?> _getDraft(String postId) async {
    return (_db.select(_db.draftPostsTable)
          ..where((t) => t.id.equals(postId)))
        .getSingleOrNull();
  }

  Future<List<DraftPostPhotoDto>> _getDraftPhotos(String postId) async {
    return (_db.select(_db.draftPostPhotosTable)
          ..where((t) => t.postId.equals(postId)))
        .get();
  }

  Future<void> _updateDraftStatus(
    String postId,
    String status, {
    String? error,
  }) async {
    await (_db.update(_db.draftPostsTable)
          ..where((t) => t.id.equals(postId)))
        .write(DraftPostsTableCompanion(
      syncStatus: Value(status),
      lastError: Value(error),
    ));
  }

  Future<void> _updatePhotoStatus(String photoId, String status) async {
    await (_db.update(_db.draftPostPhotosTable)
          ..where((t) => t.id.equals(photoId)))
        .write(DraftPostPhotosTableCompanion(
      uploadStatus: Value(status),
    ));
  }

  Future<void> _deleteOutboxEntry(String outboxId) async {
    await (_db.delete(_db.syncOutboxTable)
          ..where((t) => t.id.equals(outboxId)))
        .go();
  }
}

/// 압축 완료된 사진 데이터.
class _CompressedPhoto {
  const _CompressedPhoto({
    required this.original,
    required this.photoId,
    required this.displayBytes,
    required this.thumbBytes,
    required this.size,
  });

  final DraftPostPhotoDto original;
  final String photoId;
  final Uint8List displayBytes;
  final Uint8List thumbBytes;
  final ui.Size? size;
}
