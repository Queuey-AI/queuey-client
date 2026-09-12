using System;
using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Cli.Advise;

/// <summary>How events should leave this repository.</summary>
public enum SendPath
{
    /// <summary>Nothing here looks like it produces events.</summary>
    None,

    /// <summary>Events already survive a crash somewhere; send from there. Do not rebuild what exists.</summary>
    ClientFromExistingDurability,

    /// <summary>Local disk survives a restart, so a spool can hold events through an outage.</summary>
    Edge,

    /// <summary>Edge as a daemon with a loopback endpoint — durability for a language the SDK does not ship for.</summary>
    EdgeDaemon,

    /// <summary>Publish straight to the ingress with the SDK.</summary>
    Client,

    /// <summary>Publish straight to the ingress over plain HTTP.</summary>
    PlainHttp,
}

/// <summary>What we think, why we think it, and what we could not know from the code.</summary>
public sealed record Advice
{
    public bool Sends { get; init; }
    public bool Receives { get; init; }
    public SendPath Send { get; init; }

    /// <summary>One sentence a human can act on.</summary>
    public string Headline { get; init; } = "";

    /// <summary>The reasoning, each line carrying the file it was read from.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    /// <summary>What to do next, in order.</summary>
    public IReadOnlyList<string> NextSteps { get; init; } = Array.Empty<string>();

    /// <summary>What the repository cannot answer, and a human must.</summary>
    public IReadOnlyList<string> Questions { get; init; } = Array.Empty<string>();

