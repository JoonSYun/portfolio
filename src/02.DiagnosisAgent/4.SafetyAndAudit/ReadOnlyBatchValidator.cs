using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 에이전트가 보낸 조회 배치를 실행 전에 검사한다.
/// <para>
/// 본 방어선은 진단 계정의 DB 권한(쓰기·프로시저 실행 거부)이다. 이 검사는 명백한 쓰기를 미리 돌려보내
/// 모델이 바로 고치게 하는 보조 장치다.
/// </para>
/// <para>
/// 잠금은 읽기 권한만으로 걸 수 있어 DB 권한이 막지 못하므로 이 검사가 유일한 방어선이다(<see cref="LockFinder"/>).
/// 그래서 실행될 SQL 이 전부 여기서 보여야 한다. 동적 SQL 은 글자 그대로 적힌 것(<c>EXEC(N'…')</c>, <c>sp_executesql N'…'</c>)만 받아
/// 안쪽도 같은 규칙으로 보고, 변수가 끼면(<c>EXEC(@sql)</c>) 실행 전에 내용을 알 수 없어 거절한다.
/// </para>
/// </summary>
public sealed class ReadOnlyBatchValidator
{
    // OPENQUERY·동적 SQL 안쪽 SQL 을 다시 검사할 때의 중첩 한도.
    private const int MaxNesting = 2;

    // 쓸 수 있는 테이블 힌트 — 잠금을 걸지 않는 것만. 허용 목록이라 여기 적기 전까지는 거부된다.
    private static readonly IReadOnlySet<TableHintKind> AllowedTableHints = new HashSet<TableHintKind>
    {
        TableHintKind.NoLock, TableHintKind.ReadUncommitted, TableHintKind.NoExpand,
        TableHintKind.Index, TableHintKind.ForceSeek, TableHintKind.ForceScan,
    };

    public BatchValidationResult Validate(string? sql) => Validate(sql, 0);

    private BatchValidationResult Validate(string? sql, int depth)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return BatchValidationResult.Reject("SQL 이 비어 있습니다.");

