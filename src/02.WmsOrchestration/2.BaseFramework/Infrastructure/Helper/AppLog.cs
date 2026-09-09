using Microsoft.Extensions.Configuration;
using System.Net;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    public static class AppLog
    {
        public static string systemName { get; set; }
        private static readonly string serverIp;

        static AppLog()
        {
            // Load configuration
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            systemName = config["SystemName"] ?? "Orchestration";

            // Resolve server IP (IPv4 only)
            serverIp = GetLocalIPv4() ?? "0.0.0.0";
        }

        /// <summary>
        /// Returns log prefix with UTC timestamp, system name, and server IP.
        /// </summary>
        public static string Log(string message)
            => $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} - {systemName} - {serverIp}] - {message}";

        /// <summary>
        /// Retrieves the first local IPv4 address.
        /// </summary>
        private static string? GetLocalIPv4()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return ip.ToString();
                }
            }
            catch
            {
                // swallow exceptions, fallback to default
            }

            return null;
        }
    }
}
