using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kairos.Cli;

/// <summary>The options of <c>kairos audit export</c>.</summary>
internal sealed record AuditExportArguments
{
    public string Server { get; init; } = "";

    public Guid Namespace { get; init; }

    public string Reason { get; init; } = "";

    public string Out { get; init; } = ".";

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public IReadOnlyList<Guid> Runs { get; init; } = [];

    public string TokenVariable { get; init; } = "KAIROS_TOKEN";

    public static AuditExportArguments Parse(IReadOnlyList<string> args)
    {
        var result = new AuditExportArguments();
        var runs = new List<Guid>();
        for (int i = 0; i < args.Count; i++)
        {
            string name = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{name} needs a value.");
            switch (name)
            {
                case "--server": result = result with { Server = Value() }; break;
                case "--namespace": result = result with { Namespace = Guid.TryParse(Value(), out Guid ns) ? ns : throw new ArgumentException("--namespace must be a GUID.") }; break;
                case "--reason": result = result with { Reason = Value() }; break;
                case "--out": result = result with { Out = Value() }; break;
                case "--token-env": result = result with { TokenVariable = Value() }; break;
                case "--from": result = result with { From = Time(Value(), name) }; break;
                case "--to": result = result with { To = Time(Value(), name) }; break;
                case "--run": runs.Add(Guid.TryParse(Value(), out Guid run) ? run : throw new ArgumentException("--run must be a GUID.")); break;
                default: throw new ArgumentException($"Unknown option '{name}'.");
            }
        }

        if (!Uri.TryCreate(result.Server, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            throw new ArgumentException("audit export needs --server <https://host>.");
        }

        if (result.Namespace == Guid.Empty)
        {
            throw new ArgumentException("audit export needs --namespace <guid>.");
        }

        if (string.IsNullOrWhiteSpace(result.Reason))
        {
            throw new ArgumentException("--reason \"<why>\" is required: it is written to the manifest and to the audit log with your sign-in.");
        }

        if (result.From is null && result.To is null && runs.Count == 0)
        {
            throw new ArgumentException("Say what to export: --from/--to for a time range, or --run <guid> for particular runs.");
        }

        return result with { Runs = runs };
    }

    private static DateTimeOffset Time(string text, string option) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset value)
            ? value : throw new ArgumentException($"{option} must be a date or a date and time (UTC unless it says otherwise).");
}

/// <summary>
/// Asks a Kairos server for signed audit evidence and writes the three files of the bundle to a directory, where <c>kairos audit verify</c> can check them. You sign in to the server (the administrator role is
/// needed); the server reads and signs, and holds no payload key, so sealed steps are reported as not opened.
/// </summary>
internal static class AuditExportCommand
{
    public static async Task<int> RunAsync(AuditExportArguments args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken, HttpMessageHandler? handler = null)
    {
        string? token = Environment.GetEnvironmentVariable(args.TokenVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            await stderr.WriteLineAsync($"Set {args.TokenVariable} to the token you signed in to the server with (the administrator role is needed).").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        using HttpClient http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        var body = new Dictionary<string, object?> { ["reason"] = args.Reason, ["from"] = args.From, ["to"] = args.To, ["runs"] = args.Runs.Count == 0 ? null : args.Runs };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(args.Server), $"/v1/namespaces/{args.Namespace:D}/audit/evidence"))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await stderr.WriteLineAsync("The server could not be reached.").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string why = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "sign in with the administrator role for this namespace",
                    HttpStatusCode.NotImplemented => "this server has no evidence signing key configured",
                    HttpStatusCode.BadRequest => "the request was not accepted (check --reason and the range)",
                    _ => $"the server answered {(int)response.StatusCode}",
                };
                await stderr.WriteLineAsync($"The server refused the export: {why}. Nothing was written.").ConfigureAwait(false);
                return ExitCode.Failed;
            }

            byte[] zipBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(args.Out);
            string? manifestName = null;
            try
            {
                using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
                if (zip.Entries.Count != 3 || zip.Entries.Any(e => e.Name != e.FullName || e.Name.Length == 0 || !e.Name.StartsWith("audit-", StringComparison.Ordinal)
                    || !(e.Name.EndsWith(".manifest.json", StringComparison.Ordinal) || e.Name.EndsWith(".jsonl", StringComparison.Ordinal) || e.Name.EndsWith(".sig", StringComparison.Ordinal))))
                {
                    await stderr.WriteLineAsync("The server's answer is not an evidence bundle. Nothing was written.").ConfigureAwait(false);
                    return ExitCode.Failed;
                }

                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (File.Exists(Path.Combine(args.Out, entry.Name)))
                    {
                        await stderr.WriteLineAsync($"'{entry.Name}' already exists in {args.Out}. Nothing was written.").ConfigureAwait(false);
                        return ExitCode.Usage;
                    }
                }

                // Written under temporary names and renamed together, the signature last: a bundle that is there is a whole bundle.
                string tag = ".tmp-" + Guid.NewGuid().ToString("N");
                var written = new List<(string Temp, string Final)>();
                try
                {
                    foreach (ZipArchiveEntry entry in zip.Entries.OrderBy(e => e.Name.EndsWith(".sig", StringComparison.Ordinal)))
                    {
                        string final = Path.Combine(args.Out, entry.Name);
                        string temp = final + tag;
                        await using (Stream source = entry.Open())
                        await using (FileStream target = File.Create(temp))
                        {
                            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                        }

                        written.Add((temp, final));
                    }

                    foreach ((string temp, string final) in written)
                    {
                        File.Move(temp, final);
                    }
                }
                finally
                {
                    foreach ((string temp, _) in written.Where(w => File.Exists(w.Temp)))
                    {
                        File.Delete(temp);
                    }
                }

                manifestName = zip.Entries.First(e => e.Name.EndsWith(".manifest.json", StringComparison.Ordinal)).Name;
            }
            catch (InvalidDataException)
            {
                await stderr.WriteLineAsync("The server's answer is not an evidence bundle. Nothing was written.").ConfigureAwait(false);
                return ExitCode.Failed;
            }

            await stdout.WriteLineAsync($"Wrote {manifestName} with its evidence and signature to {args.Out}. The server recorded each history it read in the audit log under your sign-in.").ConfigureAwait(false);
            await stdout.WriteLineAsync("Check it with: kairos audit verify <bundle> --key <the public key of the evidence signing key>. Sealed steps were not opened (the server holds no payload key).").ConfigureAwait(false);
            return ExitCode.Passed;
        }
    }
}
