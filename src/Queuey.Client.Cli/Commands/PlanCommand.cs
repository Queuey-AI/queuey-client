using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey plan</c> — the server-side plan: every write <c>apply</c> would send goes as a dry run, so
/// the answer is what Queuey would accept and change, not only what the file says locally. Stores nothing
/// unless asked: <c>--store</c> stores the plan in Queuey (F3.11) and seals it with the policy's decision,
/// and <c>--submit</c> also sends it to Queuey's inbox when a person approves it. Changes no configuration,
/// and exits non-zero when any write would be refused, 5 when a submitted plan waits for a person.
/// </summary>
/// <remarks>
/// A verb, not an <c>apply</c> flag: a CLI that predates it answers "Unknown command" instead of
/// running the apply it was asked to plan.
/// </remarks>
internal static class PlanCommand
{
    // --adopt planlegger det en person har løsrevet, som apply --adopt ville skrevet det (Queuey F2.4). Planen lages her og
    // lagres ingen steder, som før (BØR 2 fra reviewen av #64: en PR-jobb som kjører queuey plan, skal ikke fylle innboksen).
    // --store lagrer og forsegler den i Queuey (F3.11), og --submit sender den også til innboksen når en person skal godkjenne
    // den. --local sier standarden uttrykkelig. --repo, --repo-path, --commit og --no-git sier hvor fila ligger, som for apply.
    internal static readonly CommandOptions Options = new(
        "plan", flags: new[] { "json", "local", "store", "submit", "no-git" },
        values: new[] { "file", "adopt", "profile", "repo", "repo-path", "commit" });

    /// <summary>
    /// The version of <c>plan --json</c>'s shape for a plan only this client made (the default, and <c>--store</c> against a
    /// Queuey that stores no plans):
    /// <c>{ schemaVersion, file, tenant, planId, planHash, wouldSucceed, changeCount, queues, steps, skipped, applyStarted }</c>,
    /// with <c>planId</c> null since F3.11, so it is not taken for a plan Queuey stores. A script that reads it checks this first.
    /// </summary>
    // planId, planHash, queues og hvert stegs state kom til i samme versjon (Queuey F2.3, 2026-10-06): ingen tag har sluppet
    // versjon 1 ennå, og feltene legger bare til. Det samme gjelder skipped (Queuey F2.4, 2026-10-06).
    // Samme mønster som apply --dry-run --json, der Kenneth valgte et versjonert objekt (2026-10-05). Formen er ny med
    // queuey plan, så den har en versjon fra første utgave, og ingen leser må gjette når den endres.
    internal const int JsonSchemaVersion = 1;

    /// <summary>
    /// The version of <c>plan --store --json</c>'s shape, a plan Queuey stores (Queuey F3.11): version 1's fields, with Queuey's
    /// <c>planId</c> and <c>planHash</c>, and <c>stored</c>, <c>status</c>, <c>decision</c>, <c>rule</c>, <c>class</c>,
    /// <c>approvalUrl</c>, <c>expiresAt</c> and <c>warnings</c>.
    /// </summary>
    // Hashen er serverens (v2, 64 hex-tegn) og id-en er Queueys, så betydningen av to felt endres: derfor en ny versjon.
    internal const int StoredJsonSchemaVersion = 2;

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        if (map.Has("local") && (map.Has("store") || map.Has("submit")))
            return CliErrors.Usage(map, "conflicting_options",
                $"--local makes the plan here and stores nothing, and --{(map.Has("store") ? "store" : "submit")} stores it in Queuey: give one.");

        // Samme profil som apply (F2.7): fila med profilens verdier, og tilkoblingen hos brukeren.
        string? profile = CliHost.Profile(map);
        if (!ApplyCommand.TryReadDeploymentFile(map, out string path, out DeploymentFile file, out failure, profile)) return failure;
        if (profile is not null)
            file = ApplyCommand.ForProfile(file, profile, path);

        // Samme workspace som apply ville skrevet til, etter samme regel.
        ResolvedConfig config = CliHost.ResolveForDeployment(map, profile is null ? file.ResolveTenant() : file.Tenant, path);

