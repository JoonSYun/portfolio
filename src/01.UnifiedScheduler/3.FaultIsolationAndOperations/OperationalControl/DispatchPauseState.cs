namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// Dispatch 전역 일시정지 상태. <see cref="Portfolio.UnifiedScheduler.Infrastructure.DispatchPauseStore"/> 가
/// 파일(<c>dispatch-pause.json</c>)에서 로드 / 저장하며, <see cref="Portfolio.UnifiedScheduler.Jobs.Dispatcher"/>의
/// cron 자동 발사 진입부 가드(<see cref="Portfolio.UnifiedScheduler.Jobs.Dispatcher.Fire"/>)에서 읽는다.
/// 수동 실행(<see cref="Portfolio.UnifiedScheduler.Jobs.Dispatcher.EnqueueOne"/> /
/// <see cref="Portfolio.UnifiedScheduler.Jobs.Dispatcher.EnqueueDomain"/>) 은 가드 대상이 아니다.
/// </summary>
public class DispatchPauseState
{
    /// <summary>true 면 cron 자동 발사가 차단된다. 기본 false.</summary>
    public bool IsPaused { get; set; }

    /// <summary>토글이 일어난 시각(KST). 가독성을 위해 분 단위까지만 의미를 갖는다.</summary>
    public DateTime? PausedDt { get; set; }

    /// <summary>토글을 수행한 관리자의 UserName(Email). JWT sub → DB UserName 로 해소.</summary>
    public string? PausedBy { get; set; }

    /// <summary>토글 사유(자유 텍스트). 운영 감사 / 다른 운영자에게 맥락 전달용.</summary>
    public string? Reason { get; set; }
}
