// ==========================================
// 1. OrchestrationConstants.cs
// ==========================================
namespace Portfolio.WmsOrchestration.Model
{
    /// <summary>
    /// Centralized constants for orchestration system.
    /// Prevents magic strings/numbers and ensures consistency across services.
    /// </summary>
    public static class OrchestrationConstants
    {
        /// <summary>
        /// Default chunk size when no configuration is specified.
        /// </summary>
        public const int DEFAULT_CHUNK_SIZE = 300;

        /// <summary>
        /// Default priority for asynchronous batch processing.
        /// </summary>
        public const int DEFAULT_ASYNC_PRIORITY = 20;

        /// <summary>
        /// Default priority for synchronous (bypass) processing.
        /// Higher priority ensures faster queue processing.
        /// </summary>
        public const int DEFAULT_SYNC_PRIORITY = 90;

        public const int DEFAULT_NTIS_PRIORITY = 10;

        /// <summary>
        /// Default user identifier when no authenticated user is available.
        /// </summary>
        public const string SYSTEM_USER_ID = "System";

        /// <summary>
        /// Delimiter used in composite key generation.
        /// </summary>
        public const char COMPOSITE_KEY_SEPARATOR = '|';

        /// <summary>
        /// Placeholder for null values in chunk identifiers.
        /// </summary>
        public const string NULL_IDENTIFIER = "null";

        /// <summary>
        /// HTTP header name for idempotency key.
        /// </summary>
        public const string IDEMPOTENCY_KEY_HEADER = "Idempotency-Key";

        /// <summary>
        /// Batch type identifiers.
        /// </summary>
        public static class BatchTypes
        {
            public const string CHUNK_COUNT = "ChunkCount";
            public const string IDENTIFIER = "IdentifierType";
            public const string BYPASS = "Bypass";
        }

        /// <summary>
        /// Error messages.
        /// </summary>
        public static class ErrorMessages
        {
            public const string EMPTY_PAYLOAD = "Payload is empty.";
            public const string ORCHESTRATION_FAILED = "Failed to create orchestration job.";
            public const string STANDARD_API_MASTER_FAILED = "Failed to create Standard API Master.";
            public const string INVALID_JOB_ID = "Invalid JobId provided.";
            public const string INVALID_BUSINESS_IDENTIFIER = "Invalid Business Identifier provided.";
            public const string PROPERTY_NOT_FOUND = "Property '{0}' not found on type '{1}'.";
        }

        /// <summary>
        /// Success messages.
        /// </summary>
        public static class SuccessMessages
        {
            public const string JOB_CREATED_CHUNK_COUNT = "Job created (Chunk Count mode)";
            public const string JOB_CREATED_IDENTIFIER = "Job created (Identifier mode)";
            public const string JOB_CREATED_BYPASS = "Job created (Bypass mode)";
        }
    }

    /// <summary>
    /// Orchestration 실행 옵션
    /// </summary>
    public class OrchestrationOptions
    {
        public string JobIdentifier { get; set; } = string.Empty;
        public bool IsSynchronous { get; set; } = false;
        public int Priority { get; set; } = OrchestrationConstants.DEFAULT_ASYNC_PRIORITY;
        public string BrandCode { get; set; } = string.Empty;
        public string SlipDiv { get; set; } = string.Empty;
        public string Delimiter { get; set; } = string.Empty;
        public string JobIdentifierType { get; set; } = string.Empty;

        /// <summary>
        /// Job 레벨 Process 정의 (선택)
        /// </summary>
        public List<JobProcessCreate_Dto>? JobProcesses { get; set; }

        /// <summary>
        /// Chunk 레벨 Step 정의 (필수)
        /// </summary>
        public List<ChunkStepCreate_Dto> ChunkSteps { get; set; } = new();

        public static OrchestrationOptions Default() => new()
        {
            IsSynchronous = false,
            Priority = OrchestrationConstants.DEFAULT_ASYNC_PRIORITY
        };

        public static OrchestrationOptions Bypass() => new()
        {
            IsSynchronous = true,
            Priority = OrchestrationConstants.DEFAULT_SYNC_PRIORITY
        };
    }

    /// <summary>
    /// Context object passed to chunk processors.
    /// Provides type-safe access to chunk metadata without using object arrays.
    /// </summary>
    public class ChunkProcessingContext
    {
        public Guid JobId { get; init; }
        public Guid ChunkId { get; init; }
        public string Identifier { get; init; } = string.Empty;
        public int TotalItemCount { get; init; }
        public int ChunkItemCount { get; init; }
        public int ChunkIndex { get; init; }
        public string? Delimiter { get; init; }

        /// <summary>
        /// Creates context from chunk identifiers DTO.
        /// </summary>
        public static ChunkProcessingContext FromIdentifiers(ChunkIdentifiers_DTO dto, int chunkIndex)
        {
            return new ChunkProcessingContext
            {
                JobId = dto.JobId,
                ChunkId = dto.ChunkId,
                Identifier = dto.Identifier,
                TotalItemCount = dto.TotalItemCount,
                ChunkItemCount = dto.ItemCount,
                ChunkIndex = chunkIndex,
                Delimiter = dto.Delimiter
            };
        }
    }

    /// <summary>
    /// Delegate for processing DTOs before chunk serialization.
    /// Provides type-safe context instead of object array.
    /// </summary>
    /// <typeparam name="T">DTO type being processed.</typeparam>
    /// <param name="items">List of items in the current chunk.</param>
    /// <param name="context">Chunk processing context with metadata.</param>
    public delegate void ChunkProcessor<T>(IList<T> items, ChunkProcessingContext context);

    /// <summary>
    /// Result DTO for orchestration execution.
    /// Provides comprehensive information about the created orchestration job.
    /// </summary>
    public class OrchestrationResult
    {
        /// <summary>
        /// Unique job identifier for the orchestration.
        /// </summary>
        public Guid JobId { get; init; }

        /// <summary>
        /// Total number of chunks created for this job.
        /// </summary>
        public int TotalChunkCount { get; init; }

        /// <summary>
        /// Total number of items in the original request payload.
        /// </summary>
        public int TotalItemCount { get; init; }

        /// <summary>
        /// Domain type for this orchestration.
        /// </summary>
        public string DomainType { get; init; } = string.Empty;

        /// <summary>
        /// User identifier who initiated the orchestration.
        /// </summary>
        public string UserId { get; init; } = string.Empty;

        /// <summary>
        /// Timestamp when the orchestration was created.
        /// </summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>
        /// Indicates whether this orchestration is synchronous (bypass mode).
        /// </summary>
        public bool IsSynchronous { get; init; }

        /// <summary>
        /// Queue priority for chunk processing.
        /// </summary>
        public int Priority { get; init; }

        /// <summary>
        /// Brand code associated with this orchestration.
        /// </summary>
        public string BrandCode { get; init; } = string.Empty;
    }
}