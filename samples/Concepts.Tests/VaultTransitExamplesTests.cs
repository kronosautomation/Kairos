using Kairos.Cloud.SelfHosted;
using Kairos.Samples.Concepts;

namespace Kairos.Samples.Concepts.Tests;

/// <summary>The vault-transit regions of the key-providers-vault page build a provider from a token file and refuse a token given as text.</summary>
public sealed class VaultTransitExamplesTests
{
    [Fact]
    public void The_sample_builds_a_provider_from_a_token_file()
    {
        string file = Path.Combine(Path.GetTempPath(), "kairos-sample-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "fake-token-for-the-sample");
        try
        {
            using var keys = Assert.IsType<VaultTransitKeyProvider>(VaultTransitExamples.Keys(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void The_kubernetes_sample_builds_a_provider_without_a_token_in_sight()
    {
        using var keys = Assert.IsType<VaultTransitKeyProvider>(VaultTransitExamples.KubernetesKeys());
    }
}
