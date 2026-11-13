using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;
using Temporalio.Extensions.OpenTelemetry;

namespace OrderSaga.App.Infrastructure;

internal static class ObservabilityExtensions
{
    /// <summary>
    /// Structured JSON logs on stdout plus OpenTelemetry traces and metrics. Temporal's tracing interceptor propagates
    /// the trace context through the workflow history, so one trace covers the HTTP request that placed the order, the
    /// workflow, and every activity on whichever worker ran it. The OTLP exporter is enabled only when
    /// OTEL_EXPORTER_OTLP_ENDPOINT is set.
    /// </summary>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? builder.Environment.ApplicationName;
        var serviceVersion = typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString(3);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddSource(
                    TracingInterceptor.ClientSource.Name,
                    TracingInterceptor.WorkflowsSource.Name,
                    TracingInterceptor.ActivitiesSource.Name)
                .AddSource("Npgsql"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql"));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}
