using System.Reflection;
using MassTransit;
using RabbitMQ.Client;

namespace Portfolio.WmsOrchestration.BaseFramework;

/// <summary>
/// [담당업무 2] Attribute 하나로 큐 선언. 컨슈머 클래스에 붙이면 Exchange·Queue·바인딩·재시도·DLQ 가 자동 구성된다.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MessageQueueAttribute : Attribute
{
    public string QueueName { get; }
    public int PrefetchCount { get; init; } = 16;
    public int ConcurrentMessageLimit { get; init; } = 8;
    public bool Durable { get; init; } = true;
    public bool UseDeadLetter { get; init; } = true;

    public MessageQueueAttribute(string queueName) => QueueName = queueName;
}

/// <summary>
/// [담당업무 2] 바인딩 엔진 — Attribute 만 추가하면 메시지 큐가 자동 등록된다.
///
/// 이전에는 컨슈머를 추가할 때마다 버스 설정 파일에 ReceiveEndpoint 블록을 손으로 썼다 (누락·오타·머지 충돌).
/// 이 엔진은 시작 시 어셈블리를 스캔해 [MessageQueue] 가 붙은 컨슈머마다:
///   · 큐 선언 (durable, DLQ 라우팅)
///   · Exchange → Queue 바인딩 (메시지 타입 기준)
///   · prefetch / 동시성 / 재시도 / 재전달 정책
/// 을 일괄 적용한다. 신규 도메인은 Consumer 로직만 작성하면 파이프라인에 편입된다.
/// </summary>
public static class QueueBindingEngine
{
    public static void AddOrchestrationBus(this IServiceCollection services, IConfiguration cfg, params Assembly[] consumerAssemblies)
    {
        var bindings = Discover(consumerAssemblies);

        services.AddMassTransit(bus =>
        {
            foreach (var b in bindings) bus.AddConsumer(b.ConsumerType);
            bus.AddSagaStateMachine<DistributedOrchestration.BulkSyncSaga, DistributedOrchestration.BulkSyncState>()
               .EntityFrameworkRepository(r => { r.ConcurrencyMode = ConcurrencyMode.Optimistic; r.ExistingDbContext<FaultTolerance.OrchestrationDbContext>(); });

            bus.AddEntityFrameworkOutbox<FaultTolerance.OrchestrationDbContext>(o => { o.UsePostgres(); o.UseBusOutbox(); }); // 3.FaultTolerance

            bus.UsingRabbitMq((ctx, rmq) =>
            {
                rmq.Host(cfg["RabbitMq:Host"], cfg["RabbitMq:VHost"] ?? "/", h => { h.Username(cfg["RabbitMq:User"]!); h.Password(cfg["RabbitMq:Password"]!); });

                foreach (var b in bindings)
                {
                    rmq.ReceiveEndpoint(b.Attribute.QueueName, ep =>
                    {
                        ep.Durable = b.Attribute.Durable;
                        ep.PrefetchCount = b.Attribute.PrefetchCount;
                        ep.ConcurrentMessageLimit = b.Attribute.ConcurrentMessageLimit;
                        ep.ExchangeType = ExchangeType.Fanout;

                        ep.UseEntityFrameworkOutbox<FaultTolerance.OrchestrationDbContext>(ctx);   // 인박스 + 아웃박스
                        FaultTolerance.RetryPolicies.Apply(ep);                                       // 재시도 · 지연 재전달

                        if (b.Attribute.UseDeadLetter)
                            ep.DiscardFaultedMessages();   // _error 큐로 — DeadLetterReprocessor 가 재처리 (3.FaultTolerance)

                        ep.ConfigureConsumer(ctx, b.ConsumerType);
                    });
                }

                rmq.ConfigureEndpoints(ctx); // Saga 엔드포인트 등 나머지는 관례로
            });
        });
    }

    private static IReadOnlyList<(Type ConsumerType, MessageQueueAttribute Attribute)> Discover(Assembly[] assemblies) =>
        assemblies.SelectMany(a => a.GetTypes())
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<MessageQueueAttribute>()))
            .Where(x => x.Attr is not null && !x.Type.IsAbstract && x.Type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>)))
            .Select(x => (x.Type, x.Attr!))
            .ToList();
}
