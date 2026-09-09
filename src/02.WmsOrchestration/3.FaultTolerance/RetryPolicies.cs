using System.Net.Sockets;
using MassTransit;
using Npgsql;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Portfolio.WmsOrchestration.FaultTolerance;

/// <summary>
/// [담당업무 3] 재시도 정책 — 두 층.
///
///   메시지 층 (MassTransit) : 컨슈머가 예외를 던지면 즉시 재시도 → 지연 재전달 → DLQ
///   호출 층 (Polly)         : 외부 API / DB 한 번의 호출에 타임아웃·재시도·서킷 브레이커
///
/// 어떤 예외를 재시도할지 명시한다. 검증 실패·업무 거부는 재시도해도 같은 결과 — 바로 DLQ 로.
/// 타임아웃·네트워크·DB 락은 잠깐 뒤면 풀린다 — 지수 백오프로 재시도.
/// 이 구분이 "타임아웃·네트워크 장애·DB 락이 겹치는 상황에서도 서버 응답 장애 0건" 의 핵심이었다.
/// </summary>
public static class RetryPolicies
{
    /// <summary>메시지 층 — 모든 ReceiveEndpoint 에 QueueBindingEngine 이 일괄 적용.</summary>
    public static void Apply(IReceiveEndpointConfigurator ep)
    {
        // 1) 즉시 재시도: 짧은 락/네트워크 흔들림
        ep.UseMessageRetry(r =>
        {
            r.Exponential(retryLimit: 3, minInterval: TimeSpan.FromMilliseconds(200), maxInterval: TimeSpan.FromSeconds(5), intervalDelta: TimeSpan.FromMilliseconds(500));
            r.Handle<TimeoutException>();
            r.Handle<SocketException>();
            r.Handle<HttpRequestException>();
            r.Handle<NpgsqlException>(ex => ex.IsTransient);
            r.Handle<Microsoft.Data.SqlClient.SqlException>(ex => ex.Number is 1205 or -2 or 4060);   // 데드락 · 타임아웃 · DB 불가
            r.Ignore<ArgumentException>();
            r.Ignore<InvalidOperationException>();       // 업무 규칙 위반 — 재시도 무의미
        });

        // 2) 지연 재전달: 즉시 재시도가 다 실패하면 큐로 되돌려 시간을 두고 다시
        ep.UseDelayedRedelivery(r => r.Intervals(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)));
        // 3) 그래도 실패 → _error 큐 (DLQ) → DeadLetterReprocessor
    }

    /// <summary>호출 층 — 외부 API 호출 하나를 감싼다. HttpClient 핸들러에 DI 로 주입.</summary>
    public static ResiliencePipeline<HttpResponseMessage> ExternalApi() => new ResiliencePipelineBuilder<HttpResponseMessage>()
        .AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(10) })
        .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(300),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(r => (int)r.StatusCode >= 500 || r.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        })
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            FailureRatio = 0.5, MinimumThroughput = 20, SamplingDuration = TimeSpan.FromSeconds(30), BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>().Handle<HttpRequestException>().HandleResult(r => (int)r.StatusCode >= 500)
        })
        .Build();
}
