using Serilog;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Yarp.ReverseProxy.Model;
using Portfolio.WmsOrchestration.Gateway.Extensions;

// =====================================================
// Bootstrap Logger
// =====================================================
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("API Gateway 시작 중...");

    var builder = WebApplication.CreateBuilder(args);

    // =====================================================
    // Host / Kestrel
    // =====================================================
    builder.WebHost.UseUrls("http://0.0.0.0:6999");
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = 50 * 1024 * 1024; // 50MB
    });

    // =====================================================
    // Serilog
    // =====================================================
    builder.Host.UseSerilog((context, services, configuration) =>
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext());

    // =====================================================
    // Services
    // =====================================================

    // YARP
    builder.Services
        .AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

    // Health Checks
    builder.Services.AddHealthChecks();

    builder.Services
        .AddHealthChecksUI(options =>
        {
            options.SetEvaluationTimeInSeconds(10);
            options.SetMinimumSecondsBetweenFailureNotifications(60);
            options.MaximumHistoryEntriesPerEndpoint(100);
        })
        .AddInMemoryStorage();

    // CORS
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                .SetIsOriginAllowed(_ => true);
        });
    });

    // Rate Limiting (Extension)
    builder.Services.AddApiRateLimiting(builder.Configuration);

    var app = builder.Build();

    // =====================================================
    // Middleware
    // =====================================================

    // WebSockets (SignalR 안정성)
    app.UseWebSockets();

    // Serilog Request Logging
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate =
            "[API-Gateway] {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";

        // 핵심: Health 요청은 로그 레벨을 낮춰서 출력 안 되게 함
        options.GetLevel = (httpContext, elapsed, ex) =>
        {
            var path = httpContext.Request.Path.Value;

            if (path != null &&
                (path.StartsWith("/health") || path.StartsWith("/health-api")))
            {
                return Serilog.Events.LogEventLevel.Verbose;
            }

            return Serilog.Events.LogEventLevel.Information;
        };

        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
            diagnosticContext.Set("RemoteIP", httpContext.Connection.RemoteIpAddress?.ToString());

            var auth = httpContext.Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(auth))
            {
                diagnosticContext.Set("HasAuthorizationHeader", true);
                diagnosticContext.Set("AuthorizationScheme", auth.Split(' ').FirstOrDefault());
                diagnosticContext.Set("TokenLength", auth.Length);
            }
            else
            {
                diagnosticContext.Set("HasAuthorizationHeader", false);
                diagnosticContext.Set("AuthorizationScheme", "None");
            }

            if (httpContext.Features.Get<IReverseProxyFeature>() != null)
            {
                diagnosticContext.Set("IsProxied", true);
            }
        };
    });

    // HTTPS (Production)
    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    // CORS
    app.UseCors();

    // Rate Limiter (YARP 앞단)
    app.UseRateLimiter();

    // =====================================================
    // Endpoints
    // =====================================================

    // Health
    app.UseHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";

            var result = new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString()
                })
            };

            await context.Response.WriteAsync(
                System.Text.Json.JsonSerializer.Serialize(result));
        }
    });

    app.MapHealthChecksUI(options =>
    {
        options.UIPath = "/health-ui";
        options.ApiPath = "/health-api";
    });

    // Root Info
    app.MapGet("/", () => new
    {
        service = "API Gateway",
        version = "1.0.0",
        timestamp = DateTime.UtcNow,
        environment = app.Environment.EnvironmentName,
        endpoints = new
        {
            health = "/health",
            healthUI = "/health-ui",
            orchestrationService = "/api/orchestration"
        }
    });

    // Debug Headers (개발용)
    app.MapGet("/debug/headers", (HttpContext ctx) =>
    {
        return Results.Ok(new
        {
            hasAuthorization = ctx.Request.Headers.ContainsKey("Authorization"),
            authorizationScheme =
                ctx.Request.Headers["Authorization"]
                    .FirstOrDefault()?
                    .Split(' ')
                    .FirstOrDefault(),
            hasAccessTokenQuery = ctx.Request.Query.ContainsKey("access_token"),
            path = ctx.Request.Path.Value
        });
    });

    // YARP Reverse Proxy (항상 마지막)
    app.MapReverseProxy();

    var urls = builder.Configuration["ASPNETCORE_URLS"] ?? "http://0.0.0.0:6999";
    Log.Information("API Gateway 시작 완료 - URLs: {URLs}", urls);

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "API Gateway 시작 실패");
}
finally
{
    Log.CloseAndFlush();
}
