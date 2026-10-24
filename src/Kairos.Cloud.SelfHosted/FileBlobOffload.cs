using System.Security.Cryptography;

namespace Kairos.Cloud.SelfHosted;

/// <summary>
/// Content-addressed blobs on a local or network file system (also the shape of any S3-compatible adapter). One directory tree per
/// tenant and namespace; the name is the SHA-256 of the bytes. Writes are atomic (temp file then move) and reads verify the hash.
/// </summary>
public sealed class FileBlobOffload : IBlobOffload
{
    private readonly string _root;

    public FileBlobOffload(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
    }

    public async ValueTask<BlobRef> PutAsync(TenantScope scope, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        string hex = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        string path = PathFor(scope, hex);
        if (File.Exists(path))
        {
            return new BlobRef(hex, content.Length); // content-addressed: the same bytes are already there
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, content.ToArray(), cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temp, path, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(path))
            {
                // A concurrent writer stored the same bytes first. Content-addressed, so the existing file is identical.
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return new BlobRef(hex, content.Length);
    }

    public async ValueTask<byte[]?> GetAsync(TenantScope scope, BlobRef reference, CancellationToken cancellationToken)
    {
        string path = PathFor(scope, reference.Sha256Hex);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length != reference.Length || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), reference.Sha256Hex, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A stored blob no longer matches its content address.");
        }

        return bytes;
    }

    public ValueTask<bool> DeleteAsync(TenantScope scope, BlobRef reference, CancellationToken cancellationToken)
    {
        string path = PathFor(scope, reference.Sha256Hex);
        bool existed = File.Exists(path);
        if (existed)
        {
            File.Delete(path);
        }

        return ValueTask.FromResult(existed);
    }

    private string PathFor(TenantScope scope, string sha256Hex)
    {
        // The reference is caller-supplied: only an exact lowercase 64-digit hex string may reach the file system.
        if (sha256Hex is null || sha256Hex.Length != 64 || !sha256Hex.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A blob reference is a lowercase SHA-256 hex string.", nameof(sha256Hex));
        }

        return Path.Combine(_root, scope.TenantId.ToString("N"), scope.NamespaceId.ToString("N"), sha256Hex[..2], sha256Hex);
    }
}
