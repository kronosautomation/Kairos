using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2A;

namespace Kairos.A2A;

/// <summary>Why an Agent Card was or was not accepted.</summary>
/// <param name="IsValid">True when at least one signature on the card verified against a trusted key.</param>
/// <param name="KeyId">The trusted key that verified, when valid.</param>
/// <param name="Reason">What was wrong, when not valid.</param>
public sealed record AgentCardVerification(bool IsValid, string? KeyId, string? Reason)
{
    public static AgentCardVerification Valid(string keyId) => new(true, keyId, null);

    public static AgentCardVerification Invalid(string reason) => new(false, null, reason);
}

/// <summary>
/// Signs Agent Cards the way the A2A specification describes: a JWS (RFC 7515) with a protected header, over the card's canonical JSON
/// (RFC 8785) with the <c>signatures</c> field left out. Only ES256 (ECDSA P-256 with SHA-256) is produced, so a verifier needs just the public key.
/// </summary>
public sealed class AgentCardSigner
{
    public const string Algorithm = "ES256";

    private readonly ECDsa _key;
    private readonly string _keyId;

    /// <param name="privateKey">A P-256 key pair. The signer keeps a reference; dispose it yourself.</param>
    /// <param name="keyId">Names the key in the signature header; verifiers look it up in their trusted keys.</param>
    public AgentCardSigner(ECDsa privateKey, string keyId)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (privateKey.KeySize != 256)
        {
            throw new ArgumentException("Agent Cards are signed with ECDSA P-256.", nameof(privateKey));
        }

        _key = privateKey;
        _keyId = keyId;
    }

    /// <summary>The public half, to give to the agents and organizations that should trust this publisher (SubjectPublicKeyInfo).</summary>
    public byte[] ExportPublicKey() => _key.ExportSubjectPublicKeyInfo();

    /// <summary>Returns the card with one more signature, bound to everything else in it. Existing signatures are kept.</summary>
    public AgentCard Sign(AgentCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        JsonElement element = AgentCardSigning.ToElement(card);
        return AgentCardSigning.Deserialize(AgentCardSigning.AddSignature(element, SignatureFor(element)));
    }

    /// <summary>Signs a card as JSON and returns the signed JSON, for publishers that build the card document themselves.</summary>
    public byte[] SignJson(ReadOnlySpan<byte> cardJson)
    {
        using JsonDocument doc = JsonDocument.Parse(cardJson.ToArray());
        return AgentCardSigning.AddSignature(doc.RootElement, SignatureFor(doc.RootElement));
    }

    private AgentCardSignature SignatureFor(JsonElement card)
    {
        string header = AgentCardSigning.Base64Url(Encoding.UTF8.GetBytes($"{{\"alg\":\"{Algorithm}\",\"kid\":{JsonSerializer.Serialize(_keyId, AgentCardSigning.StringInfo)},\"typ\":\"JOSE\"}}"));
        string payload = AgentCardSigning.Base64Url(AgentCardSigning.Payload(card));
        byte[] signature = _key.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new AgentCardSignature { Protected = header, Signature = AgentCardSigning.Base64Url(signature) };
    }
}

/// <summary>
/// Checks Agent Card signatures against keys you trust. A card is accepted only if one of its signatures verifies with a key you named; the key
/// named in the card is never fetched or believed (a card's <c>jku</c> is ignored), the algorithm must be exactly ES256, and anything unusual in
/// the protected header refuses the signature.
/// </summary>
public sealed class AgentCardVerifier
{
    private readonly Dictionary<string, ECDsa> _trusted = new(StringComparer.Ordinal);

    /// <param name="trustedKeys">Key id to ECDSA P-256 public key (SubjectPublicKeyInfo), for example from <see cref="AgentCardSigner.ExportPublicKey"/>.</param>
    public AgentCardVerifier(IReadOnlyDictionary<string, byte[]> trustedKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        foreach ((string id, byte[] key) in trustedKeys)
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out _);
            if (ecdsa.KeySize != 256)
            {
                ecdsa.Dispose();
                throw new ArgumentException($"Key '{id}' is not an ECDSA P-256 key.", nameof(trustedKeys));
            }

