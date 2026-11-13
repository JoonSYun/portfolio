using System.IO.Compression;
using System.Text;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// GitHub zipball 을 진단용 복사본으로 푼다. 텍스트 파일만 골라 비밀값을 가린 뒤(<see cref="SecretMasker"/>) 모두 UTF-8(BOM 없음)로 다시 쓴다.
/// 읽을 때가 아니라 쓸 때 가린다 — 복사본에 값이 남아 있으면 search_code 일치 건수로 값을 한 글자씩 알아낼 수 있다.
/// zipball 은 최상위 폴더 하나(<c>{owner}-{repo}-{sha7}/</c>) 아래에 파일이 있어 첫 경로 조각을 벗긴다.
/// </summary>
public static class MirrorBuilder
{
    public const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>
    /// 가리기 규칙(<see cref="SecretMasker"/>) 버전. 규칙을 바꾸면 올린다 — 완료 표시 파일의 버전이 다르면
    /// 그 복사본은 쓰지 않고 다음 동기화에서 같은 SHA 를 다시 받는다.
    /// </summary>
    public const int MaskVersion = 2;

    private const string MaskVersionPrefix = "mask=";

    /// <summary>완료 표시 파일 <c>{저장소}@{SHA}.complete</c> 의 내용 — 첫 줄 SHA, 둘째 줄 가리기 규칙 버전.</summary>
    public static string CompleteMarker(string sha) => $"{sha}\n{MaskVersionPrefix}{MaskVersion}";

    /// <summary>표시 파일이 현재 가리기 규칙으로 만든 복사본인지. 버전 줄이 없으면(가리기 전 복사본) 아니다.</summary>
    public static bool IsCurrentMarker(string markerText)
        => markerText.Split('\n').Any(line => line.Trim() == MaskVersionPrefix + MaskVersion);

    public static readonly IReadOnlySet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".sql", ".config", ".xml", ".json", ".asmx", ".asax", ".aspx", ".ascx", ".cshtml",
        ".js", ".ts", ".tsx", ".jsx", ".resx", ".settings", ".csproj", ".sln", ".txt", ".md", ".ini",
        ".wsdl", ".xsd", ".xaml", ".java", ".kt", ".gradle",
    };

    public static readonly IReadOnlySet<string> ExcludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "packages", "node_modules", ".git", ".vs", "dist", "build",
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <returns>쓴 파일 수</returns>
    public static int ExtractToMirror(Stream zipStream, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var root  = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var count = 0;

        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;                       // 폴더 항목
            var parts = entry.FullName.Split('/');
            if (parts.Length < 2) continue;                                   // 최상위 폴더 밖 파일은 없다
            var rel = parts[1..];
            if (rel[..^1].Any(ExcludedDirs.Contains)) continue;
            if (!TextExtensions.Contains(Path.GetExtension(entry.Name))) continue;
            if (entry.Length > MaxFileBytes) continue;

            var dest = Path.GetFullPath(Path.Combine(root, Path.Combine(rel)));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // zip 경로 탈출 방지

            using var source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, SecretMasker.Mask(TextFileDecoder.Decode(buffer.ToArray())), Utf8NoBom);
            count++;
        }
        return count;
    }
}
