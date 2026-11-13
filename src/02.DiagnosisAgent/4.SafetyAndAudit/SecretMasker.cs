using System.Text;
using System.Text.RegularExpressions;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 진단 도구가 모델에 내보내는 텍스트의 비밀값(비밀번호·API 키·토큰)을 가린다
/// (docs/DIAGNOSIS_SECRET_MASKING_DESIGN_2026-11.md 4장).
/// <para>
/// 줄 단위로 처리하고 줄바꿈 문자를 그대로 이어 붙여 줄 수를 바꾸지 않는다 — 보고서의 줄 번호가 GitHub 원본과 맞아야 한다.
/// 개인키 블록과 설정 파일의 <c>&lt;setting&gt;</c>→<c>&lt;value&gt;</c> 만 줄을 넘는 상태를 갖는다.
/// 서버·DB 이름, 사용자 이름, 키 이름, 빈 값은 진단 근거라 남긴다. 이미 가린 값은 그대로라 여러 번 적용해도 결과가 같다.
/// </para>
/// <para>
/// 정규식은 모두 NonBacktracking 이다 — 2MB 파일에서도 입력 길이에 비례한 시간에 끝난다.
/// 규칙을 바꾸면 <c>MirrorBuilder.MaskVersion</c> 을 올린다(올려야 기존 소스 복사본이 다시 만들어진다).
/// </para>
/// </summary>
public static class SecretMasker
{
    public const string Mark = "***(가림)";

    /// <summary>코드 대입 형태는 이 길이 이상만 가린다 — 상수·화면 문구(<c>TokenPrefix = "wsd_"</c>) 오탐을 줄인다.</summary>
    private const int MinCodeLiteralChars = 8;

    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>키 이름에 이 단어가 들어 있으면 비밀값으로 본다(규칙 2).</summary>
    private const string SecretWord =
        "password|passwd|pwd|secret|apikey|api_key|api-key|accesskey|access_key|privatekey|private_key|token";

    // 규칙 3: 위치·키 이름과 상관없이 형태만으로 알아보는 토큰.
    private static readonly Regex KnownToken = new(
        @"github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{36}|sk-ant-[A-Za-z0-9_-]{20,}"
        + @"|(?:AKIA|ASIA)[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{35}", Options);

    // 규칙 5: URL 자격증명 — scheme://사용자:비밀번호@호스트 의 비밀번호. 사용자 이름·호스트는 남긴다.
    //   비밀번호에 @ 가 들어 있을 수 있어 경로(/) 앞 마지막 @ 까지를 값으로 본다.
    private static readonly Regex UrlCredential = new(
        @"(?<pre>\b[a-z][a-z0-9+.\-]*://[^/\s:@""'<>]*:)(?<val>[^/\s""'<>]+)@", Options);

    // 규칙 1: 연결 문자열·대입의 Password·Pwd 값.
    //   따옴표 값은 따옴표 안을 가리고 앞 접두사(T-SQL N'…', C# @"…")는 남긴다(QuotedPassword).
    //   따옴표 없는 값은 ; " ' < 앞까지. 첫 글자로 = 를 받지 않아 비교식(pwd == …)은 건드리지 않는다.
    private const string PasswordKey = @"(?<key>(?:password|pwd)\s*=\s*)";
    private static readonly Regex PasswordSingleQuoted = new(PasswordKey + @"(?<pfx>[N@$]{0,2})'(?<val>[^'\r\n]*)'", Options);
    private static readonly Regex PasswordDoubleQuoted = new(PasswordKey + @"(?<pfx>[N@$]{0,2})""(?<val>[^""\r\n]*)""", Options);
    private static readonly Regex PasswordBare         = new(PasswordKey + @"(?<val>[^;""'<=\s][^;""'<\r\n]*)", Options);

    // 규칙 2 JSON: "…비밀 단어…": "값" — 값 안의 이스케이프(\")까지 한 값으로 본다.
    private static readonly Regex JsonSecret = new(
        @"(?<pre>""[^""\r\n]*(?:" + SecretWord + @")[^""\r\n]*""\s*:\s*"")(?<val>(?:[^""\\\r\n]|\\.)*)""", Options);

    // 규칙 2 XML: key="…비밀 단어…" value="값"
    private static readonly Regex XmlSecret = new(
        @"(?<pre>key=""[^""\r\n]*(?:" + SecretWord + @")[^""\r\n]*""\s+value="")(?<val>[^""\r\n]*)""", Options);