            _trusted[id] = ecdsa;
        }
    }

    /// <summary>Verifies a card exactly as it was received. Use this for cards you fetched, so nothing the SDK does not model is lost before the check.</summary>
    public AgentCardVerification VerifyJson(ReadOnlySpan<byte> cardJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(cardJson.ToArray());
        }
        catch (JsonException)
        {
            return AgentCardVerification.Invalid("the card is not valid JSON");
        }

        using (doc)
        {
            return VerifyElement(doc.RootElement);
        }
    }

    /// <summary>Verifies a card object by serializing it again. Prefer <see cref="VerifyJson"/> for a card received from elsewhere.</summary>
    public AgentCardVerification Verify(AgentCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return VerifyElement(AgentCardSigning.ToElement(card));
    }

    private AgentCardVerification VerifyElement(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object || !card.TryGetProperty("signatures", out JsonElement signatures) || signatures.ValueKind != JsonValueKind.Array || signatures.GetArrayLength() == 0)
        {
            return AgentCardVerification.Invalid("the card carries no signature");
        }

        string payload = AgentCardSigning.Base64Url(AgentCardSigning.Payload(card));
        string reason = "no signature verified against a trusted key";
        foreach (JsonElement entry in signatures.EnumerateArray())
        {
            (string? keyId, string? why) = CheckEntry(entry, payload);
            if (keyId is not null)
            {
                return AgentCardVerification.Valid(keyId);
            }

            reason = why ?? reason;
        }

        return AgentCardVerification.Invalid(reason);
    }

    private (string? KeyId, string? Reason) CheckEntry(JsonElement entry, string payload)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("protected", out JsonElement protectedElement) || protectedElement.ValueKind != JsonValueKind.String ||
            !entry.TryGetProperty("signature", out JsonElement signatureElement) || signatureElement.ValueKind != JsonValueKind.String)
        {
            return (null, "a signature is malformed");
        }

        string protectedB64 = protectedElement.GetString()!;
        if (protectedB64.Length > 2048)
        {
            return (null, "a signature header is too large");
        }

        try
        {
            using JsonDocument header = JsonDocument.Parse(AgentCardSigning.FromBase64Url(protectedB64));
            JsonElement h = header.RootElement;
            if (h.ValueKind != JsonValueKind.Object)
            {
                return (null, "a signature header is not an object");
            }

            foreach (JsonProperty p in h.EnumerateObject())
            {
                if (p.Name is not ("alg" or "kid" or "typ" or "jku"))
                {
                    return (null, $"a signature header has the unsupported field '{p.Name}'"); // includes "crit": unknown critical extensions are refused
                }
            }

            if (!h.TryGetProperty("alg", out JsonElement alg) || alg.GetString() != AgentCardSigner.Algorithm)
            {
                return (null, "a signature does not use ES256");
            }

            if (!h.TryGetProperty("kid", out JsonElement kid) || kid.ValueKind != JsonValueKind.String || !_trusted.TryGetValue(kid.GetString()!, out ECDsa? key))
            {
                return (null, "a signature names a key that is not trusted");
            }

            byte[] signature = AgentCardSigning.FromBase64Url(signatureElement.GetString()!);
            bool ok = signature.Length == 64 && key.VerifyData(
                Encoding.ASCII.GetBytes(protectedB64 + "." + payload), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return ok ? (kid.GetString(), null) : (null, "a signature does not match the card");
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return (null, "a signature is malformed");
        }
    }
}

internal static class AgentCardSigning
{
    public static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<string> StringInfo =
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<string>)A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(string));

    public static JsonElement ToElement(AgentCard card) =>
        JsonSerializer.SerializeToElement(card, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<AgentCard>)A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentCard)));

    public static AgentCard Deserialize(byte[] json) =>
        JsonSerializer.Deserialize(json, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<AgentCard>)A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(AgentCard)))
        ?? throw new FormatException("Empty Agent Card.");

    /// <summary>The bytes that are signed: the canonical form of the card without its signatures.</summary>
    public static byte[] Payload(JsonElement card)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (JsonProperty p in card.EnumerateObject())
            {
                if (p.Name != "signatures")
                {
                    p.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using JsonDocument without = JsonDocument.Parse(stream.ToArray());
        return JsonCanonicalizer.Canonicalize(without.RootElement);
    }

    public static byte[] AddSignature(JsonElement card, AgentCardSignature signature)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (JsonProperty p in card.EnumerateObject())
            {
                if (p.Name != "signatures")
                {
                    p.WriteTo(writer);
                }
            }

            writer.WriteStartArray("signatures");
            if (card.TryGetProperty("signatures", out JsonElement existing) && existing.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in existing.EnumerateArray())
                {
                    e.WriteTo(writer);
                }
            }

            writer.WriteStartObject();
            writer.WriteString("protected", signature.Protected);
            writer.WriteString("signature", signature.Signature);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        string padded = s.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
