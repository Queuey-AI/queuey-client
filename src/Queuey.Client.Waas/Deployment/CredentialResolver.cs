using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Client.Waas;

/// <summary>
/// Turns the credential <b>names</b> a deployment file carries into the <c>cred_…</c> public ids the
/// API stores, resolved once per run against the workspace's credentials.
/// </summary>
/// <remarks>
/// Names are the contract a committed file needs: ids are minted per workspace, so a file holding
/// one applies only to the environment it was written in. Resolving here is what lets a single file
/// converge staging and production.
/// </remarks>
internal sealed class CredentialResolver
{
    private const string IdPrefix = "cred_";

    private readonly IQueueyManagement _management;
    private readonly string _tenantPublicId;
    private readonly CredentialStoring _storing;
    private Dictionary<string, string>? _byName;

    /// <summary>
    /// A resolver for <paramref name="tenantPublicId"/>, whose errors say how to store a missing credential the way
    /// <paramref name="storing"/> does for where the file goes: a person pastes it outside dev.
    /// </summary>
    public CredentialResolver(IQueueyManagement management, string tenantPublicId, CredentialStoring storing)
    {
        _management = management;
        _tenantPublicId = tenantPublicId;
        _storing = storing;
    }

    /// <summary>
    /// Resolves every credential name a deployment names — the workspace's delivery and each
    /// queue's, signing included — before anything is written. Every missing name is reported at
    /// once, and nothing has been sent when it is.
    /// </summary>
    public async Task<ResolvedDeliveries> ResolveAllAsync(
        WorkspaceDelivery? workspace, IEnumerable<DeploymentQueuePlan> plans, CancellationToken cancellationToken)
    {
        // Før 2026-09-24 ble en køs credential-navn slått opp først etter at køen var opprettet. Et navn
        // som manglet, feilet køen der og lot den ligge i logOnly, og neste apply beholdt modusen og ga
        // exit 0. Derfor slås alle navn opp her, før første skriving.
        // Typen følger med navnet, så forslaget om å lagre det har riktig --type: authMode for leveransen, HmacSigning for
        // signeringen. En kø uten egen authMode arver workspacets, og da er typen den.
        var references = new List<(string Name, string Where, string? Type)>();
        string? workspaceType = CredentialStoring.TypeForAuthMode(workspace?.AuthMode);
        Collect(workspace?.CredentialRef, "workspace.delivery.credentialRef", workspaceType);
        Collect(workspace?.Signing?.CredentialRef, "workspace.delivery.signing.credentialRef", CredentialStoring.RequestDefaultType);
        foreach (DeploymentQueuePlan plan in plans)
        {
            Collect(plan.Delivery?.CredentialRef, $"queues.{plan.Definition.Name}.delivery.credentialRef",
                plan.Delivery?.AuthMode is { } mode ? CredentialStoring.TypeForAuthMode(mode) : workspaceType);
            Collect(plan.Delivery?.Signing?.CredentialRef, $"queues.{plan.Definition.Name}.delivery.signing.credentialRef",
                CredentialStoring.RequestDefaultType);
        }

        if (references.Count > 0)
        {
            Dictionary<string, string> byName = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var missing = references.Where(r => !byName.ContainsKey(r.Name)).ToList();
            if (missing.Count > 0)
                throw Missing(missing, byName);
        }

        var queues = new Dictionary<string, QueueDelivery>(StringComparer.Ordinal);
        foreach (DeploymentQueuePlan plan in plans)
        {
            if (plan.Delivery is { } delivery)
                queues[plan.Definition.Name] = await ResolveAsync(delivery, cancellationToken).ConfigureAwait(false);
        }

        // Ingressens credential-navn slås ikke opp her: Queuey gjør det, og godtar et navn som ikke finnes ennå (F2.3). Planen
        // vil bare kunne si om navnet finnes, så lista lastes for det, og en nøkkel som ikke får lese den, gir «vet ikke».
        IReadOnlyCollection<string>? known = null;
        if (plans.Any(p => p.Ingress?.SignedRequest?.CredentialRef is not null))
        {
            try
            {
                known = (await LoadAsync(cancellationToken).ConfigureAwait(false)).Keys.ToList();
            }
            catch (QueueyForbiddenException)
            {
                known = null;
            }
        }

        return new ResolvedDeliveries(
            workspace is null ? null : await ResolveAsync(workspace, cancellationToken).ConfigureAwait(false),
            queues,
            known);

        void Collect(string? reference, string where, string? type)
        {
            if (string.IsNullOrWhiteSpace(reference)) return;
            string name = reference!.Trim();
            if (!name.StartsWith(IdPrefix, StringComparison.Ordinal))
                references.Add((name, where, type));
        }
    }

