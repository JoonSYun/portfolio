using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// Job Process Saga State Entity Mapping
    /// </summary>
    public class JobProcessSagaStateMap : SagaClassMap<JobProcessSagaState>
    {
        protected override void Configure(EntityTypeBuilder<JobProcessSagaState> entity, ModelBuilder model)
        {
            // ================================
            // Table Mapping
            // ================================
            entity.ToTable("job_process_saga_state");

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
            // IJobContextSaga
            // ================================
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
            // JobProcess-specific
            // ================================
            entity.Property(x => x.JobIdentifier)
                .HasMaxLength(200);

            entity.Property(x => x.JobStatus)
                .IsRequired()
                .HasMaxLength(20);

            entity.Property(x => x.ProcessPlanJson)
                .IsRequired()
                .HasDefaultValue("[]");

            entity.Property(x => x.PayloadJson)
                .IsRequired()
                .HasDefaultValue("[]");

            entity.Property(x => x.TotalProcessCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(x => x.CurrentProcessIndex)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(x => x.ExecutedProcessesJson)
                .IsRequired()
                .HasDefaultValue("[]");

            entity.Property(x => x.CompletedProcessCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(x => x.FailedProcessCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(x => x.FailedProcessIndex);

            // ================================
            // Indexes (Job 단위 테이블 정책)
            // ================================
            entity.HasIndex(x => x.DomainType)
                .HasDatabaseName("IX_job_process_saga_state_DomainType");

            entity.HasIndex(x => x.JobIdentifier)
                .HasDatabaseName("IX_job_process_saga_state_JobIdentifier");

            entity.HasIndex(x => x.UserId)
                .HasDatabaseName("IX_job_process_saga_state_UserId");
        }
    }
}