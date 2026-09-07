namespace Portfolio.WmsOrchestration.DistributedOrchestration;

// [담당업무 1] Job · Chunk · Step 3계층 메시지 계약.
//
//   Job   : "브랜드 A 의 어제 주문 12,000건을 쇼핑몰 X 와 동기화하라" — 요청 한 건
//   Chunk : Job 을 500건씩 자른 병렬 처리 단위 — 각각 다른 워커에서 돈다
//   Step  : Chunk 안의 한 건 처리 — 실패는 Step 에 격리된다
//
// Saga 는 Job 상태만 본다. Chunk 결과가 모이면 Job 이 끝나고, 하나라도 보상 불가면 Job 이 보상된다.

public record BulkSyncJobSubmitted(Guid JobId, string TenantId, string SyncType, DateOnly TargetDate, int TotalItems);
public record ChunkCreated(Guid JobId, Guid ChunkId, int ChunkIndex, int ChunkCount, IReadOnlyList<string> ItemKeys);
public record ChunkCompleted(Guid JobId, Guid ChunkId, int Succeeded, int Failed, IReadOnlyList<StepFailure> Failures);
public record ChunkFailed(Guid JobId, Guid ChunkId, string Reason);          // 청크 전체가 처리 불가 (인프라 장애 등)
public record CompensateChunk(Guid JobId, Guid ChunkId);                    // Saga → 워커: 이 청크의 반영분을 되돌려라
public record ChunkCompensated(Guid JobId, Guid ChunkId);
public record BulkSyncJobCompleted(Guid JobId, int Succeeded, int Failed);
public record BulkSyncJobCompensated(Guid JobId, string Reason);

public record StepFailure(string ItemKey, string Error, bool Retryable);

/// <summary>처리 대상 항목 하나 — Step 의 입력.</summary>
public record SyncItem(string Key, string TenantId, IReadOnlyDictionary<string, string> Payload);
