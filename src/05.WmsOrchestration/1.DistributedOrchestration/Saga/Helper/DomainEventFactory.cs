// [담당업무 1] Step/Process 타입 문자열 → 도메인 이벤트 인스턴스. Saga 가 도메인을 몰라도 다음 Step 을 발행할 수 있게 한다.

using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Infrastructure;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// 도메인 이벤트 생성 팩토리
    /// 
    /// 책임:
    /// - Reflection 기반 이벤트 타입 해석
    /// - 이벤트 인스턴스 생성
    /// - 타입 검증
    /// 
    /// 사용처:
    /// - ChunkStepSaga
    /// - ChunkStepCompensationSaga
    /// - JobProcessSaga
    /// </summary>
    public static class DomainEventFactory
    {
        /// <summary>
        /// 도메인 이벤트 생성 (타입 검증 포함)
        /// </summary>
        /// <typeparam name="T">생성할 이벤트의 Base 타입</typeparam>
        /// <param name="eventType">이벤트 타입 문자열 (예: "ValidateOrderStepEvent")</param>
        /// <returns>생성된 이벤트 인스턴스</returns>
        /// <exception cref="InvalidOperationException">
        /// - 타입을 찾을 수 없는 경우
        /// - 타입이 Base 클래스를 상속하지 않은 경우
        /// - 인스턴스 생성에 실패한 경우
        /// </exception>
        public static T CreateDomainEvent<T>(string eventType) where T : class
        {
            // 1. Type 해석
            Type messageType;
            try
            {
                messageType = ReflectionHelper.GetDomainType(eventType);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to resolve event type: {eventType}", ex);
            }

            // 2. 타입 검증 - Base 클래스 상속 확인
            if (!typeof(T).IsAssignableFrom(messageType))
            {
                throw new InvalidOperationException(
                    $"EventType must inherit from {typeof(T).Name}: {eventType}. " +
                    $"Actual type: {messageType.FullName}");
            }

            // 3. 인스턴스 생성
            var evt = Activator.CreateInstance(messageType) as T;

            if (evt == null)
            {
                throw new InvalidOperationException(
                    $"Failed to create event instance: {eventType}. " +
                    $"Type: {messageType.FullName}");
            }

            return evt;
        }

        /// <summary>
        /// 도메인 이벤트 생성 (로깅 포함 버전)
        /// </summary>
        /// <typeparam name="T">생성할 이벤트의 Base 타입</typeparam>
        /// <param name="eventType">이벤트 타입 문자열</param>
        /// <param name="logger">로거 인스턴스</param>
        /// <param name="contextInfo">로깅용 컨텍스트 정보 (예: "ChunkId=xxx")</param>
        /// <returns>생성된 이벤트 인스턴스</returns>
        public static T CreateDomainEvent<T>(
            string eventType,
            ILogger logger,
            string contextInfo) where T : class
        {
            try
            {
                var evt = CreateDomainEvent<T>(eventType);

                logger.LogDebug(
                    AppLog.Log("[DomainEventFactory] Event created successfully. EventType={EventType}, Context={Context}"),
                    eventType, contextInfo);

                return evt;
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex,
                    AppLog.Log("[DomainEventFactory] Failed to create event. EventType={EventType}, Context={Context}"),
                    eventType, contextInfo);
                throw;
            }
        }
    }
}