    /// <summary>
    /// The code a missing credential fails with: the same as <c>queuey credentials rotate</c> gives for a name the workspace has no
    /// credential under. Exit 1 in the CLI, as a refusal, not 3: the key and the host are fine, the workspace lacks the credential.
    /// </summary>
    // Gullflyten 2026-10-09: et manglende navn i leveringen ga exit 3, koden for nøkkel- og vertsfeil, mens det samme i ingressen
    // er et notat. Ingressen godtar et navn som ikke finnes ennå (Queuey F2.3); leveringen kan ikke sendes uten id-en, så den
    // nektes før noe skrives, i både plan og apply, som et avslag (exit 1).
    internal const string NotFoundCode = "credential_not_found";

    private QueueyException Missing(List<(string Name, string Where, string? Type)> missing, Dictionary<string, string> byName)
    {
        var names = missing.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();

        // Utenfor dev limer en person inn verdien (F2.9); før sto credentials set her også i prod. Ett navn får sin egen type,
        // flere får plassholdere, siden typene kan være ulike. Navnene kommer fra fila uten noen formsjekk, så de vises bare i
        // den trygge formen (review av #58, B1): før sto de rått i feilen og i kommandoen, forbi TerminalText.
        string? type = missing.Select(m => m.Type).Distinct().Count() == 1 ? missing[0].Type : null;
        return new QueueyException(
            (names.Count == 1
                ? CredentialNameRules.Showable(names[0]) is { } shown
                    ? $"No credential named '{shown}' in workspace {_tenantPublicId}"
                    : $"No credential with the name the file gives in workspace {_tenantPublicId}"
                : $"No credentials named {string.Join(", ", names.Select(Quoted))} in workspace {_tenantPublicId}")
            + $" ({string.Join("; ", missing.Select(m => m.Where))}). Nothing was changed. "
            + (names.Count == 1
                ? $"Store it first. {_storing.HowToStore(names[0], type)} "
                : $"Store each first. {_storing.HowToStore(CredentialStoring.Placeholder, null)}"
                  + (names.Any(n => CredentialNameRules.Showable(n) is null) ? " " + CredentialStoring.NotShown : "") + " ")
            + Available(byName), errorCode: NotFoundCode);
    }

    private static string Quoted(string name) => CredentialNameRules.Showable(name) is { } shown ? $"'{shown}'" : "a name that is not shown";

    // Navnene workspacet har, er lagret av en med skrivetilgang: de vises i den trygge formen, og resten telles.
    private static string Available(Dictionary<string, string> byName)
    {
        if (byName.Count == 0)
            return "This workspace has no credentials yet.";

        List<string> shown = byName.Keys.Select(CredentialNameRules.Showable).OfType<string>().OrderBy(k => k, StringComparer.Ordinal).ToList();
        int hidden = byName.Count - shown.Count;
        string others = hidden == 1 ? "1 whose name is not shown" : $"{hidden} whose names are not shown";
        return hidden == 0 ? $"Available: {string.Join(", ", shown)}."
            : shown.Count == 0 ? $"Available: {others}."
            : $"Available: {string.Join(", ", shown)}, and {others}.";
    }

