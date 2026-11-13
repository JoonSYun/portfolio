using System.Data;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using Portfolio.UnifiedScheduler.Infrastructure;

namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// net472 helper exe(<c>Portfolio.UnifiedScheduler.LegacyBridge.exe</c>) 로 Base64 압축 문자열을
/// 넘겨 <see cref="DataTable"/> 을 돌려받는 out-of-proc 브릿지 클라이언트.
///
/// <para>
/// 배경: net8 은 BinaryFormatter 가 DataTable 의 Remoting binary 재귀 역직렬화를 차단해
/// Invalid BinaryFormatter stream 오류가 난다. Bridge 프로세스(net472)에서 DataTable 을
/// 복원한 뒤 <see cref="DataTable.WriteXml(System.IO.TextWriter, XmlWriteMode)"/> 로
/// 스키마 포함 XML 을 stdout 전달 → Scheduler 에서
/// <see cref="DataTable.ReadXml(System.IO.TextReader)"/> 로 동일 DataTable 왕복 복원.
/// </para>
/// </summary>
public class LegacyBridgeClient
{
    private readonly LegacyBridgeOptions _opt;
    private readonly ILogger<LegacyBridgeClient> _logger;

    public LegacyBridgeClient(
        IOptions<LegacyBridgeOptions>   options,
        ILogger<LegacyBridgeClient>     logger)
    {
        _opt    = options.Value;
        _logger = logger;
    }

    /// <summary>Base64 압축 문자열 → <see cref="DataTable"/> 로 복원.</summary>
    public async Task<DataTable> ToDataTableAsync(string base64Compressed, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(base64Compressed))
            throw new InvalidOperationException("[LegacyBridge] empty input");

        var stdout = await RunBridgeAsync("decompress", base64Compressed, ct).ConfigureAwait(false);

