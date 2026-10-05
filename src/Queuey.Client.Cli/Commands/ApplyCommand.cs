using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey apply</c> — converges Queuey from a declarative deployment file. The deploy-pipeline
/// verb: reviewable in a pull request, idempotent, and non-zero on anything short of convergence.
/// </summary>
internal static class ApplyCommand
{
    // --plan ble et eget verb (2026-09-24): et verb en eldre CLI ikke kjenner, feiler i alle versjoner,
    // mens `apply --plan` i en CLI fra før flagget var en ekte apply. Ordet får et hint i stedet.
    internal static readonly CommandOptions Options = new(
        "apply",
        flags: new[] { "dry-run", "check", "continue-on-error", "json" },
        values: new[] { "file" },
        hints: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan"] = "`apply --plan` is now `queuey plan`: it asks Queuey what apply would change, and writes nothing.",
        });

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        if (!TryReadDeploymentFile(map, out string path, out DeploymentFile file, out failure)) return failure;
        bool dryRun = map.Has("dry-run");

        if (dryRun)
        {
            // Samme regel for tenant som apply, så en dry-run feiler der applyen ville feilet.
            DeploymentTenant.EnsureNoConflict(map, CliHost.Env, file.ResolveTenant(), path);

            // Network-free: resolving validates names and policy, which is the failure worth catching
            // before a deploy window rather than during one.
            // Expanding first means an unset ${VAR} fails here, in the dry run, rather than during
            // the deploy it was meant to protect.
            file.Expand().Resolve();

            // Det som vises, er fila slik den står, med ${VAR} uutvidet. Før skrev --json de utvidede verdiene, også et
            // token i en ?code=, mens teksten viste workspacet uutvidet og køene utvidet (review 2026-10-05).
            IReadOnlyList<DeploymentQueuePlan> plans = file.Resolve();
            if (map.Has("json"))
                Console.WriteLine(JsonSerializer.Serialize(ToJsonDryRun(file, plans), CliHost.JsonOut));
            else
                WritePlan(path, file, plans);
            return ExitCodes.Success;

        }

        // Workspacet fila navngir, ellers det konfigurerte — og feil når --tenant eller QUEUEY_TENANT sier
        // noe annet enn fila. Samme regel som verify, så de treffer samme workspace.
        ResolvedConfig config = CliHost.ResolveForDeployment(map, file.ResolveTenant(), path);
        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        if (map.Has("check"))
            return await CheckAsync(service, file, path, map);

        QueueSyncResult result;
        try
        {
            result = await service.ApplyDeploymentAsync(file, new SyncOptions { ContinueOnError = map.Has("continue-on-error") });
        }
        catch (QueueySyncException ex)
        {
            result = ex.Queues!;
        }

        string? tenant = config.TenantPublicId;

        if (map.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(ToJsonResult(result, path, config, tenant), CliHost.JsonOut));
        else
            WriteHuman(result, path, config, tenant);

        return result.AllSucceeded ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    /// <summary>
    /// The deployment file named by <c>--file</c>, or <c>queuey.deploy.json</c> here. A missing file is
    /// a usage error, written the way the caller asked for errors.
    /// </summary>
    internal static bool TryReadDeploymentFile(ArgMap map, out string path, out DeploymentFile file, out int failure)
    {
        path = map.Get("file") ?? DeploymentFile.DefaultFileName;
        file = null!;
        failure = ExitCodes.Success;

        if (!File.Exists(path))
        {
            failure = CliErrors.Usage(map, "missing_file", $"No deployment file at '{path}'.", "Create one, or pass --file <path>.");
            return false;
        }

        file = ParseNamed(path);
        return true;
    }

    /// <summary>
    /// The deployment file at <paramref name="path"/>, parsed, with the path in front of the error when it
    /// cannot be: the parser speaks of "the deployment file", and a command can read more than one file.
    /// </summary>
    internal static DeploymentFile ParseNamed(string path)
    {
        try
        {
            return DeploymentFile.Parse(File.ReadAllText(path));
        }
        catch (QueueyConfigurationException ex)
        {
            // Parseren sier «the deployment file», ikke hvilken (review 2026-10-05).
            throw new QueueyConfigurationException($"{path}: {ex.Message}") { SuggestedAction = ex.SuggestedAction };
        }
    }

    /// <summary>
    /// The CI gate: report what applying would change, write nothing, and exit non-zero on drift so a
    /// divergence is noticed at review time rather than during an incident.
    /// </summary>
    private static async Task<int> CheckAsync(IQueueyService service, DeploymentFile file, string path, ArgMap map)
    {
        IReadOnlyList<DriftItem> drift = await service.CheckDeploymentAsync(file);

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                file = path,
                inSync = drift.Count == 0,
                drift = drift.Select(d => new { d.Path, d.Declared, d.Actual }),
            }, CliHost.JsonOut));
        }
        else if (drift.Count == 0)
        {
            Console.WriteLine($"{path} matches the workspace — applying it would change nothing.");
        }
        else
        {
            Console.WriteLine($"{path} has drifted from the workspace ({drift.Count} difference(s)):");
            foreach (DriftItem d in drift)
                Console.WriteLine($"  ~ {d}");
            Console.WriteLine("Run `queuey apply` to converge, or `queuey pull` if the workspace is right.");
        }

        return drift.Count == 0 ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    private static void WritePlan(string path, DeploymentFile file, IReadOnlyList<DeploymentQueuePlan> plans)
    {
        Console.WriteLine($"Queuey apply (dry-run) — {path}");

        if (file.Workspace is { } w)
        {
            var parts = new List<string>();
            if (w.Environment is not null) parts.Add($"environment={w.Environment}");
            if (w.Ordering is not null) parts.Add($"ordering={w.Ordering}");
            if (w.RetentionDays is { } days) parts.Add($"retentionDays={days}");
            parts.AddRange(Backoff(w.Backoff));
            if (w.Ingress?.AuthMode is { } auth) parts.Add($"ingressAuth={auth}");
            if (w.Ingress?.EventType is { } et) parts.Add($"eventType={et.From}:{et.Name}");
            if (w.Ingress?.GroupKey is { } gk) parts.Add($"groupKey={gk.From}:{gk.Name}");
            if (w.Delivery?.BaseUrl is { } url) parts.Add($"baseUrl={url}");

            if (parts.Count > 0)
                Console.WriteLine($"  workspace\t{string.Join(" ", parts)}");
            foreach (string note in CeilingNotes(w.Backoff))
                Console.WriteLine($"    ! {note}");
        }

        foreach (DeploymentQueuePlan p in plans)
        {
            string dest = p.Delivery is null
                ? "inherits the workspace"
                : p.Delivery.Inherit ? "back to inheriting" : $"url={p.Delivery.Url}";

            // Modus står først fordi den avgjør om noe leveres i det hele tatt.
            var parts = new List<string>
            {
                p.Mode is { } mode ? $"mode={mode.ToFileText()}" : "mode=(deliver when it has a destination, if new)",
                dest,
            };
            QueuePolicy policy = p.Definition.Policy;
            if (policy.Ordering is not null) parts.Add($"ordering={policy.Ordering}");
            parts.AddRange(Backoff(policy.Backoff));
            if (policy.Filter is { } filter) parts.Add($"filter=({filter})");

            Console.WriteLine($"  • {p.Definition.Name}\t{string.Join(" ", parts)}");
            foreach (string note in CeilingNotes(policy.Backoff))
                Console.WriteLine($"    ! {note}");
        }

        Console.WriteLine($"{plans.Count} queue(s) declared. Nothing was sent.");
    }

    /// <summary>
    /// What a dry run says about a wait above Queuey's ceilings. It cannot say more without asking Queuey:
    /// apply refuses the wait only when it would change what is in place, which is what it reads first.
    /// </summary>
    internal static IEnumerable<string> CeilingNotes(RetryBackoff? backoff)
    {
        if (backoff?.BaseDelayMs is { } baseMs && baseMs > RetryBackoff.BaseDelayCeilingMs)
            yield return FormattableString.Invariant(
                $"backoff.baseDelayMs={baseMs} is above the {RetryBackoff.BaseDelayCeilingMs} (one hour) a first wait may be: apply refuses it unless that wait is already in place.");
        if (backoff?.MaxDelayMs is { } maxMs && maxMs > RetryBackoff.MaxDelayCeilingMs)
            yield return FormattableString.Invariant(
                $"backoff.maxDelayMs={maxMs} is above the {RetryBackoff.MaxDelayCeilingMs} (24 hours) the longest wait may be: apply refuses it unless that wait is already in place.");
    }

    private static IEnumerable<string> Backoff(RetryBackoff? backoff)
    {
        if (backoff is { } b)
        {
            if (b.BaseDelayMs is { } baseMs) yield return $"backoff.baseDelayMs={baseMs}";
            if (b.MaxDelayMs is { } maxMs) yield return $"backoff.maxDelayMs={maxMs}";
            if (b.Jitter is { } jitter) yield return $"backoff.jitter={jitter}";
        }
    }

    private static void WriteHuman(QueueSyncResult result, string path, ResolvedConfig config, string? tenant)
    {
        Console.WriteLine($"Queuey apply — {path} → {config.ResolvedApiBase()}  (tenant {tenant ?? "?"})");

        foreach (QueueApplyResult r in result.Applied)
        {
            if (r.Succeeded)
                Console.WriteLine($"  ✓ {r.Name}\t{r.PublicId}\t{(r.Created ? "created" : "exists")}{(r.PolicyApplied ? ", policy" : "")}"
                                  + (r.Mode is { } mode ? $", {mode}" : ""));
            else if (r.Created)
                // Opprettet før feilen: køen finnes, og modusen den fikk, er det som avgjør om den leverer.
                Console.WriteLine($"  ✗ {r.Name}\t{r.PublicId}\tcreated, {r.Mode ?? "mode unknown"} — {FormatError(r.Error)}");
            else
                Console.WriteLine($"  ✗ {r.Name}\t{FormatError(r.Error)}");

            // Serverens forslag står under feilen den hører til. Før 2026-09-24 viste bare --plan og
            // feil som stoppet hele kommandoen det; en vanlig apply mistet det.
            if (!r.Succeeded && r.Error?.SuggestedAction is { } action)
                Console.WriteLine($"      → {action}");
        }

        foreach (string skipped in result.NotAttempted)
            Console.WriteLine($"  – {skipped}\tnot attempted (stopped at an earlier failure)");

        foreach (string warning in result.Warnings)
            Console.WriteLine($"  ! {warning}");

        Console.WriteLine($"{result.Succeeded} applied ({result.Created} created), {result.Failed} failed, "
                          + $"{result.NotAttempted.Count} not attempted"
                          + (result.NotAttempted.Count > 0 ? " — re-run to converge (applying is idempotent)" : ""));
    }

    private static string FormatError(QueueyException? e)
        => e is null ? "failed" : $"{(e.StatusCode?.ToString() ?? "error")} {e.ErrorCode} {e.Message}".Replace("  ", " ").Trim();

    /// <summary>
    /// The version of <c>apply --dry-run --json</c>'s shape. 1 was the bare array of queues that
    /// 0.1.0-preview.8 printed; 2 is the object with the workspace and the queues. A script that reads
    /// it checks this first.
    /// </summary>
    internal const int DryRunJsonSchemaVersion = 2;

    /// <summary>
    /// The dry run as JSON: <c>{ schemaVersion, workspace, queues }</c>. The workspace is null when the
    /// file declares none, and carries its <c>environment</c>, <c>policy</c>, <c>delivery</c>, <c>ingress</c> and
    /// <c>notes</c>; each queue carries the notes the dry run has about it.
    /// </summary>
    private static object ToJsonDryRun(DeploymentFile file, IReadOnlyList<DeploymentQueuePlan> plans)
    {
        // Lista var bare køer, så en ventetid over taket på workspacet hadde ingen plass (re-review 2026-10-05).
        // Kenneth valgte et versjonert objekt (2026-10-05) framfor å legge workspacet inn i lista: nøklene sier hva
        // hver del er, og et skript som leste lista, feiler tydelig i stedet for å lese workspacet som en kø.
        return new
        {
            schemaVersion = DryRunJsonSchemaVersion,
            workspace = file.Workspace is { } w ? ToJsonWorkspace(w) : null,
            queues = plans.Select(ToJsonPlan).ToArray(),
        };
    }

    // Miljø-merket står som fila skriver det (Queuey F2.2, 2026-10-05). Lagt til i versjon 2, som ikke er sluppet ennå.
    private static object ToJsonWorkspace(DeploymentWorkspace w) => new
    {
        environment = w.Environment,
        policy = new
        {
            w.Ordering,
            w.DlqEnabled,
            w.RetentionDays,
            w.Idempotent,
            backoff = w.Backoff is { } b ? new { b.BaseDelayMs, b.MaxDelayMs, b.Jitter } : null,
        },
        delivery = ToJson(w.Delivery),
        ingress = ToJson(w.Ingress),
        notes = CeilingNotes(w.Backoff).ToArray(),
    };

    private static object ToJsonPlan(DeploymentQueuePlan p) => new
    {
        p.Definition.Name,
        // null: leave it, and a queue this file creates delivers when it has a destination.
        mode = p.Mode?.ToFileText(),
        policy = new
        {
            p.Definition.Policy.Ordering,
            p.Definition.Policy.DlqEnabled,
            p.Definition.Policy.RetentionDays,
            p.Definition.Policy.Idempotent,
            backoff = p.Definition.Policy.Backoff is { } b ? new { b.BaseDelayMs, b.MaxDelayMs, b.Jitter } : null,
            filter = p.Definition.Policy.Filter is { } f
                ? new { match = f.Match ?? "all", conditions = f.Conditions?.Select(c => new { c.Field, c.Op, c.Value }) }
                : null,
        },
        delivery = ToJson(p.Delivery),
        ingress = ToJson(p.Ingress),
        notes = CeilingNotes(p.Definition.Policy.Backoff).ToArray(),
    };

    // Levering og ingress som fila skriver dem, felt for felt, og som serverens config-lesing har dem: der er
    // ingress.eventType { from, name }, så en sti fra `queuey plan` peker på det samme her. Før 2026-10-05 var eventType
    // og groupKey bare navnet, uten hvor det leses fra, og timeoutMs, signing, rateLimit, authHeaderName, method og
    // successStatusCode manglet, så en fil som satte dem, så ut som en som lot dem stå.
    private static object? ToJson(WorkspaceDelivery? d) => d is null ? null : new
    {
        d.BaseUrl,
        d.AuthMode,
        d.CredentialRef,
        d.AuthHeaderName,
        d.Method,
        d.TimeoutMs,
        signing = ToJson(d.Signing),
        rateLimit = ToJson(d.RateLimit),
    };

    private static object? ToJson(QueueDelivery? d) => d is null ? null : new
    {
        d.Url,
        d.Inherit,
        d.AuthMode,
        d.CredentialRef,
        d.AuthHeaderName,
        d.TimeoutMs,
        signing = ToJson(d.Signing),
        rateLimit = ToJson(d.RateLimit),
    };

    private static object? ToJson(DeliverySigning? s) => s is null ? null : new { s.Enabled, s.CredentialRef, s.TemplateKey };

    private static object? ToJson(DeliveryRateLimit? r) => r is null ? null : new { r.MaxRequests, r.PerSeconds };

    private static object? ToJson(DeploymentIngress? i) => i is null ? null : new
    {
        i.AuthMode,
        eventType = ToJson(i.EventType),
        groupKey = ToJson(i.GroupKey),
        i.SuccessStatusCode,
    };

    private static object? ToJson(ContextSource? s) => s is null ? null : new { s.From, s.Name };

    private static object ToJsonResult(QueueSyncResult result, string path, ResolvedConfig config, string? tenant) => new
    {
        file = path,
        apiHost = config.ResolvedApiBase().ToString(),
        tenant,
        total = result.Total,
        succeeded = result.Succeeded,
        created = result.Created,
        failed = result.Failed,
        notAttempted = result.NotAttempted,
        warnings = result.Warnings,
        queues = result.Applied.Select(r => new
        {
            r.Name, r.Succeeded, r.PublicId, r.Created, r.PolicyApplied, r.Mode,
            error = r.Error?.Message,
            errorCode = r.Error?.ErrorCode,
            action = r.Error?.SuggestedAction,
            status = r.Error?.StatusCode,
        }),
    };
}
