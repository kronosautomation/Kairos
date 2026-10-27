using Npgsql;

namespace Kairos.Persistence.Postgres;

/// <summary>Connects with a password that is a short-lived token (<see cref="IDatabaseTokenSource"/>) instead of one kept in configuration.</summary>
public static class TokenPassword
{
    /// <summary>How often a token is asked for again after a success. Shorter than the shortest lifetime in use (an RDS IAM token lives 15 minutes), and cheap: credentials libraries cache.</summary>
    public static readonly TimeSpan DefaultRefresh = TimeSpan.FromMinutes(5);

    /// <summary>How soon to try again after a failure. Connections made meanwhile use the last good token until it stops working.</summary>
    public static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Every physical connection the data source opens (the store's and the work-notification connection) signs in with the token. The connection string must carry no password: one would be silently
    /// replaced, and a password that is kept in configuration is what this is for not having.
    /// </summary>
    public static NpgsqlDataSourceBuilder UseTokenPassword(this NpgsqlDataSourceBuilder builder, IDatabaseTokenSource source, TimeSpan? refresh = null, TimeSpan? retry = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        if (!string.IsNullOrEmpty(builder.ConnectionStringBuilder.Password))
        {
            throw new InvalidOperationException("The connection string has a password, and a token source is configured too. Remove the password from the connection string.");
        }

        return builder.UsePeriodicPasswordProvider(
            async (settings, cancellationToken) =>
            {
                string token = await source.GetPasswordAsync(new DatabaseEndpoint(settings.Host ?? "", settings.Port, settings.Username ?? ""), cancellationToken).ConfigureAwait(false);
                return string.IsNullOrEmpty(token) ? throw new InvalidOperationException("The token source returned no token.") : token;
            },
            refresh ?? DefaultRefresh,
            retry ?? DefaultRetry);
    }
}
