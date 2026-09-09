using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// ChunkStep Compensation Saga State Entity Mapping
    /// </summary>
    public class ChunkStepCompensationSagaStateMap : SagaClassMap<ChunkStepCompensationSagaState>
    {
        protected override void Configure(
            EntityTypeBuilder<ChunkStepCompensationSagaState> entity,
            ModelBuilder model)
        {
            // ================================
            // Table Mapping
            // ================================
            entity.ToTable("chunk_step_compensation_saga_state");

            // ================================
            // Primary Key
            // ================================
            entity.HasKey(x => x.CorrelationId);

            entity.Property(x => x.CorrelationId)
                .ValueGeneratedNever();

            // ================================
            // ISagaState
            // ================================
            entity.Property(x => x.CurrentState);

            entity.Property(x => x.Version)
                .HasDefaultValue(0)
                .IsConcurrencyToken();

            // ================================
            // IChunkIdentifierSaga (includes IJobContextSaga)
            // ================================
            entity.Property(x => x.JobId)
                .IsRequired();

            entity.Property(x => x.ChunkId)
                .IsRequired();

            entity.Property(x => x.Identifier)
                .HasMaxLength(200);

            entity.Property(x => x.ChunkIndex)
                .IsRequired();

            entity.Property(x => x.DomainType)
                .IsRequired()
                .HasMaxLength(50);

            entity.Property(x => x.TotalChunkCount)
                .IsRequired();

            entity.Property(x => x.Delimiter);

            entity.Property(x => x.RequestBrandCode);

            entity.Property(x => x.SlipDiv);

            entity.Property(x => x.UserId)
                .IsRequired()
                .HasMaxLength(100);

            // ================================
            // ITimestampedSaga
            // ================================
            entity.Property(x => x.CreatedAt)
                .IsRequired();

            entity.Property(x => x.UpdatedAt)
                .IsRequired();

            entity.Property(x => x.CompletedAt);

            // ================================
            // IErrorTrackingSaga
            // ================================
            entity.Property(x => x.ErrorCode)
                .HasMaxLength(100);

            entity.Property(x => x.ErrorMessage)
                .HasMaxLength(500);

            entity.Property(x => x.ErrorDetail);

            // ================================
            // Compensation-specific
            // ================================
            entity.Property(x => x.CompensationId)
                .IsRequired();

            entity.Property(x => x.CompensationPlanJson)
                .IsRequired()
                .HasDefaultValue("[]");

            entity.Property(x => x.TotalCompensationSteps)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(x => x.CurrentCompensationIndex)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(x => x.FailedIdentifiersJson)
                .IsRequired()
                .HasDefaultValue("[]");

            entity.Property(x => x.PayloadJson)
                .IsRequired();

            entity.Property(x => x.CompensatedStepIndexesJson)
                .IsRequired()
                .HasDefaultValue("[]");

            entity.Property(x => x.CompensatedStepCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(x => x.FailedStepIndex);

            entity.Property(x => x.FailedStepType)
                .HasMaxLength(200);

            entity.Property(x => x.FailedAt);

            // ================================
            // Indexes (Chunk 단위 테이블 정책)
            // ================================
            entity.HasIndex(x => new { x.JobId, x.ChunkIndex })
                .HasDatabaseName("IX_chunk_step_compensation_saga_state_JobId_ChunkIndex");

            entity.HasIndex(x => x.ChunkId)
                .HasDatabaseName("IX_chunk_step_compensation_saga_state_ChunkId");

            entity.HasIndex(x => x.Identifier)
                .HasDatabaseName("IX_chunk_step_compensation_saga_state_Identifier");

            entity.HasIndex(x => x.UserId)
                .HasDatabaseName("IX_chunk_step_compensation_saga_state_UserId");
        }
    }
}