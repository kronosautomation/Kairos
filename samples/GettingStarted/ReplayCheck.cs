using Kairos;
using Kairos.Samples.GettingStarted;

[assembly: ReplayCheckSetup(typeof(ReplayCheckSetup))]

namespace Kairos.Samples.GettingStarted;

#region replay-setup
/// <summary>
/// Tells <c>kairos replay-check</c> which workflows this build contains. Replay never runs activities, so the services behind them can be empty here.
/// If the application uses client-side encryption, also call <c>options.UsePayloadProtector(...)</c> with the same key provider it uses at runtime:
/// the job that runs the check needs the right to open payloads, and nothing leaves that job.
/// </summary>
public sealed class ReplayCheckSetup : IReplayCheckSetup
{
    public void Configure(KairosOptions options) => options.AddOrderProcessing(new NoServices());

    private sealed class NoServices : IOrderServices
    {
        public Task<string> ChargeAsync(string orderId, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Activities are not run by a replay check.");

        public Task ShipAsync(string orderId, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Activities are not run by a replay check.");
    }
}
#endregion