        var dt = new DataTable();
        try
        {
            using var reader = new StringReader(stdout);
            dt.ReadXml(reader);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"[LegacyBridge] stdout XML parse failed. stdoutHead={Truncate(stdout, 300)}", ex);
        }

        // 무명 테이블 방어 (defense-in-depth) — 브릿지가 이미 이름을 보장하지만, 복원된 DataTable 이
        // 이후 다른 경로에서 WriteXml(WriteSchema) 될 때 "DataTable name is not set" 으로 터지지 않도록
        // 여기서도 기본 이름을 보장한다. 수신부는 컬럼명으로만 접근하므로 이름 값 자체는 의미 없음.
        if (string.IsNullOrEmpty(dt.TableName))
            dt.TableName = "Table";

        _logger.LogDebug(
            AppLog.Log("[LegacyBridge] DataTable restored table={Table} cols={Cols} rows={Rows}"),
            dt.TableName, dt.Columns.Count, dt.Rows.Count);

        return dt;
    }

    /// <summary>
    /// <see cref="DataTable"/> → Base64 압축 문자열. net472 bridge 쪽 <c>BinaryFormatter</c> +
    /// Deflate + Base64 포맷을 왕복 복원할 수 있도록 그대로 사용한다.
    /// 호출부는 결과 문자열을 레거시 SOAP <c>sValues[0]</c> 에 그대로 넣는다.
    /// </summary>
    public async Task<string> ToCompressedBase64Async(DataTable dt, CancellationToken ct = default)
    {
        if (dt is null)
            throw new InvalidOperationException("[LegacyBridge] DataTable is null");

        // WriteXml(WriteSchema) 는 루트 엘리먼트 이름을 TableName 에서 가져오므로 비어 있으면
        // "Cannot serialize the DataTable. DataTable name is not set." 예외가 난다.
        // 레거시 BinaryFormatter 경로는 무명 테이블도 허용했고 수신부는 컬럼명으로만 접근하므로,
        // 이름 값 자체는 의미 없다. 직렬화만 가능하도록 기본 이름을 보장한다.
        if (string.IsNullOrEmpty(dt.TableName))
            dt.TableName = "Table";

        using var sw = new StringWriter();
        dt.WriteXml(sw, XmlWriteMode.WriteSchema);

        var stdout = await RunBridgeAsync("compress", sw.ToString(), ct).ConfigureAwait(false);
        return stdout.Trim();
    }

    /// <summary>
    /// net472 helper exe(<c>LegacyBridge.exe</c>)를 1회 실행하고 결과를 받아오는 프로세스 간 통신(IPC) 구동부.
    ///
    /// <para>
    /// helper 와는 다른 프로세스라 C# 반환값을 주고받을 수 없다. OS 표준 스트림 + 종료 코드로만 통신한다 —
    /// 이 메서드가 그 4개 통로를 다루는 부모 쪽 짝이다(자식 쪽: <c>Program.RunDecompress/RunCompress</c>):
    /// <list type="bullet">
    ///   <item><b>stdin</b>: <paramref name="input"/> 를 써넣고 닫는다(EOF 신호) → helper 의 <c>Console.In.ReadToEnd()</c>.</item>
    ///   <item><b>stdout</b>: helper 의 <c>Console.Out.Write(...)</c> 가 써낸 <b>실제 결과 데이터</b>를 끝까지 읽어 반환한다.</item>
    ///   <item><b>stderr</b>: 실패 시 진단 메시지. 비정상 종료일 때만 로그에 남긴다.</item>
    ///   <item><b>exit code</b>: helper 의 <c>return</c> 값(0=성공). 0 이 아니면 데이터가 아니라 실패이므로 throw.</item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task<string> RunBridgeAsync(string mode, string input, CancellationToken ct)
    {
        var exe = string.IsNullOrWhiteSpace(_opt.ExePath)
            ? LegacyBridgeOptions.DefaultPath
            : _opt.ExePath;

        if (!File.Exists(exe))
            throw new FileNotFoundException(
                $"[LegacyBridge] helper exe not found: {exe}. " +
                "Deploy Portfolio.UnifiedScheduler.LegacyBridge build output to that path.", exe);

        // 세 스트림을 모두 리다이렉트해 부모가 직접 읽고 쓴다. UseShellExecute=false 라야 리다이렉트가 가능하고,
        // 인코딩은 helper 쪽 Console.Input/OutputEncoding(UTF-8)과 반드시 일치해야 한글이 안 깨진다.
        var psi = new ProcessStartInfo
        {
            FileName                = exe,
            UseShellExecute         = false,
            RedirectStandardInput   = true,
            RedirectStandardOutput  = true,
            RedirectStandardError   = true,
            CreateNoWindow          = true,
            StandardInputEncoding   = Encoding.UTF8,
            StandardOutputEncoding  = Encoding.UTF8,
            StandardErrorEncoding   = Encoding.UTF8,
        };
        psi.ArgumentList.Add(mode);   // 첫 인자 = 모드("decompress"/"compress"). helper 의 args[0].

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // [stdin] 입력을 써넣고 즉시 닫는다 — Close() 가 EOF 가 되어 helper 의 ReadToEnd() 가 끝난다.
        // 닫지 않으면 helper 가 입력 끝을 못 보고 멈춰 교착(deadlock)에 빠진다.
        await proc.StandardInput.WriteAsync(input.AsMemory(), ct);
        proc.StandardInput.Close();

        // [stdout/stderr] 두 스트림을 동시에 비동기로 읽기 시작한다. 한쪽만 읽으면 다른 쪽 버퍼가 차서
        // 자식이 write 에서 블록될 수 있으므로(파이프 버퍼 교착), 둘 다 await 전에 먼저 걸어 둔다.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        // helper 가 멈춰도 영원히 매달리지 않도록 타임아웃을 건다. 타임아웃 시 프로세스 트리째 종료.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_opt.TimeoutSeconds));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException($"[LegacyBridge] {_opt.TimeoutSeconds}s timeout (mode={mode})");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // [exit code] 0 이 아니면 helper 가 실패 신호를 보낸 것(stdout 은 데이터가 아님). stderr 를 로그에 남기고 throw.
        if (proc.ExitCode != 0)
        {
            _logger.LogError(
                AppLog.Log("[LegacyBridge] mode={Mode} exit={Exit} stderr={Stderr}"),
                mode, proc.ExitCode, Truncate(stderr, 2000));
            throw new InvalidOperationException(
                $"[LegacyBridge] Failed mode={mode} exit={proc.ExitCode}: {Truncate(stderr, 500)}");
        }

        // 성공 — stdout 에 담긴 결과(XML 또는 Base64)를 호출부로 반환한다.
        return stdout;
    }

    private static string Truncate(string s, int n)
        => string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..n] + "…";
}
