using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Default liveness check: the process is up and answering.
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// The commit this assembly was built from.
    ///
    /// <c>dotnet publish -p:SourceRevisionId=&lt;sha&gt;</c> appends <c>+&lt;sha&gt;</c> to
    /// <see cref="AssemblyInformationalVersionAttribute"/>, so the SHA is whatever follows
    /// the first <c>+</c>. A build that set none — every local build — has no <c>+</c> and
    /// reports "unknown".
    /// </summary>
    private static readonly string BuildSha =
        typeof(Extensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            is { } v && v.IndexOf('+') is var i && i >= 0 && i < v.Length - 1
                ? v[(i + 1)..]
                : "unknown";

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Only checks tagged "live" run here, and the only one registered is "self"
        // (always healthy, no detail in the body) — safe to expose publicly, and it
        // gives Azure App Service a health-check path to probe once this is hosted.
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        // Which build is answering, as the plain commit SHA baked in at publish time
        // (`-p:SourceRevisionId`), or "unknown" for a local build that set none. A deploy
        // smoke test needs this: the *old* build also returns 200 on every page all the
        // way through a slot swap, so "the site responds" cannot distinguish the new
        // build from the one it replaces. Gating on this value is what makes "ready"
        // mean "the new build is serving".
        app.MapGet("/version", () => Results.Text(BuildSha, "text/plain"));

        // /health reports every registered check by name and status, which is
        // information disclosure on a public endpoint — development only.
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks("/health");
        }

        return app;
    }
}
