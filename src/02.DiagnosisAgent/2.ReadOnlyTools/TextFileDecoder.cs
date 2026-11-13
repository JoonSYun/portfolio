using System.Text;

namespace Portfolio.DiagnosisAgent;

/// <summary>
/// 소스 파일 바이트를 문자열로 바꾼다. WMS 소스는 UTF-8 과 EUC-KR 파일이 섞여 있어, BOM 이 없으면
/// 엄격한 UTF-8 로 먼저 풀어 보고 실패하면 CP949(EUC-KR 상위 집합)로 푼다.
/// </summary>
public static class TextFileDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Cp949;

    static TextFileDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp949 = Encoding.GetEncoding(949);
    }

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Cp949.GetString(bytes);
        }
    }
}
