namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// 메일 전송 점검용 진단 Job(<see cref="Jobs.MailTestJob"/>) 의 page-level payload DTO.
/// 실제 사용하는 값은 없으며, <see cref="ISchedulerParam.BCODE"/> 계약을 만족시키기 위한 최소 구현이다.
/// 메일 수신자 라우팅은 <see cref="JobArgs.BrandCode"/> + SCH_BRAND_ADD_MAIL 로 결정되므로 본 DTO 와 무관하다.
/// </summary>
public class MailTestParam : ISchedulerParam
{
    /// <summary>브랜드(사업부) 코드. 진단 Job 이라 검증/사용하지 않으며 빈 값이어도 무방하다.</summary>
    public string BCODE { get; set; } = "";
}
