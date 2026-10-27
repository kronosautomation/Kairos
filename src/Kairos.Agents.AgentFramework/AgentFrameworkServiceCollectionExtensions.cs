using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kairos.Agents.AgentFramework;

public static class AgentFrameworkServiceCollectionExtensions
{
    /// <summary>
    /// Makes Microsoft Agent Framework agents durable on the Kairos that <c>AddKairos</c> added: <c>services.AddKairos(...).AddAgentFramework(af =&gt; af.AddAgent(supportAgent))</c>.
    /// The agents run on this host's workers; <see cref="DurableAgents"/> (and each agent as a keyed <see cref="AIAgent"/> service, keyed by its name) gives callers the durable agent.
    /// </summary>
    /// <exception cref="InvalidOperationException">AddKairos was not called first, or an agent cannot be made durable as registered.</exception>
    public static IServiceCollection AddAgentFramework(this IServiceCollection services, Action<AgentFrameworkBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        KairosOptions options = services
            .Where(d => d.ServiceType == typeof(KairosOptions))
            .Select(d => d.ImplementationInstance as KairosOptions)
            .FirstOrDefault(o => o is not null)
            ?? throw new InvalidOperationException("Call AddKairos first: AddAgentFramework adds its workflow and activities to the Kairos that AddKairos set up.");

        var builder = new AgentFrameworkBuilder();
        configure(builder);
        AgentFrameworkRuntime runtime = AgentFrameworkRuntime.Create(builder);
        options.AddActivities(runtime.Activities);
        options.AddWorkflow(AgentFrameworkNames.Conversation, runtime.Conversation);

        services.AddSingleton(runtime);
        services.AddSingleton(sp => new DurableAgents(runtime, sp.GetRequiredService<KairosClient>(), options.Scope, sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(sp => new AgentClient(sp.GetRequiredService<KairosClient>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IHostedService, AgentFrameworkLifecycle>();
        foreach (string name in runtime.AgentNames)
        {
            string captured = name;
            services.AddKeyedSingleton<AIAgent>(captured, (sp, _) => sp.GetRequiredService<DurableAgents>().Get(captured));
            services.AddKeyedSingleton<DurableAIAgent>(captured, (sp, _) => sp.GetRequiredService<DurableAgents>().Get(captured));
        }

        return services;
    }
}

/// <summary>Connects the adapter to the host's client before any worker starts, and says at startup which tools nobody classified.</summary>
internal sealed partial class AgentFrameworkLifecycle(AgentFrameworkRuntime runtime, KairosClient client, ILogger<AgentFrameworkRuntime> logger) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        runtime.Bind(client);
        foreach (string tool in runtime.Unclassified)
        {
            LogUnclassified(logger, tool);
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 1, Level = LogLevel.Warning,
        Message = "Agent Framework tool '{Tool}' is not classified, so Kairos treats it as side-effecting and holds its calls for approval unless a policy allows them. Mark it at registration: ReadOnly(\"{Tool}\"), AllowWithoutApproval(\"{Tool}\") or RequireApproval(\"{Tool}\").")]
    private static partial void LogUnclassified(ILogger logger, string tool);
}
