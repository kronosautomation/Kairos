using Kairos.Testing;

namespace Kairos.Core.Tests;

public sealed class InMemoryStoreContractTests : StoreContractTests
{
    protected override Task<IKairosStore> CreateStoreAsync() => Task.FromResult<IKairosStore>(new InMemoryKairosStore());
}
