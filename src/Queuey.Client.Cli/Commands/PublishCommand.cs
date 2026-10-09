using System;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey publish</c> — publishes one event to a queue the way a producer does: with the configured key, to the queue's
/// ingress URL. The answer has the event's id, so <c>queuey verify &lt;queue&gt; --event &lt;id&gt;</c> can follow it.
/// </summary>
// F2.7 (2026-10-06): publish gikk bare til streams og krevde --event. En stream er en kø på ingressen, så det er samme
// kall; nå er hendelsestypen valgfri, og publish går til hvilken som helst kø. Svaret er versjonert og har eventPublicId,
// aldri payloaden eller en nøkkel. Workspacet følger samme regel som apply og verify, så verify finner køen publish brukte.
internal static class PublishCommand
{
    internal static readonly CommandOptions Options = new(
        "publish",
        flags: new[] { "stdin", "json" },
        values: new[] { "event", "key", "data", "file", "idempotency-key", "content-type", "stream", "queue", "deployment", "profile" },
        positionals: 1);

    // Kildene til eventen. Én av dem.
    private static readonly string[] BodySources = { "data", "file", "stdin" };

    /// <summary>The version of <c>publish --json</c>'s shape; a script checks it first, as for the other commands.</summary>
    internal const int JsonSchemaVersion = 1;

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string[] named = new[] { "queue", "stream" }.Where(map.Has).ToArray();
        if (named.Length > 1 || (named.Length == 1 && map.FirstPositional is not null))
            return CliErrors.Usage(map, "conflicting_options", "Name the queue once: as <queue>, --queue or --stream.");

        string? queue = map.FirstPositional ?? map.Get("queue") ?? map.Get("stream");
        if (string.IsNullOrWhiteSpace(queue))
            return CliErrors.Usage(map, "missing_argument", "publish requires <queue>: the queue's name, as its ingress URL has it, or its id (que_…).");

        foreach (string option in new[] { "event", "key", "idempotency-key", "content-type" })
        {
            if (map.Has(option) && string.IsNullOrWhiteSpace(map.Get(option)))
                return CliErrors.Usage(map, "missing_value", $"--{option} takes a value.");
        }

        // F2.7-review (2026-10-06): verdiene gjentas ikke. MediaTypeHeaderValue.Parse gjentok en content type den ikke kunne
        // lese, og en idempotency-nøkkel sendes som header, lagres på eventet og kan gå videre til mottakeren.
        if (map.Get("content-type") is { } contentType && !MediaTypeHeaderValue.TryParse(contentType, out _))
            return CliErrors.Usage(map, "invalid_value",
                "--content-type is not a media type. The value is not shown.", "Give one such as application/json, the default.");
        if (QueuePublisher.StartsLikeASecret(map.Get("idempotency-key")))
            return CliErrors.Usage(map, "invalid_value",
                "--idempotency-key starts like a secret (an API key or a signing secret). The value is not shown.",
                "Pick a key that names the event, such as order-A-1: it is sent with the event and stored on it.");

        byte[]? body = ReadBody(map, out string? bodyCode, out string? bodyError);
        if (bodyError != null)
            return CliErrors.Usage(map, bodyCode!, bodyError);

        // Workspacet etter samme regel som apply og verify: fila sin tenant, ellers den konfigurerte, og feil når --tenant
        // eller QUEUEY_TENANT navngir et annet enn fila. Ellers kunne verify lete etter køen i et annet workspace.
        (string? fileTenant, string filePath) = DeploymentTenant.FromDeploymentOption(map, CliHost.Profile(map));
        ResolvedConfig configured = CliHost.Resolve(map, profiles: true);
        // Uten API-nøkkel signerer publish med nøkkelen keys mint --write .env skrev, fra miljøet eller ./.env (2026-10-09).
        ResolvedConfig config = CliHost.WithIngressSigning(DeploymentTenant.Resolve(configured, map, CliHost.Env, fileTenant, filePath));

        // En publisering er en skriving på dataplanet (F2.7-review): bestemmer deploy-fila workspacet, sies det før noe sendes,
        // og hvilket workspace konfigurasjonen ellers ville gitt. Ellers kunne en testevent havne et annet sted enn ventet.
        string? tenantFrom = string.IsNullOrWhiteSpace(fileTenant) ? null : filePath;
        // Med en profil (F2.7) har profilen allerede krevd at fila og tilkoblingen er enige om workspacet.
        string? passedOver = tenantFrom is not null && configured.TenantPublicId is { } other
                             && !string.Equals(other, config.TenantPublicId, StringComparison.Ordinal) ? other : null;
        bool json = map.Has("json");
        if (tenantFrom is not null && !json)
            Console.Error.WriteLine(TerminalText.Line(
                $"Publishing to {config.TenantPublicId}, the workspace {tenantFrom} names" +
                (passedOver is null ? "." : $", not {passedOver}, which the configuration names.")));

