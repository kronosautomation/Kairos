using Kairos.Cloud.SelfHosted;
using Kairos.Worker;

namespace Kairos.SelfHosted.Tests;

/// <summary>The key windows have documented defaults and ceilings: a data key seals for an hour by default and at most 24, an unwrapped key is cached for 15 minutes by default and at most one hour.</summary>
public sealed class EnvelopeOptionsLimitsTests
{
    private static EnvelopePayloadProtector Make(EnvelopeOptions options) => new(LocalKeyProvider.CreateEphemeral(), options: options);

    [Fact]
    public void The_defaults_are_pinned()
    {
        var options = new EnvelopeOptions();

        Assert.Equal(TimeSpan.FromHours(1), options.DataKeyLifetime);
        Assert.Equal(TimeSpan.FromMinutes(15), options.UnwrappedKeyTtl);
        Assert.Equal(10_000, options.MaxUsesPerDataKey);
    }

    [Fact]
    public void The_ceilings_are_24_hours_and_one_hour()
    {
        Assert.Equal(TimeSpan.FromHours(24), EnvelopeOptions.MaxDataKeyLifetime);
        Assert.Equal(TimeSpan.FromHours(1), EnvelopeOptions.MaxUnwrappedKeyTtl);
    }

    [Fact]
    public void A_data_key_lifetime_up_to_the_ceiling_is_accepted_and_above_it_is_refused()
    {
        using EnvelopePayloadProtector atCeiling = Make(new EnvelopeOptions { DataKeyLifetime = TimeSpan.FromHours(24) });

        ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(() => Make(new EnvelopeOptions { DataKeyLifetime = TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1) }));
        Assert.Equal("DataKeyLifetime", e.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(new EnvelopeOptions { DataKeyLifetime = TimeSpan.FromDays(365) }));
    }

    [Fact]
    public void An_unwrapped_key_cache_up_to_the_ceiling_is_accepted_and_above_it_is_refused()
    {
        using EnvelopePayloadProtector atCeiling = Make(new EnvelopeOptions { UnwrappedKeyTtl = TimeSpan.FromHours(1) });

        ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(() => Make(new EnvelopeOptions { UnwrappedKeyTtl = TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1) }));
        Assert.Equal("UnwrappedKeyTtl", e.ParamName);
    }

    [Fact]
    public void The_shorter_scopes_are_inside_the_ceilings()
    {
        using EnvelopePayloadProtector one = Make(EnvelopeOptions.OneKeyPerValue);
        using EnvelopePayloadProtector shortLived = Make(EnvelopeOptions.ShortLived(TimeSpan.FromMinutes(10)));
    }
}
