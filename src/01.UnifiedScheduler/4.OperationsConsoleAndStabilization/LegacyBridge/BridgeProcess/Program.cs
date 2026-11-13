using System;
using System.Data;
using System.IO;
using System.Text;

namespace Portfolio.UnifiedScheduler.LegacyBridge
{
    /// <summary>
    /// net472 helper exe — net8 본체(Portfolio.UnifiedScheduler)가 직접 못 하는
    /// 레거시 <c>BinaryFormatter</c> + DataTable Remoting-binary 역직렬화를 대신 수행하는 우회 프로세스.
    ///
    /// <para><b>왜 별도 exe 인가</b><br/>
    /// net8 은 <c>BinaryFormatter</c> 의 DataTable Remoting-binary 재귀 역직렬화를 차단한다
    /// ("Invalid BinaryFormatter stream"). 레거시 인터페이스 서버는 응답을
    /// <c>BinaryFormatter → Deflate → Base64</c> 포맷으로 돌려주는데(서버 contract 라 변경 불가),
    /// net8 에서 이걸 직접 풀 수 없다. 그래서 옛 런타임(net472)으로 빌드한 이 helper 가
    /// 위험한 역직렬화만 떠맡고, 결과는 안전한 DataTable XML 로 변환해 본체에 넘긴다.
    /// 반대 방향(compress)도 동일하게 net472 쪽에서만 BinaryFormatter 직렬화를 수행한다.
    /// </para>
    ///
    /// <para><b>데이터를 어떻게 주고받나 — 핵심</b><br/>
    /// 이 프로그램은 본체와 <b>다른 프로세스</b>다. 다른 프로세스끼리는 C# 의 <c>return</c> 값으로
    /// 객체를 건넬 수 없다(메모리가 분리돼 있음). 통신은 OS 가 제공하는 표준 스트림 + 종료 코드뿐이다:
    /// <list type="bullet">
    ///   <item><b>stdin</b> (부모→자식): 입력. 본체가 <c>proc.StandardInput</c> 에 써준다 — Base64(decompress) / XML(compress).</item>
    ///   <item><b>stdout</b> (자식→부모): <b>실제 결과 데이터.</b> 아래 <c>Console.Out.Write(...)</c> 가 곧 반환 통로다 — XML(decompress) / Base64(compress).</item>
    ///   <item><b>stderr</b> (자식→부모): 에러/진단 메시지. 본체는 실패 시에만 읽어 로그에 남긴다.</item>
    ///   <item><b>exit code</b> (자식→부모): 아래 메서드들이 <c>return</c> 하는 <see cref="int"/>. 데이터가 아니라 성공/실패 <i>신호</i>(0~255)다. 본체는 <c>proc.ExitCode</c> 로 읽는다.</item>
    /// </list>
    /// 즉 메서드 시그니처가 <c>int</c> 인 것은 종료 코드용이고, 결과 페이로드는 전부 <c>stdout</c> 으로 나간다.
    /// (부모 쪽 짝: <c>LegacyBridgeClient.RunBridgeAsync</c> 의 StandardInput/StandardOutput/ExitCode 처리.)
    /// </para>
    ///
    /// <list type="bullet">
    ///   <item>
    ///     <c>(no args)</c> | <c>decompress</c> — stdin Base64 압축 문자열 → <see cref="DataCompress.Decompress(string)"/>
    ///     → <see cref="DataTable"/> → <see cref="DataTable.WriteXml(TextWriter, XmlWriteMode)"/> → stdout.
    ///   </item>
    ///   <item>
    ///     <c>compress</c> — stdin DataTable XML(ReadXml 호환) → <see cref="DataTable.ReadXml(TextReader)"/>
    ///     → <see cref="DataCompress.Compress(DataTable)"/> → Base64 압축 문자열 → stdout.
    ///   </item>
    /// </list>
    ///
    /// Exit codes (= 각 메서드의 return 값 = 프로세스 종료 코드):
    ///   0 — 성공
    ///   1 — 예외 (스택트레이스는 stderr)
    ///   2 — stdin 비어있음
    ///   3 — 역직렬화 결과가 DataTable/DataSet 이 아님 (decompress)
    ///   4 — 알 수 없는 모드
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                // stdin/stdout 인코딩을 UTF-8 로 고정 — 한글 컬럼명/값이 깨지지 않게.
                // 본체(LegacyBridgeClient)도 동일하게 UTF-8 로 read/write 하므로 양쪽이 맞아야 한다.
                Console.InputEncoding  = Encoding.UTF8;
                Console.OutputEncoding = Encoding.UTF8;

                // 첫 인자가 모드. 인자가 없으면(본체가 mode 만 넘기고 비울 일은 없지만 방어) decompress 로 본다.
                string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "decompress";

