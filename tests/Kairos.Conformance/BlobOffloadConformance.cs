using System.Security.Cryptography;

namespace Kairos.Conformance;

/// <summary>What every <see cref="IBlobOffload"/> must do: content addressing, tenant isolation, integrity and safe handling of hostile references.</summary>
public abstract class BlobOffloadConformance
{
    protected static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    protected static readonly TenantScope B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2"));

    protected abstract IBlobOffload CreateOffload();

    /// <summary>Damages the stored bytes behind a reference so the next read must notice. Return false if the adapter cannot do this.</summary>
    protected virtual Task<bool> CorruptAsync(TenantScope scope, BlobRef reference) => Task.FromResult(false);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    [InlineData(5_000_000)]
    public async Task Stored_bytes_come_back_identical_under_their_sha256(int length)
    {
        IBlobOffload offload = CreateOffload();
        byte[] content = RandomNumberGenerator.GetBytes(length);

        BlobRef reference = await offload.PutAsync(A, content, CancellationToken.None);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), reference.Sha256Hex);
        Assert.Equal(length, reference.Length);
        Assert.Equal(content, await offload.GetAsync(A, reference, CancellationToken.None));
    }

    [Fact]
    public async Task Putting_the_same_bytes_twice_is_idempotent()
    {
        IBlobOffload offload = CreateOffload();
        byte[] content = RandomNumberGenerator.GetBytes(1000);

        BlobRef first = await offload.PutAsync(A, content, CancellationToken.None);
        BlobRef second = await offload.PutAsync(A, content, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(content, await offload.GetAsync(A, first, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_references_are_absent_not_errors()
    {
        IBlobOffload offload = CreateOffload();
        var unknown = new BlobRef(Convert.ToHexStringLower(SHA256.HashData("nothing"u8)), 7);

        Assert.Null(await offload.GetAsync(A, unknown, CancellationToken.None));
        Assert.False(await offload.DeleteAsync(A, unknown, CancellationToken.None));
    }

    [Fact]
    public async Task Another_tenant_or_namespace_cannot_see_read_or_delete_a_blob()
    {
        IBlobOffload offload = CreateOffload();
        byte[] content = "CANARY-PAYLOAD"u8.ToArray();
        BlobRef reference = await offload.PutAsync(A, content, CancellationToken.None);

        foreach (TenantScope foreign in new[] { B, new TenantScope(A.TenantId, B.NamespaceId), new TenantScope(B.TenantId, A.NamespaceId) })
        {
            Assert.Null(await offload.GetAsync(foreign, reference, CancellationToken.None));
            Assert.False(await offload.DeleteAsync(foreign, reference, CancellationToken.None));
        }

        Assert.Equal(content, await offload.GetAsync(A, reference, CancellationToken.None)); // untouched by the attempts
    }

    [Fact]
    public async Task Equal_content_in_two_tenants_is_stored_separately()
    {
        IBlobOffload offload = CreateOffload();
        byte[] content = RandomNumberGenerator.GetBytes(64);
        BlobRef inA = await offload.PutAsync(A, content, CancellationToken.None);
        BlobRef inB = await offload.PutAsync(B, content, CancellationToken.None);

        Assert.True(await offload.DeleteAsync(A, inA, CancellationToken.None));

        Assert.Null(await offload.GetAsync(A, inA, CancellationToken.None));
        Assert.Equal(content, await offload.GetAsync(B, inB, CancellationToken.None)); // B's copy is not A's
    }

    [Fact]
    public async Task Deleted_blobs_are_gone()
    {
        IBlobOffload offload = CreateOffload();
        BlobRef reference = await offload.PutAsync(A, RandomNumberGenerator.GetBytes(10), CancellationToken.None);

        Assert.True(await offload.DeleteAsync(A, reference, CancellationToken.None));
        Assert.Null(await offload.GetAsync(A, reference, CancellationToken.None));
        Assert.False(await offload.DeleteAsync(A, reference, CancellationToken.None));
    }

    [Fact]
    public async Task Corrupted_storage_is_detected_on_read()
    {
        IBlobOffload offload = CreateOffload();
        BlobRef reference = await offload.PutAsync(A, RandomNumberGenerator.GetBytes(256), CancellationToken.None);
        if (!await CorruptAsync(A, reference))
        {
            return;
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () => await offload.GetAsync(A, reference, CancellationToken.None));
    }

    [Theory]
    [InlineData("../../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("")]
    [InlineData("ABCDEF")]
    [InlineData("zz")]
    [InlineData("/absolute/path")]
    public async Task Hostile_references_are_rejected_before_touching_storage(string reference)
    {
        IBlobOffload offload = CreateOffload();
        var hostile = new BlobRef(reference, 1);

        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await offload.GetAsync(A, hostile, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await offload.DeleteAsync(A, hostile, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_puts_of_the_same_content_leave_one_intact_blob()
    {
        IBlobOffload offload = CreateOffload();
        byte[] content = RandomNumberGenerator.GetBytes(100_000);

        BlobRef[] refs = new BlobRef[32];
        await Parallel.ForAsync(0, refs.Length, async (i, ct) => refs[i] = await offload.PutAsync(A, content, ct));

        Assert.Single(refs.Distinct());
        Assert.Equal(content, await offload.GetAsync(A, refs[0], CancellationToken.None));
    }
}
