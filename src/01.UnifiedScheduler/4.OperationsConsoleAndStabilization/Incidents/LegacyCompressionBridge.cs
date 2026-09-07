using System.Diagnostics;
using System.Text.Json;

namespace Portfolio.UnifiedScheduler.OperationsConsoleAndStabilization.Incidents;

/// <summary>
/// [담당업무 4] 레거시 포맷 호환 — .NET Framework 전용 압축 포맷 브리지 프로세스.
///
/// 문제: 레거시 인터페이스가 .NET 8 에서 해석되지 않는 .NET Framework 전용 직렬화+압축 포맷으로
///       페이로드를 보낸다. 상대 시스템은 수정 불가.
/// 분석: 포맷 내부 구조(헤더/BinaryFormatter 기반 객체 그래프/압축 스트림)를 뜯어 .NET 8 에서
///       안전하게 복원할 수 없음을 확인.
/// 해결: 해석만 담당하는 작은 .NET Framework 4.8 브리지 프로세스를 두고, .NET 8 스케줄러는
///       stdin/stdout 으로 JSON 만 주고받는다. 레거시 인터페이스는 한 줄도 수정하지 않았다.
/// </summary>
public sealed class LegacyCompressionBridge
{
    private readonly string _bridgeExePath;
    private readonly ILogger<LegacyCompressionBridge> _log;

    public LegacyCompressionBridge(IConfiguration cfg, ILogger<LegacyCompressionBridge> log)
    {
        _bridgeExePath = cfg["Legacy:BridgeExe"] ?? @"C:\scheduler\bridge\LegacyFormatBridge.exe";
        _log = log;
    }

    /// <summary>레거시 압축 페이로드 → 도메인 DTO. 브리지 프로세스가 죽어도 스케줄러는 살아있다.</summary>
    public async Task<T> DecodeAsync<T>(byte[] legacyPayload, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_bridgeExePath, "decode")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("브리지 프로세스 시작 실패");

        await proc.StandardInput.BaseStream.WriteAsync(legacyPayload, ct);
        proc.StandardInput.Close();

        var json = await proc.StandardOutput.ReadToEndAsync(ct);
        var err = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            _log.LogError("브리지 디코드 실패 exit={Exit} {Err}", proc.ExitCode, err);
            throw new LegacyFormatException(err);
        }

        return JsonSerializer.Deserialize<T>(json) ?? throw new LegacyFormatException("빈 응답");
    }
}

public sealed class LegacyFormatException : Exception { public LegacyFormatException(string m) : base(m) { } }
