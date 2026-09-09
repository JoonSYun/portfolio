// [담당업무 2] Attribute 바인딩 엔진 — Attribute 가 붙은 DTO/Consumer 를 스캔해 Exchange·Queue·재시도를 자동 구성한다.

using MassTransit;
using Portfolio.WmsOrchestration.Exceptions;
using Portfolio.WmsOrchestration.Model;
using System.Reflection;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// Responsible for automatically binding domain DTOs and Consumers
    /// to RabbitMQ exchanges and queues using MassTransit.
    ///
    /// Key Responsibilities:
    /// - Scan assemblies for DTOs annotated with MassTransitAutoBindAttribute.
    /// - Register message topology (exchange name, type, routing rules).
    /// - Detect Consumers annotated with MassTransitConsumerAutoBindAttribute.
    /// - Automatically create ReceiveEndpoints per consumer.
    ///
    /// WHY:
    /// This enables a declarative, attribute-driven binding model so that
    /// new domains or message types can be onboarded without modifying bus configuration.
    ///
    /// Conventions applied:
    /// - English-only comments
    /// - WHY-oriented explanations
    /// - Consistent naming and structured layout
    /// - No behavior change from original implementation
    /// </summary>
    public static class MassTransitAutoBinder
    {
        // ================================================================
        // PUBLIC ENTRYPOINT
        // ================================================================

        /// <summary>
        /// Scan all assemblies, register message types, and configure consumers.
        /// </summary>
        public static void AddAutoBoundReceiveEndpoints(
            this IRabbitMqBusFactoryConfigurator cfg,
            IBusRegistrationContext context,
            params Type[] consumerTypes)
        {
            // Step 1: Register DTO-level binding (Exchange / Publish configuration)
            CollectAndRegisterMessageTypes(cfg);

            // Step 2: Register receive endpoints for each consumer
            foreach (var consumerType in consumerTypes)
            {
                var consumerAttr = consumerType.GetCustomAttribute<MassTransitConsumerAutoBindAttribute>();
                if (consumerAttr == null)
                    continue;

                // Extract original DTO type from IConsumer<ChunkPayload_Message<T>> or others
                var originalMessageType = ExtractOriginalMessageTypeFromConsumer(consumerType);
                if (originalMessageType == null)
                    continue;

                // Get DTO annotation
                var dtoAttr = originalMessageType.GetCustomAttribute<MassTransitAutoBindAttribute>();
                if (dtoAttr == null)
                    continue;

                RegisterReceiveEndpoint(
                    cfg,
                    context,
                    consumerType,
                    originalMessageType,
                    dtoAttr,
                    consumerAttr.QueueName);
            }
        }

        // ================================================================
        // MESSAGE TYPE REGISTRATION
        // ================================================================

        /// <summary>
        /// Scan assemblies for DTOs with MassTransitAutoBindAttribute and
        /// register publish topology for each message type.
        ///
        /// WHY:
        /// Exchange name and publish configuration are defined at DTO-level
        /// to centralize message routing rules.
        /// </summary>
        private static void CollectAndRegisterMessageTypes(IRabbitMqBusFactoryConfigurator cfg)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            var messageTypes = assemblies
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .Where(t =>
                    t.GetCustomAttribute<MassTransitAutoBindAttribute>() != null
                    && !t.IsAbstract
                    && !t.IsInterface)
                .ToList();

            var registered = new HashSet<Type>();

            foreach (var type in messageTypes)
            {
                if (registered.Contains(type))
                    continue;

                var attr = type.GetCustomAttribute<MassTransitAutoBindAttribute>();
                if (attr != null)
                {
                    RegisterPublishEndpoint(cfg, type, attr);
                    registered.Add(type);
                }
            }
        }

        // ================================================================
        // DTO → Consumer Message Type Resolver
        // ================================================================

        /// <summary>
        /// Extract original DTO type from:
        /// - IConsumer<ChunkPayload_Message<T>>
        /// - IConsumer<JobPayload_Message<T>>
        /// - IConsumer<T>
        /// </summary>
        private static Type? ExtractOriginalMessageTypeFromConsumer(Type consumerType)
        {
            var iface = consumerType
                .GetInterfaces()
                .FirstOrDefault(i =>
                    i.IsGenericType &&
                    i.GetGenericTypeDefinition() == typeof(IConsumer<>));

            if (iface == null)
                return null;

            Type consumeType = iface.GetGenericArguments()[0];
            return UnwrapPayloadMessage(consumeType);
        }

        /// <summary>
        /// If the type is ChunkPayload_Message<T> or JobPayload_Message<T>, return T.
        /// Otherwise return the type itself.
        ///
        /// WHY:
        /// Consumers consume wrapped message envelopes, but attribute config is on DTO (T).
        /// </summary>
        private static Type UnwrapPayloadMessage(Type t)
        {
            if (!t.IsGenericType)
                return t;

            var genDef = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments();

            if (genDef == typeof(ChunkPayload_Message<>))
                return args[0];

            if (genDef == typeof(JobPayload_Message<>))
                return args[0];

            return t;
        }

        /// <summary>
        /// Resolve message type for endpoint binding based on ConsumerType (CHUNK / JOB / NONE).
        /// </summary>
        public static Type ResolveReceiveEndpointMessageType(Type originalType, string consumerType)
        {
            return consumerType switch
            {
                nameof(ConsumerBindingType.CHUNK) => typeof(ChunkPayload_Message<>).MakeGenericType(originalType),
                nameof(ConsumerBindingType.JOB) => typeof(JobPayload_Message<>).MakeGenericType(originalType),
                nameof(ConsumerBindingType.NONE) => originalType,
                nameof(ConsumerBindingType.JOB_SAGA_EVENT) => originalType,
                _ => originalType
            };
        }

        // ================================================================
        // PUBLISH (Exchange) REGISTRATION
        // ================================================================

        private static void RegisterPublishEndpoint(
            IRabbitMqBusFactoryConfigurator cfg,
            Type dtoType,
            MassTransitAutoBindAttribute attr)
        {
            var publishType = ResolveReceiveEndpointMessageType(dtoType, attr.ConsumerType);

            var method = typeof(MassTransitAutoBinder)
                .GetMethod(nameof(RegisterPublishEndpointGeneric),
                    BindingFlags.NonPublic | BindingFlags.Static);

            method?
                .MakeGenericMethod(publishType)
                .Invoke(null, new object[] { cfg, attr });
        }

        /// <summary>
        /// Configure message topology for TMessage.
        /// </summary>
        private static void RegisterPublishEndpointGeneric<TMessage>(
            IRabbitMqBusFactoryConfigurator cfg,
            MassTransitAutoBindAttribute attr)
            where TMessage : class
        {
            cfg.Message<TMessage>(x =>
            {
                //x.SetEntityName(attr.ExchangeName);
            });

            // WHY:
            // ExchangeType is defined at DTO level, but MassTransit automatically
            // creates exchanges upon publish. Custom binding is handled at ReceiveEndpoint.
        }

        // ================================================================
        // RECEIVE ENDPOINT REGISTRATION
        // ================================================================

        private static void RegisterReceiveEndpoint(
            IRabbitMqBusFactoryConfigurator cfg,
            IBusRegistrationContext context,
            Type consumerType,
            Type originalMessageType,
            MassTransitAutoBindAttribute dtoAttr,
            string queueName)
        {
            var messageType = ResolveReceiveEndpointMessageType(originalMessageType, dtoAttr.ConsumerType);

            var method = typeof(MassTransitAutoBinder)
                .GetMethod(nameof(RegisterReceiveEndpointGeneric),
                    BindingFlags.NonPublic | BindingFlags.Static);

            method?
                .MakeGenericMethod(consumerType, messageType)
                .Invoke(null, new object[] { cfg, context, queueName, dtoAttr });
        }

        /// <summary>
        /// Create ReceiveEndpoint for TConsumer handling TMessage.
        ///
        /// WHY:
        /// All queue settings (priority, retry, concurrency, binding)
        /// are controlled by attributes to ensure consistent behavior.
        /// </summary>
        private static void RegisterReceiveEndpointGeneric<TConsumer, TMessage>(
            IRabbitMqBusFactoryConfigurator cfg,
            IBusRegistrationContext context,
            string queueName,
            MassTransitAutoBindAttribute dtoAttr)
            where TConsumer : class, IConsumer
            where TMessage : class
        {
            cfg.ReceiveEndpoint(queueName, e =>
            {
                // Basic endpoint configuration
                e.PrefetchCount = dtoAttr.PrefetchCount;
                e.ConcurrentMessageLimit = dtoAttr.ConcurrentMessageLimit;
                e.SetQueueArgument("x-max-priority", dtoAttr.MaxPriority);

                // Retry configuration
                e.UseMessageRetry(r =>
                {
                    r.Handle<DBLockException>();
                    r.Interval(dtoAttr.RetryCount, TimeSpan.FromSeconds(dtoAttr.RetryDelaySeconds));
                });

                // Prevent auto-binding to exchange by MassTransit
                e.ConfigureConsumeTopology = false;

                //// Bind this queue to exchange
                //e.Bind(dtoAttr.ExchangeName, s =>
                //{
                //    s.ExchangeType = dtoAttr.ExchangeType;
                //    s.RoutingKey = string.IsNullOrEmpty(dtoAttr.RoutingKey)
                //        ? queueName
                //        : dtoAttr.RoutingKey;
                //});

                // Register consumer
                e.ConfigureConsumer<TConsumer>(context);
            });
        }
    }
}
