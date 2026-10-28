using System.IO.Compression;
using System.Net;
using System.Text;
using Kairos.Cli;

namespace Kairos.Cli.Tests;

/// <summary><c>kairos audit export</c>: signs in with a token, asks the server, and writes a bundle only when what came back is a whole bundle with safe file names.</summary>
public sealed class AuditExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kairos-audit-export-" + Guid.NewGuid().ToString("N"));
    private readonly string _tokenVariable = "KAIROS_TEST_TOKEN_" + Guid.NewGuid().ToString("N");
    private static readonly Guid Ns = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, null);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
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

    private static byte[] Zip(params string[] names)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in names)
            {
                using Stream s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes("content of " + name));
            }
        }

        return buffer.ToArray();
    }

    private AuditExportArguments Arguments(params string[] extra) => AuditExportArguments.Parse(
        ["--server", "https://kairos.example", "--namespace", Ns.ToString(), "--reason", "annual audit", "--from", "2026-01-01", "--out", _dir, "--token-env", _tokenVariable, .. extra]);

    private static HttpResponseMessage Ok(byte[] zip) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };

    [Fact]
    public async Task A_whole_bundle_is_written_and_the_request_carries_the_token_the_reason_and_the_range()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "secret-token");
        var handler = new Handler(_ => Ok(Zip("audit-abc.manifest.json", "audit-abc.jsonl", "audit-abc.sig")));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exit = await AuditExportCommand.RunAsync(Arguments(), stdout, stderr, default, handler);

        Assert.True(exit == ExitCode.Passed, stderr.ToString());
        Assert.Equal($"/v1/namespaces/{Ns:D}/audit/evidence", handler.Seen!.RequestUri!.AbsolutePath);
        Assert.Equal("secret-token", handler.Seen.Headers.Authorization!.Parameter);
        Assert.Contains("annual audit", handler.Body, StringComparison.Ordinal);
        Assert.Contains("2026-01-01", handler.Body, StringComparison.Ordinal);
        Assert.Equal(["audit-abc.jsonl", "audit-abc.manifest.json", "audit-abc.sig"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
        Assert.DoesNotContain("secret-token", stdout.ToString() + stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refusal_writes_nothing(HttpStatusCode status)
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "t");
        var stderr = new StringWriter();

        int exit = await AuditExportCommand.RunAsync(Arguments(), new StringWriter(), stderr, default, new Handler(_ => new HttpResponseMessage(status)));

        Assert.Equal(ExitCode.Failed, exit);
        Assert.Contains("Nothing was written", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
    }

    [Theory]
    [InlineData("../evil.jsonl", "audit-abc.manifest.json", "audit-abc.sig")]
    [InlineData("audit-abc.jsonl", "audit-abc.manifest.json", "notes.txt")]
    public async Task An_answer_that_is_not_a_bundle_with_plain_names_writes_nothing(string a, string b, string c)
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "t");

        int exit = await AuditExportCommand.RunAsync(Arguments(), new StringWriter(), new StringWriter(), default, new Handler(_ => Ok(Zip(a, b, c))));

        Assert.Equal(ExitCode.Failed, exit);
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "evil.jsonl")));
    }

    [Fact]
    public async Task A_bundle_is_never_overwritten_and_a_missing_token_sends_nothing()
    {
        Environment.SetEnvironmentVariable(_tokenVariable, "t");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "audit-abc.sig"), "earlier");
        var stderr = new StringWriter();
        int exit = await AuditExportCommand.RunAsync(Arguments(), new StringWriter(), stderr, default, new Handler(_ => Ok(Zip("audit-abc.manifest.json", "audit-abc.jsonl", "audit-abc.sig"))));
        Assert.Equal(ExitCode.Usage, exit);
        Assert.Equal("earlier", await File.ReadAllTextAsync(Path.Combine(_dir, "audit-abc.sig")));
        Assert.Single(Directory.GetFiles(_dir));

        Environment.SetEnvironmentVariable(_tokenVariable, null);
        var handler = new Handler(_ => Ok([]));
        Assert.Equal(ExitCode.Usage, await AuditExportCommand.RunAsync(Arguments(), new StringWriter(), new StringWriter(), default, handler));
        Assert.Null(handler.Seen);
    }

    [Fact]
    public void The_arguments_need_a_server_a_namespace_a_reason_and_something_to_export()
    {
        string[] full = ["--server", "https://h", "--namespace", Ns.ToString(), "--reason", "r", "--from", "2026-01-01"];
        _ = AuditExportArguments.Parse(full);

        Assert.Throws<ArgumentException>(() => AuditExportArguments.Parse(["--namespace", Ns.ToString(), "--reason", "r", "--from", "2026-01-01"]));
        Assert.Throws<ArgumentException>(() => AuditExportArguments.Parse(["--server", "http://remote.example", "--namespace", Ns.ToString(), "--reason", "r", "--from", "2026-01-01"]));
        Assert.Throws<ArgumentException>(() => AuditExportArguments.Parse(["--server", "https://h", "--reason", "r", "--from", "2026-01-01"]));
        Assert.Throws<ArgumentException>(() => AuditExportArguments.Parse(["--server", "https://h", "--namespace", Ns.ToString(), "--from", "2026-01-01"]));
        Assert.Throws<ArgumentException>(() => AuditExportArguments.Parse(["--server", "https://h", "--namespace", Ns.ToString(), "--reason", "r"]));
        Assert.Single(AuditExportArguments.Parse(["--server", "https://h", "--namespace", Ns.ToString(), "--reason", "r", "--run", Guid.NewGuid().ToString()]).Runs);
    }
}
