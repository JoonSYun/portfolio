namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// Job 비즈니스 예외 — 외부 서비스/프로시저 호출은 정상적으로 끝났으나 그 응답이
/// 업무적 실패를 담고 있을 때 던진다. 예: ERP 인터페이스 프로시저가 결과 컬럼으로 돌려준 실패,
/// 택배사 SOAP 의 <c>code != "Y"</c>, 외부 반품 API 의 실패 상태 코드,
/// 회수 프로시저의 per-row 실패 등.
///
/// <para>
/// 구조/인프라 실패(응답 null·row 0개·역직렬화 실패·ConnectionString 누락·DB 저장 0 등)는
/// <see cref="System.InvalidOperationException"/> 등 일반 예외로 두어 구분한다.
/// </para>
///
/// <para>
/// <see cref="Jobs.JobBase{TSelf,TParam}"/> 의 catch 가 <c>ex is BusinessException</c> 으로 판정해
/// 알림 라우팅을 분기한다: BusinessException → 브랜드 추가 메일 + 개발팀, 그 외 일반 예외 → 개발팀만.
/// HTTP 계층의 <see cref="BusinessRuleException"/>(<see cref="DomainException"/> 파생, StatusCode 매핑) 와는
/// 역할이 다르며 상속 관계도 없다.
/// </para>
/// </summary>
public sealed class BusinessException : Exception
{
    /// <summary>
    /// 업무 실패를 판정한 원본 데이터의 평문 덤프(선택). 설정하면 개발팀 메일 본문에만 실린다 —
    /// 브랜드 담당자 본문(제한)과 <c>SCH_JOB_LOG.ERROR_MESSAGE</c> 에는 들어가지 않는다.
    ///
    /// <para>
    /// 덤프를 <see cref="Exception.Message"/> 대신 별도 필드로 두는 이유: Message 는 브랜드 본문에 노출되고
    /// <c>ERROR_MESSAGE</c>(2000자, head-first 절단) 도 함께 쓰므로, 덤프를 Message 에 섞으면 브랜드에 원본이
    /// 새고 스택트레이스가 절단으로 밀려난다.
    /// </para>
    /// </summary>
    public string? Dump { get; init; }

    public BusinessException(string message) : base(message) { }
    public BusinessException(string message, Exception inner) : base(message, inner) { }
}
