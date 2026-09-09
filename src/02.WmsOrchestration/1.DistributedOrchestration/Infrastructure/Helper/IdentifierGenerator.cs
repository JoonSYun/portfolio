using System.Security.Cryptography;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    public static class IdentifierGenerator
    {
        /// <summary>
        /// Sequential Guid for PostgreSQL
        /// Vary First 6 bytes with timestamp in milliseconds
        /// It depends on PostgreSQL's index sorting behavior : 0~3rd byte is more significant than 4~7th byte
        /// </summary>
        /// <returns></returns>
        public static Guid GetSequentialGUID()
        {
            var guidArray = Guid.NewGuid().ToByteArray();
            var now = DateTime.UtcNow;

            // 밀리초 단위 타임스탬프
            var timestamp = now.Ticks / 10000L;
            var timestampBytes = BitConverter.GetBytes(timestamp);

            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(timestampBytes);
            }

            // PostgreSQL은 앞쪽이 중요하므로 0~5번 인덱스에 시간 정보 배치
            Array.Copy(timestampBytes, 2, guidArray, 0, 6);

            return new Guid(guidArray);
        }

        public static string GenerateSecureRandomCode(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

            return string.Create(length, chars, (span, charset) =>
            {
                Span<byte> randomBytes = stackalloc byte[length];
                RandomNumberGenerator.Fill(randomBytes);

                for (int i = 0; i < span.Length; i++)
                {
                    span[i] = charset[randomBytes[i] % charset.Length];
                }
            });
        }
    }
}
