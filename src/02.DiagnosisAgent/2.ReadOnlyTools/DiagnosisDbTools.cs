using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using Portfolio.Admin.Infrastructure.SystemMonitor;
using Portfolio.DiagnosisAgent.Models;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 진단 에이전트의 DB 도구. 진단 전용 읽기 계정 연결(<c>Diag_WMS_*</c> / <c>Diag_IF_*</c>)만 쓴다.
/// 어느 환경(_TEST/_PROD)으로 붙을지는 실행이 정하고, 모델은 server(wms/if)만 고른다.
/// 쓰기·프로시저 실행 차단은 그 계정의 DB 권한이 맡고, 조회 배치는 실행 전에 <see cref="ReadOnlyBatchValidator"/> 로 한 번 더 거른다.
/// 결과는 모델이 읽을 텍스트로 돌려준다.
/// <para>
/// 이 파일이 맡는 보안 장치:
///   연결    — 모델은 server(wms/if)만 고른다. 연결은 읽기 전용 로그인 4개 중 하나로만 정해진다(<see cref="ConnectionName"/>).
///   실행 전 — run_query 는 <see cref="ReadOnlyBatchValidator"/> 를 통과해야 DB 에 연결한다.
///   실행 중 — 세션 READ UNCOMMITTED, 시간 상한, 결과 집합·행·칸 길이 상한.
///   결과    — 비밀값 열은 칸째, 그 밖 값·메시지·SQL 오류·객체 본문은 <see cref="SecretMasker"/> 로 가린다.
/// </para>
/// </summary>
public sealed class DiagnosisDbTools
{
    // 상한. run_query 도구 설명(DiagnosisToolTexts.RunQuery)에 "결과 집합 5개, 각 200행, 30초" 가 글로 적혀 있어 값을 바꾸면 그 문구도 고친다.
    public const int MaxResultSets      = 5;             // 결과 집합 수. 넘으면 나머지는 읽지 않고 "생략" 으로 적는다
    public const int MaxRows            = 200;           // 결과 집합마다 모델에 보이는 행 수
    public const int MaxValueChars      = 200;           // 칸 하나의 글자 수(가린 뒤에 자른다)
    public const int QueryTimeoutSec    = 30;            // run_query 실행 시간
    private const int CatalogTimeoutSec = 60;            // 카탈로그 조회(DB 목록·객체 검색·정의 읽기) 명령마다
    private const int MaxObjectResults  = 100;           // search_db_objects 에 보이는 줄 수(전체 건수는 머리줄에 따로 적는다)
    private const int MaxCountedRows    = MaxRows * 50;  // 보이는 행 뒤로는 세기만 하다가 이 수(1만)에서 읽기를 멈추고 "n행 이상" 으로 적는다

    // 모델이 보낸 종류 이름 → sys.objects.type. 문자열이 SQL 로 흘러가지 않게 이 사전을 거친다.
    private static readonly IReadOnlyDictionary<string, string[]> TypeMap =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["procedure"] = ["P"],
            ["view"]      = ["V"],
            ["function"]  = ["FN", "IF", "TF"],
            ["trigger"]   = ["TR"],
        };

    // 아래 카탈로그 쿼리 셋은 서버 코드에 고정된 SQL 이라 검사기를 거치지 않는다. 모델 입력은 매개변수로만 들어간다.
    // 세션 격리 수준을 바꾸는 건 run_query 뿐이라 여기서는 NOLOCK 을 직접 붙인다.

    // 객체 검색에서 database 를 비웠을 때 돌 DB 목록 — 시스템 DB(id 1~4: master·tempdb·model·msdb) 제외,
    // ONLINE 이고 진단 로그인이 들어갈 수 있는(HAS_DBACCESS) DB 만.
    private const string DatabasesSql = @"