    // 규칙 2 코드: 비밀 단어가 든 식별자 [As 타입] = 또는 : 뒤의 문자열 리터럴. ==(비교)는 = 뒤에 따옴표가 아니라 걸리지 않는다.
    private static readonly Regex CodeSecret = new(
        @"(?<pre>\b[A-Za-z0-9_]*(?:" + SecretWord + @")[A-Za-z0-9_]*(?:\s+As\s+[A-Za-z0-9_.]+)?\s*[:=]\s*@?)"
        + @"(?:""(?<dv>[^""\r\n]*)""|'(?<sv>[^'\r\n]*)')", Options);

    // 규칙 2 설정 파일: <setting name="…비밀 단어…"> 뒤 첫 <value>…</value> (App.config·.settings 는 값이 다음 줄에 온다).
    private static readonly Regex SecretSetting = new(@"<setting\s+name=""[^""\r\n]*(?:" + SecretWord + @")[^""\r\n]*""", Options);
    private static readonly Regex SettingValue  = new(@"<value>(?<val>[^<\r\n]*)</value>", Options);
    private static readonly Regex SettingEnd    = new(@"</setting>", Options);

    // 규칙 4: 개인키 블록 — 경계 표시는 남기고 그 사이를 가린다(여러 줄이면 줄마다, 한 줄이면 사이 전체).
    private static readonly Regex PrivateKeyBegin = new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", Options);
    private static readonly Regex PrivateKeyEnd   = new(@"-----END [A-Z ]*PRIVATE KEY-----", Options);

    // run_query 결과 열 이름 — 이름에 이 단어가 들어간 열의 값은 칸째 가린다(설계서 6장).
    private static readonly Regex SecretColumnName = new(
        "password|passwd|pwd|token|apikey|api_key|secret|authkey|auth_key|인증키|비밀번호|암호", Options);

