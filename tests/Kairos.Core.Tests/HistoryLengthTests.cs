using Kairos.Testing;

namespace Kairos.Core.Tests;

/// <summary>
/// A workflow may decide on <see cref="IWorkflowContext.HistoryLength"/> (that is what <see cref="IWorkflowContext.ShouldContinueAsNew"/> is). The value it sees when the code first runs must be the one replay
/// shows it later, at every point, or a decision taken exactly at a threshold diverges.
/// </summary>
public sealed class HistoryLengthTests
{
    [Fact]
    public async Task The_length_a_workflow_sees_is_the_same_on_replay_at_every_step()
    {
        var engine = new TestEngine();
        WorkflowFunc workflow = async (ctx, _) =>
        {
            for (int i = 0; i < 6; i++)
            {
                await ctx.ExecuteActivityAsync("record", new byte[] { (byte)ctx.HistoryLength }); // the length it saw becomes part of history
                await ctx.DelayAsync(TimeSpan.FromSeconds(1));
            }

            return [(byte)ctx.HistoryLength];
        };
        engine.RegisterWorkflow("lengths", workflow);
        engine.RegisterActivity("record", input => Task.FromResult(input));
        Guid id = engine.StartWorkflow("lengths", [], Guid.NewGuid(), Guid.NewGuid());
        await engine.RunAsync(id);
        IReadOnlyList<HistoryEvent> history = engine.GetHistory(id);

        ReplayResult replay = WorkflowReplayer.Replay(workflow, history); // throws NonDeterminismException when a recorded input differs from what the code now computes

        Assert.True(replay.IsClosed);
        Assert.Empty(replay.NewCommands);
    }

    [Fact]
    public async Task A_decision_taken_exactly_at_the_threshold_replays_the_same()
    {
        // Every possible threshold: whichever length the decision is taken at, replay must agree with the live run.
        for (int threshold = 3; threshold < 20; threshold++)
        {
            var engine = new TestEngine();
            int at = threshold;
            WorkflowFunc workflow = async (ctx, _) =>
            {
                int rounds = 0;
                while (rounds < 8)
                {
                    if (ctx.HistoryLength >= at)
                    {
                        await ctx.ExecuteActivityAsync("past", Array.Empty<byte>());
                    }
                    else
                    {
                        await ctx.ExecuteActivityAsync("before", Array.Empty<byte>());
                    }

                    rounds++;
                }

                return [];
            };
            engine.RegisterWorkflow("threshold", workflow);
            engine.RegisterActivity("before", input => Task.FromResult(input));
            engine.RegisterActivity("past", input => Task.FromResult(input));
            Guid id = engine.StartWorkflow("threshold", [], Guid.NewGuid(), Guid.NewGuid());
            await engine.RunAsync(id);

            ReplayResult replay = WorkflowReplayer.Replay(workflow, engine.GetHistory(id));

            Assert.True(replay.IsClosed, $"threshold {threshold}");
        }
    }
}
