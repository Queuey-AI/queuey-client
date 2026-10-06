using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey verify</c> — proves a queue's flow with Queuey's flow verification: follows an event from the ingress to its
/// final state, step by step. The event is one already in the queue (<c>--event</c>), the next one of a type
/// (<c>--event-type</c>), or a test event Queuey sends (<c>--send</c>). The step after <c>apply</c>, because an apply that
/// exits 0 says the configuration landed, not that events arrive.
/// </summary>
// F2.6 (2026-10-06): Queuey verifiserer, og CLI-en starter og leser. Før publiserte verify selv og fulgte eventen med
// GET /events, og ga sitt eget verdikt. Nå er det samme trinn og samme utfall som MCP-verktøyene og konsollen viser.
internal static class VerifyCommand
{
    internal static readonly CommandOptions Options = new(
        "verify",
        flags: new[] { "send", "stdin", "json" },
        values: new[] { "event", "event-type", "ingress-auth", "data", "file", "timeout", "wait", "deployment", "queue" },
        positionals: 1,
        hints: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Fantes før F2.6, da verify publiserte selv. Queuey sender testeventen som JSON.
            ["content-type"] = "--content-type is not an option for queuey verify any more: Queuey sends the test event (--send) " +
                               "through the queue's ingress as application/json.",
        });

    // Kildene til en testevent. Bare med --send.
    private static readonly string[] BodySources = { "data", "file", "stdin" };

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string? queue = map.FirstPositional ?? map.Get("queue");
        if (string.IsNullOrWhiteSpace(queue))
            return CliErrors.Usage(map, "missing_argument", "verify requires <queue>: the queue's name, or its id (que_…).");

        if (Timeout(map, out TimeSpan? timeout) is { } badTimeout)
            return badTimeout;

        if (Request(map, timeout, out FlowVerificationRequest? request) is { } refused)
            return refused;

        // Workspacet apply skrev til, etter samme regel som apply: fila sin tenant, ellers den konfigurerte, og feil når
        // --tenant eller QUEUEY_TENANT navngir et annet enn fila. Det trengs for å finne køen ved navn.
        (string? fileTenant, string filePath) = DeploymentFileTenant(map);
        ResolvedConfig config = CliHost.ResolveForDeployment(map, fileTenant, filePath);

        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        bool json = map.Has("json");
        FlowVerification result = await service.VerifyFlowAsync(queue!, request!, json ? null : new StartLine(queue!));

        // Workspacet er det Queuey sier køen ligger i; det konfigurerte når svaret ikke har det.
        string? tenant = result.Subject.WorkspacePublicId ?? config.TenantPublicId;
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(ToJson(queue!, tenant, result), CliHost.JsonOut));
        else
            WriteHuman(queue!, tenant, result);

        return result.Passed ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    /// <summary>
    /// What the command line asks Queuey to verify, or the exit code of the usage error that says why it cannot ask. Nothing
    /// is read from the network, and stdin only for a test event.
    /// </summary>
    private static int? Request(ArgMap map, TimeSpan? timeout, out FlowVerificationRequest? request)
    {
        request = null;
        string[] sources = BodySources.Where(map.Has).ToArray();

        if (map.Has("event"))
        {
            if (Value(map, "event", "the id of the event to follow (evt_…)", out string? eventId) is { } missing)
                return missing;
            if (!eventId!.StartsWith("evt_", StringComparison.Ordinal))
                return CliErrors.Usage(map, "invalid_value",
                    "--event takes an event's id (evt_…), as publish answered it. The value is not shown, since it is not one.");

            string[] others = new[] { "event-type", "ingress-auth", "send" }.Concat(BodySources).Where(map.Has).ToArray();
            if (others.Length > 0)
                return CliErrors.Usage(map, "conflicting_options",
                    $"--event follows an event that is already in the queue, so it takes no {string.Join(", ", others.Select(o => "--" + o))}.",
                    "Leave --event out to wait for the next event of a type (--event-type), or to send a test event (--send).");

            request = FlowVerificationRequest.FollowEvent(eventId, timeout);
            return null;
        }

        string? eventType = null;
        if (map.Has("event-type") && Value(map, "event-type", "the event type", out eventType) is { } noType)
            return noType;
        string? ingressAuth = null;
        if (map.Has("ingress-auth") && Value(map, "ingress-auth", "the signed-request template the ingress verifies with, such as stripe", out ingressAuth) is { } noScheme)
            return noScheme;

        if (map.Has("send"))
        {
            if (ingressAuth is not null)
                return CliErrors.Usage(map, "conflicting_options",
                    "Queuey never signs a test event as a provider, so --send takes no --ingress-auth.",
                    "To prove a provider's flow, leave --send out and wait for the provider's own event: --event-type <type> --ingress-auth <template>.");

            if (sources.Length == 0)
                return CliErrors.Usage(map, "missing_body", "--send needs the test event: --data <json>, --file <path>, or --stdin.",
                    "It is delivered to the real receiver like any other event, so send data it treats as harmless.");
            if (sources.Length > 1)
                return CliErrors.Usage(map, "conflicting_options",
                    $"Give the test event once: {string.Join(", ", sources.Select(o => "--" + o))} were all given.");

            byte[]? body = ReadBody(map, out string? code, out string? error, out string? action);
            if (error is not null)
                return CliErrors.Usage(map, code!, error, action);

            // JSON og høyst 64 KB slik den sendes, sjekket i SDK-en (review av #51): over 128 KB svarte Kestrel 413 uten kropp,
            // og brukeren så bare en generell feil. Meldingen viser aldri en del av eventen.
            try
            {
                request = FlowVerificationRequest.SendTestEvent(body!, eventType, timeout);
            }
            catch (ArgumentException ex)
            {
                return CliErrors.Usage(map, "invalid_value", ex.Message,
                    "Send one JSON value of at most 64 KB that the receiver treats as harmless, such as {\"test\":true}.");
            }

            return null;
        }

        // Review av #51: --send er ikke hovedveien. Queuey sender en testevent bare der aktiv verifisering er slått på (ikke i
        // prod-Queuey i dag), workspacet er merket som ikke prod, og ingressen er åpen. Pek på veiene som virker overalt.
        if (sources.Length > 0)
            return CliErrors.Usage(map, "send_required",
                $"{string.Join(", ", sources.Select(o => "--" + o))} is a test event, and verify sends one only with --send.",
                "To verify the producer's own event, publish it with a producer key and run `queuey verify <queue> --event <evt_…>` " +
                "with the id the ingress answered, or start `queuey verify <queue> --event-type <type>` and trigger the event yourself, " +
                "such as with `stripe trigger`. " + SendConditions);

        if (eventType is null)
        {
            return ingressAuth is not null
                ? CliErrors.Usage(map, "missing_argument",
                    "--ingress-auth goes with --event-type: a provider sends many kinds of events, so waiting on the template alone " +
                    "would take the first event of any kind.",
                    "Pass the event type you trigger as well, such as --event-type payment_intent.succeeded --ingress-auth stripe.")
                : CliErrors.Usage(map, "missing_argument",
                    "Say what to verify: --event <evt_…> follows an event the producer published, --event-type <type> waits for " +
                    "the next event of that type, and --send with --data, --file or --stdin has Queuey send a test event where it may.",
                    "Without one, verify would take the first event that arrived, from anyone.");
        }

        request = FlowVerificationRequest.WaitForEvent(eventType, ingressAuth, timeout);
        return null;
    }

    /// <summary>When Queuey sends a test event, in one sentence, for the errors that point away from <c>--send</c>.</summary>
    internal const string SendConditions =
        "--send works only where Queuey has active verification switched on (production Queuey does not today), the key's " +
        "workspace is tagged dev, test or staging, and the queue's ingress takes events without a key or a signature.";

    /// <summary>
    /// How long Queuey follows the event, from <c>--timeout</c> or its other name <c>--wait</c>: null for Queuey's default.
    /// </summary>
    private static int? Timeout(ArgMap map, out TimeSpan? timeout)
    {
        timeout = null;
        if (map.Has("timeout") && map.Has("wait"))
            return CliErrors.Usage(map, "conflicting_options", "--timeout and --wait are the same option: give one.");

        string name = map.Has("wait") ? "wait" : "timeout";
        if (!map.Has(name))
            return null;

        string? raw = map.Get(name);
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds < 1)
            return CliErrors.Usage(map, "invalid_value",
                $"--{name} takes whole seconds, at least 1; got '{(raw is null ? "" : CliErrors.Shown(raw))}'.",
                "Queuey follows an event for at most 15 minutes (900 seconds), and for a minute when --timeout is left out.");

        timeout = TimeSpan.FromSeconds(seconds);
        return null;
    }

    /// <summary>The option's value, or the exit code of the usage error for an option given without one.</summary>
    private static int? Value(ArgMap map, string option, string what, out string? value)
    {
        value = map.Get(option);
        if (!string.IsNullOrWhiteSpace(value))
            return null;

        value = null;
        return CliErrors.Usage(map, "missing_value", $"--{option} takes a value: {what}.");
    }

    /// <summary>The deployment file's tenant, when there is a file — named by --deployment, or the default one here.</summary>
    private static (string? Tenant, string Path) DeploymentFileTenant(ArgMap map)
    {
        string? named = map.Get("deployment");
        string path = named ?? DeploymentFile.DefaultFileName;
        if (!File.Exists(path))
        {
            if (named is not null)
                throw new QueueyConfigurationException($"No deployment file at '{path}'.");
            return (null, path);
        }

        return (DeploymentTenant.ReadFromFile(CliFiles.ReadAllText(path), path), path);
    }

    /// <summary>The line verify writes to stderr once Queuey follows an event, so a person knows to trigger it.</summary>
    private sealed class StartLine : IProgress<FlowVerification>
    {
        private readonly string _queue;
        private bool _written;

        public StartLine(string queue) => _queue = queue;

        public void Report(FlowVerification value)
        {
            if (_written) return;
            _written = true;
            if (value.Settled) return;

            long seconds = Math.Max(0, (long)Math.Ceiling((value.ObserveUntil - value.CreatedAt).TotalSeconds));
            string queue = Where(_queue, value.Subject.QueuePublicId);
            Console.Error.WriteLine(value.Mode switch
            {
                FlowVerificationModes.ObservedSession =>
                    $"Waiting up to {seconds} s for the next '{value.Expectations.EventType}' event on {queue}" +
                    (value.Expectations.IngressAuth is { } scheme ? $", verified with {scheme}" : "") +
                    ". Trigger it now: an event that arrived before this does not count.",
                FlowVerificationModes.Active =>
                    $"Sent a test event to {queue}{Event(value)}. Following it for up to {seconds} s.",
                _ => $"Following{Event(value)} on {queue} for up to {seconds} s.",
            });
        }

        private static string Event(FlowVerification value)
            => value.Subject.EventPublicId is { } id ? " " + id : "";
    }

    private static string Where(string queue, string? queuePublicId)
        => queuePublicId is null || queuePublicId == queue ? queue : $"{queue} ({queuePublicId})";

    private static void WriteHuman(string queue, string? tenant, FlowVerification v)
    {
        string outcome = v.Settled
            ? v.Outcome switch
            {
                FlowVerificationOutcomes.Passed => "Passed",
                FlowVerificationOutcomes.Failed => "Failed",
                FlowVerificationOutcomes.TimedOut => "Timed out",
                FlowVerificationOutcomes.NotTried => "Not tried",
                _ => v.Outcome ?? "No outcome",
            }
            : "Not settled";

        string @event = v.Subject.EventPublicId is { } id ? $", event {id}" : "";
        Console.WriteLine($"{(v.Passed ? "✓" : "✗")} {outcome} — {Where(queue, v.Subject.QueuePublicId)} in {tenant ?? "?"}{@event}");
        if (!string.IsNullOrWhiteSpace(v.Summary))
            Console.WriteLine($"  {v.Summary}");

        int width = v.Steps.Count == 0 ? 0 : v.Steps.Max(s => (s.Name ?? "").Length);
        foreach (FlowVerificationStep step in v.Steps)
        {
            string details = Details(step.Evidence);
            Console.WriteLine($"  {Mark(step.Status)} {(step.Name ?? "").PadRight(width)}  {step.Status}{(details.Length == 0 ? "" : "  " + details)}");
        }

        if (!v.Settled)
            Console.WriteLine($"  Queuey had not settled the verification when verify stopped reading it ({v.Outcome ?? "no outcome"} so far).");
        Console.WriteLine($"  Verification {v.VerificationId}.");
    }

    private static string Mark(string? status) => status switch
    {
        FlowStepStatuses.Passed => "✓",
        FlowStepStatuses.Failed => "✗",
        FlowStepStatuses.Skipped => "–",
        FlowStepStatuses.Pending => "…",
        _ => "?",
    };

    /// <summary>
    /// A step's evidence as <c>name=value</c>, in the order Queuey defines it. Only the fields the SDK reads, so a field it does
    /// not know never shows; and Queuey's evidence has no payload value, secret, header value or response body to begin with.
    /// </summary>
    private static string Details(FlowStepEvidence? e)
    {
        if (e is null) return "";

        var parts = new List<string>();
        void Add(string name, object? value)
        {
            string? text = value switch
            {
                null => null,
                bool b => b ? "true" : "false",
                DateTimeOffset at => at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                IEnumerable<string> list => string.Join(",", list),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
            if (!string.IsNullOrEmpty(text))
                parts.Add($"{name}={text}");
        }

        Add("reason", e.Reason);
        Add("eventId", e.EventId);
        Add("scheme", e.Scheme);
        Add("credentialId", e.CredentialId);
        Add("eventTypeRecorded", e.EventTypeRecorded);
        Add("groupKeyRecorded", e.GroupKeyRecorded);
        Add("partitionKeyRecorded", e.PartitionKeyRecorded);
        Add("attemptId", e.AttemptId);
        Add("attemptNumber", e.AttemptNumber);
        Add("attempts", e.Attempts);
        Add("targetId", e.TargetId);
        Add("targetHost", e.TargetHost);
        Add("targetPath", e.TargetPath);
        Add("sent", e.Sent);
        Add("probe", e.Probe);
        Add("auth", e.Auth);
        Add("signing", e.Signing);
        Add("resigned", e.Resigned);
        Add("headers", e.Headers);
        Add("statusCode", e.StatusCode);
        Add("durationMs", e.DurationMs);
        Add("failureClass", e.FailureClass);
        Add("decision", e.Decision);
        Add("status", e.Status);
        Add("nextRetryAt", e.NextRetryAt?.ToUniversalTime());
        Add("holdReason", e.HoldReason);
        Add("heldBy", e.HeldBy);
        Add("targets", e.Targets);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// The version of <c>verify --json</c>'s shape. A script that reads it checks this first, as it does in
    /// <c>apply --dry-run --json</c> and <c>plan --json</c>.
    /// </summary>
    // Versjon 2 (F2.6, 2026-10-06): utfallet er Queuey sin flytverifisering, under «verification», i Queuey sin egen form med
    // sin egen schemaVersion. Versjon 1 var verify sitt eget verdikt (verdict, action), fra da verify publiserte selv.
    internal const int JsonSchemaVersion = 2;

    private static object ToJson(string queue, string? tenant, FlowVerification v) => new
    {
        schemaVersion = JsonSchemaVersion,
        tenant,
        queue,
        queuePublicId = v.Subject.QueuePublicId,
        verification = v,
    };

    private static byte[]? ReadBody(ArgMap map, out string? code, out string? error, out string? action)
    {
        code = error = action = null;

        if (map.Has("stdin"))
            return Encoding.UTF8.GetBytes(Console.In.ReadToEnd());

        string? file = map.Get("file");
        if (map.Has("file"))
        {
            if (string.IsNullOrWhiteSpace(file)) { code = "missing_value"; error = "--file takes a value: the path of the test event to send."; return null; }
            if (!File.Exists(file)) { code = "missing_file"; error = $"File not found: {file}"; return null; }
            byte[] bytes = CliFiles.ReadAllBytes(file);
            if (IsDeploymentFile(bytes))
            {
                code = "deployment_file_as_event";
                error = $"--file is the test event to send, and {file} is a deployment file.";
                action = "Name the deployment file with --deployment, and send the test event with --data, --file or --stdin.";
                return null;
            }
            return bytes;
        }

        string? data = map.Get("data");
        if (data is null) { code = "missing_value"; error = "--data takes a value: the test event to send, as JSON."; return null; }
        return Encoding.UTF8.GetBytes(data);
    }

    /// <summary>
    /// Whether the bytes are a deployment file rather than an event: a JSON object with only the file's top-level fields,
    /// among them <c>workspace</c> or <c>queues</c>. <c>apply</c> and <c>plan</c> take the deployment file with
    /// <c>--file</c>, so it is the mistake to expect.
    /// </summary>
    // Review 2026-10-05: verify --file er eventen, mens apply og plan --file er deploy-fila. Et feil valg sendte deploy-fila
    // som event til den ekte mottakeren.
    internal static bool IsDeploymentFile(byte[] bytes)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var names = new List<string>();
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
                names.Add(property.Name);

            return names.Count > 0
                   && names.All(n => n is "$schema" or "tenant" or "workspace" or "queues")
                   && names.Any(n => n is "workspace" or "queues");
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
