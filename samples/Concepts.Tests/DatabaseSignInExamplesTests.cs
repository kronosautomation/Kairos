using Kairos.Samples.Concepts;

namespace Kairos.Samples.Concepts.Tests;

/// <summary>The regions of the database-sign-in page: the token source hands the data source whatever its fetch returns, for the endpoint it was asked about, and connecting with it is a chainable option.</summary>
public sealed class DatabaseSignInExamplesTests
{
    [Fact]
    public async Task The_example_token_source_returns_what_its_fetch_returns_for_the_endpoint_it_was_asked_about()
    {
        DatabaseEndpoint? asked = null;
        var source = new DatabaseSignInExamples.MyTokenSource((endpoint, _) =>
        {
            asked = endpoint;
            return ValueTask.FromResult("short-lived-token");
        });

        string token = await source.GetPasswordAsync(new DatabaseEndpoint("db.example.test", 5432, "kairos_dataplane"), CancellationToken.None);

        Assert.Equal("short-lived-token", token);
        Assert.Equal(new DatabaseEndpoint("db.example.test", 5432, "kairos_dataplane"), asked);
    }

    [Fact]
    public void Connecting_with_a_token_source_returns_the_same_options_for_chaining()
    {
        var options = new KairosOptions();
        var source = new DatabaseSignInExamples.MyTokenSource((_, _) => ValueTask.FromResult("t"));

        KairosOptions result = DatabaseSignInExamples.Connect(options, "Host=db.example.test;Database=kairos;Username=kairos_dataplane;SSL Mode=VerifyFull", source);

        Assert.Same(options, result);
    }
}
