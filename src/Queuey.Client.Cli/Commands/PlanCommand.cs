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
    internal static readonly CommandOptions Options = new("plan", flags: new[] { "json" }, values: new[] { "file" });

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        if (!ApplyCommand.TryReadDeploymentFile(map, out string path, out DeploymentFile file, out failure)) return failure;

        // Samme workspace som apply ville skrevet til, etter samme regel.
        ResolvedConfig config = CliHost.ResolveForDeployment(map, file.ResolveTenant(), path);
        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        DeploymentPlan plan = await service.PlanDeploymentAsync(file);

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                file = path,
                tenant = plan.Tenant,
                wouldSucceed = plan.WouldSucceed,
                changeCount = plan.ChangeCount,
                steps = plan.Steps.Select(s => new
                {
                    target = s.Target,
                    aspect = s.Aspect,
                    creates = s.Creates,
                    changes = s.Changes.Select(c => new { path = c.Path, from = c.From, to = c.To }),
                    notes = s.Notes,
                    error = s.Error is null ? null : new { code = s.Error.ErrorCode, message = s.Error.Message, action = s.Error.SuggestedAction, status = s.Error.StatusCode },
                }),
            }, CliHost.JsonOut));
            return plan.WouldSucceed ? ExitCodes.Success : ExitCodes.RuntimeError;
        }

        Console.WriteLine($"Queuey plan — {path} → {config.ResolvedApiBase()}  (tenant {plan.Tenant})");
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

        int refusals = plan.Steps.Count(s => s.Error is not null);
        Console.WriteLine($"{plan.ChangeCount} change(s), {refusals} refusal(s). Nothing was changed."
                          + (refusals > 0 ? " Fix the refusals, then plan again." : ""));
        return plan.WouldSucceed ? ExitCodes.Success : ExitCodes.RuntimeError;
    }
}
