using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// ChunkStepSagaState EF Core 매핑 설정
    /// </summary>
    public class ChunkStepSagaStateMap : SagaClassMap<ChunkStepSagaState>
    {
        protected override void Configure(EntityTypeBuilder<ChunkStepSagaState> entity, ModelBuilder model)
        {
            // ================================
            // Table Mapping
            // ================================
            entity.ToTable("chunk_step_saga_state");

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
            entity.Property(x => x.ErrorCode);

            entity.Property(x => x.ErrorMessage);

            entity.Property(x => x.ErrorDetail);

            // ================================
            // ChunkStep-specific
            // ================================
            entity.Property(x => x.PayloadJson)
                .IsRequired();

            entity.Property(x => x.StepPlanJson)
                .IsRequired()
                .HasDefaultValue("[]");

            entity.Property(x => x.TotalStepCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(x => x.CurrentStepIndex)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(x => x.PendingCompensationsJson)
                .IsRequired()
                .HasDefaultValue("[]");

            entity.Property(x => x.HasPartialSuccess)
                .IsRequired()
                .HasDefaultValue(false);

            entity.Property(x => x.IsStepFailed)
                .IsRequired()
                .HasDefaultValue(false);

            entity.Property(x => x.PartialFailureDetailsJson)
                .IsRequired()
                .HasDefaultValue("{}");

            entity.Property(x => x.ProcessedItemCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(x => x.TotalFailCount)
                .IsRequired()
                .HasDefaultValue(0);

            // ================================
            // Indexes (Chunk 단위 테이블 정책)
            // ================================
            entity.HasIndex(x => new { x.JobId, x.ChunkIndex })
                .HasDatabaseName("IX_chunk_step_saga_state_JobId_ChunkIndex");

            entity.HasIndex(x => x.ChunkId)
                .HasDatabaseName("IX_chunk_step_saga_state_ChunkId");

            entity.HasIndex(x => x.Identifier)
                .HasDatabaseName("IX_chunk_step_saga_state_Identifier");

            entity.HasIndex(x => x.UserId)
                .HasDatabaseName("IX_chunk_step_saga_state_UserId");
        }
    }
}