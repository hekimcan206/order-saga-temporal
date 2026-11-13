using System.Diagnostics;
using System.Text.Json.Serialization;
using OrderSaga.App.Api;
using OrderSaga.App.Demo;
using OrderSaga.App.Infrastructure;
using OrderSaga.Downstream;
using OrderSaga.Workflows;
using Scalar.AspNetCore;
using Serilog;
using Temporalio.Extensions.Hosting;
using Temporalio.Extensions.OpenTelemetry;

// One image, four roles:
//   --migrate-only   apply the downstream schema as the schema owner and exit;
//   --demo           drive the HTTP API through the demo scenarios and exit 0 only if all of them hold;
//   Roles=api        serve the HTTP API (starts sagas, queries, updates, signals);
//   Roles=worker     run the Temporal worker (workflow + activities). Roles=api,worker runs both in one process.
if (args.Contains("--demo"))
{
    var demoConfig = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
    using var http = new HttpClient
    {
        BaseAddress = new Uri(demoConfig["Demo:ApiUrl"] ?? "http://localhost:8080/"),
        Timeout = TimeSpan.FromSeconds(30),
    };
    http.DefaultRequestHeaders.Add("X-Api-Key", demoConfig["Api:Key"] ?? "");
    return await new DemoRunner(http, Console.Out).RunAsync(CancellationToken.None);
}

var builder = WebApplication.CreateBuilder(args);
builder.AddObservability();

var roles = (builder.Configuration["Roles"] ?? "api,worker")
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var temporal = builder.Configuration.GetSection(TemporalOptions.Section).Get<TemporalOptions>() ?? new TemporalOptions();

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection(TemporalOptions.Section));
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.Section));
builder.Services.AddDownstreamSystems(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// The client connects lazily, so the API starts (and reports not-ready) while Temporal is still coming up.
var tracing = new TracingInterceptor();
builder.Services.AddTemporalClient(options =>
{
    options.TargetHost = temporal.Address;
    options.Namespace = temporal.Namespace;
    options.Interceptors = [tracing];
});

if (roles.Contains("worker"))
{
    builder.Services
        .AddHostedTemporalWorker(temporal.Address, temporal.Namespace, temporal.TaskQueue)
        .ConfigureOptions(options =>
        {
            options.ClientOptions!.Interceptors = [tracing];
            options.Interceptors = [tracing];
            // On shutdown, give in-flight activities a moment to finish instead of abandoning them to a timeout.
            options.GracefulShutdownTimeout = TimeSpan.FromSeconds(10);
            if (temporal.StickyQueueTimeout is { } sticky)
            {
                options.StickyQueueScheduleToStartTimeout = sticky;
            }
        })
        .AddScopedActivities<OrderActivities>()
        .AddWorkflow<OrderSagaWorkflow>();
}

builder.Services.AddHealthChecks()
    .AddCheck<TemporalHealthCheck>("temporal", tags: [HealthEndpoints.ReadyTag])
    .AddCheck<DatabaseHealthCheck>("downstream-db", tags: [HealthEndpoints.ReadyTag]);

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));

builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "order-saga-temporal";
    document.Info.Version = "v1";
    document.Info.Description = "Delivery order saga on Temporal: place, inspect, cancel, and report courier assignments.";
    return Task.CompletedTask;
}));

var app = builder.Build();

if (args.Contains("--migrate-only"))
{
    await app.Services.GetRequiredService<MigrationRunner>().MigrateAsync();
    return 0;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(options =>
    options.GetLevel = (context, _, exception) =>
        exception is null && context.Response.StatusCode < 400 &&
        (context.Request.Path.StartsWithSegments("/health") || HttpMethods.IsGet(context.Request.Method))
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("order-saga-temporal"));
}

app.MapHealthEndpoints();
if (roles.Contains("api"))
{
    app.MapOrderEndpoints();
}

await app.RunAsync();
return 0;

/// <summary>Entry point; public so integration tests can reference the assembly.</summary>
public partial class Program;
