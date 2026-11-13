using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// Admin "Run now" 진입점에서 사용자가 선택하는 실행 모드.
/// JSON 직렬화는 camelCase ("test"/"operational") — <see cref="RunAsModeConverter"/> 가 보장.
///
/// 실효 IsTest 결정 규칙:
///   - DB 측 <see cref="TargetRow.IsTest"/> == true 면 항상 test 강제 (mode 무시).
///   - DB 측 false 일 때만 사용자 선택이 의미를 가진다.
///   - DB=Y && mode=Operational 조합은 컨트롤러에서 400 으로 반려 (UI 가 보낸 잘못된 선택지 방어).
/// </summary>
[JsonConverter(typeof(RunAsModeConverter))]
public enum RunAsMode
{
    Test,
    Operational,
}

/// <summary>
/// .NET 8 에서 enum 멤버 이름을 camelCase 로 직렬화하기 위한 컨버터. <see cref="JsonStringEnumMemberNameAttribute"/>
/// 는 .NET 9+ 라 사용 불가 — JsonNamingPolicy.CamelCase 로 동일 효과 (Test→"test", Operational→"operational").
/// </summary>
public sealed class RunAsModeConverter : JsonStringEnumConverter<RunAsMode>
{
    public RunAsModeConverter() : base(JsonNamingPolicy.CamelCase) { }
}

/// <summary>
/// "Run now" 컨트롤러의 body. <see cref="RunAsMode"/> 가 누락되면 ModelState 검증에서 400.
/// </summary>
public class RunNowRequest
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "runAs is required ('test' or 'operational').")]
    public RunAsMode? RunAs { get; set; }
}
