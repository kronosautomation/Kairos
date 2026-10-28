using System.Security.Authentication;
using Npgsql;

namespace Kairos.Persistence.Postgres;

/// <summary>
/// The TLS version Kairos asks of the connection to PostgreSQL. The default is TLS 1.3 only: a database that offers nothing newer than 1.2 refuses the connection, and the person
/// who configured it is told by the handshake failure instead of by a quietly weaker session.
/// </summary>
public static class PostgresTls
{
    /// <summary>
    /// Makes connections built from <paramref name="builder"/> negotiate TLS 1.3 (or, with <paramref name="allowTls12"/>, 1.2 and 1.3). It changes nothing for a connection string that
    /// does not use TLS (<c>SSL Mode=Disable</c>, or <c>Allow</c>/<c>Prefer</c> against a server that answers without it): those have no TLS version to ask for.
    /// </summary>
    /// <param name="builder">The data source builder, before <c>Build()</c>.</param>
    /// <param name="allowTls12">True to accept TLS 1.2 as well, for a database that cannot do 1.3. A choice made in code, visible in review.</param>
    public static NpgsqlDataSourceBuilder RequireTls13(this NpgsqlDataSourceBuilder builder, bool allowTls12 = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        SslProtocols protocols = allowTls12 ? SslProtocols.Tls12 | SslProtocols.Tls13 : SslProtocols.Tls13;
        return builder.UseSslClientAuthenticationOptionsCallback(options => options.EnabledSslProtocols = protocols);
    }

    /// <summary>True when the connection string insists on TLS (<c>Require</c>, <c>VerifyCA</c> or <c>VerifyFull</c>).</summary>
    public static bool InsistsOnTls(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        return new NpgsqlConnectionStringBuilder(connectionString).SslMode is SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull;
    }

    /// <summary>What the hosts apply: TLS 1.3 when the connection string insists on TLS, nothing when it does not (a local development database without TLS stays usable).</summary>
    public static NpgsqlDataSourceBuilder ApplyDefault(this NpgsqlDataSourceBuilder builder, string connectionString, bool allowTls12 = false) =>
        InsistsOnTls(connectionString) ? builder.RequireTls13(allowTls12) : builder;
}
