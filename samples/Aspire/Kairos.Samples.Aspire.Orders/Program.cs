using System.Text;
using Kairos;
using Kairos.Samples.Aspire.Orders;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

#region telemetry
// What every Aspire service does: send traces and metrics to the endpoint Aspire provides (OTEL_EXPORTER_OTLP_ENDPOINT).
// There is no AddSource("Kairos") and no AddMeter("Kairos"): AddKairos subscribes both.
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation())
    .UseOtlpExporter();
#endregion

#region kairos
// The database comes from the Kairos resource in the AppHost (ConnectionStrings:kairos). In development this also creates the Kairos roles and schema.
builder.Configuration["ConnectionStrings:kairos"] = await DevDatabase.PrepareAsync(
    builder.Configuration["ConnectionStrings:kairos"] ?? throw new InvalidOperationException("Run this through the AppHost: it provides ConnectionStrings:kairos."));

builder.Services.AddKairos(options => options.UsePostgresFromConfiguration(builder.Configuration).AllowUnencryptedPayloads().AddOrders());
#endregion

WebApplication app = builder.Build();

// AddKairos added the "kairos" health check (store reachable, workers running, nothing parked).
app.MapHealthChecks("/health");

#region endpoints
// A run started from a request is a child of that request's trace.
app.MapPost("/orders/{id}", async (string id, KairosClient kairos) =>
{
    StartRunResult run = await kairos.StartAsync(KairosOptions.DefaultScope, $"order-{id}", OrderWorkflow.Name, Encoding.UTF8.GetBytes(id));
    return Results.Accepted($"/orders/{id}", new { runId = run.RunId, started = run.Started });
});

app.MapGet("/orders/{id}", async (string id, IKairosStore store) =>
{
    RunKey? run = await store.GetCurrentRunAsync(KairosOptions.DefaultScope, $"order-{id}", CancellationToken.None);
    return run is null ? Results.NotFound() : Results.Ok(new { state = (await store.GetRunStateAsync(run.Value, CancellationToken.None))?.ToString() });
});
#endregion

app.Run();
