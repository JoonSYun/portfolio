using Serilog.Core;
using Serilog.Events;

namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// 모든 로그 이벤트에 조직 표준 속성(SystemName, Application)을 자동 첨부하는 Enricher.
/// Seq 대시보드에서 여러 시스템을 한 인스턴스에 모을 때 필터링 키로 사용.
/// </summary>
/// <remarks>
/// Serilog 의 Enricher 확장 포인트. <c>appsettings.json</c> 의 Serilog:Enrich 배열에
/// 이름만 적어도 사용 가능하지만, 이 프로젝트에서는 생성자 인자(systemName)를 주입해야 하므로
/// Program.cs 의 UseSerilog 람다에서 <c>.Enrich.With(new AppLogEnricher(...))</c> 로 등록한다.
/// </remarks>
public class AppLogEnricher : ILogEventEnricher
{
    private readonly string _systemName;
    private readonly string _applicationName;
    private readonly string _machineName;

    public AppLogEnricher(string systemName, string applicationName)
    {
        _systemName      = systemName;
        _applicationName = applicationName;
        _machineName     = Environment.MachineName;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        // AddPropertyIfAbsent 로 기존 속성 덮어쓰지 않음 — LogContext.PushProperty 가 우선
        logEvent.AddPropertyIfAbsent(factory.CreateProperty("SystemName",  _systemName));
        logEvent.AddPropertyIfAbsent(factory.CreateProperty("Application", _applicationName));
        logEvent.AddPropertyIfAbsent(factory.CreateProperty("MachineName", _machineName));
    }
}
