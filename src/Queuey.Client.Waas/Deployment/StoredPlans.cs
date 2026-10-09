using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Queuey.Client.Waas;

// Lagrede planer (Queuey F3.11, beslutning 1 og 6 i plan-approval-f311). Planen bygges på serveren av de vanlige dry runs:
// en tom plan, hver skriving som dry run med X-Queuey-Plan, det apply sender til en kø planen oppretter (desired), og
// forseglingen, som svarer med policyens avgjørelse. Trenger planen en person, sendes den til innboksen (submit). En godkjent
// plan applyes med planId og planHash, og hver skriving i applyen er da ett av planens steg, sendt én gang.
//
// Diffen godkjenneren ser, er serverens. Klientens egen plan (hashen v1, DeploymentPlanHash) er uendret og er det
// `queuey plan --local` viser: den lagres ingen steder og har ingen id hos Queuey.

/// <summary>
/// A configuration plan Queuey stores (Queuey F3.11): built from the dry runs of a deployment's writes, sealed with a hash
/// and the policy's decision, and approved in Queuey's inbox when the policy gives it to a person. Apply writes it with
/// <see cref="SyncOptions.Plan"/>.
/// </summary>
public sealed class StoredPlan
{
    /// <summary>The plan's id in Queuey: <c>plan_…</c>. Opaque: compare it, never parse it.</summary>
    public string PlanId { get; init; } = default!;

    /// <summary>The workspace it was built for.</summary>
    public string Tenant { get; init; } = default!;

    /// <summary>
    /// Where it stands: <c>proposed</c> while it is built and once sealed, <c>pending_approval</c> while it waits for a
    /// person, <c>approved</c>, <c>rejected</c>, <c>expired</c>, <c>executing</c>, <c>succeeded</c>, <c>failed</c>,
    /// <c>plan_stale</c>, or a status a later Queuey adds, shown as it comes.
    /// </summary>
    public string Status { get; init; } = default!;

    /// <summary><c>undecided</c> until sealed, then the policy's: <c>execute</c>, <c>requires_approval</c> or <c>denied</c>.</summary>
    public string Decision { get; init; } = "undecided";

    /// <summary>The policy rule behind <see cref="Decision"/>, once sealed.</summary>
    public string? Rule { get; init; }

    /// <summary>The strictest class among its steps (<c>creates</c>, <c>metadata</c>, <c>changes</c>, <c>deletes</c>), once sealed.</summary>
    public string? Class { get; init; }

    /// <summary>The hash a person's approval binds, as 64 hex characters, once sealed.</summary>
    public string? Hash { get; init; }

    /// <summary>The version the approval binds with the hash.</summary>
    public int Version { get; init; }

    /// <summary>Where a person approves it, when the policy gives it to one and Queuey has a console address.</summary>
    public string? ApprovalUrl { get; init; }

    /// <summary>Until when it can be built, waits for a person, or can be applied, depending on where it stands.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>What an apply of it takes back from a detach: the workspace's id and queue ids.</summary>
    public IReadOnlyList<string> Adopt { get; init; } = Array.Empty<string>();

    /// <summary>Its steps as Queuey stored them, when they were read.</summary>
    public IReadOnlyList<StoredPlanStep> Steps { get; init; } = Array.Empty<StoredPlanStep>();

    /// <summary>True once it is sealed: it has a hash and a decision.</summary>
    public bool IsSealed => Hash is not null && Decision != Decisions.Undecided;

    /// <summary>True while it waits in the inbox for a person.</summary>
    public bool IsPendingApproval => Status == Statuses.PendingApproval;

    /// <summary>
    /// True when an apply may write it now: a person approved it, or the policy runs it and it has not been started.
    /// </summary>
    public bool CanBeApplied => IsSealed && (Status == Statuses.Approved || (Status == Statuses.Proposed && Decision == Decisions.Execute));

    /// <summary>True when it was sealed for a person, and has not been sent to the inbox.</summary>
    public bool NeedsSubmitting => IsSealed && Status == Statuses.Proposed && Decision == Decisions.RequiresApproval;

    /// <summary>The statuses this client acts on. Any other is shown as Queuey wrote it.</summary>
    public static class Statuses
    {
        /// <summary>Being built, or sealed and not yet sent anywhere.</summary>
        public const string Proposed = "proposed";

        /// <summary>Waiting in the inbox for a person.</summary>
        public const string PendingApproval = "pending_approval";

        /// <summary>A person approved it: an apply may write it.</summary>
        public const string Approved = "approved";

        /// <summary>An apply of it has started.</summary>
        public const string Executing = "executing";

