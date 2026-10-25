using System.Collections.Immutable;
using Kairos.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Kairos.Analyzers.Tests;

public sealed class AnalyzerTests
{
    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        string[] trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return [.. trusted.Append(typeof(IWorkflowContext).Assembly.Location).Append(typeof(Kairos.Agents.IAgentContext).Assembly.Location).Append(typeof(Microsoft.Extensions.AI.ChatMessage).Assembly.Location).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    }

    private static async Task<string[]> RunAsync(string body, string usings = "")
    {
        string source = "using System; using System.IO; using System.Threading; using System.Threading.Tasks; using Kairos;\n" + usings + "\n" + body;
        CSharpCompilation compilation = CSharpCompilation.Create(
            "T", [CSharpSyntaxTree.ParseText(source)], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);

        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new WorkflowDeterminismAnalyzer(), new SecretInHistoryAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Select(d => d.Id).Order(StringComparer.Ordinal)];
    }

    private static string Workflow(string statements) =>
        "public static class W { [Workflow] public static async Task<byte[]> RunAsync(IWorkflowContext ctx, byte[] input) { " + statements + " return input; } }";

    [Theory]
    [InlineData("var t = DateTime.UtcNow;", "KAI001")]
    [InlineData("var t = DateTime.Now;", "KAI001")]
    [InlineData("var t = DateTimeOffset.UtcNow;", "KAI001")]
    [InlineData("var t = TimeProvider.System.GetUtcNow();", "KAI001")]
    [InlineData("var t = Environment.TickCount64;", "KAI001")]
    [InlineData("var t = System.Diagnostics.Stopwatch.StartNew();", "KAI001")]
    [InlineData("var g = Guid.NewGuid();", "KAI002")]
    [InlineData("var r = new Random();", "KAI002")]
    [InlineData("var r = Random.Shared.Next();", "KAI002")]
    [InlineData("var b = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);", "KAI002")]
    [InlineData("var t = File.ReadAllText(\"x\");", "KAI003")]
    [InlineData("var c = new System.Net.Http.HttpClient();", "KAI003")]
    [InlineData("Console.WriteLine(1);", "KAI003")]
    [InlineData("var v = Environment.GetEnvironmentVariable(\"X\");", "KAI003")]
    [InlineData("var p = System.Diagnostics.Process.GetCurrentProcess();", "KAI003")]
    [InlineData("await Task.Run(() => 1);", "KAI004")]
    [InlineData("await Task.Delay(1);", "KAI004")]
    [InlineData("Thread.Sleep(1);", "KAI004")]
    [InlineData("await Task.Factory.StartNew(() => 1);", "KAI004")]
    [InlineData("Parallel.For(0, 1, i => { });", "KAI004")]
    [InlineData("await ctx.DelayAsync(TimeSpan.Zero).ConfigureAwait(false);", "KAI004")]
    [InlineData("Counter.Value++;", "KAI005")]
    public async Task Workflow_methods_are_flagged(string statements, string expected)
    {
        string[] ids = await RunAsync(Workflow(statements), "public static class Counter { public static int Value; }");

        Assert.Contains(expected, ids);
    }

    [Theory]
    [InlineData("var t = DateTime.UtcNow;")]
    [InlineData("var g = Guid.NewGuid();")]
    [InlineData("var t = File.ReadAllText(\"x\");")]
    [InlineData("await Task.Delay(1);")]
    [InlineData("Counter.Value++;")]
    public async Task The_same_code_is_fine_in_an_activity_and_in_ordinary_code(string statements)
    {
        string activity = "public static class A { [Activity] public static async Task<byte[]> DoAsync(byte[] input) { " + statements + " return input; } }";
        string ordinary = "public static class O { public static async Task<byte[]> HelperAsync(byte[] input) { " + statements + " return input; } }";
        const string counter = "public static class Counter { public static int Value; }";

        Assert.Empty(await RunAsync(activity, counter));
        Assert.Empty(await RunAsync(ordinary, counter));
    }

    [Fact]
    public async Task A_method_taking_the_context_is_workflow_code_even_without_the_attribute()
    {
        string[] ids = await RunAsync("public static class W { public static Task<byte[]> RunAsync(IWorkflowContext ctx, byte[] input) { var t = DateTime.UtcNow; return Task.FromResult(input); } }");

        Assert.Equal(["KAI001"], ids);
    }

    [Fact]
    public async Task Lambdas_and_local_functions_inside_workflow_code_are_workflow_code()
    {
        string[] ids = await RunAsync(Workflow("Func<DateTime> now = () => DateTime.UtcNow; DateTime Local() => DateTime.Now; _ = now() + (Local() - Local());"));

        Assert.Equal(["KAI001", "KAI001"], ids);
    }

    [Fact]
    public async Task A_lambda_registered_as_a_workflow_is_checked()
    {
        string[] ids = await RunAsync(
            "public static class R { public static Func<IWorkflowContext, byte[], Task<byte[]>> Make() => async (ctx, input) => { var g = Guid.NewGuid(); await Task.CompletedTask; return input; }; }");

        Assert.Equal(["KAI002"], ids);
    }

    [Fact]
    public async Task An_activity_lambda_without_the_context_is_not_workflow_code()
    {
        string[] ids = await RunAsync(
            "public static class R { public static Func<byte[], Task<byte[]>> Make() => async input => { var g = Guid.NewGuid(); await Task.CompletedTask; return input; }; }");

        Assert.Empty(ids);
    }

    [Fact]
    public async Task Well_behaved_workflow_code_is_clean()
    {
        string clean = Workflow(
            "DateTimeOffset at = ctx.Now; Guid id = ctx.NewGuid(); await ctx.DelayAsync(TimeSpan.FromMinutes(5)); " +
            "byte[] r = await ctx.ExecuteActivityAsync(\"charge\", input); byte[] s = await ctx.WaitForSignalAsync(\"approve\"); " +
            "_ = TimeSpan.FromSeconds(1) + (at - at); _ = id; _ = r.Length + s.Length; const int Limit = 3; _ = Limit;");

        Assert.Empty(await RunAsync(clean));
    }

    [Fact]
    public async Task ConfigureAwait_true_and_static_readonly_and_constants_are_allowed()
    {
        string source = "public static class Cfg { public const int Max = 3; public static readonly string Name = \"x\"; }\n"
            + Workflow("await ctx.DelayAsync(TimeSpan.Zero).ConfigureAwait(true); _ = Cfg.Max; _ = Cfg.Name;");

        Assert.Empty(await RunAsync(source));
    }

    [Fact]
    public async Task Credential_like_parameters_are_flagged_on_workflows_and_activities()
    {
        string source =
            "public static class S {\n" +
            "  [Workflow] public static Task<byte[]> W(IWorkflowContext ctx, string apiKey) => Task.FromResult(new byte[0]);\n" +
            "  [Activity] public static Task<byte[]> A(string password, byte[] clientSecret, string orderId, CancellationToken cancellationToken) => Task.FromResult(new byte[0]);\n" +
            "}";

        string[] ids = await RunAsync(source);

        Assert.Equal(["KAI006", "KAI006", "KAI006"], ids);
    }

    [Fact]
    public async Task Parameters_of_ordinary_methods_are_not_flagged()
    {
        Assert.Empty(await RunAsync("public static class S { public static void Login(string password) { } }"));
    }

    [Fact]
    public async Task A_compilation_that_does_not_use_Kairos_is_untouched()
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "T", [CSharpSyntaxTree.ParseText("public static class W { public static void M() { var t = System.DateTime.UtcNow; } }")],
            References.Where(r => !r.Display!.Contains("Kairos.Abstractions", StringComparison.Ordinal)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        ImmutableArray<Diagnostic> diagnostics = await compilation.WithAnalyzers([new WorkflowDeterminismAnalyzer()]).GetAnalyzerDiagnosticsAsync();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_method_taking_the_agent_context_is_workflow_code()
    {
        string[] ids = await RunAsync(
            "public static class A { public static async Task<byte[]> RunAsync(Kairos.Agents.IAgentContext ctx, byte[] input) { var t = DateTime.UtcNow; await Task.CompletedTask; return input; } }");

        Assert.Equal(["KAI001"], ids);
    }

    [Fact]
    public async Task A_lambda_registered_as_an_agent_workflow_is_workflow_code()
    {
        string[] ids = await RunAsync(
            "public static class A { public static Func<Kairos.Agents.IAgentContext, byte[], Task<byte[]>> Make() => async (ctx, input) => { var g = Guid.NewGuid(); await Task.CompletedTask; return input; }; }");

        Assert.Equal(["KAI002"], ids);
    }

    [Fact]
    public async Task A_credential_like_parameter_on_an_agent_workflow_is_flagged()
    {
        string[] ids = await RunAsync(
            "public static class A { public static Task<byte[]> RunAsync(Kairos.Agents.IAgentContext ctx, string apiKey) => Task.FromResult(new byte[0]); }");

        Assert.Equal(["KAI006"], ids);
    }
}
