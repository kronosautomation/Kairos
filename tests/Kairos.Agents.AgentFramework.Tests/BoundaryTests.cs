using System.Reflection;
using System.Text.Json;
using Kairos.Agents;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>"No model-vendor SDK in Kairos.Agents*": the adapter reaches models only through Microsoft.Extensions.AI, whatever chat client the application gives its agent.</summary>
public sealed class BoundaryTests
{
    /// <summary>Names (parts of names) of model vendors' SDKs and of the Agent Framework packages that wrap them.</summary>
    private static readonly string[] Vendors =
    [
        "OpenAI", "Anthropic", "Azure.AI", "Google.GenAI", "Google.Cloud.AIPlatform", "Google.Ai", "GenerativeAI", "Mistral", "Cohere", "OllamaSharp", "Betalgo", "LangChain", "SemanticKernel", "Amazon.Bedrock", "AWSSDK.Bedrock", "Gemini", "HuggingFace",
    ];

    /// <summary>The Agent Framework packages the adapter may use. The provider packages (.OpenAI, .Foundry and the like), hosting and the Durable Task one are not among them.</summary>
    private static readonly string[] AgentFrameworkPackages = ["Microsoft.Agents.AI", "Microsoft.Agents.AI.Abstractions"];

    [Fact]
    public void The_assembly_references_no_vendor_sdk_directly_or_through_anything_it_loads()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>([typeof(AgentFrameworkRuntime).Assembly]);
        while (queue.TryDequeue(out Assembly? assembly))
        {
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is { } name && seen.Add(name) && !name.StartsWith("System", StringComparison.Ordinal) && !name.StartsWith("Microsoft.CSharp", StringComparison.Ordinal))
                {
                    try
                    {
                        queue.Enqueue(Assembly.Load(reference));
                    }
                    catch (FileNotFoundException)
                    {
                        // an assembly the host does not carry is one the code never reaches
                    }
                }
            }
        }

        Assert.NotEmpty(seen);
        foreach (string name in seen)
        {
            Assert.False(Vendors.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase)), $"The adapter reaches {name}, a model vendor's SDK.");
        }
    }

    [Fact]
    public void The_package_depends_on_the_engine_the_model_abstractions_and_two_agent_framework_packages_only()
    {
        string[] allowed =
        [
            "System", "netstandard", "mscorlib", "Microsoft.Bcl", "Microsoft.CSharp", "Kairos.Core", "Kairos.Abstractions", "Kairos.Agents", "Kairos.Hosting", "Microsoft.Extensions.AI", "Microsoft.Agents.AI",
            "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Hosting.Abstractions", "Microsoft.Extensions.Logging.Abstractions",
        ];
        string[] referenced = [.. typeof(AgentFrameworkRuntime).Assembly.GetReferencedAssemblies().Select(a => a.Name!)];

        Assert.All(referenced, name => Assert.True(allowed.Any(p => name.StartsWith(p, StringComparison.Ordinal)), $"Kairos.Agents.AgentFramework references {name}."));
        Assert.DoesNotContain(referenced, n => n.StartsWith("Npgsql", StringComparison.Ordinal) || n.StartsWith("Kairos.Persistence", StringComparison.Ordinal) || n.StartsWith("Kairos.Cloud", StringComparison.Ordinal));
        Assert.All(referenced.Where(n => n.StartsWith("Microsoft.Agents.AI", StringComparison.Ordinal)), n => Assert.Contains(n, AgentFrameworkPackages));
    }

    [Fact]
    public void No_package_in_the_dependency_closure_is_a_vendor_sdk_or_a_provider_package_of_the_framework()
    {
        // The libraries section of the dependency manifest lists every package and project that was restored for the tests of this package, and so for anything that references it.
        string deps = Path.Combine(AppContext.BaseDirectory, "Kairos.Agents.AgentFramework.Tests.deps.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(deps));
        string[] libraries = [.. doc.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name.Split('/')[0])];

        Assert.Contains("Microsoft.Agents.AI", libraries);
        foreach (string name in libraries)
        {
            Assert.False(Vendors.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase)), $"The dependency closure contains {name}, a model vendor's SDK.");
        }

        // The framework packages that would bring a provider, a hosting layer or the Azure Durable Task route with them.
        string[] unwanted = [.. libraries.Where(n => n.StartsWith("Microsoft.Agents.AI.", StringComparison.Ordinal) && !AgentFrameworkPackages.Contains(n))];
        Assert.Empty(unwanted);
    }
}