        /// <summary>An apply wrote every step.</summary>
        public const string Succeeded = "succeeded";

        /// <summary>What it rests on moved; a new plan shows what is left.</summary>
        public const string PlanStale = "plan_stale";
    }

    /// <summary>The policy's decisions.</summary>
    public static class Decisions
    {
        /// <summary>Not sealed yet.</summary>
        public const string Undecided = "undecided";

        /// <summary>The policy runs it: it can be applied at once.</summary>
        public const string Execute = "execute";

        /// <summary>A person approves it first.</summary>
        public const string RequiresApproval = "requires_approval";

        /// <summary>The policy refuses it.</summary>
        public const string Denied = "denied";
    }

    // Formen sjekkes før noe sendes (Queuey sjekker den igjen): plan_ og tegnene en id har, høyst 64. Aldri delt på _.
    internal static bool IsPlanId(string? value)
        => value is { Length: > 5 and <= 64 } && value.StartsWith("plan_", StringComparison.Ordinal)
           && value.Skip(5).All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_' || c == '-'));

    internal static StoredPlan From(PlanWireResponse wire, string tenant) => new()
    {
        PlanId = wire.PlanId ?? throw new QueueyException("Queuey answered without the plan's id."),
        Tenant = string.IsNullOrEmpty(wire.Workspace) ? tenant : wire.Workspace!,
        Status = wire.Status ?? Statuses.Proposed,
        Decision = wire.Decision ?? Decisions.Undecided,
        Rule = wire.Rule,
        Class = wire.Class,
        Hash = wire.Hash,
        Version = wire.Version,
        ExpiresAt = wire.ExpiresAt,
        Adopt = wire.Adopt ?? new List<string>(),
        Steps = (wire.Steps ?? new List<PlanStepWire>()).Select(s => new StoredPlanStep
        {
            Index = s.Index,
            Target = s.Target ?? string.Empty,
            Aspect = s.Aspect ?? string.Empty,
            Creates = s.Creates,
            Class = s.Class,
            Desired = s.Desired is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } desired ? desired.Clone() : null,
            Refusal = s.Refusal,
        }).ToList(),
    };
}

/// <summary>One step of a <see cref="StoredPlan"/>, as Queuey stored it.</summary>
public sealed class StoredPlanStep
{
    /// <summary>Its place in the plan.</summary>
    public int Index { get; init; }

    /// <summary>The queue (<c>que_…</c>) or workspace (<c>ten_…</c>) it writes, or the name of a queue it creates.</summary>
    public string Target { get; init; } = default!;

    /// <summary>What it writes: <c>queue</c>, <c>environment</c>, <c>ingress</c>, <c>policy</c>, <c>delivery</c>, <c>mode</c>, …</summary>
    public string Aspect { get; init; } = default!;

    /// <summary>True when it creates its queue.</summary>
    public bool Creates { get; init; }

    /// <summary>Its class in the policy, once sealed; null for a step that changes nothing.</summary>
    public string? Class { get; init; }

    /// <summary>For a queue it creates: what apply sends once the queue exists.</summary>
    public JsonElement? Desired { get; init; }

    /// <summary>The code Queuey refused its dry run with, or null.</summary>
    public string? Refusal { get; init; }
}

/// <summary>
/// Queuey refused to start an apply without a configuration plan (<c>plan_required</c>, Queuey F3.11): an API key applies to
/// this workspace only through a plan, which the policy runs or a person approves. Nothing was written. Build the plan with
/// <see cref="IQueueyPlans.StorePlanAsync"/>, and apply it with <see cref="SyncOptions.Plan"/>; <c>queuey apply</c> does both.
/// </summary>
public sealed class QueueyPlanRequiredException : QueueyException
{
    /// <summary>The code Queuey answers with.</summary>
    public const string Code = "plan_required";

    internal QueueyPlanRequiredException(QueueyException refusal)
        : base(refusal.Message, refusal.StatusCode ?? 403, Code, refusal)
    {
        SuggestedAction = refusal.SuggestedAction;
    }

    internal static bool Is(QueueyException ex) => ex.ErrorCode == Code;
}

/// <summary>
/// The warnings Queuey answered with (<c>X-Queuey-Warning</c>, one per warning, <c>code: message</c>), in the order they
/// came and without repeats. A collector passes each one on to the one around it.
/// </summary>
internal sealed class ServerWarnings
{
    private readonly List<string> _items = new();
    private readonly Action<string>? _onWarning;

    public ServerWarnings(Action<string>? onWarning = null) => _onWarning = onWarning;

