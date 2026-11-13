using Serilog;

namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// Serilog 호스트 와이어링 — appsettings 의 <c>Serilog</c> 섹션 + <see cref="AppLogEnricher"/> 를 합쳐
/// 표준 로그 컨텍스트(SystemName / ApplicationName) 를 모든 로그 라인에 자동 부착한다.
/// </summary>
/// <remarks>
/// /api/admin/** 의 요청·응답 페이로드 로깅은 <see cref="Filters.ApiLoggingFilter"/> 가 컨트롤러 단계에서 별도 처리.
/// 본 확장은 호스트 레벨 글로벌 Serilog 설정만 담당.
/// </remarks>
public static class SerilogExtensions
{
    /// <summary>
    /// <see cref="WebApplicationBuilder.Host"/> 에 Serilog 를 부착하고 <see cref="AppLog"/> 의
    /// application-name prefix 를 process 수명 동안 고정한다.
    /// </summary>
    /// <remarks>
    /// Program.cs 의 다른 DI/파이프라인 등록보다 먼저 호출되어야 부트스트랩 단계 로그가
    /// 일관된 형태(콘솔 → 파일 → Seq) 로 흘러간다.
    /// </remarks>
    public static WebApplicationBuilder AddSchedulerSerilog(this WebApplicationBuilder builder)
    {
        // AppLog 는 정적 헬퍼 — process 수명 동안 한 번만 set. 이후 모든 AppLog.Log("...") 호출이
        // 같은 prefix("[ApplicationName] ...") 로 정규화된다.
        AppLog.ApplicationName =
            builder.Configuration["ApplicationName"] ?? "Portfolio.UnifiedScheduler";

        builder.Host.UseSerilog((context, services, configuration) =>
        {
            // 두 값은 enricher 에 주입돼 모든 LogEvent 의 속성으로 자동 추가된다 —
            // Seq / 콘솔 검색 시 멀티 서비스 환경에서 서비스 식별 표준 키.
            var systemName      = context.Configuration["SystemName"]      ?? "Portfolio";
            var applicationName = context.Configuration["ApplicationName"] ?? "Portfolio.UnifiedScheduler";

            configuration
                // appsettings.json 의 "Serilog" 섹션(MinimumLevel / WriteTo / Override) 적용.
                .ReadFrom.Configuration(context.Configuration)

                // DI 컨테이너에 등록된 ILogEventEnricher 들을 자동 적재 — 향후 enricher 추가시
                // 이 코드 변경 없이 services.AddSingleton<ILogEventEnricher, ...>() 만으로 반영.
                .ReadFrom.Services(services)

                // LogContext.PushProperty 로 push 한 스코프 속성을 LogEvent 로 흘려보내는 통로.
                // 잡 실행 컨텍스트(LogId, DomainCode, BrandCode 등) 와 RequestContextMiddleware 가
                // push 한 사용자 정보(UserId/UserName/UserRole) 가 로그에 따라붙는 메커니즘.
                .Enrich.FromLogContext()

                // 표준 컨텍스트(SystemName, ApplicationName) 를 모든 로그에 강제 주입 —
                // 누락 시 통합 로그 파이프라인의 식별 / 라우팅이 깨진다.
                .Enrich.With(new AppLogEnricher(systemName, applicationName))

                // MVC 가 자동으로 푸시하는 ActionId (액션 디스크립터 GUID — 호출별 ID 가
                // 아니라 부팅 시점 메서드 식별자라 디버깅 가치 낮음) 제거. ActionName 은 유지.
                .Enrich.With(new StripActionIdEnricher());
        });

        return builder;
    }
}
