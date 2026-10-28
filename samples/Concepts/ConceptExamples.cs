using System.Text;
using Kairos.Persistence.Postgres;
using Kairos.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace Kairos.Samples.Concepts;

/// <summary>Small workflows that the concept pages quote. Each region is exercised by a test in Concepts.Tests.</summary>
public static class DeterminismExamples
{
#pragma warning disable KAI002 // deliberately wrong: this is the example of what not to do
    #region wrong
    // Do not write workflow code like this. When the workflow is replayed it generates a different id than the first run did,
    // so the recorded step no longer matches what the code asks for.
    public static async Task<byte[]> WrongAsync(IWorkflowContext ctx, byte[] input)
    {
        string requestId = Guid.NewGuid().ToString("N");
        return await ctx.ExecuteActivityAsync("send", Encoding.UTF8.GetBytes(requestId));
    }
    #endregion
#pragma warning restore KAI002

    #region right
    // The context hands out ids and times that are recorded, so a replay sees exactly the values the first run saw.
    public static async Task<byte[]> RightAsync(IWorkflowContext ctx, byte[] input)
    {
        string requestId = ctx.NewGuid().ToString("N");
        DateTimeOffset requestedAt = ctx.Now;
        return await ctx.ExecuteActivityAsync("send", Encoding.UTF8.GetBytes($"{requestId}@{requestedAt:O}"));
    }
    #endregion
}

public static class SignalExamples
{
    #region approval
    // Waits for a person's decision. While it waits it holds no thread and no memory, for as long as it takes.
    public static async Task<byte[]> ApprovalAsync(IWorkflowContext ctx, byte[] input)
    {
        byte[] decision = await ctx.WaitForSignalAsync("decision");
        return Encoding.UTF8.GetString(decision) == "approve"
            ? await ctx.ExecuteActivityAsync("publish", input)
            : Encoding.UTF8.GetBytes("rejected");
    }
    #endregion

    #region send-signal
    // The signal id makes a retried request harmless: delivering "decision-1" twice signals the run once.
    public static ValueTask<bool> ApproveAsync(KairosClient client, RunKey run) =>
        client.SignalAsync(run, "decision", Encoding.UTF8.GetBytes("approve"), signalId: "decision-1");
    #endregion
}

public static class VersioningExamples
{
    #region version-1
    public static async Task<byte[]> OrderV1Async(IWorkflowContext ctx, byte[] input)
    {
        await ctx.ExecuteActivityAsync("charge", input);
        await ctx.WaitForSignalAsync("packed");
        return await ctx.ExecuteActivityAsync("ship", input);
    }
    #endregion

    #region version-2
    // A new step between "charge" and "packed". Runs that are already past this point keep the old behaviour; new runs get the check.
    public static async Task<byte[]> OrderV2Async(IWorkflowContext ctx, byte[] input)
    {
        await ctx.ExecuteActivityAsync("charge", input);
        if (ctx.IsPatched("fraud-check"))
        {
            await ctx.ExecuteActivityAsync("fraud-check", input);
        }

        await ctx.WaitForSignalAsync("packed");
        return await ctx.ExecuteActivityAsync("ship", input);
    }
    #endregion

    #region pinned-builds
    // Two builds side by side. A run stays on the build it started with, so a workflow can change shape freely between builds.
    public static KairosOptions AddOrderBuilds(this KairosOptions options) => options
        .AddWorkflow("order", OrderV1Async, buildId: "2026-10-01")
        .AddWorkflow("order", OrderV2Async, buildId: "2026-11-01");
    #endregion
}

public static class ActivityExamples
{
    #region long-activity
    // An activity that takes minutes. It tells Kairos it is alive after each chunk, and passes the cancellation token to everything
    // that waits or does I/O so it stops promptly if the attempt is taken away from it.
    public static async Task<byte[]> TranscodeAsync(ActivityContext activity, byte[] input, CancellationToken cancellationToken)
    {
        for (int chunk = 0; chunk < 10; chunk++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
            await activity.HeartbeatAsync();
        }

        return input;
    }
    #endregion
}

public static class LongRunningExamples
{
    #region batches
    // Processes work in a loop that could run for months. Every so often it hands over to a fresh run with an empty history,
    // carrying forward only what it needs.
    public static async Task<byte[]> ProcessBatchesAsync(IWorkflowContext ctx, byte[] input)
    {
        int next = BitConverter.ToInt32(input);
        while (next < 100)
        {
            await ctx.ExecuteActivityAsync("process-batch", BitConverter.GetBytes(next));
            next++;

            if (ctx.ShouldContinueAsNew)
            {
                throw ctx.ContinueAsNew(BitConverter.GetBytes(next));
            }
        }

        return BitConverter.GetBytes(next);
    }
    #endregion
}