SELECT name FROM sys.databases WITH (NOLOCK)
WHERE database_id > 4 AND state_desc = N'ONLINE' AND HAS_DBACCESS(name) = 1
ORDER BY name;";

    // 본문(sys.sql_modules.definition)에 키워드가 든 객체. 검색어·LIKE 패턴·종류 코드는 모두 매개변수다.
    // MatchCount = (본문 길이 − 키워드를 지운 본문 길이) / 키워드 길이 = 본문 안 등장 횟수.
    // LIKE 의 % _ [ 는 ToLikePattern 이 \ 로 무력화하고 ESCAPE '\' 로 받는다.
    private const string SearchSql = @"
SELECT TOP (100)
    s.name          AS SchemaName,
    o.name          AS Name,
    RTRIM(o.type)   AS ObjectType,
    o.modify_date   AS ModifiedAt,
    CAST((DATALENGTH(m.definition) - DATALENGTH(REPLACE(m.definition, @keyword, N'')))
         / NULLIF(DATALENGTH(@keyword), 0) AS int) AS MatchCount
FROM sys.sql_modules m WITH (NOLOCK)
JOIN sys.objects o WITH (NOLOCK) ON o.object_id = m.object_id
JOIN sys.schemas s WITH (NOLOCK) ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 0
  AND o.type IN @types
  AND m.definition LIKE @pattern ESCAPE '\'
ORDER BY o.name;";

    // 객체 하나의 정의. 이름 해석은 OBJECT_ID 가 연결의 현재 DB 기준으로 한다("usp_x"·"dbo.usp_x"·"[dbo].[usp_x]").
    // sql_modules 를 LEFT JOIN 해 모듈이 없는 객체(테이블)도 행이 나오게 하고, 테이블·본문 NULL(암호화 등)은 부르는 쪽에서 가른다.
    private const string DefinitionSql = @"
SELECT
    s.name          AS SchemaName,
    o.name          AS Name,
    RTRIM(o.type)   AS ObjectType,
    o.type_desc     AS ObjectTypeDesc,
    o.create_date   AS CreatedAt,
    o.modify_date   AS ModifiedAt,
    m.definition    AS Definition
