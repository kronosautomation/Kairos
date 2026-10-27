namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>What names a conversation and a message inside a step: the same when the step is run again, different for every different thing said.</summary>
public sealed class WorkflowStepScopeTests
{
    private static WorkflowStepScope Scope(string step = "step-1") => new("w", step, TimeSpan.FromMinutes(5));

    [Fact]
    public void The_same_call_in_the_same_step_gets_the_same_name_whenever_the_step_is_run()
    {
        Assert.Equal(Scope().Name("message", "writer", "s", "hello"), Scope().Name("message", "writer", "s", "hello"));
    }

    [Fact]
    public void What_is_said_who_is_asked_and_in_which_conversation_all_change_the_name()
    {
        string name = Scope().Name("message", "writer", "s", "hello");

        Assert.NotEqual(name, Scope().Name("message", "writer", "s", "goodbye"));
        Assert.NotEqual(name, Scope().Name("message", "editor", "s", "hello"));
        Assert.NotEqual(name, Scope().Name("message", "writer", "t", "hello"));
        Assert.NotEqual(name, Scope().Name("session", "writer", "s", "hello"));
    }

    [Fact]
    public void The_same_call_in_another_step_gets_another_name()
    {
        Assert.NotEqual(Scope("step-1").Name("message", "writer", "s", "hello"), Scope("step-2").Name("message", "writer", "s", "hello"));
    }

    [Fact]
    public void Two_executors_that_say_the_same_thing_in_one_step_get_two_names_in_whatever_order_they_ask()
    {
        WorkflowStepScope one = Scope();
        string first = one.Name("message", "writer", "s", "hello");
        string second = one.Name("message", "writer", "s", "hello");

        Assert.NotEqual(first, second);
        WorkflowStepScope other = Scope();
        Assert.Equal(first, other.Name("message", "writer", "s", "hello")); // the nth asker of a thing is named by n, not by who it is
    }

    [Fact]
    public void The_conversations_a_step_used_are_listed_in_order_and_once()
    {
        WorkflowStepScope scope = Scope();
        scope.Own("af/writer/wf.b");
        scope.Own("af/writer/wf.a");
        scope.Own("af/writer/wf.b");

        Assert.Equal(["af/writer/wf.a", "af/writer/wf.b"], scope.Owned);
    }

    [Fact]
    public void Waiting_is_counted_once_however_often_the_scope_that_counts_it_is_disposed()
    {
        var measurements = new List<long>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "kairos.af.workflow.conversation_waits")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (tags.ToArray().Any(t => t.Key == "workflow" && (string?)t.Value == "counted-once"))
            {
                lock (measurements)
                {
                    measurements.Add(value);
                }
            }
        });
        listener.Start();
        var scope = new WorkflowStepScope("counted-once", "k", TimeSpan.FromMinutes(5));

        IDisposable waiting = scope.StartWaiting();
        waiting.Dispose();
        waiting.Dispose();

        lock (measurements)
        {
            Assert.Equal([1L, -1L], measurements);
        }
    }
}
