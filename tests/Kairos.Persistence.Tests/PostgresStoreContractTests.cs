using Kairos.Persistence.Postgres;
using Kairos.Testing;

namespace Kairos.Persistence.Tests;

/// <summary>The exact same contract the in-memory reference satisfies, against real PostgreSQL as the non-owner data-plane role.</summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresStoreContractTests(PostgresFixture fixture) : StoreContractTests, IAsyncLifetime
{
    private TestDatabase? _db;

    protected override async Task<IKairosStore> CreateStoreAsync()
    {
        _db = await fixture.NewDatabaseAsync();
        return _db.NewStore();
    }

    protected override async Task<StoreStats> StatsAsync(IKairosStore store, TenantScope scope) =>
        await ((PostgresKairosStore)store).GetStatsAsync(scope);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }
}
