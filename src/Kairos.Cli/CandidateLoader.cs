using System.Reflection;
using System.Runtime.Loader;

namespace Kairos.Cli;

/// <summary>The workflow code and payload protector of a candidate build, as its setup registered them.</summary>
internal sealed record Candidate(WorkflowRegistry Workflows, IPayloadProtector? Protector, string SetupType, BlobOffloadOptions? BlobOffload = null);

/// <summary>Loads the assembly of a candidate build beside this tool and asks its <see cref="IReplayCheckSetup"/> for the workflows. Nothing of the application is started.</summary>
internal static class CandidateLoader
{
    public static Candidate Load(string assemblyPath, string? setupTypeName)
    {
        string full = Path.GetFullPath(assemblyPath);
        if (!File.Exists(full))
        {
            throw new ArgumentException($"The candidate build '{assemblyPath}' does not exist.");
        }

        var context = new CandidateContext(full);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(full);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            throw new ArgumentException($"'{assemblyPath}' is not a .NET assembly that can be loaded ({ex.GetType().Name}).", ex);
        }

        Type setupType = FindSetupType(assembly, setupTypeName);
        var setup = (IReplayCheckSetup)(Activator.CreateInstance(setupType) ?? throw new ArgumentException($"{setupType.FullName} could not be created."));
        var options = new KairosOptions();
        setup.Configure(options);
        return new Candidate(options.WorkflowRegistry, options.PayloadProtector, setupType.FullName ?? setupType.Name, options.BlobOffload);
    }

    private static Type FindSetupType(Assembly assembly, string? name)
    {
        if (name is not null)
        {
            Type type = assembly.GetType(name, throwOnError: false) ?? throw new ArgumentException($"The build has no type named '{name}'.");
            return typeof(IReplayCheckSetup).IsAssignableFrom(type) ? type : throw new ArgumentException($"{name} does not implement IReplayCheckSetup.");
        }

        Type[] named = [.. assembly.GetCustomAttributes<ReplayCheckSetupAttribute>().Select(a => a.SetupType).Distinct()];
        return named.Length switch
        {
            1 => named[0],
            0 => throw new ArgumentException(
                "The build names no replay-check setup. Add [assembly: ReplayCheckSetup(typeof(YourSetup))] to it (YourSetup implements IReplayCheckSetup), or pass --setup <type name>."),
            _ => throw new ArgumentException("The build names several replay-check setups; choose one with --setup <type name>."),
        };
    }

    /// <summary>The dependencies of the candidate come from its folder; everything of Kairos comes from this tool, so the types are the same on both sides.</summary>
    private sealed class CandidateContext(string path) : AssemblyLoadContext("kairos-candidate", isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is { } n && (n.StartsWith("Kairos.", StringComparison.Ordinal) || n == "kairos"))
            {
                return null; // shared with the tool
            }

            string? resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            return resolved is null ? null : LoadFromAssemblyPath(resolved);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return resolved is null ? 0 : LoadUnmanagedDllFromPath(resolved);
        }
    }
}
