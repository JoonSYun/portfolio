namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 지식 문서(<c>Infrastructure/Diagnosis/Knowledge/*.md</c>)를 이름 순으로 이어 시스템 프롬프트로 쓴다.
/// 문서만 고쳐 배포하면 에이전트 행동이 바뀐다. 매 요청 같은 내용이라 프롬프트 캐시로 읽힌다.
/// 대화 화면은 <see cref="LoadAll"/> 로 기존 문서 뒤에 <c>Knowledge/Chat</c> 을 붙인다(하위 폴더는 <see cref="Load"/> 가 읽지 않는다).
/// </summary>
public sealed class KnowledgeBase
{
    private KnowledgeBase(string systemPrompt) => SystemPrompt = systemPrompt;

    public string SystemPrompt { get; }

    public static KnowledgeBase Load(string directory) => LoadAll(directory);

    /// <summary>폴더를 주어진 순서대로, 폴더 안에서는 이름 순으로 잇는다.</summary>
    public static KnowledgeBase LoadAll(params string[] directories)
        => new(string.Join("\n\n---\n\n", directories.SelectMany(ReadParts)));

    private static List<string> ReadParts(string directory)
    {
        if (!Directory.Exists(directory))
            throw new InvalidOperationException($"진단 지식 문서 폴더가 없습니다: {directory}");
        var parts = Directory.GetFiles(directory, "*.md")
                             .Order(StringComparer.Ordinal)
                             .Select(File.ReadAllText)
                             .ToList();
        if (parts.Count == 0)
            throw new InvalidOperationException($"진단 지식 문서가 없습니다: {directory}");
        return parts;
    }
}
