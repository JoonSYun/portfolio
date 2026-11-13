using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Portfolio.WmsOrchestration.Model;

namespace Portfolio.WmsOrchestration.Saga
{
    public class JobSagaStateMap : SagaClassMap<JobSagaState>
    {
        protected override void Configure(EntityTypeBuilder<JobSagaState> entity, ModelBuilder model)
        {
            // ================================
            // Table Mapping
            // ================================
            entity.ToTable("job_saga_state");

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
                .IsConcurrencyToken();

            // ================================
            // IJobIdentifierSaga
            // ================================
            entity.Property(x => x.JobIdentifier)
                .HasMaxLength(200);

            entity.Property(x => x.JobIdentifierType)
                .HasMaxLength(50);

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
            // Job-specific
            // ================================
            entity.Property(x => x.SignalR_YN)
                .IsRequired();

            entity.Property(x => x.IsBypass)
                .IsRequired()
                .HasDefaultValue(false);

            entity.Property(x => x.ChunkStatus);

            entity.Property(x => x.ChunkSuccessCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(x => x.ChunkPartialSuccessCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(x => x.ChunkFailedCount)
                .IsRequired()
                .HasDefaultValue(0);

            entity.Property(x => x.JobProcessStatus);

            entity.Property(x => x.CreatedAt)
                .IsRequired();

            entity.Property(x => x.SuspendedAt)
                .IsRequired();

            entity.Property(x => x.CompletedAt)
                .IsRequired();

            // ================================
            // Indexes (Job 단위 테이블 정책)
            // ================================
            entity.HasIndex(x => x.DomainType)
                .HasDatabaseName("IX_job_saga_state_DomainType");

            entity.HasIndex(x => x.JobIdentifier)
                .HasDatabaseName("IX_job_saga_state_JobIdentifier");

            entity.HasIndex(x => x.UserId)
                .HasDatabaseName("IX_job_saga_state_UserId");
        }
    }
}