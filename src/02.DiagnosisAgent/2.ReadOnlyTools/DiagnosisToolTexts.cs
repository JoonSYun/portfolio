namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 조회 도구 설명 문구 — 진단 에이전트(<see cref="DiagnosisTools"/>)와 MCP(<c>Mcp/DiagnosisMcpTools</c>)가 같이 쓴다.
/// 에이전트 쪽 문구를 바꾸면 기존 진단·대화 화면 프롬프트도 바뀐다.
/// </summary>
public static class DiagnosisToolTexts
{
    // ── 도구 설명 ─────────────────────────────────────────────

    public const string FindFiles =
        "소스 복사본에서 파일 이름(또는 경로) 패턴으로 파일을 찾는다. 화면코드로 폼 파일을 찾을 때 쓴다(예: frmSCM00007*). "
        + "와일드카드 * ? 를 쓰고, '/' 가 들어가면 경로 전체와 비교한다(** 는 폴더 여러 단계).";

    public const string SearchCode =
        "소스 복사본에서 정규식(.NET)으로 줄 단위 검색한다. 결과는 '저장소/경로:줄: 내용'. 한글 그대로 찾을 수 있다. "
        + "결과가 많으면 repo·path_glob 으로 좁힌다.";

    public const string ReadFile =
        "소스 파일의 줄 범위를 줄 번호와 함께 읽는다. path 는 find_files·search_code 결과의 경로를 그대로 써도 된다.";

    public const string SearchDbObjects =
        "DB 의 프로시저·뷰·함수·트리거 본문에서 키워드를 찾는다. 오류 문구(RAISERROR 메시지)나 테이블·열 이름으로 찾을 때 쓴다. "
        + "database 를 비우면 그 서버의 접근 가능한 모든 DB 를 찾는다. 결과는 객체 목록이고 본문은 read_db_object 로 읽는다.";

    public const string ReadDbObject =
        "프로시저·뷰·함수·트리거 정의를 줄 번호와 함께 읽는다. 생성·수정일도 함께 나온다.";

    public const string RunQuery =
        "읽기 전용 SQL 배치를 실행한다. SELECT·변수·#임시 테이블·커서·글자 그대로 적은 동적 SQL 을 쓸 수 있고, "
        + "실제 테이블 쓰기·프로시저 실행·변수로 만든 동적 SQL·잠금 힌트·격리 수준 상향은 거절된다. "
        + "세션이 READ UNCOMMITTED 로 열려 있어 NOLOCK 을 따로 붙이지 않아도 된다. 결과 집합 5개, 각 200행, 30초까지. "
        + "필요한 열만 고르고, 키 조건으로 좁혀 조회한다. 개인정보(이름·주소·연락처) 열은 조회하지 않는다.";

    public const string SearchLogs =
        "이 관리자 서버의 Seq 에서 애플리케이션 로그(이 서버에 깔린 관리자·통합 스케줄러·연동 서비스 등, Information 이상)를 찾는다. "
        + "예외 스택·외부 호출 실패처럼 코드·DB 에 남지 않는 근거를 볼 때 쓴다. 대상 환경(운영/테스트)과 상관없이 이 서버의 로그이고 결과 첫 줄에 출처가 나온다. "
        + "결과는 건마다 시각(KST)·레벨·서비스·SourceContext·LogId·메시지·예외 앞부분이고, 받은 범위 안에서 시각 순이다.";

    // ── 인자 설명 ─────────────────────────────────────────────

    public const string RepoArg          = "저장소 이름(생략하면 전체)";
    public const string ServerArg        = "wms=WMS 업무 인스턴스, if=IF 인스턴스";
    public const string StartLineArg     = "시작 줄(기본 1)";
    public const string EndLineArg       = "끝 줄(생략하면 시작부터 200줄, 한 번에 최대 400줄)";
    public const string NamePatternArg   = "예: frmSCM00007*, *.asmx, WmsClient/**/frm*.cs";
    public const string PatternArg       = "정규식. 특수문자를 그대로 찾으려면 \\ 로 이스케이프";
    public const string PathGlobArg      = "경로 패턴. 예: *.cs, *.Designer.cs, WmsClient/**";
    public const string IgnoreCaseArg    = "대소문자 무시(기본 false)";
    public const string ReadRepoArg      = "저장소 이름";
    public const string PathArg          = "저장소 안 경로";
    public const string DatabaseAllArg   = "DB 이름(생략하면 전체). 예: WmsMain";
    public const string KeywordArg       = "2글자 이상. 그대로(부분 일치) 찾는다";
    public const string DatabaseArg      = "DB 이름. 예: WmsMain";
    public const string ObjectNameArg    = "객체 이름. 예: usp_Outbound_PackingList_Get 또는 dbo.usp_...";
    public const string QueryDatabaseArg = "기본 DB. 예: WmsMain";
    public const string SqlArg           = "T-SQL 배치";
    public const string LogFilterArg     = "Seq 필터식(생략하면 전체). 문자열은 작은따옴표. 예: LogId = 'abc', @Level = 'Error', Has(@Exception), @Message like '%주문%'";
    public const string LogFromArg       = "시작 시각(KST, yyyy-MM-dd HH:mm:ss). 생략하면 끝 시각 24시간 전";
    public const string LogToArg         = "끝 시각(KST, yyyy-MM-dd HH:mm:ss). 생략하면 지금";
    public const string LogCountArg      = "최대 건수(기본 50, 최대 200). 넘으면 최근 것부터";

    // ── MCP 전용 인자 설명(에이전트는 실행이 환경을 정하고, types 는 스키마 enum 으로 보여 준다) ──

    public const string TypesArg = "procedure·view·function·trigger 중에서 고른다(생략하면 전부)";
    public const string EnvArg   = "prod(운영, 기본) 또는 test(테스트). 사용자가 테스트 서버를 말하지 않으면 생략한다";

    public const string RequestIdArg = "start_request 가 돌려준 요청 ID";
}
