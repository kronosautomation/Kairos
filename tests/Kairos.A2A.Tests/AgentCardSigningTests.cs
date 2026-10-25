using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2A;
using Kairos.A2A;

namespace Kairos.A2A.Tests;

public sealed class JsonCanonicalizerTests
{
    private static string Canon(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(doc.RootElement));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("10", "10")]
    [InlineData("-5", "-5")]
    [InlineData("4.5", "4.5")]
    [InlineData("0.002", "0.002")]
    [InlineData("2e-3", "0.002")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("1e-7", "1e-7")]
    [InlineData("1E30", "1e+30")]
    [InlineData("1e21", "1e+21")]
    [InlineData("1e20", "100000000000000000000")]
    [InlineData("333333333.33333329", "333333333.3333333")]
    [InlineData("123456789012345680000", "123456789012345680000")]
    [InlineData("1.5e300", "1.5e+300")]
    public void Numbers_use_the_ecmascript_form(string input, string expected) => Assert.Equal(expected, Canon(input));

    [Fact]
    public void Members_are_ordered_by_utf16_code_units_as_in_the_rfc_example()
    {
        string canon = Canon("""{"\u20ac":"Euro Sign","\r":"Carriage Return","\ufb33":"Hebrew Letter Dalet With Dagesh","1":"One","\ud83d\ude00":"Emoji: Grinning Face","\u0080":"Control","\u00f6":"Latin Small Letter O With Diaeresis"}""");

        Assert.Equal(
            "{\"\\r\":\"Carriage Return\",\"1\":\"One\",\"\u0080\":\"Control\",\"\u00f6\":\"Latin Small Letter O With Diaeresis\",\"\u20ac\":\"Euro Sign\",\"\ud83d\ude00\":\"Emoji: Grinning Face\",\"\ufb33\":\"Hebrew Letter Dalet With Dagesh\"}",
            canon);
    }

    [Fact]
    public void Strings_use_the_minimal_escaping()
    {
        Assert.Equal("\"\u20ac$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"", Canon("\"\\u20ac$\\u000F\\u000aA'\\u0042\\u0022\\u005c\\\\\\\"/\""));
    }

    [Fact]
    public void Whitespace_and_nesting_are_normalised_and_arrays_keep_their_order()
    {
        Assert.Equal("""{"a":[3,1,2],"b":{"x":true,"y":null,"z":"s"}}""", Canon("""
            { "b" : { "z":"s", "y": null, "x": true },
              "a" : [ 3, 1, 2 ] }
            """));
    }
}

public sealed class AgentCardSigningTests
{
    private static AgentCard Card() => new()
    {
        Name = "Invoice agent",
        Description = "Pays invoices after approval.",
        Version = "1.2.0",
        SupportedInterfaces = [new AgentInterface { Url = "https://agents.example.com/invoice", ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],
        Capabilities = new AgentCapabilities { Streaming = false },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["application/json"],
        Skills = [new AgentSkill { Id = "pay", Name = "Pay an invoice", Description = "Releases a payment.", Tags = ["finance"] }],
    };

    private static (AgentCardSigner Signer, AgentCardVerifier Verifier, ECDsa Key) Keys(string kid = "k1")
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new AgentCardSigner(key, kid);
        return (signer, new AgentCardVerifier(new Dictionary<string, byte[]> { [kid] = signer.ExportPublicKey() }), key);
    }