    /// <summary>이름은 걸리지만 비밀값이 아닌 열 — LLM 토큰 사용량, 관리 화면에 보이는 토큰 앞자리, 만료 시각.</summary>
    private static readonly IReadOnlySet<string> NonSecretColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "TokenPrefix", "RefreshTokenExpiryTime",
    };

    /// <summary>
    /// 조회 결과 열이 비밀값 열인지 — 열 이름으로만 본다. 키 값은 패턴이 없어 <see cref="Mask"/> 로는 못 잡기 때문이다.
    /// 별칭으로 이름을 바꾼 열(<c>API_KEY AS k</c>)·이름 없는 계산 열은 알아보지 못한다(사용자가 모두 관리자라는 전제로 받아들임).
    /// </summary>
    public static bool IsSecretColumn(string columnName)
        => SecretColumnName.IsMatch(columnName) && !NonSecretColumns.Contains(columnName);

    public static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb             = new StringBuilder(text.Length);
        var inKey          = false;
        var pendingSetting = false;
        var start          = 0;
        while (true)
        {
            var nl         = text.IndexOf('\n', start);
            var lineEnd    = nl < 0 ? text.Length : nl;
            var contentEnd = lineEnd > start && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            var line       = text[start..contentEnd];

            if (inKey) line = MaskKeyBody(line, out inKey);
            else if (PrivateKeyBegin.Match(line) is { Success: true } begin) line = MaskKeyStart(line, begin, out inKey);
            else line = MaskSettingValue(MaskLine(line), ref pendingSetting);

            sb.Append(line).Append(text, contentEnd, (nl < 0 ? text.Length : nl + 1) - contentEnd);
            if (nl < 0) return sb.ToString();
            start = nl + 1;
        }
    }

    private static string MaskLine(string line)
    {
        line = KnownToken.Replace(line, Mark);
        line = UrlCredential.Replace(line, m => m.Groups["val"].Value == Mark ? m.Value : $"{m.Groups["pre"].Value}{Mark}@");
        line = QuotedPassword(PasswordSingleQuoted, line, '\'');
        line = QuotedPassword(PasswordDoubleQuoted, line, '"');
        var bare = line;
        line = PasswordBare.Replace(bare, m =>
        {
            // N'…'·@"…"·$"…" 의 접두사만 값으로 잡힌 것 — 따옴표 쪽 규칙이 이미 처리했다.
            var val   = m.Groups["val"].Value;
            var after = m.Index + m.Length;
            if (val.Length <= 2 && val.All(c => c is 'N' or 'n' or '@' or '$') && after < bare.Length && bare[after] is '\'' or '"')
                return m.Value;
            return m.Groups["key"].Value + Mark;
        });
        line = JsonSecret.Replace(line, m => KeepEmpty(m, m.Groups["val"].Value, $"{m.Groups["pre"].Value}{Mark}\""));
        line = XmlSecret.Replace(line, m => KeepEmpty(m, m.Groups["val"].Value, $"{m.Groups["pre"].Value}{Mark}\""));
        line = CodeSecret.Replace(line, m =>
        {
            var quote = m.Groups["dv"].Success ? '"' : '\'';
            var val   = m.Groups["dv"].Success ? m.Groups["dv"].Value : m.Groups["sv"].Value;
            if (val.Length < MinCodeLiteralChars || IsConcatenation(val)) return m.Value;
            return $"{m.Groups["pre"].Value}{quote}{Mark}{quote}";
        });
        return line;
    }

    /// <summary>
    /// 따옴표로 감싼 Password·Pwd 값. 그대로 두는 것: 빈 값, C# 보간(<c>$"…"</c>), 이어 붙이기(따옴표 안이 +·& 로 시작),
    /// 닫는 따옴표 바로 뒤가 글자·숫자인 것(그 따옴표는 다음 속성·토큰을 여는 따옴표 — <c>Password=" providerName="…</c>).
    /// </summary>
    private static string QuotedPassword(Regex regex, string line, char quote) => regex.Replace(line, m =>
    {
        var val   = m.Groups["val"].Value;
        var after = m.Index + m.Length;
        if (val.Length == 0 || m.Groups["pfx"].Value.Contains('$') || IsConcatenation(val)
            || (after < line.Length && char.IsLetterOrDigit(line[after])))
            return m.Value;
        return $"{m.Groups["key"].Value}{m.Groups["pfx"].Value}{quote}{Mark}{quote}";
    });

    /// <summary>"…Password=" + pwd + "…" 의 두 따옴표 사이(" + pwd + ")는 리터럴이 아니라 이어 붙이기다(C# +, VB &).</summary>
    private static bool IsConcatenation(string quoted) => quoted.TrimStart() is ['+' or '&', ..];

    /// <summary>빈 값은 그대로 둔다 — 비어 있다는 것도 진단 근거다.</summary>
    private static string KeepEmpty(Match m, string val, string masked) => val.Length == 0 ? m.Value : masked;

    /// <summary>BEGIN 이 있는 줄. 같은 줄에 END 가 있으면 사이를 가리고 끝, 없으면 BEGIN 뒤를 가리고 블록 안으로 들어간다.</summary>
    private static string MaskKeyStart(string line, Match begin, out bool inKey)
    {
        var bodyStart = begin.Index + begin.Length;
        var end       = PrivateKeyEnd.Match(line, bodyStart);
        inKey = !end.Success;
        if (end.Success) return line[..bodyStart] + (end.Index > bodyStart ? Mark : "") + line[end.Index..];
        return bodyStart < line.Length ? line[..bodyStart] + Mark : line;
    }

    /// <summary>블록 안의 줄. END 가 있으면 그 앞을 가리고 블록을 나간다.</summary>
    private static string MaskKeyBody(string line, out bool inKey)
    {
        var end = PrivateKeyEnd.Match(line);
        inKey = !end.Success;
        if (!end.Success) return Mark;
        return (end.Index > 0 ? Mark : "") + line[end.Index..];
    }

    /// <summary>비밀 이름 <c>&lt;setting&gt;</c> 뒤 첫 <c>&lt;value&gt;</c> 를 가린다. <c>&lt;/setting&gt;</c> 를 만나면 기다림을 끝낸다.</summary>
    private static string MaskSettingValue(string line, ref bool pending)
    {
        if (SecretSetting.IsMatch(line)) pending = true;
        if (!pending) return line;

        if (SettingValue.Match(line) is { Success: true } value)
        {
            pending = false;
            var val = value.Groups["val"];
            if (val.Length > 0 && val.Value != Mark)
                line = line[..val.Index] + Mark + line[(val.Index + val.Length)..];
        }
        if (SettingEnd.IsMatch(line)) pending = false;
        return line;
    }
}
