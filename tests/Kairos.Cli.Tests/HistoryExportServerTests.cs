using System.Net;
using System.Text;
using Kairos.Cli;
using Kairos.Testing;

namespace Kairos.Cli.Tests;

/// <summary>Exporting through a server: the person signs in (a bearer token), the server decides by role and writes the audit entry; the command only carries the request and checks what comes back.</summary>
public sealed class HistoryExportServerTests : IDisposable
{
    private static readonly TenantScope Scope = new(Guid.Parse("99999999-0000-0000-0000-000000000001"), Guid.Parse("99999999-0000-0000-0000-0000000000a1"));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kairos-export-srv-" + Guid.NewGuid().ToString("N"));
    private readonly string _tokenVariable = "KAIROS_TEST_TOKEN_" + Guid.NewGuid().ToString("N");

    public HistoryExportServerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, null);
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static async Task<string> HistoryTextAsync(Guid run)
    {
        var engine = new TestEngine();
        engine.RegisterWorkflow("order", Orders.Original);
        engine.RegisterActivity("charge", input => Task.FromResult(input));
        Guid id = engine.StartWorkflow("order", Encoding.UTF8.GetBytes("x"), Scope.TenantId, Scope.NamespaceId);
        await engine.RunAsync(id);
        HistoryExport export = (await HistoryExporter.ExportClaimedAsync(engine.Store, "t", new RunKey(Scope.TenantId, Scope.NamespaceId, id), "test", false, engine.Time))!;
        return export.FileText;
    }

    private (HistoryExportArguments Args, string File) Arguments(Guid run, params string[] extra)
    {
        string file = Path.Combine(_dir, run.ToString("N") + ".khist");
        return (HistoryExportArguments.Parse([run.ToString(), "--reason", "ticket 9", "--actor", "ops", "--out", file, "--server", "https://kairos.example", "--namespace", Scope.NamespaceId.ToString(), "--token-env", _tokenVariable, .. extra]), file);
    }

    [Fact]
    public async Task The_request_carries_the_token_and_the_reason_and_a_verified_answer_is_written()
    {
        Guid run = Guid.NewGuid();
        string text = await HistoryTextAsync(run);
        Environment.SetEnvironmentVariable(_tokenVariable, "secret-token");
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
        (HistoryExportArguments args, string file) = Arguments(run);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exit = await HistoryExportCommand.RunAsync(args, null, TimeProvider.System, stdout, stderr, default, handler);

        Assert.True(exit == ExitCode.Passed, stderr.ToString());
        Assert.Equal($"/v1/namespaces/{Scope.NamespaceId:D}/runs/{run:D}/export", handler.Seen!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", handler.Seen.Headers.Authorization!.Scheme);
        Assert.Equal("secret-token", handler.Seen.Headers.Authorization.Parameter);
        Assert.Contains("ticket 9", handler.Body, StringComparison.Ordinal);
        Assert.Equal(text, await File.ReadAllTextAsync(file));
        Assert.DoesNotContain("secret-token", stdout.ToString() + stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refusal_or_error_from_the_server_writes_no_file(HttpStatusCode status)
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "t");
        (HistoryExportArguments args, string file) = Arguments(Guid.NewGuid());
        var stderr = new StringWriter();

        int exit = await HistoryExportCommand.RunAsync(args, null, TimeProvider.System, new StringWriter(), stderr, default, new Handler(_ => new HttpResponseMessage(status)));

        Assert.Equal(ExitCode.Failed, exit);
        Assert.False(File.Exists(file));
        Assert.Contains("Nothing was exported", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_that_does_not_verify_is_not_written()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "t");
        (HistoryExportArguments args, string file) = Arguments(Guid.NewGuid());

        int exit = await HistoryExportCommand.RunAsync(args, null, TimeProvider.System, new StringWriter(), new StringWriter(), default,
            new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not a history file") }));

        Assert.Equal(ExitCode.Failed, exit);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Without_a_token_nothing_is_sent()
    {
        (HistoryExportArguments args, _) = Arguments(Guid.NewGuid());
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        int exit = await HistoryExportCommand.RunAsync(args, null, TimeProvider.System, new StringWriter(), new StringWriter(), default, handler);

        Assert.Equal(ExitCode.Usage, exit);
        Assert.Null(handler.Seen);
    }

    [Fact]
    public void The_arguments_refuse_a_server_without_a_namespace_over_plain_http_or_mixed_with_a_database_credential()
    {
        string run = Guid.NewGuid().ToString();
        string ns = Scope.NamespaceId.ToString();

        Assert.Contains("--namespace", Assert.Throws<ArgumentException>(() => HistoryExportArguments.Parse([run, "--reason", "r", "--actor", "a", "--server", "https://h"])).Message, StringComparison.Ordinal);
        Assert.Contains("https", Assert.Throws<ArgumentException>(() => HistoryExportArguments.Parse([run, "--reason", "r", "--actor", "a", "--server", "http://h.example", "--namespace", ns])).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => HistoryExportArguments.Parse([run, "--reason", "r", "--actor", "a", "--server", "https://h", "--namespace", ns, "--connection", "Host=x"]));
        Assert.Throws<ArgumentException>(() => HistoryExportArguments.Parse([run, "--reason", "r", "--actor", "a", "--server", "https://h", "--namespace", ns, "--tenant", Guid.NewGuid().ToString()]));
        Assert.Equal(Scope.NamespaceId, HistoryExportArguments.Parse([run, "--reason", "r", "--actor", "a", "--server", "http://localhost:5000", "--namespace", ns]).ServerNamespace);
    }
}