                switch (mode)
                {
                    case "":
                    case "decompress":
                        return RunDecompress();

                    case "compress":
                        return RunCompress();

                    default:
                        // 알 수 없는 모드 — stderr 로 사유를 남기고 exit code 4.
                        Console.Error.WriteLine("[Bridge] unknown mode: " + mode);
                        return 4;
                }
            }
            catch (Exception ex)
            {
                // 어떤 예외든 여기서 잡아 stderr 로 타입+메시지+스택을 내보내고 exit code 1 로 끝낸다.
                // 본체는 ExitCode != 0 을 보고 실패로 처리하며, 이 stderr 내용을 로그에 truncate 해 남긴다.
                // (stdout 으로 내보내면 본체가 데이터로 오인하므로, 진단은 반드시 stderr 로.)
                Console.Error.WriteLine("[Bridge] 실패: " + ex.GetType().FullName + ": " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        /// <summary>
        /// decompress 모드: 레거시 Base64 압축 문자열(stdin) → DataTable → 스키마 포함 XML(stdout).
        /// 반환하는 int 는 데이터가 아니라 종료 코드다(실제 결과는 stdout 으로 나간다).
        /// </summary>
        private static int RunDecompress()
        {
            // [입력] stdin 전체를 읽는다 = 본체가 써준 Base64 압축 문자열.
            string base64 = Console.In.ReadToEnd();
            if (base64 != null) base64 = base64.Trim();

            if (string.IsNullOrEmpty(base64))
            {
                Console.Error.WriteLine("[Bridge] stdin 이 비어있음");
                return 2;
            }

            // [역직렬화] Base64 → Deflate 해제 → BinaryFormatter.Deserialize. net8 이 막아서 여기(net472)서만 가능한 단계.
            // 레거시는 DataTable 또는 DataSet 을 돌려줄 수 있다.
            object obj = DataCompress.Decompress(base64);

            DataTable dt;
            if (obj is DataTable) dt = (DataTable)obj;
            else if (obj is DataSet && ((DataSet)obj).Tables.Count > 0) dt = ((DataSet)obj).Tables[0];  // DataSet 이면 첫 테이블만 사용
            else
            {
                // DataTable/DataSet 도 아니고 비어있지도 않은 예상 밖 타입 — 어떤 타입이었는지 stderr 에 남기고 exit code 3.
                Console.Error.WriteLine("[Bridge] 역직렬화 결과가 DataTable/DataSet 아님: " +
                    (obj == null ? "<null>" : obj.GetType().FullName));
                return 3;
            }

            // WriteXml(WriteSchema) 는 XSD 루트 엘리먼트 이름을 TableName 에서 가져오므로, 비어 있으면
            // "Cannot serialize the DataTable. DataTable name is not set." 예외가 난다.
            // 레거시 서버 응답은 무명 DataTable(new DataTable())을 그대로 돌려주는 경우가 있어
            // 직렬화만 가능하도록 기본 이름을 보장한다. 수신부는 컬럼명으로만 접근하므로 이름 값 자체는 무의미.
            // (compress 측 가드 및 LegacyBridgeClient 의 send/receive 가드와 4중 대칭.)
            if (string.IsNullOrEmpty(dt.TableName))
                dt.TableName = "Table";

            // [출력] WriteSchema 로 컬럼명 + 타입(스키마)까지 함께 직렬화한다 — 본체가 ReadXml 로 복원할 때
            // 타입을 잃지 않게 하기 위함(복원된 DataTable 이 SQL TVP 로 그대로 들어가므로 타입 충실도가 중요).
            // 그 XML 문자열을 Console.Out(=stdout)에 써내는 이 줄이 곧 "결과 반환"이다. 본체가 stdout 을 읽어 간다.
            using (var sw = new StringWriter())
            {
                dt.WriteXml(sw, XmlWriteMode.WriteSchema);
                Console.Out.Write(sw.ToString());
            }
            return 0;  // exit code 0 = 성공 신호. 데이터는 위 stdout 으로 이미 나갔다.
        }

        /// <summary>
        /// compress 모드: DataTable XML(stdin) → DataTable → 레거시 Base64 압축 문자열(stdout).
        /// decompress 의 정확한 역방향. 반환 int 는 종료 코드(결과는 stdout).
        /// </summary>
        private static int RunCompress()
        {
            // [입력] stdin 전체 = 본체가 dt.WriteXml(WriteSchema) 로 만들어 써준 DataTable XML.
            string xml = Console.In.ReadToEnd();
            if (xml != null) xml = xml.Trim();

            if (string.IsNullOrEmpty(xml))
            {
                Console.Error.WriteLine("[Bridge] stdin 이 비어있음");
                return 2;
            }

            // [복원] XML → DataTable. WriteSchema 로 만들어졌으므로 컬럼 타입까지 살아난다.
            DataTable dt = new DataTable();
            using (var sr = new StringReader(xml))
            {
                dt.ReadXml(sr);
            }

            // 무명 테이블 방어 (defense-in-depth) — 들어온 XML 에 이름이 없을 수도 있으므로 기본 이름을 보장한다.
            // 압축(BinaryFormatter)은 무명 테이블도 허용하지만, 이후 어딘가에서 다시 WriteXml(WriteSchema)될 때
            // "DataTable name is not set" 으로 터지지 않도록 decompress 측과 대칭으로 막아 둔다.
            if (string.IsNullOrEmpty(dt.TableName))
                dt.TableName = "Table";

            // [출력] 레거시가 기대하는 BinaryFormatter+Deflate+Base64 포맷으로 압축해 stdout 으로 반환.
            // 본체는 이 문자열을 SOAP 요청의 sDatas/sValues 슬롯에 그대로 실어 레거시 서버로 보낸다.
            string base64 = DataCompress.Compress(dt);
            Console.Out.Write(base64);
            return 0;  // exit code 0 = 성공. 결과 Base64 는 위 stdout 으로 나갔다.
        }
    }
}