        if (config.SigningFrom is { } from && !json)
            Console.Error.WriteLine($"Signing with key {TerminalText.Line(config.SigningKeyId)} from {from}.");

        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        QueuePublishResult result = await service.PublishToQueueAsync(queue!.Trim(), body!, new QueuePublishOptions
        {
            EventType = map.Get("event"),
            GroupKey = map.Get("key"),
            IdempotencyKey = map.Get("idempotency-key"),
            ContentType = map.Get("content-type"),
            Source = map.Get("source"),
        });

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(ToJson(result, tenantFrom), CliHost.JsonOut));
        else
            WriteHuman(result);

        return ExitCodes.Success;
    }

    /// <summary>
    /// The command that follows the event, or null when the ingress gave no id. Only words safe in a shell go in (F2.7-review):
    /// the queue's name when it is a plain word, else its id, else a placeholder; and the event's id when it is one.
    /// </summary>
    internal static string? VerifyCommandFor(QueuePublishResult result)
    {
        if (CommandWords.Id(result.EventPublicId, "evt_") is not { } eventId)
            return null;

        string queue = CommandWords.Word(result.Queue) ?? CommandWords.Id(result.QueuePublicId, "que_") ?? "<queue>";
        return $"queuey verify {queue} --event {eventId}";
    }

    private static object ToJson(QueuePublishResult r, string? tenantFrom) => new
    {
        schemaVersion = JsonSchemaVersion,
        tenant = r.TenantPublicId,
        tenantFrom = tenantFrom ?? "configuration",
        queue = r.Queue,
        queuePublicId = r.QueuePublicId,
        eventPublicId = r.EventPublicId,
        receivedAtUtc = r.ReceivedAtUtc,
        mode = r.Mode,
        replayed = r.Replayed,
        verify = VerifyCommandFor(r),
    };

    // Navn og id-er kommer fra serveren eller kommandolinjen, så hver linje går gjennom TerminalText.
    private static void WriteHuman(QueuePublishResult r)
    {
        string where = r.QueuePublicId is null ? r.Queue : $"{r.Queue} ({r.QueuePublicId})";
        if (r.EventPublicId is null)
        {
            string queue = CommandWords.Word(r.Queue) ?? CommandWords.Id(r.QueuePublicId, "que_") ?? "<queue>";
            Console.WriteLine(TerminalText.Line(
                $"Published to {where} in {r.TenantPublicId}. Its ingress answered without a receipt (it answers 204), so the event's id is unknown."));
            Console.WriteLine($"  → To follow it, wait for it with `queuey verify {queue} --event-type <type>` and publish again.");
            return;
        }

        Console.WriteLine(TerminalText.Line(
            $"Published {r.EventPublicId} to {where} in {r.TenantPublicId}{(r.Mode is null ? "" : $", mode {r.Mode}")}."));
        if (r.Replayed)
            Console.WriteLine("  The idempotency key matched an earlier event, so this is that event: nothing new was stored.");
        if (VerifyCommandFor(r) is { } verify)
            Console.WriteLine($"  → Follow it: {verify}");
    }

    private static byte[]? ReadBody(ArgMap map, out string? code, out string? error)
    {
        code = error = null;

        string[] given = BodySources.Where(map.Has).ToArray();
        if (given.Length > 1)
        {
            code = "conflicting_options";
            error = $"Give the event once: {string.Join(", ", given.Select(o => "--" + o))} were all given.";
            return null;
        }

        if (map.Has("stdin"))
            return Encoding.UTF8.GetBytes(Console.In.ReadToEnd());

        string? file = map.Get("file");
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file)) { code = "missing_file"; error = $"File not found: {file}"; return null; }
            return CliFiles.ReadAllBytes(file);
        }

        string? data = map.Get("data");
        if (data != null)
            return Encoding.UTF8.GetBytes(data);

        code = "missing_body";
        error = "publish requires the event: --data <json>, --file <path>, or --stdin.";
        return null;
    }
}
