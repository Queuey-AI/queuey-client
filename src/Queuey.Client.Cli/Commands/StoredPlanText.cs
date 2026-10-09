using System;
using System.Threading.Tasks;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

// Lagrede planer i CLI-en (Queuey F3.11, beslutning 6 i plan-approval-f311): det plan og apply skriver om en plan Queuey har,
// og ventingen på en persons godkjenning. Planen venter i innboksen i 24 timer; apply --wait venter høyst --timeout.

/// <summary>What plan and apply write about a plan Queuey stores, and the wait for a person's approval.</summary>
internal static class StoredPlanText
{
    /// <summary>How long <c>apply --wait</c> waits for a person when <c>--timeout</c> is left out: 30 minutes.</summary>
    internal const int DefaultWaitSeconds = 30 * 60;

    /// <summary>How often <c>--wait</c> reads the plan again. A setting for the tests.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The plan's id and hash, its decision and rule, and what to do next, one line each.</summary>
    internal static void Write(StoredPlan plan)
    {
        Console.WriteLine($"  {TerminalText.Line(plan.PlanId)}  stored in Queuey"
                          + (plan.Hash is { } hash ? $", hash {TerminalText.Line(hash)} (version {plan.Version})" : ", not sealed"));
        if (!plan.IsSealed)
            return;

        Console.WriteLine($"  Decision: {TerminalText.Line(plan.Decision)}"
                          + (plan.Rule is { } rule ? $" by policy rule {TerminalText.Line(rule)}" : "")
                          + (plan.Class is { } cls ? $" (class {TerminalText.Line(cls)})" : "")
                          + $", {TerminalText.Line(plan.Status)}.");
    }

    /// <summary>What comes next for <paramref name="plan"/>, after the plan command's steps.</summary>
    internal static void WriteNext(StoredPlan plan)
    {
        if (plan.IsPendingApproval)
        {
            Console.WriteLine("  It waits for a person's approval in Queuey's inbox"
                              + (plan.ApprovalUrl is { } url ? $": {TerminalText.Line(url)}" : ".")
                              + (plan.ExpiresAt is { } until ? $" It expires {until.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z." : ""));
            Console.WriteLine($"  Apply it once approved: queuey apply --plan {plan.PlanId}  (--wait waits for the approval)");
        }
        else if (plan.CanBeApplied)
        {
            Console.WriteLine($"  The policy runs it: queuey apply --plan {plan.PlanId} applies it.");
        }
        else if (plan.Decision == StoredPlan.Decisions.Denied)
        {
            Console.WriteLine("  The policy refuses it, so nothing applies it. A person makes the change in the Queuey console.");
        }
    }

    /// <summary>The plan as <c>--json</c> writes it.</summary>
    internal static object ToJson(StoredPlan plan) => new
    {
        planId = plan.PlanId,
        planHash = plan.Hash,
        version = plan.Version,
        status = plan.Status,
        decision = plan.Decision,
        rule = plan.Rule,
        @class = plan.Class,
        approvalUrl = plan.ApprovalUrl,
        expiresAt = plan.ExpiresAt,
    };

    /// <summary>
    /// Reads <paramref name="plan"/> again until it no longer waits for a person, or <paramref name="timeout"/> has passed.
    /// The plan as it stood last.
    /// </summary>
    internal static async Task<StoredPlan> WaitAsync(IQueueyService service, StoredPlan plan, TimeSpan timeout, bool json)
    {
        if (!json)
            Console.WriteLine($"Waiting up to {(int)timeout.TotalSeconds} s for a person to approve {plan.PlanId}…");

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (plan.IsPendingApproval)
        {
            TimeSpan left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero)
                break;
            await Task.Delay(left < PollInterval ? left : PollInterval);
            plan = await service.GetStoredPlanAsync(plan.PlanId, plan.Tenant);
        }

        return plan;
    }

    /// <summary>
    /// Why <paramref name="plan"/> cannot be applied, as an error the CLI writes: its status, and that a new plan shows what
    /// is left. Null when it can be.
    /// </summary>
    internal static QueueyException? WhyNotApplicable(StoredPlan plan)
    {
        if (plan.CanBeApplied)
            return null;
        if (plan.Decision == StoredPlan.Decisions.Denied)
            return new QueueyException($"The policy refuses plan {plan.PlanId}" + (plan.Rule is { } rule ? $" (rule {rule})" : "")
                                       + ", so nothing applies it.", errorCode: "plan_denied")
            {
                SuggestedAction = "A person makes the change in the Queuey console.",
            };
        if (!plan.IsSealed)
            return new QueueyException($"Plan {plan.PlanId} is not sealed, so it cannot be applied.", errorCode: "plan_not_sealed")
            {
                SuggestedAction = PlanAgain,
            };
        return new QueueyException($"Plan {plan.PlanId} is {plan.Status}, so it is not applied.", errorCode: "plan_not_applicable")
        {
            SuggestedAction = PlanAgain,
        };
    }

    /// <summary>What to do after a plan went stale or a write was not in it.</summary>
    internal const string PlanAgain = "Plan again: `queuey apply` makes a new plan of what is left, and `queuey plan` shows it first.";

    /// <summary>True for the refusals that mean the plan no longer fits: <c>plan_stale</c> and <c>not_in_plan</c>.</summary>
    internal static bool MeansPlanAgain(string? code) => code is "plan_stale" or "not_in_plan";
}
