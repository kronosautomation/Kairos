using System.Security.Cryptography;
using System.Text;
using A2A;
using Kairos.A2A;
using Kairos.Agents;
using Kairos.Agents.A2aClient;

namespace Kairos.Samples.Agents;

public static class A2aExamples
{
    #region a2a-sign
    // The publisher signs the Agent Card it serves. Anyone who holds the public key can check the card is exactly what the publisher signed.
    public static (AgentCard Card, byte[] PublicKey) SignCard(AgentCard card, ECDsa privateKey)
    {
        var signer = new AgentCardSigner(privateKey, keyId: "billing-2026");
        return (signer.Sign(card), signer.ExportPublicKey());
    }
    #endregion

    #region a2a-verify
    // A caller trusts the keys it names and nothing the card says about itself. A card that is unsigned, altered, or signed by another key is refused.
    public static bool IsTrusted(AgentCard card, byte[] publisherPublicKey)
    {
        var verifier = new AgentCardVerifier(new Dictionary<string, byte[]> { ["billing-2026"] = publisherPublicKey });
        return verifier.Verify(card).IsValid;
    }
    #endregion

    #region a2a-client-setup
    // Name the remote agents a workflow may call. Cards are fetched from the address of each agent and checked against the keys you trust.
    public static A2aClientRuntime ConfigureA2a(HttpClient http, byte[] billingPublicKey)
    {
        var verifier = new AgentCardVerifier(new Dictionary<string, byte[]> { ["billing-2026"] = billingPublicKey });
        return new A2aClientsBuilder()
            .AddAgent("billing", new Uri("https://agents.example.com/billing/"), http, verifier) // refuses an unsigned or untrusted card
            .ReadOnlyAgents("billing") // answers questions only; any other agent waits for an approval before it is called
            .Build();
    }
    #endregion

    #region a2a-call
    public static async Task<byte[]> CollectInvoiceAsync(IAgentContext ctx, byte[] input)
    {
        string invoice = Encoding.UTF8.GetString(input);
        try
        {
            // Recorded once. A long task is polled with durable timers, so a restart resumes polling instead of sending the message again.
            A2aAgentResult result = await ctx.CallA2aAgentAsync("billing", $"What is the status of invoice {invoice}?");
            return Encoding.UTF8.GetBytes(result.Text ?? "no answer");
        }
        catch (A2aInputRequiredException ask)
        {
            // The agent asked a question. Answer it by continuing the same task.
            A2aAgentResult result = await ctx.CallA2aAgentAsync("billing", "The invoice is for customer 42.", new A2aCallOptions { TaskId = ask.TaskId });
            return Encoding.UTF8.GetBytes(result.Text ?? "no answer");
        }
    }
    #endregion
}
