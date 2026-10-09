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

    /// <summary>What decides Edge where the repository does not show it: whether the disk outlives a restart.</summary>
    public const string DiskQuestion =
        "Does the disk where this runs survive a restart — a server or VM, or a container with a volume? Then Queuey.Edge fits, " +
        "and holds events through an outage; a container without a volume or a serverless function cannot keep a spool.";

    /// <summary>The page that shows how to sign a request by hand.</summary>
    public const string SignedRequestsDocs = "https://queuey.ai/docs/md/how-to/signed-requests";

    public const string DoNotRebuild =
        "Do not hand-write a local queue, retry loop, backoff or offline buffer. That is what Queuey.Edge " +
        "is, already built and maintained — reach for it instead, unless you have been asked for your own.";

    /// <summary>The production ingress route, in the shape every docs page and the quickstart use.</summary>
    public const string IngressRoute = "https://ingress.queuey.ai/events/{tenantPublicId}/{queueName}";

    /// <summary>The file <c>keys mint</c> writes the Edge daemon's signing key to, which the service loads as its environment.</summary>
    public const string DaemonEnvironmentFile = "edge.env";

    /// <summary>Where the self-contained CLI binaries are, for a machine with no .NET.</summary>
    public const string ReleasesUrl = "https://github.com/Queuey-AI/queuey-client/releases/latest";

    /// <param name="facts">What the scan found.</param>
    /// <param name="write">
    /// Where <c>keys mint</c> and <c>credentials generate</c> put a secret for this repository: <c>user-secrets</c> for a .NET
    /// project with a <c>UserSecretsId</c>, else <c>.env</c> (<see cref="SecretTarget.SuggestedFor"/>).
    /// </param>
    public static Advice For(RepoFacts facts, string write = ".env")
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
            NextSteps = NextSteps(facts, send, write).ToArray(),
            Questions = Questions(facts, send).ToArray(),
            ReceivingSteps = receives ? ReceivingRecipe(facts, write).ToArray() : Array.Empty<string>(),
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
            yield return facts.EcosystemEvidence.Count > 0
                ? $"Ecosystem: {Join(facts.EcosystemEvidence)}."
                : $"Ecosystem: {string.Join(", ", facts.Ecosystems)}.";

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
                    // Blindtest 2 (2026-10-09, funn 7): et lite web-API fikk Client uten at svaret sa hvorfor ikke Edge. Grunnen er disken:
                    // ingenting her viser at den overlever en omstart, og uten det kan ingen spool holde events.
                    yield return $"Nothing in the {facts.FilesRead.Count} file(s) read (filesRead) shows disk that survives a restart — " +
                                 "no volume, systemd unit or server install — so a spool cannot be counted on, and Queuey.Edge needs one. " +
                                 "Nothing suggests an unreliable network either, so publish directly. If this runs where the disk survives " +
                                 "a restart (a server or VM, or a container with a volume), Queuey.Edge fits instead: see the questions.";
                break;
        }

        if (facts.Receives)
            yield return $"Something here takes webhooks in: {Join(facts.Receiving)}.";
    }

    private static IEnumerable<string> NextSteps(RepoFacts facts, SendPath send, string write)
    {
        switch (send)
        {
            case SendPath.ClientFromExistingDurability:
                yield return "Add Queuey.Client to the project that owns the consumer: dotnet add package Queuey.Client --prerelease.";
                yield return "Publish from inside the consumer that already runs after the commit — not from the request path.";
                yield return "Keep your bus or outbox. Queuey is the destination, not a replacement for it.";
                break;

            case SendPath.Edge:
                yield return "Add Queuey.Edge to the producing project: dotnet add package Queuey.Edge --prerelease.";
                yield return "Point Storage.Path at the durable disk, not a temp directory.";
                yield return "Publish with PublishAsync — it commits locally before it returns, and transfer, retries and backlog draining are Edge's job.";
                // Edge-signering (Kenneth 2026-10-09): Edge signerer med samme nøkkel som klienten, lest etter de samme reglene.
                yield return write == SecretTarget.UserSecretsWord
                    ? "Configure it with options.UseSettings(builder.Configuration): Edge reads the workspace's signing key from the user " +
                      "secrets and signs every transfer when it sends it, so a backlog never goes out with a stale signature."
                    : "Configure it with options.UseEnvironmentVariables(): Edge reads the workspace's signing key from the environment, " +
                      "and in Development from .env, and signs every transfer when it sends it, so a backlog never goes out with a stale signature.";
                // Security-review av #70 (B1): helse-nøkkelen fra miljøet eller konfigurasjonen, aldri en literal.
                yield return "Turn on Health.ReportToCloud so the node shows up under Edge nodes and Queuey can tell you when it goes quiet. " +
                             "Queuey's check-in takes an API key today: put a publish-only key in " +
                             (write == SecretTarget.UserSecretsWord
                                 ? "the user secrets as QUEUEY_EDGE_HEALTH_API_KEY, which UseSettings reads"
                                 : "the environment as QUEUEY_EDGE_HEALTH_API_KEY, which UseEnvironmentVariables reads") +
                             ". Only the check-in uses it; events stay signed.";
                break;

            case SendPath.EdgeDaemon:
                yield return "Install the queuey CLI on the machine: dotnet tool install -g Queuey.Cli --prerelease, " +
                             $"or — with no .NET — the self-contained binary for its platform from {ReleasesUrl}.";
                yield return "Run: queuey edge run --spool /var/lib/queuey/spool.db --listen 7300, with the signing key in its " +
                             $"environment: load {DaemonEnvironmentFile} as the service's environment file (systemd EnvironmentFile=). " +
                             $"Add --report-health to see the node under Edge nodes, with a publish-only key as QUEUEY_EDGE_HEALTH_API_KEY in {DaemonEnvironmentFile}: " +
                             "Queuey's check-in takes an API key today, and only the check-in uses it.";
                yield return "Publish from your code to http://localhost:7300/events/{tenant}/{queue} — same wire shape as the cloud ingress, and a 202 means it is committed locally.";
                break;

            case SendPath.Client:
                yield return "Add Queuey.Client (dotnet add package Queuey.Client --prerelease) and publish to the ingress, with the workspace's signing key " +
                             (write == SecretTarget.UserSecretsWord
                                 ? "from the user secrets (QueueyOptions.UseSettings(key => builder.Configuration[key]))."
                                 : "from the environment, and in Development from .env (QueueyOptions.UseEnvironmentVariables()).");
                if (facts.IsEphemeral)
                    yield return "If the network here is unreliable, write to the database you already have and publish from a worker that reads it — that is your durability, since local disk is not.";
                break;

            case SendPath.PlainHttp:
                // The whole integration is one request, so say which one. "Use
                // your HTTP client of choice" was correct and left an agent to
                // guess the route and the header name.
                // Skillen (2026-10-09): workspacet tar bare signerte requests, så et kall med X-Api-Key ville blitt avvist.
                yield return $"POST each event to {IngressRoute} with Content-Type: application/json, signed with the workspace's " +
                             "signing key (QUEUEY_SIGNING_KEY_ID and QUEUEY_SIGNING_SECRET): five X-Queuey-* headers and an HMAC-SHA256 " +
                             $"over the method, path, query, timestamp, nonce and body hash, as {SignedRequestsDocs} shows. A 202 means " +
                             "Queuey has it durably. There is no package to add for this.";
                yield return CallFor(facts);
                foreach (var step in WhereToPublishFrom(facts))
                    yield return step;
                if (facts.IsEphemeral)
                    yield return "If the network here is unreliable, write to the database you already have and publish from a worker that reads it — that is your durability, since local disk is not.";
                break;
        }

        if (send != SendPath.None)
        {
            // Skillen (blindtest 2, funn 7): workspace-nøkkelen som standard, --queue bare med en grunn. Daemonen står på en maskin
            // vi ikke styrer, så den får en nøkkel for sin kø, i fila tjenesten laster som miljø (den leser ikke .env).
            yield return send == SendPath.EdgeDaemon
                ? "Log in (queuey login --profile dev), apply the deployment file (queuey apply --profile dev), and make the node's " +
                  $"signing key for its queue only, since it runs on a machine you may not control: queuey keys mint --queue <queue> --profile dev --write {DaemonEnvironmentFile}. " +
                  "It needs a login that may manage keys, and the secret never passes through the terminal."
                : "Log in (queuey login --profile dev), apply the deployment file (queuey apply --profile dev), and make the app's " +
                  $"signing key for the workspace: queuey keys mint --profile dev --write {write}. It reaches every queue in the workspace, " +
                  "also queues made later; add --queue <queue> only to limit it to one" +
                  (send == SendPath.Edge ? ", as for a node on a machine you do not control" : "") +
                  ". It needs a login that may manage keys, and the secret never passes through the terminal: the app reads the same value Queuey verifies.";
        }
    }

    private static IEnumerable<string> Questions(RepoFacts facts, SendPath send)
    {
        // The network is never in the repository. Ask it wherever it would
        // change the answer — which is anywhere a spool is possible.
        if (send is SendPath.Client or SendPath.PlainHttp && !facts.IsEphemeral)
        {
            yield return NetworkQuestion;
            yield return DiskQuestion;
        }

        if (send is SendPath.Edge or SendPath.EdgeDaemon)
            yield return "Confirm the path you point the spool at is on the durable disk, not a temp directory that the host clears.";

        if (send == SendPath.ClientFromExistingDurability)
            yield return "Confirm the consumer runs after the commit, not inside the request — that is what makes it durable.";
    }

    /// <summary>
    /// The signed publish call in the language the repository is written in. None of them needs a Queuey package: the
    /// signature is an HMAC-SHA256 the language's standard library computes.
    /// </summary>
    private static string CallFor(RepoFacts facts)
    {
        if (facts.Ecosystems.Contains("node"))
            return "In JavaScript or TypeScript: compute the signature with createHmac('sha256', secret) from node:crypto, then " +
                   "await fetch(url, { method: \"POST\", headers: { ...signatureHeaders, \"Content-Type\": \"application/json\" }, body }), " +
                   $"and check for status 202. {SignedRequestsDocs} has the canonical string.";

        if (facts.Ecosystems.Contains("python"))
            return "In Python: compute the signature with hmac.new(secret, canonical, hashlib.sha256), then " +
                   $"requests.post(url, data=body, headers=signature_headers), and check for status 202. {SignedRequestsDocs} has the canonical string.";

        if (facts.Ecosystems.Contains("go"))
            return "In Go: crypto/hmac with sha256 for the signature, then an http.NewRequest POST with the signature headers from " +
                   $"net/http, and check for status 202. {SignedRequestsDocs} has the canonical string.";

        return "To try it from a shell: compute the signature with openssl dgst -sha256 -hmac \"$QUEUEY_SIGNING_SECRET\" over the " +
               $"canonical string, then curl -X POST \"$url\" with the five X-Queuey-* headers. {SignedRequestsDocs} has a script.";
    }

    /// <summary>
    /// Where the call goes, which in a JavaScript repository is the part that
    /// decides whether the key stays secret. A bundler compiles anything the
    /// browser code touches into a file every visitor downloads.
    /// </summary>
    private static IEnumerable<string> WhereToPublishFrom(RepoFacts facts)
    {
        if (facts.HasServerSide)
        {
            yield return $"Publish from server-side code, which here is {Join(facts.ServerSide)}. " +
                         "Read the signing key from server-side secrets, QUEUEY_SIGNING_KEY_ID and QUEUEY_SIGNING_SECRET.";
        }
        else if (facts.IsBrowserApp)
        {
            yield return $"This builds a browser app ({Join(facts.BrowserApp)}) and I found no server-side code. " +
                         "Do not publish from the app itself: the key would ship in the bundle, readable by anyone who " +
                         "loads the page. Publish from a backend, an edge function or a serverless function instead.";
        }

        if (facts.IsBrowserApp)
            yield return "Never put the key in a variable the bundler exposes to the browser — VITE_*, NEXT_PUBLIC_*, " +
                         "REACT_APP_* are compiled into the bundle.";
    }

    /// <summary>
    /// The receiving recipe, in a form the repository can follow. The SDK's
    /// verifier is the right answer in .NET and an impossible one anywhere
    /// else — naming a class the project cannot install sends the agent off to
    /// write the HMAC by hand, which is the thing the recipe exists to prevent.
    /// </summary>
    private static IEnumerable<string> ReceivingRecipe(RepoFacts facts, string write)
    {
        // Mottakerens hemmelighet lages én gang, og samme verdi står i Queuey og der mottakeren leser den (Kenneth 2026-10-09).
        yield return $"Make the delivery secret: queuey credentials generate <queue>-signing --profile dev --write {write}. It stores the " +
                     "value in Queuey and as QUEUEY_DELIVERY_SECRET here, never shown; point the queue's delivery at it with " +
                     "\"delivery\": { \"signing\": { \"enabled\": true, \"credentialRef\": \"<queue>-signing\", \"templateKey\": \"queuey\" } }.";
        if (facts.IsDotNet)
        {
            yield return "Verify the signature over the RAW body, before anything deserializes it, with QueueyDeliveryVerifier.FromEnvironment() " +
                         "from Queuey.Client; do not write the HMAC by hand.";
        }
        else
        {
            yield return "Authenticate every delivery before you act on it. The simplest way: give the queue's delivery " +
                         "target an API key header and compare it in constant time. Or verify Queuey's signature over the " +
                         "RAW body, before anything parses it, exactly as https://queuey.ai/docs/how-to/verify-deliveries " +
                         "shows — the SDK is .NET-only, so copy the documented check rather than improvising one.";
        }

        yield return "Be idempotent on the event id (X-Queuey-Event-Id). A redelivery after a timeout carries the same id.";
        yield return "While developing, run: queuey listen --queue <name> --forward-to http://localhost:<port> — real deliveries at the path of the queue's endpoint, no inbound port open.";
    }

    private static string Join(IReadOnlyList<Evidence> evidence) =>
        string.Join(", ", evidence.Take(4).Select(e => e.ToString()))
        + (evidence.Count > 4 ? $", and {evidence.Count - 4} more" : "");
}
