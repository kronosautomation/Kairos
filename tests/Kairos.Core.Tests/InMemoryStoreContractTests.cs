using Kairos.Testing;

namespace Kairos.Core.Tests;

public sealed class InMemoryStoreContractTests : StoreContractTests
{
    protected override Task<IKairosStore> CreateStoreAsync() => Task.FromResult<IKairosStore>(new InMemoryKairosStore());

    protected override Task<StoreStats> StatsAsync(IKairosStore store, TenantScope scope) =>
        Task.FromResult(((InMemoryKairosStore)store).GetStats());
}
