// [담당업무 2] Attribute 바인딩 엔진 — DTO 에 붙이는 큐 설정 선언 (prefetch · 동시성 · 재시도 · 우선순위).

using Portfolio.WmsOrchestration.Model;
using System.Linq;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    /// <summary>
    /// MassTransit Auto Bind Attribute : DTO(Common Domain) 클래스에 적용하여 Exchange 설정
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class MassTransitAutoBindAttribute : Attribute
    {
        /// <summary>
        /// 메시지를 Publish/Consume할 때 연결되는 Exchange 이름.
        /// RabbitMQ의 Exchange 명이며, Consumer가 바인딩될 대상.
        /// </summary>
        //public string ExchangeName { get; set; }

        /// <summary>
        /// Consumer 구분값 (CHUNK/JOB/NONE 등).
        /// 특정 Consumer 유형을 자동 매핑할 때 사용되는 커스텀 타입 구분.
        /// </summary>
        public string ConsumerType { get; set; }

        /// <summary>
        /// Routing Key.
        /// Direct / Topic Exchange에서 메시지를 라우팅할 때 사용.
        /// Fanout에서는 의미 없음.
        /// </summary>
        //public string RoutingKey { get; set; }

        /// <summary>
        /// Exchange 타입.
        /// fanout/direct/topic/headers 중에서 선택.
        /// 메시지가 어떤 방식으로 라우팅될지 결정.
        /// </summary>
        //public string ExchangeType { get; set; } = "fanout";

        /// <summary>
        /// Prefetch Count.
        /// RabbitMQ 브로커로부터 한 번에 Consumer가 미리 가져오는 메시지 수.
        /// 이 값만큼 메시지를 선점하고, 아직 ACK가 되지 않은 메시지에 대해서는 추가 메시지를 받지 않음.
        /// PrefetchCount = 1이면 메시지를 하나씩 처리하며 back-pressure 발생.
        /// 스레드 수와 직접적인 연관은 없고, 브로커에서 얼마나 미리 메시지를 가져올지 결정.
        /// </summary>
        public int PrefetchCount { get; set; } = 1;

        /// <summary>
        /// Consumer가 동시에 처리할 수 있는 메시지 최대 개수.
        /// MassTransit 내부에서 비동기 Task로 동시에 실행할 수 있는 메시지 수를 제한.
        /// 예: ConcurrentMessageLimit = 10 → 최대 10개의 메시지를 동시에 처리.
        /// 실제 스레드 수는 .NET ThreadPool에서 관리되며, 스레드 10개가 고정으로 도는 것은 아님.
        /// PrefetchCount와 함께 설정하면 메시지 처리량과 메모리 점유 최적화 가능.
        /// </summary>
        public int ConcurrentMessageLimit { get; set; } = 10;

        /// <summary>
        /// 메시지 처리 실패 시 재시도 횟수.
        /// </summary>
        public int RetryCount { get; set; } = 3;

        /// <summary>
        /// 재시도 간격(초 단위).
        /// RetryCount가 1 이상이면, 이 시간마다 재시도됨.
        /// </summary>
        public int RetryDelaySeconds { get; set; } = 10;

        /// <summary>
        /// 메시지 우선순위(Priority Queue 사용 시 적용).
        /// RabbitMQ priority-queue plugin 필요.
        /// </summary>
        public int MaxPriority { get; set; } = 100;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="exchangeName"></param>
        /// <param name="consumerType"></param>
        /// <param name="routingKey"></param>
        /// <param name="exchangeType"></param>
        /// <param name="prefetchCount"></param>
        /// <param name="concurrentMessageLimit"></param>
        /// <param name="retryCount"></param>
        /// <param name="retryDelaySeconds"></param>
        /// <param name="maxPriority"></param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentException"></exception>
        public MassTransitAutoBindAttribute(
            string consumerType,
            int prefetchCount = 5,
            int concurrentMessageLimit = 10,
            int retryCount = 1,
            int retryDelaySeconds = 10,
            int maxPriority = 100)
        {
            // ConsumerType 검증
            if (!string.IsNullOrEmpty(consumerType) &&
                !IsValidConsumerType(consumerType))
                throw new ArgumentException(
                    $"ConsumerType '{consumerType}' is invalid. Valid values are: {ConsumerBindingType.CHUNK}, {ConsumerBindingType.JOB}, {ConsumerBindingType.NONE}",
                    nameof(consumerType));

            ConsumerType = consumerType ?? nameof(ConsumerBindingType.NONE);
            PrefetchCount = prefetchCount;
            ConcurrentMessageLimit = concurrentMessageLimit;
            RetryCount = retryCount;
            RetryDelaySeconds = retryDelaySeconds;
            MaxPriority = maxPriority;
        }

        private static bool IsValidConsumerType(string consumerType)
        {
            return consumerType == nameof(ConsumerBindingType.CHUNK) ||
                   consumerType == nameof(ConsumerBindingType.JOB) ||
                   consumerType == nameof(ConsumerBindingType.NONE) ||
                   consumerType == nameof(ConsumerBindingType.JOB_SAGA_EVENT);
        }

        private static bool IsValidExchangeType(string exchangeType)
        {
            return exchangeType == ExchangeTypeConst.FANOUT ||
                   exchangeType == ExchangeTypeConst.DIRECT ||
                   exchangeType == ExchangeTypeConst.TOPIC ||
                   exchangeType == ExchangeTypeConst.HEADERS;
        }
    }


    /// <summary>
    /// MassTransit Consumer Auto Bind Attribute : Consumer 클래스에 적용하여 큐 이름을 지정
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class MassTransitConsumerAutoBindAttribute : Attribute
    {
        public string QueueName { get; set; }

        public MassTransitConsumerAutoBindAttribute(string queueName)
        {
            if (string.IsNullOrEmpty(queueName))
                throw new ArgumentNullException(nameof(queueName), "QueueName cannot be null or empty.");
            QueueName = queueName;
        }
    }

    public static class ExchangeTypeConst
    {
        public const string FANOUT = "fanout";
        public const string DIRECT = "direct";
        public const string TOPIC = "topic";
        public const string HEADERS = "headers";
    }
}