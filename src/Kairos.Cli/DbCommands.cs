using System.Globalization;
using Kairos.Persistence.Postgres;
using Npgsql;

namespace Kairos.Cli;

/// <summary>The options of <c>kairos db provision</c>. Every secret is read from an environment variable, never from the command line.</summary>
internal sealed record DbProvisionArguments
{
    public string AdminConnectionVariable { get; init; } = "KAIROS_ADMIN_CONNECTION";

    public string MigratorPasswordVariable { get; init; } = "KAIROS_MIGRATOR_PASSWORD";

    public string DataPlanePasswordVariable { get; init; } = "KAIROS_DATAPLANE_PASSWORD";

    public string ControlPlanePasswordVariable { get; init; } = "KAIROS_CONTROLPLANE_PASSWORD";

    public string ReadOnlyPasswordVariable { get; init; } = "KAIROS_READONLY_PASSWORD";

    public static DbProvisionArguments Parse(IReadOnlyList<string> args)
    {
        var result = new DbProvisionArguments();
        for (int i = 0; i < args.Count; i++)
        {
            string name = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{name} needs a value.");
            result = name switch
            {
                "--admin-connection-env" => result with { AdminConnectionVariable = Value() },
                "--migrator-password-env" => result with { MigratorPasswordVariable = Value() },
                "--dataplane-password-env" => result with { DataPlanePasswordVariable = Value() },
                "--controlplane-password-env" => result with { ControlPlanePasswordVariable = Value() },
                "--readonly-password-env" => result with { ReadOnlyPasswordVariable = Value() },
                _ => throw new ArgumentException($"Unknown option '{name}' for db provision."),
            };
        }

        return result;
    }
}

/// <summary>The options of <c>kairos db migrate</c>.</summary>
internal sealed record DbMigrateArguments
{
    public string ConnectionVariable { get; init; } = "KAIROS_CONNECTION";

    public TimeSpan? LockTimeout { get; init; }

    public static DbMigrateArguments Parse(IReadOnlyList<string> args)
    {
        var result = new DbMigrateArguments();
        for (int i = 0; i < args.Count; i++)
        {
            string name = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{name} needs a value.");
            switch (name)
            {
                case "--connection-env":
                    result = result with { ConnectionVariable = Value() };
                    break;
                case "--lock-timeout":
                    string seconds = Value();
                    result = int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out int s) && s > 0
                        ? result with { LockTimeout = TimeSpan.FromSeconds(s) }
                        : throw new ArgumentException("--lock-timeout is a number of seconds greater than zero.");
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{name}' for db migrate.");
            }
        }

        return result;
    }
}

internal static class DbCommands
{
    /// <summary>
    /// First-time setup from an administrator connection: the four Kairos roles (an existing role is left as it is, password included) and the right to create the schema for the migrator.
    /// Run once per database, by a person or a deploy pipeline with administrator rights; the application never holds this credential.
    /// </summary>
    public static async Task<int> ProvisionAsync(DbProvisionArguments args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        string? admin = Environment.GetEnvironmentVariable(args.AdminConnectionVariable);
        if (string.IsNullOrEmpty(admin))
        {
            await stderr.WriteLineAsync($"Set {args.AdminConnectionVariable} to an administrator connection string (a login that may create roles).").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        (string Variable, string Role)[] passwords =
        [
            (args.MigratorPasswordVariable, PostgresProvisioning.MigratorRole),
            (args.DataPlanePasswordVariable, PostgresProvisioning.DataPlaneRole),
            (args.ControlPlanePasswordVariable, PostgresProvisioning.ControlPlaneRole),
            (args.ReadOnlyPasswordVariable, PostgresProvisioning.ReadOnlyRole),
        ];
        var values = new List<string>();
        foreach ((string variable, string role) in passwords)
        {
            string? password = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(password))
            {
                await stderr.WriteLineAsync($"Set {variable} to the password for {role}. Passwords are read from the environment so that they stay out of command lines and logs.").ConfigureAwait(false);
                return ExitCode.Usage;
            }

            values.Add(password);
        }

        try
        {
            await using NpgsqlDataSource source = NpgsqlDataSource.Create(admin);
            await PostgresProvisioning.CreateRolesAsync(source, values[0], values[1], values[2], values[3], cancellationToken).ConfigureAwait(false);
            await PostgresProvisioning.PrepareDatabaseAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (NpgsqlException ex)
        {
            await stderr.WriteLineAsync($"Provisioning failed ({ex.GetType().Name}: {ex.Message}). Nothing about the connection or the passwords is printed.").ConfigureAwait(false);
            return ExitCode.Failed;
        }

        await stdout.WriteLineAsync(
            $"Roles {PostgresProvisioning.MigratorRole}, {PostgresProvisioning.DataPlaneRole}, {PostgresProvisioning.ControlPlaneRole} and {PostgresProvisioning.ReadOnlyRole} exist " +
            $"(a role that existed before keeps its password), and {PostgresProvisioning.MigratorRole} may create the schema. Next: `kairos db migrate` as {PostgresProvisioning.MigratorRole}.").ConfigureAwait(false);
        return ExitCode.Passed;
    }

    /// <summary>Applies pending migrations as the migrator login. Concurrent runs queue on a database lock; running it twice is harmless.</summary>
    public static async Task<int> MigrateAsync(DbMigrateArguments args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        string? connection = Environment.GetEnvironmentVariable(args.ConnectionVariable);
        if (string.IsNullOrEmpty(connection))
        {
            await stderr.WriteLineAsync($"Set {args.ConnectionVariable} to the {PostgresProvisioning.MigratorRole} connection string.").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        try
        {
            await using NpgsqlDataSource source = new NpgsqlDataSourceBuilder(connection).ApplyDefault(connection).Build();
            if (!await PostgresProvisioning.IsMigratorLoginAsync(source, cancellationToken).ConfigureAwait(false))
            {
                await stderr.WriteLineAsync(
                    $"Refused: migrations are applied as {PostgresProvisioning.MigratorRole}, not with the application's or an administrator's credential. Create it with `kairos db provision`.").ConfigureAwait(false);
                return ExitCode.Usage;
            }

            IReadOnlyList<string> applied = await PostgresProvisioning.MigrateAsync(source, args.LockTimeout, cancellationToken).ConfigureAwait(false);
            await stdout.WriteLineAsync(applied.Count == 0
                ? "The schema is current; nothing to apply."
                : $"Applied {applied.Count} migration(s): {string.Join(", ", applied)}.").ConfigureAwait(false);
            return ExitCode.Passed;
        }
        catch (TimeoutException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCode.Failed;
        }
        catch (NpgsqlException ex)
        {
            await stderr.WriteLineAsync($"Migration failed and was rolled back ({ex.GetType().Name}: {ex.Message}).").ConfigureAwait(false);
            return ExitCode.Failed;
        }
    }
}