    /// <summary>The collector around this one, which gets each warning too.</summary>
    public ServerWarnings? Outer { get; set; }

    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_items)
                return _items.ToArray();
        }
    }

    public void Add(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
            return;
        string trimmed = warning.Trim();
        bool added;
        lock (_items)
        {
            added = !_items.Contains(trimmed, StringComparer.Ordinal);
            if (added)
                _items.Add(trimmed);
        }

        if (added)
            _onWarning?.Invoke(trimmed);
        Outer?.Add(trimmed);
    }

    /// <summary>The code of a warning, the part before its first colon: <c>would_require_approval</c>, for one.</summary>
    public static string? CodeOf(string warning)
    {
        int colon = warning.IndexOf(':');
        return colon <= 0 ? null : warning.Substring(0, colon).Trim();
    }
}

/// <summary>A write in an apply bound to a plan that Queuey says is already written, for a caller that needed its answer.</summary>
internal sealed class StepAlreadyWrittenException : QueueyException
{
    public StepAlreadyWrittenException(string? queueName)
        : base($"Queuey says the step that applies queue '{queueName}' is already written.", 409, QueueyControlPlaneClient.StepAlreadyAppliedCode)
    {
        QueueName = queueName;
    }

    public string? QueueName { get; }
}

// ── wire ──────────────────────────────────────────────────────────────────────

/// <summary>Wire request for <c>POST /tenants/{t}/deployment/plans</c>.</summary>
internal sealed class StartPlanWireRequest
{
    public PlanSourceWire? Source { get; set; }
    public StartApplyAdoptWire? Adopt { get; set; }
}

internal sealed class PlanSourceWire
{
    public string? Repo { get; set; }
    public string? Ref { get; set; }
    public string? Path { get; set; }
    public string? Commit { get; set; }
    public string? Workflow { get; set; }
    public string? PullRequest { get; set; }
}

/// <summary>Wire response for a plan: <c>POST …/plans</c> (without steps) and <c>GET …/plans/{plan}</c> (with them).</summary>
internal sealed class PlanWireResponse
{
    public string? PlanId { get; set; }
    public string? Workspace { get; set; }
    public string? Status { get; set; }
    public int Version { get; set; }
    public string? Decision { get; set; }
    public string? Rule { get; set; }
    public string? Class { get; set; }
    public string? Hash { get; set; }
    public string? Executes { get; set; }
    public List<string>? Adopt { get; set; }
    public int StepCount { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? SealedAt { get; set; }
    public List<PlanStepWire>? Steps { get; set; }
    public List<PlanAppliedStepWire>? AppliedSteps { get; set; }
}

/// <summary>One write an apply bound to the plan made: the step, the part of what it sends to a queue it created, and when.</summary>
internal sealed class PlanAppliedStepWire
{
    public int Step { get; set; }
    public string? Part { get; set; }
    public DateTimeOffset? At { get; set; }
}

/// <summary>
/// The stored plan an apply writes (Queuey F3.11), and how many of its writes Queuey has answered: the apply's writes go one at
/// a time, so after a lost answer a step more in the plan's <c>appliedSteps</c> is that write.
/// </summary>
internal sealed class PlanApplyProgress
{
    public PlanApplyProgress(string tenant, string planId)
    {
        Tenant = tenant;
        PlanId = planId;
    }

    public string Tenant { get; }
    public string PlanId { get; }

    /// <summary>The writes Queuey has answered as written, or as already written.</summary>
    public int Confirmed { get; set; }
}

internal sealed class PlanStepWire
{
    public int Index { get; set; }
    public string? Target { get; set; }
    public string? Aspect { get; set; }
    public bool Creates { get; set; }
    public string? Class { get; set; }
    public JsonElement? Desired { get; set; }
    public string? Refusal { get; set; }
}

/// <summary>Wire response for <c>POST …/plans/{plan}/seal</c>.</summary>
internal sealed class SealedPlanWireResponse
{
    public string? PlanId { get; set; }
    public int Version { get; set; }
    public string? Hash { get; set; }
    public string? Status { get; set; }
    public string? Decision { get; set; }
    public string? Rule { get; set; }
    public string? Class { get; set; }
    public int StepCount { get; set; }
    public string? InboxUrl { get; set; }
}

/// <summary>Wire response for <c>POST …/plans/{plan}/submit</c> (202).</summary>
internal sealed class PlanPendingWireResponse
{
    public string? Plan { get; set; }
    public string? Status { get; set; }
    public string? ApprovalUrl { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? PolicyRule { get; set; }
}

/// <summary>A warning a started apply got (<c>warnings</c> in the answer, also in <c>X-Queuey-Warning</c>).</summary>
internal sealed class ApplyWarningWire
{
    public string? Code { get; set; }
    public string? Message { get; set; }
}
