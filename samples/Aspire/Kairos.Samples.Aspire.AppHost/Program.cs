#region apphost
IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL holds the history of every run; Kairos is a named connection to it.
IResourceBuilder<PostgresServerResource> postgres = builder.AddPostgres("postgres").WithDataVolume();
IResourceBuilder<KairosResource> kairos = builder.AddKairos("kairos").WithReference(postgres);

// The app embeds Kairos. Referencing the resource hands it ConnectionStrings:kairos, and Aspire's OpenTelemetry settings carry its runs to the dashboard.
builder.AddProject<Projects.Kairos_Samples_Aspire_Orders>("orders")
    .WithReference(kairos)
    .WaitFor(postgres)
    .WithExternalHttpEndpoints();

builder.Build().Run();
#endregion