        // Uten et workspace er det ingenting i Queuey å planlegge mot. Lager apply workspacet selv (gullflyten 2026-10-09, etter
        // samme regel som apply: WorkspaceCreation), sies det her; ellers er det den vanlige feilen, med veien ut.
        if (string.IsNullOrWhiteSpace(config.TenantPublicId)
            && (profile is null ? file.Expand() : file) is var expanded
            && expanded.Workspace?.EnvironmentToSend is { } environment)
            throw WorkspaceCreation.Refusal(environment, expanded, path, CliHost.Env) ?? new QueueyConfigurationException(
                $"No workspace is named, so there is nothing in Queuey to plan against yet. {path} says environment {environment}, so "
                + $"`queuey apply` creates a workspace marked {environment} first, and then applies the file to it.")
            {
                SuggestedAction = $"Run `queuey apply`. Or make the workspace with `queuey create-tenant --name {WorkspaceCreation.NameFor(environment)} "
                                  + $"--environment {environment}`, name it with --tenant, in the file's tenant or in the profile, and plan again.",
            };

        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        // Det en person har løsrevet, planlegges ikke, med mindre --adopt tar det tilbake: planen viser det apply ville gjort.
        IReadOnlyList<string> adopt = DeploymentAdopt.Parse(map.Get("adopt"));
        bool submit = map.Has("submit");
        DeploymentPlan plan = !submit && !map.Has("store")
            ? await service.PlanDeploymentAsync(file, new SyncOptions { Adopt = adopt })
            : await provider.GetRequiredService<IQueueyPlans>().StorePlanAsync(file, new SyncOptions
            {
                Adopt = adopt,
                Source = CiSource.With(
                    GitSource.Resolve(path, map.Get("repo"), map.Get("repo-path"), map.Get("commit"), map.Has("no-git")), CliHost.Env),
            }, submit);

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(ToJson(plan, path, config.IngressSource()), CliHost.JsonOut));
            return ExitCode(plan);
        }

        Console.WriteLine($"Queuey plan — {path} → {config.ResolvedApiBase()}  (tenant {plan.Tenant})");
        WriteBody(plan, config);
        return ExitCode(plan);
    }

    /// <summary>
    /// 1 for a refusal, and for a plan the policy refuses; 5 when a submitted plan waits for a person; 0 otherwise, also for a
    /// stored plan sealed for a person and not submitted (<c>--store</c>).
    /// </summary>
    internal static int ExitCode(DeploymentPlan plan)
    {
        if (!plan.WouldSucceed)
            return ExitCodes.RuntimeError;
        if (plan.Stored is not { } stored)
            return ExitCodes.Success;
        if (stored.IsPendingApproval)
            return ExitCodes.PendingApproval;
        return stored.Decision == StoredPlan.Decisions.Denied ? ExitCodes.RuntimeError : ExitCodes.Success;
    }

    /// <summary>The plan under the command's header line: its id, queues, steps and what comes next.</summary>
    internal static void WriteBody(DeploymentPlan plan, ResolvedConfig config)
    {
        // En plan Queuey lagrer, har Queueys id og hash. En plan bare herfra (--local, eller en Queuey uten planer) har ingen id
        // (F3.11), så den ikke tas for en lagret plan, og hashen er klientens (v1).
        if (plan.Stored is { } stored)
            StoredPlanText.Write(stored);
        else
            Console.WriteLine($"  local plan, not stored in Queuey  {plan.PlanHash}");
        foreach (string warning in plan.Warnings.Where(w => ServerWarnings.CodeOf(w) is "plans_unsupported" or "plans_unavailable" or "plan_required"))
            Console.WriteLine($"  ! {TerminalText.Line(warning)}");

        // Blindtesten 2026-10-09 (funn 6): ingress-URL-en lokalt var en gammel tunnel fra innloggingen, og ingenting sa hvor den
        // kom fra. Kilden står nå over køene.
        if (plan.Queues.Count > 0)
            Console.WriteLine($"  ingress host {config.ResolvedIngressBase()}  (from {config.IngressSource()})");
        foreach (DeploymentPlanQueue queue in plan.Queues)
            Console.WriteLine($"  {queue.Name}\tingress {queue.IngressUrl}{(queue.PublicId is null ? "  (would be created)" : "")}");
        foreach (DeploymentPlanStep step in plan.Steps)
        {
            bool quiet = step.Error is null && !step.Creates && step.Changes.Count == 0 && step.Notes.Count == 0;
            Console.WriteLine($"  {step.Target} · {step.Aspect}{(quiet ? "  (no change)" : "")}");
            if (step.Creates)
                Console.WriteLine("      + would be created");
            foreach (PlannedChange change in step.Changes)
                Console.WriteLine($"      ~ {change}" + (HiddenPartChanged(change)
                    ? "  (the hidden part of the URL changes: Queuey shows it redacted to a key or a login, so both sides read the same)"
                    : ""));
            foreach (string note in step.Notes)
                Console.WriteLine($"      · {note}");
            if (step.Error is { } error)
                ApplyCommand.WriteStepError(error);
        }

        // Løsrevet av en person (Queuey F2.4): ingen steg, fordi apply lar det være.
        ApplyCommand.WriteDetached(plan.Skipped, "apply skips it");
        if (plan.Stored is null && !plan.ApplyStarted && !plan.ApplyRequiresPlan)
            Console.WriteLine("  ! Queuey started no apply for this plan (it predates managed resources): each dry run was asked "
                              + "as a write from outside a deployment file. What a person detached was skipped all the same.");

        int refusals = plan.Steps.Count(s => s.Error is not null);
        Console.WriteLine($"{plan.ChangeCount} change(s), {refusals} refusal(s). Nothing was changed."
                          + (refusals > 0 ? " Fix the refusals, then plan again." : ""));
        if (plan.Stored is { } next && refusals == 0)
            StoredPlanText.WriteNext(next);
    }

    /// <summary>
    /// Whether <paramref name="change"/> is a URL whose change lies only in the part Queuey hides (Queuey #514): both sides read
    /// the same, redacted, yet Queuey computed the plan on the whole values and found them different.
    /// </summary>
    // Security-review av #72 (B1): en URL der bare spørringen eller brukerinfoen er skjult, har ingen markør. Like sider på et
    // adressefelt er nok: Queuey fant en endring i hele verdiene.
    internal static bool HiddenPartChanged(PlannedChange change)
        => change.From is { ValueKind: JsonValueKind.String } from && change.To is { ValueKind: JsonValueKind.String } to
           && string.Equals(from.GetString(), to.GetString(), StringComparison.Ordinal)
           && (RestTargetUrlRedaction.NamesAnAddress(change.Path) || DeploymentPuller.CarriesRedactionMarker(from.GetString()));

    internal static object ToJson(DeploymentPlan plan, string path, string ingressFrom)
    {
        var steps = plan.Steps.Select(s => new
        {
            target = s.Target,
            aspect = s.Aspect,
            creates = s.Creates,
            changes = s.Changes.Select(c => new { path = c.Path, from = c.From, to = c.To, hiddenPartChanged = HiddenPartChanged(c) ? true : (bool?)null }),
            notes = s.Notes,
            state = s.State,
            desired = s.Desired,
            error = s.Error is null ? null : new
            {
                code = s.Error.ErrorCode, message = s.Error.Message,
                action = s.Error.SuggestedAction ?? Advise.PlanRetention.CapHint(s.Error.ErrorCode), status = s.Error.StatusCode,
            },
        });
        var queues = plan.Queues.Select(q => new { name = q.Name, ingressUrl = q.IngressUrl, ingressFrom, publicId = q.PublicId });

        if (plan.Stored is not { } stored)
            return new
            {
                schemaVersion = JsonSchemaVersion,
                file = path,
                tenant = plan.Tenant,
                planId = (string?)null,
                planHash = plan.PlanHash,
                wouldSucceed = plan.WouldSucceed,
                changeCount = plan.ChangeCount,
                queues,
                steps,
                skipped = plan.Skipped.Select(ApplyCommand.ToJson),
                applyStarted = plan.ApplyStarted,
            };

        return new
        {
            schemaVersion = StoredJsonSchemaVersion,
            file = path,
            tenant = plan.Tenant,
            planId = stored.PlanId,
            planHash = stored.Hash,
            stored = true,
            version = stored.Version,
            status = stored.Status,
            decision = stored.Decision,
            rule = stored.Rule,
            @class = stored.Class,
            approvalUrl = stored.ApprovalUrl,
            expiresAt = stored.ExpiresAt,
            wouldSucceed = plan.WouldSucceed,
            changeCount = plan.ChangeCount,
            queues,
            steps,
            skipped = plan.Skipped.Select(ApplyCommand.ToJson),
            warnings = plan.Warnings,
        };
    }
}