    /// <summary>Receiving is not a choice, it is a recipe. Present when something here takes webhooks in.</summary>
    public IReadOnlyList<string> ReceivingSteps { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Turns what the repository says into a recommendation.
///
/// Three questions, in order. Does this send, receive, or both — that decides
/// everything after it. On the sending side, does durability already exist, and
/// does local disk survive a restart. Note the order of those last two: a
/// service that scales to zero without a volume cannot use Edge no matter how
/// bad its network is, so disk is asked before the network. "No retries today,
/// so use Edge" gets that case wrong.
///
/// One rule runs through all of it: never advise building a local queue, retry
/// loop, backoff or offline buffer by hand. That is Edge, finished and
/// maintained. An agent is good at writing those two hundred lines, which is
/// exactly why it should be told not to.
/// </summary>
public static class Recommendation
{
    public const string NetworkQuestion =
        "Is the network where this runs one you can trust? A factory, kiosk, vehicle or on-prem box " +
        "usually is not. The repository cannot tell me this, and it is the one thing that decides Edge.";

    public const string DoNotRebuild =
        "Do not hand-write a local queue, retry loop, backoff or offline buffer. That is what Queuey.Edge " +
        "is, already built and maintained — reach for it instead, unless you have been asked for your own.";

    public static Advice For(RepoFacts facts)
    {
        if (facts is null) throw new ArgumentNullException(nameof(facts));

        var sends = LooksLikeItSends(facts);
        var receives = facts.Receives;
        var send = ChooseSendPath(facts, sends);

        return new Advice
        {
            Sends = sends,
            Receives = receives,
            Send = send,
            Headline = Headline(send, receives),
            Reasons = Reasons(facts, send).ToArray(),
            NextSteps = NextSteps(facts, send).ToArray(),
            Questions = Questions(facts, send).ToArray(),
            ReceivingSteps = receives ? ReceivingRecipe().ToArray() : Array.Empty<string>(),
        };
    }

    /// <summary>
    /// Absent a reason to think otherwise, a codebase produces events. Receiving
    /// is the narrower case, and it does not exclude sending.
    /// </summary>
    private static bool LooksLikeItSends(RepoFacts facts) =>
        facts.Ecosystems.Count > 0 || facts.Durability.Count > 0 || !facts.Receives;

    private static SendPath ChooseSendPath(RepoFacts facts, bool sends)
    {
        if (!sends) return SendPath.None;

        // 1. Durability already exists. Send from the consumer that already has
        //    it; rebuilding that layer is the most expensive wrong answer here.
        if (facts.HasDurability) return SendPath.ClientFromExistingDurability;

        // 2. Disk before network. Without storage that survives a restart, Edge
        //    is not available however unreliable the network is.
        if (facts.HasDurableDisk) return facts.IsDotNet ? SendPath.Edge : SendPath.EdgeDaemon;

        // 3. Ephemeral hosting: no spool is possible, so publish directly and
        //    lean on the database it already has if the network is unreliable.
        if (facts.IsEphemeral) return facts.IsDotNet ? SendPath.Client : SendPath.PlainHttp;

        return facts.IsDotNet ? SendPath.Client : SendPath.PlainHttp;
    }

    private static string Headline(SendPath send, bool receives) => send switch
    {
        SendPath.None when receives => "Receive with Queuey: verify the signature, be idempotent on the event id.",
        SendPath.None => "Nothing here looks like it sends or receives events yet.",
        SendPath.ClientFromExistingDurability =>
            "Send with Queuey.Client from the consumer you already have. You already have durability; keep it.",
        SendPath.Edge => "Publish through Queuey.Edge: this machine has disk that survives a restart.",
        SendPath.EdgeDaemon =>
            "Run Queuey Edge as a daemon and publish to its loopback endpoint: durable from any language.",
        SendPath.Client => "Publish to the ingress with Queuey.Client.",
        SendPath.PlainHttp => "Publish to the ingress over plain HTTP.",
        _ => "",
    } + (receives && send != SendPath.None ? " This repository also receives, so both halves apply." : "");

    private static IEnumerable<string> Reasons(RepoFacts facts, SendPath send)
    {
        if (facts.QueueyAlready.Count > 0)
            yield return $"Queuey is already here: {Join(facts.QueueyAlready)}.";

        if (facts.Ecosystems.Count > 0)
            yield return $"Ecosystem: {string.Join(", ", facts.Ecosystems)}.";

        switch (send)
        {
            case SendPath.ClientFromExistingDurability:
                yield return $"Events already survive a crash here: {Join(facts.Durability)}. " +
                             "Publish from that consumer, so an event that is committed is also one Queuey will get.";
                break;

            case SendPath.Edge:
            case SendPath.EdgeDaemon:
                yield return $"Storage survives a restart: {Join(facts.DurableDisk)}. " +
                             "That is what lets Edge hold events locally through an outage.";
                if (send == SendPath.EdgeDaemon)
                    yield return "The SDK ships for .NET only today, so the daemon plus its loopback endpoint is the way in from another language.";
                break;

            case SendPath.Client:
            case SendPath.PlainHttp:
                if (facts.IsEphemeral)
                    yield return $"Local disk does not survive here: {Join(facts.EphemeralHosting)}. " +
                                 "Edge needs a spool that outlives a restart, so it is not an option — regardless of the network.";
                else
                    yield return "Nothing here suggests an unreliable network or a spool to hold events, so publish directly.";
                break;
        }

        if (facts.Receives)
            yield return $"Something here takes webhooks in: {Join(facts.Receiving)}.";
    }

    private static IEnumerable<string> NextSteps(RepoFacts facts, SendPath send)
    {
        switch (send)
        {
            case SendPath.ClientFromExistingDurability:
                yield return "Add Queuey.Client to the project that owns the consumer.";
                yield return "Publish from inside the consumer that already runs after the commit — not from the request path.";
                yield return "Keep your bus or outbox. Queuey is the destination, not a replacement for it.";
                break;

            case SendPath.Edge:
                yield return "Add Queuey.Edge to the producing project.";
                yield return "Point Storage.Path at the durable disk, not a temp directory.";
                yield return "Publish with PublishAsync — it commits locally before it returns, and transfer, retries and backlog draining are Edge's job.";
                yield return "Turn on Health.ReportToCloud so the node shows up under Edge nodes and Queuey can tell you when it goes quiet.";
                break;

            case SendPath.EdgeDaemon:
                yield return "Install the queuey CLI on the machine: dotnet tool install -g Queuey.Cli --prerelease, or the standalone binary from the releases page.";
                yield return "Run: queuey edge run --spool /var/lib/queuey/spool.db --listen 7300 --report-health";
                yield return "Publish from your code to http://localhost:7300/events/{tenant}/{queue} — same wire shape as the cloud ingress, and a 202 means it is committed locally.";
                break;

            case SendPath.Client:
                yield return "Add Queuey.Client and publish to the ingress.";
                if (facts.IsEphemeral)
                    yield return "If the network here is unreliable, write to the database you already have and publish from a worker that reads it — that is your durability, since local disk is not.";
                break;

            case SendPath.PlainHttp:
                yield return "POST the event to the ingress with your HTTP client of choice; a 202 means Queuey has it durably.";
                if (facts.IsEphemeral)
                    yield return "If the network here is unreliable, write to the database you already have and publish from a worker that reads it — that is your durability, since local disk is not.";
                break;
        }

        if (send != SendPath.None)
        {
            yield return "Create the workspace and queue: queuey create-tenant, then queuey create-queue. The API key itself is minted in the console.";
        }
    }

    private static IEnumerable<string> Questions(RepoFacts facts, SendPath send)
    {
        // The network is never in the repository. Ask it wherever it would
        // change the answer — which is anywhere a spool is possible.
        if (send is SendPath.Client or SendPath.PlainHttp && !facts.IsEphemeral)
            yield return NetworkQuestion;

        if (send is SendPath.Edge or SendPath.EdgeDaemon)
            yield return "Confirm the path you point the spool at is on the durable disk, not a temp directory that the host clears.";

        if (send == SendPath.ClientFromExistingDurability)
            yield return "Confirm the consumer runs after the commit, not inside the request — that is what makes it durable.";
    }

    private static IEnumerable<string> ReceivingRecipe()
    {
        yield return "Verify the signature over the RAW body, before anything deserializes it. Queuey.Client ships QueueyDeliveryVerifier; do not write the HMAC by hand.";
        yield return "Be idempotent on the event id (X-Queuey-Event-Id). A redelivery after a timeout carries the same id.";
        yield return "While developing, run: queuey listen --forward-to http://localhost:<port>/<path> — real deliveries, no inbound port open.";
    }

    private static string Join(IReadOnlyList<Evidence> evidence) =>
        string.Join(", ", evidence.Take(4).Select(e => e.ToString()))
        + (evidence.Count > 4 ? $", and {evidence.Count - 4} more" : "");
}
