using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Portfolio.WmsOrchestration.Gateway.Model;
using System.Threading.RateLimiting;

namespace Portfolio.WmsOrchestration.Gateway.Extensions
{
    public static class RateLimitingServiceCollectionExtensions
    {
        /// <summary>
        /// Configure API rate limiting using ASP.NET Core built-in RateLimiter.
        ///
        /// - Fixed Window rate limiting strategy
        /// - Configuration-driven via IOptions
        /// - Designed for API Gateway (YARP) front-door protection
        ///
        /// IMPORTANT:
        /// This rate limiter runs at the ASP.NET Core middleware level.
        /// Requests are rejected BEFORE reaching YARP forwarding or controllers.
        /// </summary>
        public static IServiceCollection AddApiRateLimiting(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            // ======================================================
            // Bind rate limit configuration from appsettings.json
            //
            // Example:
            // RateLimiting:
            //   ApiLimit:
            //     PermitLimit: 100
            //     WindowSeconds: 60
            //     QueueLimit: 0
            //
            // Meaning:
            // - Allow up to 100 requests per 60-second window
            // ======================================================
            services.Configure<ApiRateLimitOptions>(
                configuration.GetSection("RateLimiting:ApiLimit"));

            // ======================================================
            // Register ASP.NET Core RateLimiter
            //
            // This limiter is applied globally to all incoming HTTP requests
            // unless scoped further by endpoint or route.
            // ======================================================
            services.AddRateLimiter(options =>
            {
                // ==================================================
                // Define rate limiting policy
                //
                // The policy determines:
                // - How requests are grouped (PartitionKey)
                // - Which rate limiting algorithm is used
                // ==================================================
                options.AddPolicy("api-limit", httpContext =>
                {
                    // Resolve rate limit options per request
                    var config =
                        httpContext.RequestServices
                            .GetRequiredService<IOptions<ApiRateLimitOptions>>()
                            .Value;

                    // --------------------------------------------------
                    // CORE BEHAVIOR:
                    //
                    // partitionKey: "global"
                    //
                    // All requests share the same limiter bucket.
                    // This means:
                    // - No IP-based separation
                    // - No user-based separation
                    // - No route-based separation
                    //
                    // Rate limiting is applied at the
                    // *API Gateway instance level*.
                    // --------------------------------------------------
                    return RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: "global",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            // Maximum number of requests allowed
                            // within a single time window
                            PermitLimit = config.PermitLimit,

                            // Fixed time window duration
                            // The counter resets after this interval
                            Window = TimeSpan.FromSeconds(config.WindowSeconds),

                            // Number of requests allowed to wait in queue
                            // before being rejected
                            //
                            // 0 = reject immediately with HTTP 429
                            QueueLimit = config.QueueLimit,

                            // Automatically replenish permits
                            // when the window resets
                            AutoReplenishment = true
                        });
                });

                // ==================================================
                // Callback executed when a request is rejected
                // due to rate limiting
                //
                // - HTTP 429 is returned
                // - Logging only (no business logic)
                // ==================================================
                options.OnRejected = (context, cancellationToken) =>
                {
                    var http = context.HttpContext;

                    var logger =
                        http.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("RateLimiting");

                    // Log essential request context for operations
                    logger.LogWarning(
                        "[RateLimit] Request rejected due to rate limiting. Method={Method}, Path={Path}, IP={IP}, User={User}",
                        http.Request.Method,
                        http.Request.Path,
                        http.Connection.RemoteIpAddress?.ToString(),
                        http.User?.Identity?.Name ?? "Anonymous");

                    // RFC 6585 - Too Many Requests
                    http.Response.StatusCode =
                        StatusCodes.Status429TooManyRequests;

                    return ValueTask.CompletedTask;
                };
            });

            return services;
        }
    }
}
