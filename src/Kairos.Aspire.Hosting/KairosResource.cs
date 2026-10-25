using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// A Kairos database in a .NET Aspire application: the PostgreSQL database that holds the history of every run. Kairos itself runs inside the apps that reference it
/// (embedded mode), so this resource is a connection string with a name and a place in the dashboard, not a container.
/// </summary>
/// <param name="name">The resource name; also the name of the connection string the referencing apps receive (<c>ConnectionStrings__{name}</c>).</param>
public sealed class KairosResource(string name) : Resource(name), IResourceWithConnectionString
{
    private PostgresDatabaseResource? _database;

    /// <summary>The PostgreSQL database that backs Kairos, once <see cref="KairosBuilderExtensions.WithReference(IResourceBuilder{KairosResource}, IResourceBuilder{PostgresDatabaseResource})"/> has been called.</summary>
    public PostgresDatabaseResource Database =>
        _database ?? throw new InvalidOperationException($"Kairos resource '{Name}' has no database. Call WithReference(postgres) on it.");

    /// <summary>True once a database is attached.</summary>
    public bool HasDatabase => _database is not null;

    /// <summary>The connection string of the attached database, resolved by Aspire when an app that references this resource starts.</summary>
    public ReferenceExpression ConnectionStringExpression => Database.ConnectionStringExpression;

    internal void Attach(PostgresDatabaseResource database)
    {
        if (_database is not null && !ReferenceEquals(_database, database))
        {
            throw new InvalidOperationException($"Kairos resource '{Name}' already has a database ('{_database.Name}').");
        }

        _database = database;
    }
}
