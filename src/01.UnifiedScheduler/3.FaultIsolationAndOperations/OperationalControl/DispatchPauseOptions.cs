namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// <see cref="Portfolio.UnifiedScheduler.Infrastructure.DispatchPauseStore"/> 의 외부 설정.
/// <c>appsettings.json</c> 의 <c>"DispatchPause"</c> 섹션과 바인딩된다.
/// </summary>
public class DispatchPauseOptions
{
    /// <summary>
    /// 일시정지 상태 영속 파일 경로. 상대경로면 ContentRoot(.Web 프로젝트 폴더 / 배포 폴더) 기준으로 해소된다.
    /// 기본값 <c>dispatch-pause.json</c> 은 ContentRoot 직속 → 개발에서는 <c>.Web/dispatch-pause.json</c>,
    /// 배포에서는 publish 결과 폴더 직속을 가리켜 두 환경 모두 쓰기 가능.
    /// </summary>
    public string FilePath { get; set; } = "dispatch-pause.json";
}