        var parser   = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out IList<ParseError> errors);
        if (errors.Count > 0)
            return BatchValidationResult.Reject($"구문 오류({errors[0].Line}행): {errors[0].Message}");

        // OPENROWSET/OPENDATASOURCE 는 다른 자격증명으로 다른 서버에 붙을 수 있어 읽기 전용이 깨진다.
        var remote = new RemoteSourceFinder();
        fragment.Accept(remote);
        if (remote.AdHocFound)
            return BatchValidationResult.Reject("OPENROWSET/OPENDATASOURCE 는 허용되지 않습니다.");

        // OPENQUERY 는 문자열을 원격에서 그대로 실행하므로 안쪽 SQL 도 같은 규칙으로 본다.
        // (원격 쓰기의 본 방어선은 진단 로그인의 링크드 서버 "자기 계정" 매핑이다 — 설계서 13장)
        if (CheckInner(remote.OpenQueryTexts, "OPENQUERY", depth) is { } openQueryCheck)
            return openQueryCheck;

        // 글자 그대로 적힌 동적 SQL 도 같은 규칙으로 본다.
        var dynamicSql = new DynamicSqlFinder();
        fragment.Accept(dynamicSql);
        if (CheckInner(dynamicSql.Texts, "동적 SQL", depth) is { } dynamicSqlCheck)
            return dynamicSqlCheck;

        var locks = new LockFinder();
        fragment.Accept(locks);
        if (locks.Reason is { } lockReason)
            return BatchValidationResult.Reject(lockReason);

        // 파서가 돌려주는 트리는 TSqlScript(입력 전체) → Batches(GO 로 나뉜 TSqlBatch) → Statements(TSqlStatement) 순이다.
        // 한 배치 안 문장은 세미콜론 유무와 상관없이 문법 단위로 나뉜다("SELECT 1 SELECT 2" → 문장 2개).
        // 여기서는 배치 맨 위 문장만 돌고, IF·BEGIN … END 안쪽 문장은 Check 가 재귀로 내려간다.
        foreach (var batch in ((TSqlScript)fragment).Batches)
            foreach (var statement in batch.Statements)
                if (Check(statement) is { } reason)
                    return BatchValidationResult.Reject(reason);

        return BatchValidationResult.Ok;
    }

    // 문자열로 실행되는 SQL 을 같은 규칙으로 검사해 첫 거부 결과를 돌려준다(통과면 null).
    private BatchValidationResult? CheckInner(IEnumerable<string> texts, string label, int depth)
    {
        foreach (var inner in texts)
        {
            if (depth >= MaxNesting)
                return BatchValidationResult.Reject($"{label} 중첩이 너무 깊습니다.");
            var check = Validate(inner, depth + 1);
            if (!check.IsValid)
                return BatchValidationResult.Reject($"{label} 안의 SQL — {check.Reason}");
        }
        return null;
    }

    // 문장 하나를 검사해 거부 사유를 돌려준다(통과면 null). 블록·분기 문장은 안쪽 문장까지 재귀로 내려간다.
    //
    // TSqlStatement 는 ScriptDom 구문 트리에서 "문장 하나"를 나타내는 추상 기반 클래스다.
    // 파서는 문장 종류마다 다른 하위 클래스(수백 종)를 만든다. 그래서 키워드가 아니라 타입으로 판정한다.
    //   SELECT …              → SelectStatement
    //   SET NOCOUNT ON        → PredicateSetStatement
    //   SET STATISTICS IO ON  → SetStatisticsStatement
    //   BEGIN … END           → BeginEndBlockStatement
    //   BEGIN TRAN            → BeginTransactionStatement  (BEGIN 으로 시작해도 블록이 아니다)
    // SET NOCOUNT ON 은 문장마다 클라이언트로 보내는 "(n개 행이 영향을 받음)" 메시지를 끈다. @@ROWCOUNT 는 그대로 채워진다.
    // SET STATISTICS IO ON 은 문장마다 테이블별 읽기 양(스캔 수, 논리·물리 읽기 페이지 수)을 메시지로 보낸다. 쿼리 튜닝용이다.
    // 둘 다 현재 세션의 출력만 바꾸고 데이터는 건드리지 않지만, 같은 SET 이라도 타입이 갈린다.
    // CTE(WITH … AS (…))는 따로 된 문장이 아니라 뒤 문장에 붙는다. WITH … SELECT 는 SelectStatement,
    // WITH … UPDATE 는 UpdateStatement 로 들어오므로 본문 동사 기준으로 판정된다.
    //
    // 아래 switch 는 허용 목록이다. 적은 타입만 통과하거나 안쪽까지 검사하고, 나머지는 마지막 `_` 갈래에서 전부 거부한다.
    // 패키지를 올려 새 문장 타입이 생겨도 여기 적기 전까지는 거부된다.
    // 타입 패턴은 하위 클래스도 잡지만 목록의 타입끼리는 상속 관계가 없어 갈래 순서는 결과에 영향이 없다.
    private static string? Check(TSqlStatement? s) => s switch
    {
        // ELSE 가 없는 IF 처럼 문장 자리가 비어 있는 경우.
        null => null,
        // 조회. UNION, CTE(WITH … SELECT), 변수 대입(SELECT @x = …)도 모두 SelectStatement 다.
        // SELECT ... INTO 로 새 테이블을 만들 때만 그 대상(Into)이 #임시 테이블인지 본다.
        SelectStatement sel => sel.Into is null || IsTemp(sel.Into)
            ? null : "SELECT INTO 는 #임시 테이블 대상만 허용됩니다.",
        // DB 를 바꾸지 않는 세션 범위 문장은 그대로 통과(잠금이 걸리는 값은 LockFinder 가 먼저 거부했다).
        //   DeclareVariableStatement / DeclareTableVariableStatement  DECLARE @x int = 1 / DECLARE @t TABLE (…)
        //   SetVariableStatement                                      SET @x = …
        //   PredicateSetStatement                                     ON·OFF 옵션(SET NOCOUNT·XACT_ABORT·ANSI_NULLS·SHOWPLAN_XML 등)
        //   SetTransactionIsolationLevelStatement                     SET TRANSACTION ISOLATION LEVEL …
        //   PrintStatement / UseStatement                             PRINT …, USE <DB>
        //   BreakStatement / ContinueStatement / ReturnStatement      WHILE 안 BREAK·CONTINUE, RETURN
        //   DeclareCursor·OpenCursor·FetchCursor·CloseCursor·DeallocateCursorStatement
        //                                                             DECLARE c CURSOR FOR SELECT … → OPEN → FETCH … INTO @v → CLOSE → DEALLOCATE
        // PredicateSetStatement 에 들지 않는 SET 은 옵션마다 타입이 따로라 여기 없고 거부된다
        // (SET STATISTICS IO → SetStatisticsStatement, SET ROWCOUNT → SetRowCountStatement,
        //  SET LOCK_TIMEOUT·DATEFORMAT·LANGUAGE → SetCommandStatement, SET IDENTITY_INSERT → SetIdentityInsertStatement).
        // 커서 위치로 고치는 UPDATE … WHERE CURRENT OF c 는 UpdateStatement 라 아래 대상 검사에서 걸린다.
        DeclareVariableStatement or DeclareTableVariableStatement or SetVariableStatement
            or PredicateSetStatement or SetTransactionIsolationLevelStatement or PrintStatement or UseStatement
            or BreakStatement or ContinueStatement or ReturnStatement
            or DeclareCursorStatement or OpenCursorStatement or FetchCursorStatement
            or CloseCursorStatement or DeallocateCursorStatement => null,
        // 흐름 제어 문장은 그 자체로는 무해하므로 안쪽 문장을 같은 규칙으로 검사한다.
        // IF: THEN 쪽에서 걸리면 그 사유, 아니면 ELSE 쪽.
        IfStatement i            => Check(i.ThenStatement) ?? Check(i.ElseStatement),
        // WHILE: 반복 본문.
        WhileStatement w         => Check(w.Statement),
        // BEGIN ... END: 블록 안 문장 전부.
        BeginEndBlockStatement b => CheckAll(b.StatementList),
        // BEGIN TRY ... END TRY BEGIN CATCH ... END CATCH: TRY 쪽 먼저, 그다음 CATCH 쪽.
        TryCatchStatement t      => CheckAll(t.TryStatements) ?? CheckAll(t.CatchStatements),
        // 테이블 생성은 세션이 끝나면 사라지는 #임시 테이블만.
        CreateTableStatement c   => IsTemp(c.SchemaObjectName) ? null : "CREATE TABLE 은 #임시 테이블만 허용됩니다.",
        // DROP TABLE a, b 처럼 여러 개를 지우면 전부 #임시 테이블이어야 한다. DROP TABLE IF EXISTS #t 도 같은 타입이다.
        DropTableStatement d     => d.Objects.All(IsTemp) ? null : "DROP TABLE 은 #임시 테이블만 허용됩니다.",
        // 데이터 변경(INSERT/UPDATE/DELETE/MERGE, 모두 DataModificationStatement 하위 타입)은
        // 대상이 #임시 테이블·@테이블 변수일 때만 허용한다(CheckTarget).
        // INSERT: 대상 검사에 더해, INSERT #t EXEC dbo.p 같은 INSERT ... EXEC 형태면 실행하는 쪽도 EXEC 규칙(CheckExecute)으로 본다.
        InsertStatement ins      => CheckTarget(ins.InsertSpecification.Target, "INSERT")
                                    ?? (ins.InsertSpecification.InsertSource is ExecuteInsertSource ex
                                        ? CheckExecute(ex.Execute) : null),
        // UPDATE / DELETE / MERGE: 쓰는 대상만 본다. FROM·USING 쪽 원본 테이블은 읽기라 검사하지 않는다.
        UpdateStatement up       => CheckTarget(up.UpdateSpecification.Target, "UPDATE"),
        DeleteStatement del      => CheckTarget(del.DeleteSpecification.Target, "DELETE"),
        MergeStatement m         => CheckTarget(m.MergeSpecification.Target, "MERGE"),
        // 단독 EXEC: 글자 그대로 적은 동적 SQL(EXEC(N'…'), sp_executesql N'…')만 허용. 변수가 낀 동적 SQL·일반 프로시저·EXEC ... AT 링크드서버는 거부.
        ExecuteStatement e       => CheckExecute(e.ExecuteSpecification),
        // 위 목록에 없는 문장은 허용 목록 밖이라 전부 거부. 사유에는 타입 이름에서 "Statement" 를 뺀 이름이 들어간다.
        //   BEGIN TRAN / COMMIT → BeginTransaction / CommitTransaction
        //   TRUNCATE TABLE      → TruncateTable
        //   ALTER TABLE … ADD   → AlterTableAddTableElement
        //   그 밖에 CREATE INDEX, CREATE PROCEDURE, GRANT, DBCC, WAITFOR, THROW, RAISERROR, 위의 별도 타입 SET 등.
        _ =>$"{s.GetType().Name.Replace("Statement", "")} 문은 허용되지 않습니다. "
             + "조회(SELECT)와 변수·#임시 테이블·커서·글자 그대로 적은 동적 SQL 만 쓸 수 있습니다.",
    };

    // StatementList 는 BEGIN … END·TRY·CATCH 블록에 든 문장 목록이다. 첫 거부 사유를 돌려준다(없으면 null).
    private static string? CheckAll(StatementList? list)
        => list?.Statements.Select(Check).FirstOrDefault(r => r is not null);

    private static string? CheckTarget(TableReference target, string verb) => target switch
    {
        VariableTableReference                            => null,
        NamedTableReference n when IsTemp(n.SchemaObject) => null,
        _ => $"{verb} 은 #임시 테이블이나 @테이블 변수만 대상으로 할 수 있습니다.",
    };

    // EXEC 한 건을 검사한다. 단독 EXEC 문과 INSERT ... EXEC 의 EXEC 쪽이 모두 여기로 온다.
    // LinkedServer 는 EXEC (…) AT <링크드 서버> 의 AT 대상이고, ExecutableEntity 는 실행 대상의 종류다.
    //   ExecutableStringList          EXEC (N'…') / EXEC (@sql) — 문자열을 실행하는 동적 SQL
    //   ExecutableProcedureReference  EXEC 프로시저명 … — sp_executesql 도 이 형태라 이름으로 골라낸다
    private static string? CheckExecute(ExecuteSpecification spec)
    {
        if (spec.LinkedServer is not null)
            return "EXEC ... AT 링크드서버 는 허용되지 않습니다.";
        if (spec.ExecutableEntity is ExecutableProcedureReference p && !IsSpExecuteSql(p))
            return "프로시저 실행은 허용되지 않습니다(글자 그대로 적은 EXEC(N'…')·sp_executesql N'…' 만 쓸 수 있습니다).";
        return LiteralSql(spec.ExecutableEntity) is null
            ? "변수로 만든 동적 SQL 은 실행 전에 검사할 수 없어 허용되지 않습니다. SQL 을 글자 그대로 적거나 호출을 나누세요."
            : null;
    }

    private static bool IsSpExecuteSql(ExecutableProcedureReference p)
        => p.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value is { } name
           && name.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase);

    // 동적 SQL 의 실행 문장. 변수가 끼어 글자 그대로가 아니면 null.
    private static string? LiteralSql(ExecutableEntity entity) => entity switch
    {
        // EXEC (N'…' + N'…') 는 조각이 나뉘어 들어온다.
        ExecutableStringList list when list.Strings.All(s => s is StringLiteral)
            => string.Concat(list.Strings.Cast<StringLiteral>().Select(s => s.Value)),
        // sp_executesql 의 실행 문장은 첫 인자다. 이름을 붙이면(@stmt = N'…') 순서가 바뀔 수 있다.
        ExecutableProcedureReference p when IsSpExecuteSql(p)
            => ((p.Parameters.FirstOrDefault(a => "@stmt".Equals(a.Variable?.Name, StringComparison.OrdinalIgnoreCase))
                 ?? p.Parameters.FirstOrDefault())?.ParameterValue as StringLiteral)?.Value,
        _ => null,
    };

    private static bool IsTemp(SchemaObjectName? n)
        => n?.BaseIdentifier?.Value?.StartsWith('#') == true && n.ServerIdentifier is null && n.DatabaseIdentifier is null;

    private sealed class RemoteSourceFinder : TSqlFragmentVisitor
    {
        public bool AdHocFound { get; private set; }
        public List<string> OpenQueryTexts { get; } = new();

        public override void Visit(OpenRowsetTableReference node) => AdHocFound = true;
        public override void Visit(BulkOpenRowset node)           => AdHocFound = true;
        public override void Visit(AdHocTableReference node)      => AdHocFound = true;
        public override void Visit(OpenQueryTableReference node)  => OpenQueryTexts.Add(node.Query?.Value ?? "");
    }

    // 글자 그대로 적힌 동적 SQL 을 모은다. 변수가 낀 것은 CheckExecute 가 거부한다.
    private sealed class DynamicSqlFinder : TSqlFragmentVisitor
    {
        public List<string> Texts { get; } = new();

        public override void Visit(ExecutableEntity node)
        {
            if (LiteralSql(node) is { } sql) Texts.Add(sql);
        }
    }

    // 읽기 권한만으로 잠금을 붙들 수 있는 표현을 찾는다. IF·커서·OPTION 안쪽까지 구문 트리 전체를 돈다.
    //   TableHint                              WITH (TABLOCKX), 옛 표기 t (TABLOCKX), OPTION (TABLE HINT(t, TABLOCKX))
    //   SetTransactionIsolationLevelStatement  REPEATABLE READ·SERIALIZABLE 은 읽은 범위에 공유 잠금을 쥔다
    //   PredicateSetStatement                  IMPLICIT_TRANSACTIONS ON 이면 SELECT 만으로 트랜잭션이 열린 채 끝난다. ANSI_DEFAULTS ON 도 이것을 켠다
    //   CursorDefinition                       SCROLL_LOCKS 는 읽은 줄을 잠근다. DECLARE c CURSOR 와 SET @c = CURSOR 가 모두 여기로 온다
    private sealed class LockFinder : TSqlFragmentVisitor
    {
        public string? Reason { get; private set; }

        public override void Visit(TableHint node)
        {
            if (!AllowedTableHints.Contains(node.HintKind))
                Reason ??= $"{node.HintKind} 힌트는 허용되지 않습니다(NOLOCK·READUNCOMMITTED·NOEXPAND·INDEX·FORCESEEK·FORCESCAN 만 쓸 수 있습니다).";
        }

        public override void Visit(SetTransactionIsolationLevelStatement node)
        {
            if (node.Level is not (IsolationLevel.ReadUncommitted or IsolationLevel.ReadCommitted or IsolationLevel.Snapshot))
                Reason ??= "격리 수준은 READ UNCOMMITTED·READ COMMITTED·SNAPSHOT 만 쓸 수 있습니다.";
        }

        public override void Visit(PredicateSetStatement node)
        {
            if (node.IsOn && (node.Options & (SetOptions.ImplicitTransactions | SetOptions.AnsiDefaults)) != 0)
                Reason ??= "IMPLICIT_TRANSACTIONS·ANSI_DEFAULTS 는 켤 수 없습니다(트랜잭션이 열린 채 끝납니다).";
        }

        public override void Visit(CursorDefinition node)
        {
            if (node.Options.Any(o => o.OptionKind == CursorOptionKind.ScrollLocks))
                Reason ??= "SCROLL_LOCKS 커서는 허용되지 않습니다.";
        }
    }
}

public sealed record BatchValidationResult(bool IsValid, string? Reason)
{
    public static readonly BatchValidationResult Ok = new(true, null);
    public static BatchValidationResult Reject(string reason) => new(false, reason);
}
