namespace Kairos;

/// <summary>
/// Tells <c>kairos replay-check</c> how to build the workflow code of your application without starting it. Implement it next to the code that configures Kairos, register
/// the same workflows, and add the payload protector if the application uses client-side encryption. It runs in the pipeline job, which needs the right to open payloads,
/// not in production.
/// </summary>
public interface IReplayCheckSetup
{
    /// <summary>Registers the workflows (and, if used, the payload protector) the build contains. Activities are never run, so they need not be registered.</summary>
    void Configure(KairosOptions options);
}

/// <summary>Names the <see cref="IReplayCheckSetup"/> of an assembly, so <c>kairos replay-check --build</c> can find it.</summary>
/// <param name="setupType">A public class with a parameterless constructor that implements <see cref="IReplayCheckSetup"/>.</param>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ReplayCheckSetupAttribute(Type setupType) : Attribute
{
    public Type SetupType { get; } = setupType;
}