FROM sys.objects o WITH (NOLOCK)
JOIN sys.schemas s WITH (NOLOCK) ON s.schema_id = o.schema_id
LEFT JOIN sys.sql_modules m WITH (NOLOCK) ON m.object_id = o.object_id
WHERE o.object_id = OBJECT_ID(@name);";

    private readonly IConfiguration         _configuration;
    private readonly SqlIdentifierValidator _validator;
    private readonly ReadOnlyBatchValidator _batchValidator;

    public DiagnosisDbTools(IConfiguration configuration, SqlIdentifierValidator validator, ReadOnlyBatchValidator batchValidator)
    {
        _configuration  = configuration;
        _validator      = validator;
        _batchValidator = batchValidator;
    }

    /// <summary>프로시저·뷰·함수·트리거 본문에서 키워드를 찾는다. database 가 비면 접근 가능한 모든 DB.</summary>
    public async Task<string> SearchObjectsAsync(
        string env, string server, string? database, string keyword, IReadOnlyList<string>? types, CancellationToken ct)
    {
        var trimmed = keyword?.Trim() ?? "";
        if (trimmed.Length < 2) throw new DiagnosisToolException("keyword 는 2글자 이상이어야 합니다.");
        var typeCodes = ResolveTypes(types);

        await using var conn = await OpenAsync(env, server, null, ct);
        var databases = string.IsNullOrWhiteSpace(database)
            ? (await conn.QueryAsync<string>(new CommandDefinition(DatabasesSql, commandTimeout: CatalogTimeoutSec, cancellationToken: ct))).ToList()
            : [database!];

        // 연결 하나로 DB 를 옮겨 가며 찾는다. 들어갈 수 없는 DB(직접 지정한 경우)는 실패 줄만 적고 다음 DB 로 넘어간다.
        var lines = new List<string>();
        var total = 0;
        foreach (var db in databases)
        {
            if (!_validator.IsValidSysname(db))
                throw new DiagnosisToolException($"DB 이름 형식이 아닙니다: {db}");
            try
            {
                conn.ChangeDatabase(db);
            }
            catch (SqlException ex)
            {
                lines.Add($"[{db}] 접근 실패: {ex.Message}");
                continue;
            }

            var rows = await conn.QueryAsync<ObjectMatchRow>(new CommandDefinition(SearchSql,
                new { keyword = trimmed, pattern = ToLikePattern(trimmed), types = typeCodes },
                commandTimeout: CatalogTimeoutSec, cancellationToken: ct));
            foreach (var r in rows)
            {
                total++;
                if (lines.Count < MaxObjectResults)
                    lines.Add($"[{db}] {r.SchemaName}.{r.Name} ({r.ObjectType}) 수정 {r.ModifiedAt:yyyy-MM-dd HH:mm} · 일치 {r.MatchCount ?? 0}회");
            }
        }

        if (total == 0)
            return $"'{trimmed}' 가 들어간 객체가 없습니다({string.Join(", ", databases)}).\n" + string.Join("\n", lines);
        var head = total > MaxObjectResults ? $"{total}개 (앞 {MaxObjectResults}개만 표시 — database·types 로 좁히세요)" : $"{total}개";
        return head + "\n" + string.Join("\n", lines) + "\n(본문은 read_db_object 로 읽으세요)";
    }

    /// <summary>객체 정의를 줄 번호와 함께 읽는다. name 은 "usp_x", "dbo.usp_x", "[dbo].[usp_x]" 모두 된다.</summary>
    public async Task<string> ReadObjectAsync(
        string env, string server, string database, string name, int startLine, int? endLine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 300)
            throw new DiagnosisToolException("name 이 비어 있거나 너무 깁니다.");

        await using var conn = await OpenAsync(env, server, database, ct);
        var row = await conn.QuerySingleOrDefaultAsync<ObjectDefinitionRow>(new CommandDefinition(
            DefinitionSql, new { name }, commandTimeout: CatalogTimeoutSec, cancellationToken: ct));

        if (row is null)
            throw new DiagnosisToolException($"{database} 에서 '{name}' 을(를) 찾을 수 없습니다. search_db_objects 로 이름을 확인하세요.");
        if (row.ObjectType == "U")
            return $"{database}.{row.SchemaName}.{row.Name} 은(는) 테이블입니다. 열 목록은 run_query 로 sys.columns 를 조회하세요.";
        if (row.Definition is null)
            return $"{database}.{row.SchemaName}.{row.Name} 은(는) 암호화되어 있거나 정의가 없어 볼 수 없습니다.";

        var title = $"{database}.{row.SchemaName}.{row.Name} ({row.ObjectTypeDesc}, 생성 {row.CreatedAt:yyyy-MM-dd}, 수정 {row.ModifiedAt:yyyy-MM-dd HH:mm})";
        // SP 본문의 링크드 서버·OPENROWSET 자격증명 등은 가린다(SecretMasker 는 줄 수를 바꾸지 않아 줄 번호가 그대로다).
        return DiagnosisTextFormat.NumberedSlice(title, DiagnosisTextFormat.SplitLines(SecretMasker.Mask(row.Definition)), startLine, endLine);
    }

    /// <summary>읽기 배치를 실행한다. 세션은 READ UNCOMMITTED(NOLOCK 수준)로 시작한다.</summary>
    public async Task<string> RunQueryAsync(string env, string server, string database, string sql, CancellationToken ct)
    {
        // 검사를 통과하기 전에는 연결하지 않는다 — 거절된 SQL 은 DB 서버에 닿지 않는다.
        var check = _batchValidator.Validate(sql);
        if (!check.IsValid) throw new DiagnosisToolException($"실행하지 않았습니다 — {check.Reason}");

        await using var conn = await OpenAsync(env, server, database, ct);
        // PRINT·정보 메시지(심각도 10 이하)는 예외가 아니라 이 이벤트로 온다. 50개까지 모아 결과 끝에 붙인다(가림은 FormatQueryResult).
        var messages = new List<string>();
        conn.InfoMessage += (_, e) =>
        {
            lock (messages)
                if (messages.Count < 50) messages.Add(e.Message);
        };

        // 사용자 배치보다 먼저 세션 격리 수준을 낮춘다. 공유 잠금을 잡지 않아 운영 업무를 막지도, 막히지도 않는다(대신 커밋 전 값이 보일 수 있다).
        // 세션 설정이라 뒤따르는 배치 전체(글자 그대로 적은 동적 SQL 안쪽 포함)의 모든 테이블에 걸린다.
        // 테이블 힌트는 이 설정보다 우선해 잠금을 다시 걸 수 있으므로, 잠금 힌트·격리 수준 상향은 검사기(LockFinder)가 거절한다.
        await using (var iso = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", conn))
            await iso.ExecuteNonQueryAsync(ct);

        var sets     = new List<QueryResultSet>();
        var moreSets = false;
        try
        {
            // 시간 상한을 넘으면 SqlException(시간 초과)으로 끝나 아래 catch 가 오류 문구로 돌려준다.
            await using var cmd    = new SqlCommand(sql, conn) { CommandTimeout = QueryTimeoutSec };
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            do
            {
                // 결과 집합이 없는 문장(SET·DECLARE·INSERT #임시 테이블 등)은 건너뛴다.
                if (reader.FieldCount == 0) continue;
                if (sets.Count == MaxResultSets)
                {
                    moreSets = true;
                    break;
                }

                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                // 비밀값 열 판정은 열 이름으로, 결과 집합마다 한 번.
                var secret  = columns.Select(SecretMasker.IsSecretColumn).ToArray();
                var rows    = new List<string?[]>();
                var count   = 0;
                var capped  = false;
                while (await reader.ReadAsync(ct))
                {
                    // 앞 MaxRows 행만 값을 담고, 그 뒤는 세기만 하다가 MaxCountedRows 에서 멈춘다.
                    count++;
                    if (rows.Count < MaxRows)
                    {
                        var values = new string?[reader.FieldCount];
                        for (var i = 0; i < values.Length; i++)
                            values[i] = reader.IsDBNull(i) ? null : FormatCell(reader.GetValue(i), secret[i]);
                        rows.Add(values);
                    }
                    else if (count >= MaxCountedRows)
                    {
                        capped = true;
                        break;
                    }
                }
                sets.Add(new QueryResultSet(columns, rows, count, capped));
            }
            while (await reader.NextResultAsync(ct));
        }
        catch (SqlException ex)
        {
            // 변환 오류 메시지 등에 값이 실린다(SELECT CAST(col AS int) → "… 'abc' 를 int 로 …") — 가린다.
            throw new DiagnosisToolFailureException($"SQL 오류: {SecretMasker.Mask(ex.Message)}");
        }

        lock (messages)
            return DiagnosisTextFormat.FormatQueryResult(sets, messages.ToList(), moreSets);
    }

    // ── 내부 ──────────────────────────────────────────────────

    /// <summary>
    /// (대상 환경, server) → 진단 연결 이름. env 는 실행 시작 때 검사를 마친 값이다.
    /// 네 연결 모두 읽기 전용 로그인 — 쓰기·프로시저 실행 차단의 본 방어선이다.
    /// 목록 밖 조합은 null 이라 거절된다. 모델은 server 만 고르므로 연결 문자열·계정에는 손댈 수 없다.
    /// </summary>
    private static string? ConnectionName(string env, string? server) => (env, server?.ToLowerInvariant()) switch
    {
        (DiagnosisEnvs.Test, "wms") => "Diag_WMS_TEST",
        (DiagnosisEnvs.Test, "if")  => "Diag_IF_TEST",
        (DiagnosisEnvs.Prod, "wms") => "Diag_WMS_PROD",
        (DiagnosisEnvs.Prod, "if")  => "Diag_IF_PROD",
        _                           => null,
    };

    private async Task<SqlConnection> OpenAsync(string env, string server, string? database, CancellationToken ct)
    {
        var csName = ConnectionName(env, server)
            ?? throw new DiagnosisToolException("server 는 'wms' 또는 'if' 여야 합니다.");
        var cs = _configuration.GetConnectionString(csName);
        if (string.IsNullOrWhiteSpace(cs))
            throw new DiagnosisToolFailureException($"진단 DB 연결({csName})이 설정되지 않았습니다.");
        // DB 이름은 모델이 보낸 값이라 연결 전에 SQL 식별자 형식인지 거른다.
        if (!string.IsNullOrWhiteSpace(database) && !_validator.IsValidSysname(database))
            throw new DiagnosisToolException($"DB 이름 형식이 아닙니다: {database}");

        var conn = new SqlConnection(cs);
        try
        {
            await conn.OpenAsync(ct);
            if (!string.IsNullOrWhiteSpace(database)) conn.ChangeDatabase(database);
            return conn;
        }
        catch (SqlException ex)
        {
            await conn.DisposeAsync();
            throw new DiagnosisToolFailureException($"DB 연결 실패({server}/{database}): {ex.Message}");
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static string[] ResolveTypes(IReadOnlyList<string>? types)
    {
        if (types is null || types.Count == 0) return TypeMap.Values.SelectMany(v => v).ToArray();
        var codes = new List<string>();
        foreach (var t in types)
        {
            if (!TypeMap.TryGetValue(t, out var c))
                throw new DiagnosisToolException($"types 는 procedure, view, function, trigger 중에서 고르세요: {t}");
            codes.AddRange(c);
        }
        return codes.Distinct().ToArray();
    }

    /// <summary>키워드를 LIKE 리터럴로 감싼다. % _ [ 는 ESCAPE 문자로 무력화(그대로 찾기).</summary>
    private static string ToLikePattern(string keyword)
    {
        var escaped = keyword.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
        return $"%{escaped}%";
    }

    /// <summary>
    /// 결과 칸 하나. 비밀값 열(<see cref="SecretMasker.IsSecretColumn"/>)의 문자열·이진값은 칸째 가린다 —
    /// 열에 든 키 값은 패턴이 없어 <see cref="SecretMasker.Mask"/> 로는 못 잡는다.
    /// 빈 값·한 글자 값(Y/N)·날짜·숫자는 진단 근거라 남긴다(NULL 은 부르는 쪽이 따로 적는다).
    /// </summary>
    internal static string FormatCell(object value, bool secretColumn)
        => secretColumn && value is string { Length: > 1 } or byte[] { Length: > 0 } ? SecretMasker.Mark : FormatValue(value);

    private static string FormatValue(object value)
    {
        var text = value switch
        {
            DateTime d       => d.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
            byte[] b         => "0x" + Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 100))) + (b.Length > 100 ? "…" : ""),
            _                => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
        // 가린 뒤에 자른다 — 자른 뒤 가리면 잘린 끝에 걸친 비밀값이 패턴에서 빠진다.
        text = SecretMasker.Mask(text);
        return text.Length <= MaxValueChars ? text : text[..MaxValueChars] + "…";
    }

    private sealed class ObjectMatchRow
    {
        public string   SchemaName { get; set; } = "";
        public string   Name       { get; set; } = "";
        public string   ObjectType { get; set; } = "";
        public DateTime ModifiedAt { get; set; }
        public int?     MatchCount { get; set; }
    }

    private sealed class ObjectDefinitionRow
    {
        public string   SchemaName     { get; set; } = "";
        public string   Name           { get; set; } = "";
        public string   ObjectType     { get; set; } = "";
        public string   ObjectTypeDesc { get; set; } = "";
        public DateTime CreatedAt      { get; set; }
        public DateTime ModifiedAt     { get; set; }
        public string?  Definition     { get; set; }
    }
}