    /// <summary>
    /// Resolves one reference. Null/blank passes through (blank means "keep the stored secret"), and
    /// so does a literal <c>cred_…</c> id. Anything else is looked up by name.
    /// </summary>
    public async Task<string?> ResolveAsync(string? reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return reference;

        string name = reference!.Trim();
        if (name.StartsWith(IdPrefix, StringComparison.Ordinal))
            return name;

        Dictionary<string, string> byName = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (byName.TryGetValue(name, out string? publicId))
            return publicId;

        throw new QueueyException(
            (CredentialNameRules.Showable(name) is { } shown
                ? $"No credential named '{shown}' in workspace {_tenantPublicId}. "
                : $"No credential with the name the file gives in workspace {_tenantPublicId}. ") +
            $"Store it first. {_storing.HowToStore(name, null)} " +
            Available(byName), errorCode: NotFoundCode);
    }

    /// <summary>Resolves both halves of a workspace delivery patch, returning a copy safe to send.</summary>
    public async Task<WorkspaceDelivery> ResolveAsync(WorkspaceDelivery delivery, CancellationToken cancellationToken)
        => new()
        {
            BaseUrl = delivery.BaseUrl,
            AuthMode = delivery.AuthMode,
            CredentialRef = await ResolveAsync(delivery.CredentialRef, cancellationToken).ConfigureAwait(false),
            AuthHeaderName = delivery.AuthHeaderName,
            Method = delivery.Method,
            TimeoutMs = delivery.TimeoutMs,
            Signing = await ResolveAsync(delivery.Signing, cancellationToken).ConfigureAwait(false),
            RateLimit = delivery.RateLimit,
        };

    /// <summary>Resolves both halves of a queue delivery patch, returning a copy safe to send.</summary>
    public async Task<QueueDelivery> ResolveAsync(QueueDelivery delivery, CancellationToken cancellationToken)
        => new()
        {
            Url = delivery.Url,
            Inherit = delivery.Inherit,
            AuthMode = delivery.AuthMode,
            CredentialRef = await ResolveAsync(delivery.CredentialRef, cancellationToken).ConfigureAwait(false),
            AuthHeaderName = delivery.AuthHeaderName,
            TimeoutMs = delivery.TimeoutMs,
            Signing = await ResolveAsync(delivery.Signing, cancellationToken).ConfigureAwait(false),
            RateLimit = delivery.RateLimit,
        };

    private async Task<DeliverySigning?> ResolveAsync(DeliverySigning? signing, CancellationToken cancellationToken)
        => signing is null ? null : new DeliverySigning
        {
            Enabled = signing.Enabled,
            CredentialRef = await ResolveAsync(signing.CredentialRef, cancellationToken).ConfigureAwait(false),
            TemplateKey = signing.TemplateKey,
        };

    /// <summary>The workspace's credentials, fetched once per run. Later names hit the cache.</summary>
    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_byName is not null)
            return _byName;

        IReadOnlyList<CredentialResult> credentials =
            await _management.ListCredentialsAsync(_tenantPublicId, cancellationToken).ConfigureAwait(false);

        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CredentialResult c in credentials)
        {
            if (!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.PublicId))
                byName[c.Name!] = c.PublicId!;
        }

        return _byName = byName;
    }
}

/// <summary>The deliveries of a deployment with every credential name turned into the id the API stores.</summary>
internal sealed class ResolvedDeliveries
{
    private readonly IReadOnlyCollection<string>? _knownCredentialNames;

    public ResolvedDeliveries(WorkspaceDelivery? workspace, IReadOnlyDictionary<string, QueueDelivery> queues,
        IReadOnlyCollection<string>? knownCredentialNames = null)
    {
        Workspace = workspace;
        Queues = queues;
        _knownCredentialNames = knownCredentialNames;
    }

    /// <summary>
    /// Whether the workspace has a credential by <paramref name="name"/>: false only when the list was read and has none,
    /// so a caller that could not read it says nothing.
    /// </summary>
    public bool KnownCredential(string name)
        => _knownCredentialNames is null || _knownCredentialNames.Contains(name.Trim(), StringComparer.Ordinal);

    /// <summary>The workspace's delivery patch, or null when the file declares none.</summary>
    public WorkspaceDelivery? Workspace { get; }

    /// <summary>Each queue's delivery patch, by queue name — only the queues that declare one.</summary>
    public IReadOnlyDictionary<string, QueueDelivery> Queues { get; }
}