/// <summary>How much one data key covers is a choice. The payload-encryption page quotes this.</summary>
public static class EncryptionExamples
{
    #region data-key-scope
    public static (EnvelopePayloadProtector Tightest, EnvelopePayloadProtector Shorter) ProtectorsWithSmallerKeyScope(IKeyProvider keys)
    {
        // Tightest: every payload gets a data key of its own. One wrap per payload, one unwrap per value read.
        var tightest = new EnvelopePayloadProtector(keys, TimeProvider.System, EnvelopeOptions.OneKeyPerValue);

        // In between: replace the data key after 10 minutes or 50 payloads, whichever comes first.
        var shorter = new EnvelopePayloadProtector(keys, TimeProvider.System, EnvelopeOptions.ShortLived(TimeSpan.FromMinutes(10), maxUses: 50));
        return (tightest, shorter);
    }
    #endregion
}

/// <summary>Large payloads in a blob store instead of the database. The payload-encryption page quotes this.</summary>
public static class BlobOffloadExamples
{
    #region blob-offload
    // Payloads longer than 256 KiB (the default) are sealed, stored in the blob store by the hash of the sealed bytes, and the history keeps a small sealed reference.
    // The same store must be given to every process that opens these histories: the workers, the client, the replay check.
    public static KairosOptions Offload(KairosOptions options, IPayloadProtector protector, IBlobOffload blobs) =>
        options
            .UsePayloadProtector(protector) // offload needs it: a blob holds ciphertext and nothing else
            .UseBlobOffload(blobs, thresholdBytes: 128 * 1024);
    #endregion
}

/// <summary>A database password that is a short-lived token. The page on database sign-in quotes this.</summary>
public static class DatabaseSignInExamples
{
    #region token-source
    // Asked for a password whenever the data source opens a connection (and again every few minutes), so nothing is kept in configuration.
    // A cloud's identity service has a real implementation of this; here it is a stand-in that returns whatever the caller gives it.
    public sealed class MyTokenSource(Func<DatabaseEndpoint, CancellationToken, ValueTask<string>> fetch) : IDatabaseTokenSource
    {
        public ValueTask<string> GetPasswordAsync(DatabaseEndpoint endpoint, CancellationToken cancellationToken) => fetch(endpoint, cancellationToken);
    }
    #endregion

    #region use-token
    // The connection string names the host, the database and the role, and carries no password.
    public static KairosOptions Connect(KairosOptions options, string connectionStringWithoutPassword, IDatabaseTokenSource tokens) =>
        options.UsePostgres(connectionStringWithoutPassword, builder => builder.UseTokenPassword(tokens));
    #endregion
}

/// <summary>Setting up telemetry beside Kairos.</summary>
public static class ObservabilityExamples
{
    #region telemetry-setup
    public static IServiceCollection AddObservedKairos(IServiceCollection services, string connectionString, IPayloadProtector protector)
    {
        services.AddOpenTelemetry()
            .UseOtlpExporter();                  // no AddSource("Kairos"), no AddMeter("Kairos"): AddKairos subscribes to both

        return services.AddKairos(options => options.UsePostgres(connectionString).UsePayloadProtector(protector));
    }
    #endregion
}

#region key-use-log
/// <summary>Writes one log line per call the protector makes to the key provider. No key and no payload is ever passed to it.</summary>
public sealed partial class LoggingKeyUse(ILogger<LoggingKeyUse> logger) : IKeyUseObserver
{
    public void KeyUsed(KeyUseEvent use) => Log(logger, use.Kind, use.Scope.TenantId, use.Scope.NamespaceId, use.KeyId, use.Succeeded);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind} of a data key for tenant {Tenant}, namespace {Namespace} under key {KeyId} (succeeded: {Succeeded})")]
    private static partial void Log(ILogger logger, KeyUseKind kind, Guid tenant, Guid @namespace, string keyId, bool succeeded);
}

public static class KeyUseExamples
{
    public static EnvelopePayloadProtector Protector(IKeyProvider keys, ILogger<LoggingKeyUse> logger) =>
        new(keys, TimeProvider.System, new EnvelopeOptions { KeyUseObserver = new LoggingKeyUse(logger) });
}
#endregion
