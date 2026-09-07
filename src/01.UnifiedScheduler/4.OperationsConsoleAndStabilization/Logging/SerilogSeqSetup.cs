using Serilog;
using Serilog.Events;

namespace Portfolio.UnifiedScheduler.OperationsConsoleAndStabilization.Logging;

/// <summary>
/// Serilog → Seq 로그 파이프라인. JobBase 가 LogContext 로 붙인 Domain/Brand/ExecutionId 가
/// 구조적 속성으로 Seq 에 들어가므로, 장애 시 "이 브랜드의 이 실행" 로그만 즉시 필터된다.
/// </summary>
public static class SerilogSeqSetup
{
    public static void Configure(WebApplicationBuilder builder) =>
        builder.Host.UseSerilog((ctx, cfg) => cfg
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Hangfire", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", "unified-scheduler")
            .Enrich.WithProperty("Node", Environment.MachineName)
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Domain}/{Brand} {Message:lj}{NewLine}{Exception}")
            .WriteTo.Seq(ctx.Configuration["Seq:Url"] ?? "http://seq:5341", apiKey: ctx.Configuration["Seq:ApiKey"]));
}
