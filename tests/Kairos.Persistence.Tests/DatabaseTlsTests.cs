using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kairos.Persistence.Postgres;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>
/// The connection to PostgreSQL asks for TLS 1.3. The test is a stand-in database that speaks just enough of the protocol to start TLS (the SSL request, answered with "S") and then reports
/// which protocol the handshake settled on, with the real Npgsql client on the other side.
/// </summary>
public sealed class DatabaseTlsTests
{
    private sealed record Handshake(bool Succeeded, SslProtocols Protocol);

    private static X509Certificate2 Certificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, "x"), "x");
    }

    /// <summary>Starts the stand-in database that accepts <paramref name="serverProtocols"/>; returns the port and the outcome of the first handshake.</summary>
    private static (int Port, Task<Handshake> Outcome, TcpListener Listener) StartDatabase(SslProtocols serverProtocols)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<Handshake> outcome = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            byte[] ssl = new byte[8];
            await stream.ReadExactlyAsync(ssl);
            await stream.WriteAsync("S"u8.ToArray());
            using X509Certificate2 certificate = Certificate();
            await using var tls = new SslStream(stream);
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = serverProtocols });
                return new Handshake(true, tls.SslProtocol);
            }
            catch (Exception e) when (e is AuthenticationException or IOException)
            {
                return new Handshake(false, SslProtocols.None);
            }
        });
        return (((IPEndPoint)listener.LocalEndpoint).Port, outcome, listener);
    }

    private static async Task TryConnectAsync(int port, Func<NpgsqlDataSourceBuilder, string, NpgsqlDataSourceBuilder> configure)
    {
        string connection = $"Host=127.0.0.1;Port={port};Username=u;Password=p;Database=d;SSL Mode=Require;Trust Server Certificate=true;Timeout=5;Pooling=false";
        await using NpgsqlDataSource source = configure(new NpgsqlDataSourceBuilder(connection), connection).Build();
        try
        {
            await using NpgsqlConnection conn = await source.OpenConnectionAsync();
        }
        catch (Exception e) when (e is NpgsqlException or IOException or AuthenticationException or TimeoutException or InvalidOperationException)
        {
            // The stand-in does not speak the rest of the protocol; only the handshake is of interest.
        }
    }

    [Fact]
    public async Task A_database_that_offers_only_TLS_1_2_is_refused_by_default()
    {
        (int port, Task<Handshake> outcome, TcpListener listener) = StartDatabase(SslProtocols.Tls12);
        try
        {
            await TryConnectAsync(port, (b, cs) => b.ApplyDefault(cs));

            Assert.False((await outcome.WaitAsync(TimeSpan.FromSeconds(20))).Succeeded);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_database_that_offers_TLS_1_3_gets_a_TLS_1_3_session()
    {
        (int port, Task<Handshake> outcome, TcpListener listener) = StartDatabase(SslProtocols.Tls12 | SslProtocols.Tls13);
        try
        {
            await TryConnectAsync(port, (b, cs) => b.ApplyDefault(cs));

            Handshake handshake = await outcome.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(handshake.Succeeded);
            Assert.Equal(SslProtocols.Tls13, handshake.Protocol);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TLS_1_2_is_accepted_only_when_the_code_says_so()
    {
        (int port, Task<Handshake> outcome, TcpListener listener) = StartDatabase(SslProtocols.Tls12);
        try
        {
            await TryConnectAsync(port, (b, cs) => b.ApplyDefault(cs, allowTls12: true));

            Handshake handshake = await outcome.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(handshake.Succeeded);
            Assert.Equal(SslProtocols.Tls12, handshake.Protocol);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("SSL Mode=Disable", false)]
    [InlineData("SSL Mode=Prefer", false)]
    [InlineData("SSL Mode=Require", true)]
    [InlineData("SSL Mode=VerifyCA", true)]
    [InlineData("SSL Mode=VerifyFull", true)]
    public void Only_a_connection_string_that_insists_on_TLS_is_given_a_TLS_version_to_ask_for(string mode, bool insists)
    {
        Assert.Equal(insists, PostgresTls.InsistsOnTls($"Host=h;Database=d;Username=u;{mode}"));
    }
}