    private static byte[] ToJson(AgentCard card) => JsonSerializer.SerializeToUtf8Bytes(card, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<AgentCard>)A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentCard)));

    [Fact]
    public void A_signed_card_verifies_and_carries_a_jws_signature()
    {
        (AgentCardSigner signer, AgentCardVerifier verifier, ECDsa key) = Keys();
        using (key)
        {
            AgentCard signed = signer.Sign(Card());

            AgentCardSignature signature = Assert.Single(signed.Signatures!);
            Assert.Equal(64, Convert.FromBase64String(signature.Signature.Replace('-', '+').Replace('_', '/').PadRight(88, '=')).Length);
            Assert.Contains("ES256", Encoding.UTF8.GetString(Convert.FromBase64String(signature.Protected.Replace('-', '+').Replace('_', '/').PadRight((signature.Protected.Length + 3) / 4 * 4, '='))), StringComparison.Ordinal);
            AgentCardVerification result = verifier.Verify(signed);
            Assert.True(result.IsValid, result.Reason);
            Assert.Equal("k1", result.KeyId);
            Assert.True(verifier.VerifyJson(ToJson(signed)).IsValid);
        }
    }

    [Theory]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("version")]
    [InlineData("url")]
    [InlineData("skill")]
    [InlineData("capability")]
    [InlineData("inputMode")]
    public void Changing_anything_in_a_signed_card_invalidates_it(string what)
    {
        (AgentCardSigner signer, AgentCardVerifier verifier, ECDsa key) = Keys();
        using (key)
        {
            AgentCard signed = signer.Sign(Card());
            switch (what)
            {
                case "name": signed.Name = "Evil agent"; break;
                case "description": signed.Description += " "; break;
                case "version": signed.Version = "9.9.9"; break;
                case "url": signed.SupportedInterfaces[0].Url = "https://evil.example.com/invoice"; break;
                case "skill": signed.Skills[0].Description = "Releases any payment."; break;
                case "capability": signed.Capabilities.Streaming = true; break;
                default: signed.DefaultInputModes = ["text/plain", "image/png"]; break;
            }

            Assert.False(verifier.Verify(signed).IsValid);
        }
    }

    [Fact]
    public void An_unsigned_card_is_refused()
    {
        (_, AgentCardVerifier verifier, ECDsa key) = Keys();
        using (key)
        {
            AgentCardVerification result = verifier.Verify(Card());

            Assert.False(result.IsValid);
            Assert.Contains("no signature", result.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_signature_from_a_key_that_is_not_trusted_is_refused_even_under_a_trusted_name()
    {
        (AgentCardSigner trusted, AgentCardVerifier verifier, ECDsa trustedKey) = Keys("publisher");
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (trustedKey)
        {
            var attacker = new AgentCardSigner(attackerKey, "publisher"); // claims to be the trusted publisher
            AgentCard forged = attacker.Sign(Card());

            Assert.False(verifier.Verify(forged).IsValid);
            Assert.True(verifier.Verify(trusted.Sign(Card())).IsValid);
        }
    }

    [Fact]
    public void A_key_id_nobody_trusts_is_refused()
    {
        (AgentCardSigner signer, _, ECDsa key) = Keys("k1");
        using (key)
        {
            var verifier = new AgentCardVerifier(new Dictionary<string, byte[]> { ["other"] = signer.ExportPublicKey() });

            AgentCardVerification result = verifier.Verify(signer.Sign(Card()));

            Assert.False(result.IsValid);
            Assert.Contains("not trusted", result.Reason, StringComparison.Ordinal);
        }
    }

    private static AgentCard WithProtectedHeader(AgentCard signed, string headerJson)
    {
        // Keeps the genuine signature bytes but swaps the protected header, as an attacker rewriting a card would.
        signed.Signatures![0].Protected = Convert.ToBase64String(Encoding.UTF8.GetBytes(headerJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return signed;
    }

    [Theory]
    [InlineData("""{"alg":"none","kid":"k1"}""")]
    [InlineData("""{"alg":"HS256","kid":"k1"}""")]
    [InlineData("""{"alg":"ES384","kid":"k1"}""")]
    [InlineData("""{"kid":"k1"}""")]
    [InlineData("""{"alg":"ES256","kid":"k1","crit":["exp"],"exp":1}""")]
    [InlineData("""{"alg":"ES256","kid":"k1","jku":"https://evil.example.com/keys.json","extra":1}""")]
    [InlineData("""["ES256"]""")]
    [InlineData("""{"alg":"ES256","kid":7}""")]
    public void A_header_that_names_another_algorithm_or_anything_unexpected_is_refused(string header)
    {
        (AgentCardSigner signer, AgentCardVerifier verifier, ECDsa key) = Keys();
        using (key)
        {
            Assert.False(verifier.Verify(WithProtectedHeader(signer.Sign(Card()), header)).IsValid);
        }
    }

    [Fact]
    public void A_jku_in_the_header_is_ignored_and_nothing_is_fetched()
    {
        (AgentCardSigner signer, AgentCardVerifier verifier, ECDsa key) = Keys();
        using (key)
        {
            // A header with a jku is outside what the signer writes, and the verifier never follows it: the card is judged by the trusted keys only.
            AgentCard card = signer.Sign(Card());
            AgentCard rewritten = WithProtectedHeader(card, """{"alg":"ES256","kid":"k1","jku":"https://evil.example.com/keys.json"}""");

            Assert.False(verifier.Verify(rewritten).IsValid); // the signature no longer matches the changed header
        }
    }

    [Fact]
    public void Garbage_signatures_never_throw()
    {
        (AgentCardSigner signer, AgentCardVerifier verifier, ECDsa key) = Keys();
        using (key)
        {
            foreach ((string prot, string sig) in new[] { ("", ""), ("!!!", "!!!"), ("e30", "AAAA"), (new string('A', 5000), "AAAA"), ("eyJhbGciOiJFUzI1NiJ9", "") })
            {
                AgentCard card = signer.Sign(Card());
                card.Signatures![0].Protected = prot;
                card.Signatures[0].Signature = sig;
                Assert.False(verifier.Verify(card).IsValid);
            }

            Assert.False(verifier.VerifyJson("not json"u8).IsValid);
            Assert.False(verifier.VerifyJson("[]"u8).IsValid);
            Assert.False(verifier.VerifyJson("""{"signatures":"x"}"""u8).IsValid);
            Assert.False(verifier.VerifyJson("""{"signatures":[1]}"""u8).IsValid);
        }
    }

    [Fact]
    public void A_signature_cannot_be_moved_to_another_card()
    {
        (AgentCardSigner signer, AgentCardVerifier verifier, ECDsa key) = Keys();
        using (key)
        {
            AgentCard a = signer.Sign(Card());
            AgentCard b = Card();
            b.Name = "Another agent";
            b.Signatures = a.Signatures;

            Assert.False(verifier.Verify(b).IsValid);
        }
    }

    [Fact]
    public void Several_signatures_are_kept_and_any_trusted_one_is_enough()
    {
        (AgentCardSigner first, _, ECDsa key1) = Keys("k1");
        (AgentCardSigner second, _, ECDsa key2) = Keys("k2");
        using (key1)
        using (key2)
        {
            AgentCard both = second.Sign(first.Sign(Card()));
            Assert.Equal(2, both.Signatures!.Count);

            var onlySecond = new AgentCardVerifier(new Dictionary<string, byte[]> { ["k2"] = second.ExportPublicKey() });
            var neither = new AgentCardVerifier(new Dictionary<string, byte[]> { ["k3"] = second.ExportPublicKey() });
            Assert.Equal("k2", onlySecond.Verify(both).KeyId);
            Assert.False(neither.Verify(both).IsValid);
        }
    }

    [Fact]
    public void A_card_received_as_json_is_verified_as_received_including_fields_the_sdk_does_not_model()
    {
        (AgentCardSigner signer, AgentCardVerifier verifier, ECDsa key) = Keys();
        using (key)
        {
            const string Unsigned = """{"name":"Remote","description":"d","version":"1","futureField":{"b":2,"a":[1,2]},"supportedInterfaces":[{"url":"https://x.example/a","protocolBinding":"JSONRPC","protocolVersion":"1.0"}],"capabilities":{},"skills":[{"id":"s","name":"s","description":"s","tags":["t"]}],"defaultInputModes":["text/plain"],"defaultOutputModes":["text/plain"]}""";

            byte[] signed = signer.SignJson(Encoding.UTF8.GetBytes(Unsigned));

            Assert.True(verifier.VerifyJson(signed).IsValid);
            string tampered = Encoding.UTF8.GetString(signed).Replace("\"a\":[1,2]", "\"a\":[1,3]", StringComparison.Ordinal);
            Assert.False(verifier.VerifyJson(Encoding.UTF8.GetBytes(tampered)).IsValid);
            // Re-serializing through the SDK drops the unknown field, which is exactly why cards from elsewhere are checked as received.
            Assert.False(verifier.Verify(AgentCardSigning_Deserialize(signed)).IsValid);
        }
    }

    private static AgentCard AgentCardSigning_Deserialize(byte[] json) =>
        JsonSerializer.Deserialize(json, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<AgentCard>)A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentCard)))!;

    [Fact]
    public void Key_requirements_are_enforced()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ArgumentException>(() => new AgentCardSigner(p384, "k"));
        Assert.Throws<ArgumentException>(() => new AgentCardVerifier(new Dictionary<string, byte[]> { ["k"] = p384.ExportSubjectPublicKeyInfo() }));
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<ArgumentException>(() => new AgentCardSigner(p256, " "));
    }
}
