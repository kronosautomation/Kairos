using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Kairos.Aspire.Hosting.Tests;

public sealed class KairosResourceTests
{
    private static IDistributedApplicationBuilder NewBuilder() => DistributedApplication.CreateBuilder(["--publisher", "manifest"]);

    [Fact]
    public void Kairos_uses_the_connection_string_of_the_database_it_is_pointed_at()
    {
        IDistributedApplicationBuilder builder = NewBuilder();
        IResourceBuilder<PostgresDatabaseResource> database = builder.AddPostgres("pg").AddDatabase("history");

        IResourceBuilder<KairosResource> kairos = builder.AddKairos("kairos").WithReference(database);

        Assert.Same(database.Resource, kairos.Resource.Database);
        Assert.Equal(database.Resource.ConnectionStringExpression.ValueExpression, kairos.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void Pointing_kairos_at_a_server_creates_a_database_named_after_it()
    {
        IDistributedApplicationBuilder builder = NewBuilder();
        IResourceBuilder<PostgresServerResource> server = builder.AddPostgres("pg");

        IResourceBuilder<KairosResource> kairos = builder.AddKairos("kairos").WithReference(server);

        Assert.Equal("kairos-db", kairos.Resource.Database.Name);
        Assert.Equal("kairos", kairos.Resource.Database.DatabaseName);
        Assert.Same(server.Resource, kairos.Resource.Database.Parent);
    }

    [Fact]
    public void A_kairos_resource_without_a_database_says_so()
    {
        IResourceBuilder<KairosResource> kairos = NewBuilder().AddKairos("kairos");

        Assert.False(kairos.Resource.HasDatabase);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => kairos.Resource.ConnectionStringExpression);
        Assert.Contains("WithReference", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Kairos_cannot_be_pointed_at_two_databases()
    {
        IDistributedApplicationBuilder builder = NewBuilder();
        IResourceBuilder<KairosResource> kairos = builder.AddKairos("kairos").WithReference(builder.AddPostgres("pg").AddDatabase("one"));

        Assert.Throws<InvalidOperationException>(() => kairos.WithReference(builder.AddPostgres("pg2").AddDatabase("two")));
    }

    [Fact]
    public async Task An_app_that_references_kairos_receives_the_connection_under_the_resource_name()
    {
        IDistributedApplicationBuilder builder = NewBuilder();
        IResourceBuilder<KairosResource> kairos = builder.AddKairos("kairos").WithReference(builder.AddPostgres("pg"));
        IResourceBuilder<ContainerResource> app = builder.AddContainer("orders", "orders").WithReference(kairos);

#pragma warning disable CS0618 // the replacement (ExecutionConfigurationBuilder) is for running apps; this reads what the reference adds
        Dictionary<string, string> environment = await app.Resource.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Publish);
#pragma warning restore CS0618

        Assert.Equal("{kairos.connectionString}", environment["ConnectionStrings__kairos"]);
    }

    [Fact]
    public void Kairos_is_a_connection_in_the_dashboard_not_a_container()
    {
        IResourceBuilder<KairosResource> kairos = NewBuilder().AddKairos("kairos");

        Assert.DoesNotContain(kairos.Resource.Annotations, a => a is ContainerImageAnnotation);
        CustomResourceSnapshot initial = Assert.IsType<ResourceSnapshotAnnotation>(kairos.Resource.Annotations.Single(a => a is ResourceSnapshotAnnotation)).InitialSnapshot;
        Assert.Equal("Kairos", initial.ResourceType);
        Assert.Equal(KnownResourceStates.Running, initial.State?.Text);
    }
}
