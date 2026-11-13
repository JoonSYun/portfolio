// [담당업무 2] Program.cs 진입점 — Saga 4종 + Consumer 등록, EF Outbox, RabbitMQ 호스트 구성.

using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Saga;
using System.Data;
using System.Reflection;

namespace Portfolio.WmsOrchestration.Extension
{
    /// <summary>
    /// 수동 바인딩을 위한 MassTransit 확장 (Program.cs용)
    /// 자동 스캔 대신 수동으로 Consumer를 특정 큐에 바인딩
    /// </summary>
    public static class MassTransitExtensions
    {
        public static IServiceCollection AddOrchestrationMassTransit(
            this IServiceCollection services,
            IConfiguration configuration,
            params Type[] consumerTypes)
        {
            // 복합 객체는 무조건 GetSection().Get<T>()
            var mqSettingConfig = configuration
                .GetSection("RabbitMQSetting")
                .Get<RabbitMQSetting>();

            if (mqSettingConfig is null)
                throw new InvalidOperationException(
                    "RabbitMQSetting section is missing in configuration.");

            services.AddMassTransit(x =>
            {
                x.SetEndpointNameFormatter(
                    new DefaultEndpointNameFormatter(prefix: "queue-", includeNamespace: false));

                // ============================================================
                // 1) Saga + Consumer 자동 스캔
                // ============================================================
                x.AddSagaStateMachine<JobSaga, JobSagaState>()
                    .EntityFrameworkRepository(r =>
                    {
                        r.ConcurrencyMode = ConcurrencyMode.Optimistic;
                        r.ExistingDbContext<JobSagaDbContext>();
                    });

                x.AddSagaStateMachine<JobProcessSaga, JobProcessSagaState>()
                    .EntityFrameworkRepository(r =>
                    {
                        r.ConcurrencyMode = ConcurrencyMode.Optimistic;
                        r.ExistingDbContext<JobProcessSagaDbContext>();
                    });

                x.AddSagaStateMachine<ChunkStepSaga, ChunkStepSagaState>()
                    .EntityFrameworkRepository(r =>
                    {
                        r.ConcurrencyMode = ConcurrencyMode.Optimistic;
                        r.ExistingDbContext<ChunkStepSagaDbContext>();
                    });

                x.AddSagaStateMachine<ChunkStepCompensationSaga, ChunkStepCompensationSagaState>()
                    .EntityFrameworkRepository(r =>
                    {
                        r.ConcurrencyMode = ConcurrencyMode.Optimistic;
                        r.ExistingDbContext<ChunkStepCompensationSagaDbContext>();
                    });

                x.AddConfigureEndpointsCallback((context, name, cfg) =>
                {
                    cfg.UseConcurrencyLimit(mqSettingConfig.ConsumerConcurrency);

                    cfg.UseMessageRetry(r =>
                    {
                        r.Immediate(mqSettingConfig.MessageRetryCount);

                        r.Ignore<OperationCanceledException>();
                    });

                    if (cfg is IRabbitMqReceiveEndpointConfigurator rmq)
                    {
                        rmq.SetQueueArgument("x-max-priority", mqSettingConfig.XMaxPriority);
                        rmq.PrefetchCount = (ushort)(mqSettingConfig.ConsumerConcurrency * 2);
                    }

                    cfg.UseEntityFrameworkOutbox<MessageOutboxDBContext>(context, o =>
                    {
                        o.MessageDeliveryLimit = mqSettingConfig.MessageDeliveryLimit;
                        o.MessageDeliveryTimeout = TimeSpan.FromSeconds(
                            mqSettingConfig.MessageDeliveryTimeoutSeconds);
                    });
                });

                x.AddConsumers(consumerTypes);

                // ============================================================
                // 1-1) EntityFramework Outbox Pattern (PostgreSQL)
                // ============================================================
                x.AddEntityFrameworkOutbox<MessageOutboxDBContext>(o =>
                {
                    o.UseSqlServer();
                    o.UseBusOutbox();

                    o.QueryDelay = TimeSpan.FromSeconds(mqSettingConfig.QueryDelaySeconds);
                    o.DuplicateDetectionWindow =
                        TimeSpan.FromMinutes(mqSettingConfig.DuplicateDetectionWindowMinutes);

                    o.IsolationLevel = IsolationLevel.ReadCommitted;
                    o.QueryTimeout = TimeSpan.FromSeconds(mqSettingConfig.DBContextTimeoutSeconds);
                });

                // ============================================================
                // 2) RabbitMQ 설정
                // ============================================================
                x.UsingRabbitMq((context, cfg) =>
                {
                    var rabbitConn = configuration.GetConnectionString("RabbitMQ");

                    cfg.MessageTopology.SetEntityNameFormatter(
                        new ExchangePrefixFormatter(prefix: "exchange"));

                    cfg.Host(rabbitConn);

                    cfg.UseConcurrencyLimit(mqSettingConfig.ServerConcurrency);

                    cfg.ConfigureEndpoints(context);
                    cfg.ConnectConsumeObserver(context.GetRequiredService<ConsumeObserver>());
                    cfg.ConnectReceiveObserver(context.GetRequiredService<ReceiveObserver>());
                    cfg.ConnectPublishObserver(context.GetRequiredService<PublishObserver>());
                    cfg.ConnectBusObserver(context.GetRequiredService<BusObserver>());
                });
            });

            services.AddSingleton<ConsumeObserver>();
            services.AddSingleton<ReceiveObserver>();
            services.AddSingleton<PublishObserver>();
            services.AddSingleton<BusObserver>();

            return services;
        }

    }
}
