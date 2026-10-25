using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>Adds Kairos to a .NET Aspire AppHost.</summary>
public static class KairosBuilderExtensions
{
    /// <summary>
    /// Adds a Kairos database resource. Point it at PostgreSQL with <see cref="WithReference(IResourceBuilder{KairosResource}, IResourceBuilder{PostgresDatabaseResource})"/>, then give
    /// apps the connection with <c>WithReference(kairos)</c>; the app calls <c>UsePostgresFromConfiguration</c> (or reads <c>ConnectionStrings:{name}</c>).
    /// Runs, tasks and metrics appear in the Aspire dashboard with no further setup, because <c>AddKairos</c> in the app subscribes the OpenTelemetry providers that Aspire's
    /// service defaults configure.
    /// </summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The resource name, for example <c>kairos</c>.</param>
    public static IResourceBuilder<KairosResource> AddKairos(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new KairosResource(name);
        return builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "Kairos",
                State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties = [new ResourcePropertySnapshot(CustomResourceKnownProperties.Source, "Kairos")],
            })
            .WithManifestPublishingCallback(context =>
            {
                context.Writer.WriteString("type", "value.v0");
                context.Writer.WriteString("connectionString", resource.ConnectionStringExpression.ValueExpression);
            });
    }

    /// <summary>Uses an existing PostgreSQL database for Kairos. Create it with <c>AddPostgres("pg").AddDatabase("kairosdb")</c>.</summary>
    public static IResourceBuilder<KairosResource> WithReference(this IResourceBuilder<KairosResource> builder, IResourceBuilder<PostgresDatabaseResource> database)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(database);
        builder.Resource.Attach(database.Resource);
        return builder;
    }

    /// <summary>
    /// Uses a PostgreSQL server for Kairos and creates the database for it (a database named after the Kairos resource, shown as <c>{name}-db</c>):
    /// <c>builder.AddKairos("kairos").WithReference(builder.AddPostgres("pg"))</c>.
    /// </summary>
    public static IResourceBuilder<KairosResource> WithReference(this IResourceBuilder<KairosResource> builder, IResourceBuilder<PostgresServerResource> server)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(server);
        return builder.WithReference(server.AddDatabase(builder.Resource.Name + "-db", databaseName: builder.Resource.Name));
    }
}
