using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey plan</c> — the server-side plan: every write <c>apply</c> would send goes as a dry run, so
/// the answer is what Queuey would accept and change, not only what the file says locally. Writes
/// nothing, and exits non-zero when any write would be refused.
/// </summary>
/// <remarks>
/// A verb, not an <c>apply</c> flag: a CLI that predates it answers "Unknown command" instead of
/// running the apply it was asked to plan.
/// </remarks>
internal static class PlanCommand
{
    // --adopt planlegger det en person har løsrevet, som apply --adopt ville skrevet det (Queuey F2.4).
    internal static readonly CommandOptions Options = new("plan", flags: new[] { "json" }, values: new[] { "file", "adopt", "profile" });

    /// <summary>
    /// The version of <c>plan --json</c>'s shape: <c>{ schemaVersion, file, tenant, planId, planHash, wouldSucceed,
    /// changeCount, queues, steps }</c>. A script that reads it checks this first, as it does in <c>apply --dry-run --json</c>.
    /// </summary>
    // planId, planHash, queues og hvert stegs state kom til i samme versjon (Queuey F2.3, 2026-10-06): ingen tag har sluppet
    // versjon 1 ennå, og feltene legger bare til. Det samme gjelder skipped (Queuey F2.4, 2026-10-06).
    // Samme mønster som apply --dry-run --json, der Kenneth valgte et versjonert objekt (2026-10-05). Formen er ny med
    // queuey plan, så den har en versjon fra første utgave, og ingen leser må gjette når den endres.
    internal const int JsonSchemaVersion = 1;

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        // Samme profil som apply (F2.7): fila med profilens verdier, og tilkoblingen hos brukeren.
        string? profile = CliHost.Profile(map);
        if (!ApplyCommand.TryReadDeploymentFile(map, out string path, out DeploymentFile file, out failure, profile)) return failure;
        if (profile is not null)
            file = ApplyCommand.ForProfile(file, profile, path);

        // Samme workspace som apply ville skrevet til, etter samme regel.
        ResolvedConfig config = CliHost.ResolveForDeployment(map, profile is null ? file.ResolveTenant() : file.Tenant, path);
        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        // Det en person har løsrevet, planlegges ikke, med mindre --adopt tar det tilbake: planen viser det apply ville gjort.
        DeploymentPlan plan = await service.PlanDeploymentAsync(file, new SyncOptions { Adopt = DeploymentAdopt.Parse(map.Get("adopt")) });

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = JsonSchemaVersion,
                file = path,
                tenant = plan.Tenant,
                planId = plan.PlanId,
                planHash = plan.PlanHash,
                wouldSucceed = plan.WouldSucceed,
                changeCount = plan.ChangeCount,
                queues = plan.Queues.Select(q => new { name = q.Name, ingressUrl = q.IngressUrl, publicId = q.PublicId }),
                steps = plan.Steps.Select(s => new
                {
                    target = s.Target,
                    aspect = s.Aspect,
                    creates = s.Creates,
                    changes = s.Changes.Select(c => new { path = c.Path, from = c.From, to = c.To }),
                    notes = s.Notes,
                    state = s.State,
                    desired = s.Desired,
                    error = s.Error is null ? null : new { code = s.Error.ErrorCode, message = s.Error.Message, action = s.Error.SuggestedAction, status = s.Error.StatusCode },
                }),
                skipped = plan.Skipped.Select(ApplyCommand.ToJson),
                applyStarted = plan.ApplyStarted,
            }, CliHost.JsonOut));
            return plan.WouldSucceed ? ExitCodes.Success : ExitCodes.RuntimeError;
        }

        Console.WriteLine($"Queuey plan — {path} → {config.ResolvedApiBase()}  (tenant {plan.Tenant})");
        Console.WriteLine($"  {plan.PlanId}  {plan.PlanHash}");
        foreach (DeploymentPlanQueue queue in plan.Queues)
            Console.WriteLine($"  {queue.Name}\tingress {queue.IngressUrl}{(queue.PublicId is null ? "  (would be created)" : "")}");
        foreach (DeploymentPlanStep step in plan.Steps)
        {
            bool quiet = step.Error is null && !step.Creates && step.Changes.Count == 0 && step.Notes.Count == 0;
            Console.WriteLine($"  {step.Target} · {step.Aspect}{(quiet ? "  (no change)" : "")}");
            if (step.Creates)
                Console.WriteLine("      + would be created");
            foreach (PlannedChange change in step.Changes)
                Console.WriteLine($"      ~ {change}");
            foreach (string note in step.Notes)
                Console.WriteLine($"      · {note}");
            if (step.Error is { } error)
            {
                Console.WriteLine($"      ✗ {error.ErrorCode ?? "refused"}: {error.Message}");
                if (error.SuggestedAction is { } action)
                    Console.WriteLine($"        → {action}");
            }
        }

        // Løsrevet av en person (Queuey F2.4): ingen steg, fordi apply lar det være.
        ApplyCommand.WriteDetached(plan.Skipped, "apply skips it");
        if (!plan.ApplyStarted)
            Console.WriteLine("  ! Queuey started no apply for this plan (it predates managed resources): each dry run was asked "
                              + "as a write from outside a deployment file. What a person detached was skipped all the same.");

        int refusals = plan.Steps.Count(s => s.Error is not null);
        Console.WriteLine($"{plan.ChangeCount} change(s), {refusals} refusal(s). Nothing was changed."
                          + (refusals > 0 ? " Fix the refusals, then plan again." : ""));
        return plan.WouldSucceed ? ExitCodes.Success : ExitCodes.RuntimeError;
    }
}
