using System.Security.Cryptography;
using System.Text;
using Kairos.Cloud.SelfHosted;
using Kairos.Conformance;
using Kairos.Worker;

namespace Kairos.SelfHosted.Tests;

/// <summary>The key-provider conformance suite plus the Vault-specific guarantees, against a real server.</summary>
[Trait("Category", "Integration")]
public abstract class TransitServerTests<TFixture>(TFixture server) : KeyProviderConformance, IClassFixture<TFixture>
    where TFixture : TransitServerFixture
{
    private static readonly TenantScope TenantA = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    private static readonly TenantScope TenantB = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b1"));

    protected override IKeyProvider CreateProvider() => server.Provider();

    [Fact]
    public async Task A_key_wrapped_for_tenant_A_cannot_be_unwrapped_for_tenant_B_and_the_answer_is_the_same_as_for_any_other_failure()
    {
        using VaultTransitKeyProvider provider = server.Provider();
        byte[] dataKey = RandomNumberGenerator.GetBytes(32);
        byte[] contextA = Encoding.UTF8.GetBytes($"{TenantA.TenantId}/{TenantA.NamespaceId}");
        byte[] contextB = Encoding.UTF8.GetBytes($"{TenantB.TenantId}/{TenantB.NamespaceId}");
        WrappedKey wrapped = await provider.WrapAsync(dataKey, contextA, CancellationToken.None);

        KeyUnwrapException wrongTenant = await Assert.ThrowsAsync<KeyUnwrapException>(async () => await provider.UnwrapAsync(wrapped, contextB, CancellationToken.None));
        KeyUnwrapException altered = await Assert.ThrowsAsync<KeyUnwrapException>(async () =>
            await provider.UnwrapAsync(wrapped with { Ciphertext = [.. wrapped.Ciphertext[..^3], .. "AAA"u8] }, contextA, CancellationToken.None));
        KeyUnwrapException unknown = await Assert.ThrowsAsync<KeyUnwrapException>(async () =>
            await provider.UnwrapAsync(wrapped with { KeyId = "transit/other" }, contextA, CancellationToken.None));

        Assert.Equal(wrongTenant.Message, altered.Message);
        Assert.Equal(wrongTenant.Message, unknown.Message);
        Assert.Equal(dataKey, await provider.UnwrapAsync(wrapped, contextA, CancellationToken.None));
    }

    [Fact]
    public async Task The_payload_protector_will_not_open_tenant_A_data_for_tenant_B()
    {
        using VaultTransitKeyProvider provider = server.Provider();
        var protector = new EnvelopePayloadProtector(provider);
        byte[] context = "run-1/input"u8.ToArray();
        byte[] sealedValue = await protector.ProtectAsync(TenantA, "secret"u8.ToArray(), context, CancellationToken.None);

        Assert.Equal("secret"u8.ToArray(), await protector.UnprotectAsync(TenantA, sealedValue, context, CancellationToken.None));
        await Assert.ThrowsAnyAsync<PayloadProtectionException>(async () => await protector.UnprotectAsync(TenantB, sealedValue, context, CancellationToken.None));

        // A second protector with no cached keys must also be refused by the server, not by a cache miss.
        var fresh = new EnvelopePayloadProtector(provider);
        await Assert.ThrowsAnyAsync<PayloadProtectionException>(async () => await fresh.UnprotectAsync(TenantB, sealedValue, context, CancellationToken.None));
    }

    [Fact]
    public async Task A_key_that_is_not_derived_is_refused_with_a_clear_error()
    {
        using VaultTransitKeyProvider provider = server.Provider(TransitServerFixture.PlainKeyName);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.WrapAsync(new byte[32], "ctx"u8.ToArray(), CancellationToken.None));
        Assert.Contains("derived", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_key_is_a_typed_failure_not_a_crash()
    {
        using VaultTransitKeyProvider provider = server.Provider("no-such-key");

        VaultTransitException ex = await Assert.ThrowsAsync<VaultTransitException>(async () =>
            await provider.WrapAsync(new byte[32], "ctx"u8.ToArray(), CancellationToken.None));
        Assert.True(ex.Failure is VaultFailure.NotFound or VaultFailure.Authentication); // the policy token may get 403 for a path it was not given
    }

    [Fact]
    public async Task After_the_key_is_rotated_old_wrapped_keys_still_open_and_new_ones_name_the_new_version()
    {
        using VaultTransitKeyProvider provider = server.Provider();
        byte[] dataKey = RandomNumberGenerator.GetBytes(32);
        byte[] context = "ctx"u8.ToArray();
        WrappedKey before = await provider.WrapAsync(dataKey, context, CancellationToken.None);

        await server.RotateAsync();
        WrappedKey after = await provider.WrapAsync(dataKey, context, CancellationToken.None);

        Assert.NotEqual(before.KeyId, after.KeyId);
        Assert.StartsWith("transit/kairos#", after.KeyId, StringComparison.Ordinal);
        Assert.Equal(dataKey, await provider.UnwrapAsync(before, context, CancellationToken.None));
        Assert.Equal(dataKey, await provider.UnwrapAsync(after, context, CancellationToken.None));
    }

    [Fact]
    public async Task A_token_without_a_policy_for_the_key_is_refused_as_an_authentication_failure_that_does_not_show_the_token()
    {
        string tokenFile = Path.Combine(Path.GetTempPath(), "kairos-bad-token-" + Guid.NewGuid().ToString("N"));
        const string fake = "fake-token-not-known-to-the-server";
        await File.WriteAllTextAsync(tokenFile, fake);
        try
        {
            using var provider = new VaultTransitKeyProvider(new VaultTransitOptions { Address = server.Address, KeyName = TransitServerFixture.KeyName }.UseTokenFile(tokenFile));

            VaultTransitException ex = await Assert.ThrowsAsync<VaultTransitException>(async () =>
                await provider.WrapAsync(new byte[32], "ctx"u8.ToArray(), CancellationToken.None));
            Assert.Equal(VaultFailure.Authentication, ex.Failure);
            Assert.DoesNotContain(fake, ex.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(tokenFile);
        }
    }
}

public sealed class VaultTransitServerTests(VaultServerFixture server) : TransitServerTests<VaultServerFixture>(server);

public sealed class OpenBaoTransitServerTests(OpenBaoServerFixture server) : TransitServerTests<OpenBaoServerFixture>(server);
