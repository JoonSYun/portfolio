namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 도구가 모델에게 돌려줄 "예상된" 오류. <c>BetaToolRunner</c> 가 예외 메시지를 그대로 오류 결과(is_error)로
/// 돌려주므로, 모델이 읽고 방법을 바꿀 수 있게 무엇이 틀렸고 어떻게 하면 되는지를 한국어로 적는다.
/// 이 타입 자체는 요청이 틀려 서버가 거절한 경우다(MCP 호출 기록의 REJECTED).
/// </summary>
public class DiagnosisToolException(string message) : Exception(message);

/// <summary>
/// 요청이 아니라 서버·대상 쪽 문제로 실행하지 못한 경우 — DB 연결·SQL 실행 오류, Seq 오류·시간 초과, 서버 설정 누락, 소스 동기화 전.
/// 모델에게 돌려주는 방식은 <see cref="DiagnosisToolException"/> 과 같고, MCP 호출 기록에서만 FAILED 로 가른다.
/// </summary>
public sealed class DiagnosisToolFailureException(string message) : DiagnosisToolException(message);
