using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli.Advise;

/// <summary>A flow advise can see in the repository, written as a Desired Flow with its evidence.</summary>
public sealed record FlowCandidate(string Summary, DesiredFlow Flow);

/// <summary>What advise makes of an intent: the enriched flow, and the design when no conflict stands in the way.</summary>
public sealed class FlowAdvice
{
    public DesiredFlow Flow { get; init; } = new();

    /// <summary>Queuey where the repository has it already: a package, a registration, a deployment file.</summary>
    public IReadOnlyList<FlowEvidence> Existing { get; init; } = Array.Empty<FlowEvidence>();

    /// <summary>Null while the flow has a conflict: advise then proposes nothing.</summary>
    public FlowDesign? Design { get; init; }
}

/// <summary>
/// Turns an intent into a flow advise can stand behind. The stated fields govern; the repository says how, not whether.
/// Each field the intent leaves out is filled from evidence, with its file and line, or assumed. Where the evidence
/// contradicts the intent, points several ways, or the intent asks for what Queuey cannot do, the flow gets a conflict and
/// advise stops there: it asks rather than proposing something else.
/// </summary>
public static class FlowAdvisor
{
    /// <param name="profile">
    /// The deployment file's profile the flow goes into, as <c>advise --profile</c> names it: one the file has, or the name of
    /// the first in a new file. Null lets advise choose by the environment each profile gives.
    /// </param>
    public static FlowAdvice Advise(DesiredFlow intent, FlowFacts facts, string root, string? queue = null, Advice? sending = null,
        string? profile = null)
    {
        if (intent is null) throw new ArgumentNullException(nameof(intent));
        if (facts is null) throw new ArgumentNullException(nameof(facts));

        DesiredFlow flow = intent.Intent();
        if (queue is not null)
            flow.Set("queue", FlowValue.Of(queue, Provenance.Stated));

        new Enrichment(flow, facts, root, seed: null, profile).Run();
        return new FlowAdvice
        {
            Flow = flow,
            Existing = facts.Queuey.Select(f => f.ToEvidence()).ToArray(),
            Design = flow.Conflicts.Count == 0 ? FlowDesigner.Design(flow, facts, sending, profile) : null,
        };
    }

    /// <summary>The flows the repository shows without an intent: one per Stripe or Supabase handler, each with its evidence.</summary>
    public static IReadOnlyList<FlowCandidate> Candidates(FlowFacts facts, string root)
    {
        var candidates = new List<FlowCandidate>();
        foreach (Handler seed in Handler.All(facts))
        {
            var flow = new DesiredFlow();
            new Enrichment(flow, facts, root, seed).Run();
            candidates.Add(new FlowCandidate(seed.Summary(), flow));
        }
        return candidates;
    }

    internal static string Title(string? kind) => kind switch
    {
        "stripe" => "Stripe",
        "supabase" => "Supabase",
        "app" => "This repository",
        _ => kind ?? "?",
    };
}

/// <summary>A handler of a provider's events: where it is, its route when one shows, and why it is that provider's.</summary>
internal sealed record Handler(string Kind, string File, int Line, RouteFinding? Route, IReadOnlyList<FlowEvidence> KindEvidence)
{
    public string Summary() => Route is { } r
        ? $"{FlowAdvisor.Title(Kind)} → Queuey → POST {r.Route} ({r.Framework}, {r.File}:{r.Line})"
        : $"{FlowAdvisor.Title(Kind)} → Queuey → the handler at {File}:{Line}, whose route I could not read";

    /// <summary>Every Stripe and Supabase handler the repository shows, one per route.</summary>
    public static IReadOnlyList<Handler> All(FlowFacts facts)
    {
        var handlers = new List<Handler>();

        foreach (IGrouping<string, StripeVerificationFinding> file in facts.StripeVerifications.GroupBy(v => v.File))
        {
            FlowEvidence[] evidence = file.Select(v => v.ToEvidence()).ToArray();
            RouteFinding[] routes = facts.Routes.Where(r => r.File == file.Key).ToArray();
            if (routes.Length == 0)
                handlers.Add(new Handler("stripe", file.Key, file.First().Line, null, evidence));
            else
                handlers.AddRange(routes.Select(r => new Handler("stripe", file.Key, file.First().Line, Mounted(facts, r), evidence)));
        }

        var supabaseFiles = new Dictionary<string, List<FlowEvidence>>(StringComparer.Ordinal);
        foreach (Finding read in facts.SupabasePayloadReads)
            Add(supabaseFiles, read.File, read.ToEvidence());
        foreach (SupabaseTriggerFinding trigger in facts.SupabaseTriggers.Where(t => t.TargetPath is not null))
        {
            foreach (RouteFinding route in facts.Routes.Where(r => FlowScan.SameRoute(r.Route, trigger.TargetPath!)))
                Add(supabaseFiles, route.File, trigger.ToEvidence());
        }

        foreach ((string file, List<FlowEvidence> evidence) in supabaseFiles)
        {
            if (facts.StripeVerifications.Any(v => v.File == file))
                continue;   // en Stripe-handler som også leser old_record, er Stripe sin
            RouteFinding[] routes = facts.Routes.Where(r => r.File == file).ToArray();
            int line = evidence.Select(e => e.Line ?? 1).Min();
            if (routes.Length == 0)
                handlers.Add(new Handler("supabase", file, line, null, evidence.Distinct().ToArray()));
            else
                handlers.AddRange(routes.Select(r => new Handler("supabase", file, line, Mounted(facts, r), evidence.Distinct().ToArray())));
        }

        return handlers;

        static void Add(Dictionary<string, List<FlowEvidence>> into, string file, FlowEvidence evidence)
        {
            if (!into.TryGetValue(file, out List<FlowEvidence>? list))
                into[file] = list = new List<FlowEvidence>();
            list.Add(evidence);
        }
    }

    /// <summary>
    /// The route with the prefix it is mounted at, within its own project: a mount that names the router's file
    /// (app.use('/webhooks', webhooksRouter) for routes/webhooks.js), a Python router's prefix in the same file
    /// (APIRouter(prefix="/api")), or a prefix for every route (Nest's setGlobalPrefix). Otherwise the route as it is written.
    /// </summary>
    internal static RouteFinding Mounted(FlowFacts facts, RouteFinding route)
    {
        if (route.FromPath)
            return route;

        string project = facts.ProjectOf(route.File);
        string module = Path.GetFileNameWithoutExtension(route.File);
        bool named = module.Length >= 3 && module is not ("index" or "main" or "app" or "server");

        // Bare monteringer i samme prosjekt: i et monorepo kan en annen app montere en fil med samme navn.
        MountFinding? mount = facts.Mounts
            .Where(m => facts.ProjectOf(m.File) == project)
            .FirstOrDefault(m =>
                m.Target == "*"
                || (m.File == route.File && (m.Target.Contains("APIRouter(", StringComparison.Ordinal)
                                             || m.Target.Contains("Blueprint(", StringComparison.Ordinal)))
                || (named && m.File != route.File && m.Target.Contains(module, StringComparison.OrdinalIgnoreCase)));
        return mount is null ? route : route with { Route = FlowScan.NormalizeRoute(FlowScan.Combine(mount.Prefix, route.Route)) };
    }
}

/// <summary>One pass over a flow: each field resolved in turn, against the intent and the evidence.</summary>
internal sealed class Enrichment
{
    private readonly DesiredFlow _flow;
    private readonly FlowFacts _facts;
    private readonly string _root;
    private readonly Handler? _seed;
    private readonly string? _profile;
    private readonly List<string> _handlerFiles = new();
    private RouteFinding? _route;

    public Enrichment(DesiredFlow flow, FlowFacts facts, string root, Handler? seed, string? profile = null)
    {
        _flow = flow;
        _facts = facts;
        _root = root;
        _seed = seed;
        _profile = profile;
    }

    public void Run()
    {
        if (_seed is not null)
            Seed(_seed);

        ResolveDeployFile();
        ResolveSourceKind();
        string? kind = _flow.String("source.kind");
        bool known = kind is not null && FlowFields.SourceKinds.Contains(kind);

        ResolveRoute(known ? kind : null);
        ResolveFramework();
        if (known)
        {
            ResolveVerification(kind!);
            ResolveAuthentication(kind!);
        }
        ResolveRawBody();
        if (known)
        {
            ResolveEventTypes(kind!);
            ResolveTable(kind!);
        }
        ResolvePort();
        ResolveBaseUrl();
        ResolveOrdering(kind);
        ResolveIdempotency();
        ResolveEnvironment();
        if (known)
            ResolveQueue(kind!);
    }

    /// <summary>
    /// A deployment file apply cannot read is one advise cannot propose a whole file from: what it proposes is the file
    /// that is there with the flow's queue in it (F2.10-review), so the file has to read first.
    /// </summary>
    private void ResolveDeployFile()
    {
        // En fil advise ikke leste (en lenke, for stor, uleselig), er der likevel: et forslag om en ny fil ville erstattet den.
        if (_facts.DeployFile is { Unread: { } reason } unread)
        {
            Conflict("unsupported", unread.File, null, null, new[] { new FlowEvidence(unread.File, null, "a deployment file advise did not read") },
                $"{unread.File} {reason}", unread.WayOut ?? $"Fix {unread.File}, then run advise --intent again.");
            return;
        }

        if (_facts.DeployFile is not { Problem: { } problem } file)
            return;

        Conflict("unsupported", file.File, null, null, new[] { new FlowEvidence(file.File, null, "a deployment file apply cannot read as it is") },
            $"{file.File} is there, but apply cannot read it as it is: {problem}",
            $"Fix {file.File} first (queuey apply --dry-run says what is wrong), then run advise --intent again: advise proposes " +
            "the whole file, starting from the one that is there.");
    }

    private void Seed(Handler seed)
    {
        _flow.Set("source.kind", FlowValue.Of(seed.Kind, Provenance.Evidence, seed.KindEvidence));
        if (seed.Route is { } route)
        {
            _route = route;
            _flow.Set("destination.route", FlowValue.Of(route.Route, Provenance.Evidence, new[] { route.ToEvidence() }));
        }
        _handlerFiles.Add(seed.Route?.File ?? seed.File);
    }

    // ── kilden ───────────────────────────────────────────────────────────

    private void ResolveSourceKind()
    {
        if (_flow["source.kind"] is { } stated)
        {
            if (!FlowFields.SourceKinds.Contains(stated.AsString!))
            {
                Conflict("unsupported", "source.kind", stated.Value, null, Array.Empty<FlowEvidence>(),
                    $"advise designs flows from stripe, supabase and app sources, not {stated.AsString}.",
                    "Is the source one of stripe, supabase or app? Otherwise declare the queue in queuey.deploy.json by hand: " +
                    "queuey schema prints every field it takes.");
            }
            return;
        }

        if (_flow.String("destination.route") is { } route)
        {
            var files = Matching(route).Select(m => m.Route.File).Distinct().ToArray();
            var kinds = files.SelectMany(KindsOf)
                .GroupBy(k => k.Kind)
                .Select(g => (Kind: g.Key, Evidence: g.SelectMany(k => k.Evidence).Distinct().ToArray()))
                .ToArray();
            if (kinds.Length == 1)
            {
                _flow.Set("source.kind", FlowValue.Of(kinds[0].Kind, Provenance.Evidence, kinds[0].Evidence));
                return;
            }

            Conflict(kinds.Length == 0 ? "missing" : "ambiguous", "source.kind", null,
                kinds.Length == 0 ? null : Values(kinds.Select(k => k.Kind)),
                kinds.SelectMany(k => k.Evidence).ToArray(),
                kinds.Length == 0
                    ? $"The intent does not say who sends the events, and the handler at {route} does not show it."
                    : $"The handler at {route} shows both Stripe and Supabase.",
                "Who sends the events: stripe, supabase or app?");
            return;
        }

        // Uten kilde og rute sier intensjonen ikke hvilken flyt den gjelder. Repoet forteller hvordan, ikke om (C1), så
        // advise velger ikke en selv, heller ikke når det bare finnes én: den legger dem frem.
        IReadOnlyList<Handler> handlers = Handler.All(_facts);
        Conflict("missing", "source.kind", null,
            handlers.Count == 0 ? null : Values(handlers.Select(h => h.Summary())),
            handlers.SelectMany(h => h.Route is { } r ? new[] { r.ToEvidence() } : h.KindEvidence.Take(1)).ToArray(),
            handlers.Count == 0
                ? "The intent does not say who sends the events, and I found no Stripe or Supabase handler here."
                : "The intent does not say which flow it is. The repository shows " +
                  (handlers.Count == 1 ? "one: " : "several: ") + string.Join("; ", handlers.Select(h => h.Summary())) + ".",
            handlers.Count == 0
                ? "Who sends the events: stripe, supabase or app? And to which route?"
                : "Which flow is it? Write its source (and its route, when there are several) into the intent, marked stated.");
    }

    private IEnumerable<(string Kind, FlowEvidence[] Evidence)> KindsOf(string file)
    {
        FlowEvidence[] stripe = _facts.StripeVerifications.Where(v => v.File == file).Select(v => v.ToEvidence()).ToArray();
        if (stripe.Length > 0)
        {
            yield return ("stripe", stripe);
            yield break;
        }

        FlowEvidence[] supabase = _facts.SupabasePayloadReads.Where(r => r.File == file).Select(r => r.ToEvidence())
            .Concat(_facts.SupabaseTriggers.Where(t => t.TargetPath is not null
                && _facts.Routes.Any(r => r.File == file && FlowScan.SameRoute(r.Route, t.TargetPath))).Select(t => t.ToEvidence()))
            .ToArray();
        if (supabase.Length > 0)
            yield return ("supabase", supabase);
    }

    // ── ruten og rammeverket ─────────────────────────────────────────────

    /// <summary>The routes that serve <paramref name="stated"/>, directly or under a prefix the repository mounts.</summary>
    private IReadOnlyList<(RouteFinding Route, MountFinding? Mount)> Matching(string stated)
    {
        var found = new List<(RouteFinding, MountFinding?)>();
        foreach (RouteFinding route in _facts.Routes)
        {
            if (FlowScan.SameRoute(route.Route, stated))
            {
                found.Add((route, null));
                continue;
            }

            if (route.FromPath)
                continue;

            // Løst med vilje: et prefiks som er montert et sted i samme prosjekt, holder. En falsk konflikt koster mer enn et
            // treff på en rute som ligger under feil prefiks. Et annet prosjekt i et monorepo teller ikke.
            string project = _facts.ProjectOf(route.File);
            MountFinding? mount = _facts.Mounts.FirstOrDefault(m => _facts.ProjectOf(m.File) == project
                && FlowScan.SameRoute(FlowScan.Combine(m.Prefix, route.Route), stated));
            if (mount is not null)
                found.Add((route, mount));
        }
        return found;
    }

    private void ResolveRoute(string? kind)
    {
        if (_route is not null)
            return;

        if (_flow["destination.route"] is { } stated)
        {
            string route = stated.AsString!;
            var matches = Matching(route);
            if (matches.Count > 0)
            {
                (RouteFinding chosen, MountFinding? mount) = matches
                    .OrderByDescending(m => kind is not null && KindsOf(m.Route.File).Any(k => k.Kind == kind))
                    .First();
                _route = chosen;
                _handlerFiles.AddRange(matches.Select(m => m.Route.File).Distinct().OrderBy(f => f == chosen.File ? 0 : 1));

                var evidence = new List<FlowEvidence> { chosen.ToEvidence() };
                if (mount is not null)
                    evidence.Add(new FlowEvidence(mount.File, mount.Line, $"mounted under {mount.Prefix}"));
                _flow.Set("destination.route", stated.WithEvidence(evidence));
                return;
            }

            if (kind is "stripe" or "supabase" && _facts.Routes.Count > 0)
            {
                RouteFinding[] known = _facts.Routes
                    .OrderByDescending(r => KindsOf(r.File).Any(k => k.Kind == kind))
                    .Take(6).ToArray();
                Conflict("contradiction", "destination.route", stated.Value, Values(known.Select(r => r.Route)),
                    known.Select(r => r.ToEvidence()).ToArray(),
                    $"No handler in this repository takes POST {route}." + (_facts.Limits.Count > 0
                        ? $" The scan was limited ({string.Join("; ", _facts.Limits)}), so the handler may be in what it left out."
                        : ""),
                    "Which route should Queuey deliver to? If the receiver is in another repository, run queuey advise there.");
                return;
            }

            _flow.Assumptions.Add(kind == "app"
                ? $"The receiver is not in this repository; {route} is taken as the intent states it."
                : $"I could not read any routes in this repository, so {route} is taken as the intent states it.");
            return;
        }

        if (kind is null)
            return;

        if (kind == "app")
        {
            Conflict("missing", "destination.route", null, null, Array.Empty<FlowEvidence>(),
                "The flow has a source but no destination.",
                "Which route should Queuey deliver the app's events to, and where is it reachable over HTTP (destination.baseUrl)?");
            return;
        }

        Handler[] handlers = Handler.All(_facts).Where(h => h.Kind == kind).ToArray();
        Handler[] routed = handlers.Where(h => h.Route is not null).ToArray();

        if (routed.Length == 1 || (routed.Length > 1 && routed.All(h => FlowScan.SameRoute(h.Route!.Route, routed[0].Route!.Route))))
        {
            _route = routed[0].Route;
            _handlerFiles.AddRange(routed.Select(h => h.File).Distinct());
            _flow.Set("destination.route", FlowValue.Of(_route!.Route, Provenance.Evidence, new[] { _route.ToEvidence() }));
            return;
        }

        if (routed.Length > 1)
        {
            Conflict("ambiguous", "destination.route", null, Values(routed.Select(h => h.Route!.Route)),
                routed.Select(h => h.Route!.ToEvidence()).ToArray(),
                $"Several handlers here could receive {FlowAdvisor.Title(kind)}'s events.",
                "Which route should Queuey deliver to?");
            return;
        }

        if (handlers.Length > 0)
        {
            _handlerFiles.AddRange(handlers.Select(h => h.File).Distinct());
            Conflict("missing", "destination.route", null, null, handlers.Select(h => new FlowEvidence(h.File, h.Line, "the handler")).ToArray(),
                $"The {FlowAdvisor.Title(kind)} handler at {handlers[0].File}:{handlers[0].Line} has no route I can read.",
                "Which route reaches it?");
            return;
        }

        Conflict("missing", "destination.route", null, null, Array.Empty<FlowEvidence>(),
            $"I found no {FlowAdvisor.Title(kind)} handler here.",
            "Which route should Queuey deliver to? If the receiver is in another repository, run queuey advise there.");
    }

    private void ResolveFramework()
    {
        string? found = _route?.Framework ?? (_handlerFiles.Count > 0 ? _facts.FrameworkOf(_handlerFiles[0]) : null);
        if (found is null)
            return;

        FlowEvidence evidence = _route is { } r
            ? new FlowEvidence(r.File, r.Line, $"served by {found}")
            : new FlowEvidence(_handlerFiles[0], null, $"served by {found}");

        if (_flow["destination.framework"] is { } stated)
        {
            if (stated.AsString == found)
                _flow.Set("destination.framework", stated.WithEvidence(new[] { evidence }));
            else if (FlowFields.Frameworks.Contains(stated.AsString!))
                Conflict("contradiction", "destination.framework", stated.Value, JsonValue.Create(found), new[] { evidence },
                    $"The handler at {evidence} is served by {found}.",
                    $"Is the handler the one at {evidence}? If the receiver is another project's, name its route.");
            return;
        }

        _flow.Set("destination.framework", FlowValue.Of(found, Provenance.Evidence, new[] { evidence }));
    }

    // ── verifisering og autentisering ────────────────────────────────────

    private void ResolveVerification(string kind)
    {
        bool handlerKnown = _handlerFiles.Count > 0;
        var stripe = _facts.StripeVerifications.Where(v => _handlerFiles.Contains(v.File)).ToList();
        StripeVerificationFinding[] elsewhere = Array.Empty<StripeVerificationFinding>();
        if (stripe.Count == 0 && handlerKnown && kind == "stripe")
        {
            // En hjelper uten egne ruter (lib/stripe.ts) verifiserer for handleren. En fil med egne ruter er en annen handler.
            stripe.AddRange(_facts.StripeVerifications.Where(v => !_facts.Routes.Any(r => r.File == v.File)));
            elsewhere = _facts.StripeVerifications.Where(v => _facts.Routes.Any(r => r.File == v.File)).ToArray();
        }

        SecretCheckFinding[] secrets = _facts.SecretChecks.Where(c => _handlerFiles.Contains(c.File)).ToArray();
        Finding[] queuey = _facts.QueueySignatureChecks.Where(c => _handlerFiles.Contains(c.File)).ToArray();

        (string Value, FlowEvidence[] Evidence)? found =
            stripe.Count > 0 ? ("stripe-signature", stripe.Select(v => v.ToEvidence()).ToArray())
            : secrets.Length > 0 ? ("shared-secret", secrets.Select(s => s.ToEvidence()).ToArray())
            : queuey.Length > 0 ? ("queuey-signature", queuey.Select(q => q.ToEvidence()).ToArray())
            : null;

        string where = _route is { } r ? $"{r.Route} ({r.File}:{r.Line})" : handlerKnown ? _handlerFiles[0] : "the route";

        if (_flow["requirements.verification"] is { } stated)
        {
            string value = stated.AsString!;
            if (found is { } f && f.Value != value)
            {
                Conflict("contradiction", "requirements.verification", stated.Value, JsonValue.Create(f.Value), f.Evidence,
                    $"The handler at {where} checks {Describe(f.Value)}, not {Describe(value)}.",
                    $"Which should the handler check: {Describe(f.Value)}, as it does, or {Describe(value)}?");
            }
            else if (found is null && handlerKnown && value == "stripe-signature")
            {
                Conflict("contradiction", "requirements.verification", stated.Value, JsonValue.Create("none"),
                    elsewhere.Select(v => v.ToEvidence()).ToArray(),
                    $"The handler at {where} does not verify Stripe's signature" +
                    (elsewhere.Length > 0 ? $"; other handlers here do ({string.Join(", ", elsewhere.Select(v => $"{v.File}:{v.Line}"))})." : "."),
                    "Should Queuey deliver to a handler that verifies Stripe's signature instead? Or add constructEvent to this one first.");
            }
            else if (found is { } same)
            {
                _flow.Set("requirements.verification", stated.WithEvidence(same.Evidence));
            }
            return;
        }

        if (found is { } evidence && (kind != "stripe" || evidence.Value == "stripe-signature"))
        {
            _flow.Set("requirements.verification", FlowValue.Of(evidence.Value, Provenance.Evidence, evidence.Evidence));
            return;
        }

        if (!handlerKnown)
            return;

        if (kind == "stripe" && found is { } other)
        {
            Conflict("contradiction", "requirements.verification", null, JsonValue.Create(other.Value), other.Evidence,
                $"The intent names Stripe as the source, but the handler at {where} checks {Describe(other.Value)}, not Stripe's " +
                "signature.",
                "Is this the handler for Stripe's events? A Stripe handler verifies Stripe's signature with constructEvent, which " +
                "keeps working when Queuey delivers.");
            return;
        }

        if (kind == "stripe")
        {
            // Koordinatorens eksempel (F2.10): intensjonen sier Stripe, men koden verifiserer ikke signaturen.
            Conflict("contradiction", "requirements.verification", null, JsonValue.Create("none"),
                elsewhere.Select(v => v.ToEvidence()).ToArray(),
                $"The intent names Stripe as the source, but the handler at {where} does not verify Stripe's signature" +
                (elsewhere.Length > 0 ? $"; other handlers here do ({string.Join(", ", elsewhere.Select(v => $"{v.File}:{v.Line}"))})." : "."),
                "Should this handler verify Stripe's signature? Add constructEvent to it, and Queuey signs each delivery again in " +
                "Stripe's format so it works unchanged. Or is another handler the one for Stripe's events?");
            return;
        }

        _flow.Set("requirements.verification", FlowValue.Of("none", Provenance.Assumed));
        _flow.Assumptions.Add($"I found no check of what reaches {where}: the handler trusts every request that gets there.");
    }

    private static string Describe(string verification) => verification switch
    {
        "stripe-signature" => "Stripe's signature",
        "queuey-signature" => "Queuey's signature",
        "shared-secret" => "a secret in a header",
        _ => "nothing",
    };

    private void ResolveAuthentication(string kind)
    {
        string[] allowed = kind switch
        {
            "stripe" => new[] { "stripe-signature" },
            "supabase" => new[] { "api-key", "none" },
            _ => new[] { "api-key", "queuey-signature", "none" },
        };

        FlowEvidence[] stripeEvidence = _flow["requirements.verification"] is { AsString: "stripe-signature" } v
            ? v.Evidence.ToArray()
            : Array.Empty<FlowEvidence>();

        if (_flow["source.authentication"] is { } stated)
        {
            if (!allowed.Contains(stated.AsString!))
            {
                Conflict("unsupported", "source.authentication", stated.Value, null, Array.Empty<FlowEvidence>(),
                    kind switch
                    {
                        "stripe" => "Stripe proves its webhooks with its signature, Stripe-Signature, and nothing else.",
                        "supabase" => "A Supabase Database Webhook cannot sign what it sends; it can send a key in a header.",
                        _ => "The app publishes with an API key or Queuey's signature.",
                    },
                    $"Use {string.Join(" or ", allowed)}.");
            }
            else if (stripeEvidence.Length > 0)
            {
                _flow.Set("source.authentication", stated.WithEvidence(stripeEvidence));
            }
            return;
        }

        if (kind == "stripe")
            _flow.Set("source.authentication", stripeEvidence.Length > 0
                ? FlowValue.Of("stripe-signature", Provenance.Evidence, stripeEvidence)
                : FlowValue.Of("stripe-signature", Provenance.Assumed));
        else
            _flow.Set("source.authentication", FlowValue.Of("api-key", Provenance.Assumed));
    }

    // ── kroppen, typene og tabellen ──────────────────────────────────────

    private void ResolveRawBody()
    {
        if (_flow["requirements.verification"] is not { AsString: "stripe-signature" } verification)
        {
            return;
        }

        string[] files = verification.Evidence.Select(e => e.File).Concat(_handlerFiles).Distinct().ToArray();
        FlowEvidence[] reads = _facts.RawBodyReads.Where(r => files.Contains(r.File)).Select(r => r.ToEvidence()).ToArray();
        FlowEvidence[] evidence = reads.Length > 0
            ? reads
            : verification.Evidence.Select(e => e with { What = "constructEvent needs the exact bytes Stripe signed" }).ToArray();

        if (_flow["destination.expectsRawBody"] is { } stated)
        {
            if (stated.AsBool == false)
                Conflict("contradiction", "destination.expectsRawBody", stated.Value, JsonValue.Create(true), evidence,
                    "The handler verifies Stripe's signature, which covers the exact bytes Stripe sent.",
                    "Does the handler verify Stripe's signature? Then it needs the raw body, and Queuey delivers it unchanged.");
            else
                _flow.Set("destination.expectsRawBody", stated.WithEvidence(evidence));
            return;
        }

        _flow.Set("destination.expectsRawBody", FlowValue.Of(true, Provenance.Evidence, evidence));
    }

    private void ResolveEventTypes(string kind)
    {
        if (_flow["source.eventTypes"] is not null || _handlerFiles.Count == 0)
            return;

        string[] files = _handlerFiles.Concat(_flow["requirements.verification"]?.Evidence.Select(e => e.File) ?? Array.Empty<string>())
            .Distinct().ToArray();
        bool rows = kind == "supabase";
        EventTypeFinding[] found = _facts.EventTypes
            .Where(t => files.Contains(t.File) && (rows ? t.Type is "INSERT" or "UPDATE" or "DELETE" : t.Type.Contains('.')))
            .ToArray();

        var evidence = found.GroupBy(t => t.Type).Select(g => new FlowEvidence(g.First().File, g.First().Line, $"handles {g.Key}")).ToList();
        var types = found.Select(t => t.Type).Distinct().ToList();

        if (rows)
        {
            foreach (SupabaseTriggerFinding trigger in TriggersForFlow())
            {
                evidence.Add(trigger.ToEvidence());
                types.AddRange(trigger.Events.Where(e => !types.Contains(e)));
            }
        }

        if (types.Count > 0)
            _flow.Set("source.eventTypes", FlowValue.Of(types, Provenance.Evidence, evidence));
    }

    /// <summary>The Supabase webhooks that post to this flow's route, or the only one there is.</summary>
    private IReadOnlyList<SupabaseTriggerFinding> TriggersForFlow()
    {
        string? route = _flow.String("destination.route");
        SupabaseTriggerFinding[] targeting = route is null
            ? Array.Empty<SupabaseTriggerFinding>()
            : _facts.SupabaseTriggers.Where(t => t.TargetPath is not null && FlowScan.SameRoute(t.TargetPath, route)).ToArray();
        if (targeting.Length > 0)
            return targeting;

        string? table = _flow.String("source.table");
        if (table is not null)
            return _facts.SupabaseTriggers.Where(t => SameTable(t.Table, table)).ToArray();

        return _facts.SupabaseTriggers.Count == 1 ? _facts.SupabaseTriggers : Array.Empty<SupabaseTriggerFinding>();
    }

    private static bool SameTable(string? a, string b)
        => a is not null && (a.Equals(b, StringComparison.OrdinalIgnoreCase)
                             || a.Split('.').Last().Equals(b.Split('.').Last(), StringComparison.OrdinalIgnoreCase));

    private void ResolveTable(string kind)
    {
        if (kind != "supabase")
            return;

        IReadOnlyList<SupabaseTriggerFinding> triggers = TriggersForFlow();
        if (_flow["source.table"] is { } stated)
        {
            if (triggers.Count > 0)
                _flow.Set("source.table", stated.WithEvidence(triggers.Select(t => t.ToEvidence())));
            return;
        }

        string[] tables = triggers.Select(t => t.Table).Where(t => t is not null).Select(t => t!).Distinct().ToArray();
        if (tables.Length == 1)
            _flow.Set("source.table", FlowValue.Of(tables[0], Provenance.Evidence, triggers.Select(t => t.ToEvidence())));
    }

    // ── porten og adressen ───────────────────────────────────────────────

    private void ResolvePort()
    {
        string? framework = _flow.String("destination.framework");
        if (framework is null || _handlerFiles.Count == 0)
            return;

        PortFinding[] ports = framework == "supabase-edge"
            ? _facts.Ports.Where(p => p.File.EndsWith("supabase/config.toml", StringComparison.Ordinal)).ToArray()
            : _facts.Ports.Where(p => _facts.ProjectOf(p.File) == _facts.ProjectOf(_handlerFiles[0])).ToArray();
        PortFinding? found = ports.FirstOrDefault(p => p.HardCoded) ?? ports.FirstOrDefault();

        if (_flow["destination.port"] is { } stated)
        {
            if (found is { HardCoded: true } && stated.AsInt != found.Port)
                Conflict("contradiction", "destination.port", stated.Value, JsonValue.Create(found.Port), new[] { found.ToEvidence() },
                    $"The receiver listens on port {found.Port}, and nothing configures it otherwise.",
                    $"Which port does the receiver run on while you develop: {stated.AsInt} or {found.Port}?");
            else if (found is not null && stated.AsInt == found.Port)
                _flow.Set("destination.port", stated.WithEvidence(new[] { found.ToEvidence() }));
            return;
        }

        if (found is not null)
        {
            _flow.Set("destination.port", FlowValue.Of(found.Port, Provenance.Evidence, new[] { found.ToEvidence() }));
            return;
        }

        int? framed = framework switch
        {
            "supabase-edge" => 54321,
            "nextjs" => 3000,
            "fastapi" => 8000,
            "flask" => 5000,
            _ => null,
        };
        if (framed is { } port)
            _flow.Set("destination.port", FlowValue.Of(port, Provenance.Assumed));
    }

    private void ResolveBaseUrl()
    {
        if (_flow["destination.baseUrl"] is { } stated)
        {
            if (DeploymentDestinations.LocalTargetRefusal(stated.AsString) is { } refusal)
                Conflict("unsupported", "destination.baseUrl", stated.Value, null, Array.Empty<FlowEvidence>(),
                    $"Queuey cannot deliver to this base URL: {refusal}",
                    "Where is the receiver reachable over HTTP? While you develop, queuey listen forwards to this machine, " +
                    "and the base URL only gives the path; leave it out if there is none yet.");
            return;
        }

        string? existing = _facts.DeployFile?.BaseUrl;
        if (existing is not null && existing.IndexOf("${", StringComparison.Ordinal) < 0
            && Uri.TryCreate(existing, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"
            && DeploymentDestinations.LocalTargetRefusal(existing) is null)
        {
            _flow.Set("destination.baseUrl", FlowValue.Of(existing.TrimEnd('/'), Provenance.Evidence,
                new[] { new FlowEvidence(_facts.DeployFile!.File, null, "the workspace's base URL in the deployment file") }));
        }
    }

    // ── kravene ──────────────────────────────────────────────────────────

    private void ResolveOrdering(string? kind)
    {
        if (_flow["requirements.ordering"] is not { } stated)
        {
            _flow.Set("requirements.ordering", FlowValue.Of("none", Provenance.Assumed));
            return;
        }

        if (stated.AsString != "per-key")
            return;

        if (_flow["requirements.orderingKey"] is not { } key)
        {
            Conflict("missing", "requirements.orderingKey", null, null, Array.Empty<FlowEvidence>(),
                "Per-key ordering needs a key to order by.",
                "Which field or header keys the order, such as customer_id or header:X-Customer-Id? Queuey reads top-level " +
                "body fields.");
            return;
        }

        string value = key.AsString!;
        bool header = value.StartsWith("header:", StringComparison.OrdinalIgnoreCase);
        string name = header ? value["header:".Length..].Trim() : value;
        if (name.Length == 0 || (!header && (name.Contains('.') || name.Contains('[') || name.Contains('/'))))
        {
            Conflict("unsupported", "requirements.orderingKey", key.Value, null, Array.Empty<FlowEvidence>(),
                "Queuey's ingress reads the key from a header or a top-level field of the body, and this one is nested." +
                (kind == "stripe" ? " Stripe puts the customer under data.object, so per-customer order is not available for " +
                                    "Stripe's events today." : ""),
                kind == "stripe"
                    ? "Can the handler do without this order? Stripe does not send its events in order either; reading the " +
                      "object's current state from Stripe when an event arrives makes the order matter less."
                    : "Which top-level field or header keys the order?");
        }
    }

    private void ResolveIdempotency()
    {
        string[] files = _handlerFiles.Concat(_flow["requirements.verification"]?.Evidence.Select(e => e.File) ?? Array.Empty<string>())
            .Distinct().ToArray();
        FlowEvidence[] dedup = _facts.Deduplication
            .Where(d => files.Contains(d.File) || d.File.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.ToEvidence()).Take(3).ToArray();

        if (_flow["requirements.idempotent"] is { } stated)
        {
            if (stated.AsBool == true && dedup.Length > 0)
                _flow.Set("requirements.idempotent", stated.WithEvidence(dedup));
            return;
        }

        if (files.Length == 0)
            return;

        _flow.Set("requirements.idempotent", dedup.Length > 0
            ? FlowValue.Of(true, Provenance.Evidence, dedup)
            : FlowValue.Of(false, Provenance.Assumed));
    }

    // ── miljøet og køen ──────────────────────────────────────────────────

    private void ResolveEnvironment()
    {
        ExistingDeployFile? file = _facts.DeployFile;
        string? existing = file is null ? null : FileEnvironments.Fixed(file);
        FlowEvidence[] evidence = existing is null
            ? Array.Empty<FlowEvidence>()
            : new[] { new FlowEvidence(file!.File, null, $"the deployment file's workspace is {existing}") };

        // Et miljø fila gir på en måte advise ikke kan følge, eller som apply nekter, er en konflikt før noe annet (review av
        // #60): ${A}x og ${A}${B} er ingen enkelt variabel, og en ugyldig profilverdi faller ikke tilbake på standardverdien.
        if (file is not null && FileEnvironments.Unreadable(file))
        {
            Conflict("unsupported", "environment", null, null,
                new[] { new FlowEvidence(file.File, null, "the deployment file's workspace.environment") },
                $"workspace.environment in {file.File} reads variables in a form advise cannot follow: it reads a fixed environment " +
                "or exactly one ${VAR}, so it cannot tell which environment the workspace is.",
                $"Make workspace.environment one variable, such as ${{{DeploymentTemplate.EnvironmentVariable}}}, or a fixed " +
                "environment, then run advise --intent again.");
            return;
        }
        // advise --profile velger profilen selv (review av #60): for profiler som deler et miljø, som eu og us i prod, kan
        // ikke et oppgitt miljø skille dem. Profilen må finnes i fila; bare en ny fil får den laget.
        if (_profile is not null && file is not null)
        {
            ChosenProfile(file, _profile, existing, evidence);
            return;
        }

        // Re-review av #60: --profile i en ny fil ga et dev-design mot profilens tilkobling. Med --profile prod ble testmodus
        // credentials set --profile prod, som kunne byttet ut prod sin ekte signeringsnøkkel med Stripe CLI-ens testnøkkel.
        // Navnet sier ikke miljøet, så profilen i en ny fil krever et oppgitt miljø, aldri et antatt dev.
        if (_profile is not null && _flow["environment"] is null)
        {
            Conflict("missing", "environment", null, null, Array.Empty<FlowEvidence>(),
                $"--profile {_profile} names the profile a new deployment file gets, and the intent states no environment, so advise " +
                "cannot tell which environment the profile is for: its name does not say.",
                $"State the environment profile {_profile} is for in the intent as environment ({string.Join(", ", DeploymentWorkspace.EnvironmentValues)}), " +
                "then run advise --intent again.");
            return;
        }

        if (_flow["environment"] is { } stated)
        {
            if (existing is not null && existing != stated.AsString)
            {
                Conflict("contradiction", "environment", stated.Value, JsonValue.Create(existing), evidence,
                    $"{file!.File} sets up a {existing} workspace.",
                    $"Is this flow for {existing}, as the deployment file is, or for {stated.AsString}, in a deployment file of its own?");
                return;
            }
            if (existing is not null)
                _flow.Set("environment", stated.WithEvidence(evidence));
            if (file is { Profiles.Count: > 0 })
                StatedProfile(file, stated.AsString!);
            else if (file is not null)
                RefusedIn(file, null);   // uten profiler brukes standardverdien når variabelen ikke er satt
            return;
        }

        // Med profiler og et miljø ingen har oppgitt, lager advise aldri en ny profil (re-review av #59, Kenneths prinsipp om å
        // stoppe ved tvetydighet). Den ene profilen fila har, brukes; flere er et spørsmål, med miljøet hver av dem gir, så
        // svaret er et miljø environment tar (før tag: spørsmålet ba om et profilnavn, som environment avviser).
        if (file is { Profiles.Count: > 1 })
        {
            Conflict("ambiguous", "environment", null, FileEnvironments.Found(file), ProfileEvidence(file),
                $"{file.File} has the profiles {FileEnvironments.Describe(file)}, and the intent states no environment, so advise " +
                "cannot tell which one the flow belongs in.",
                ProfileQuestion(file));
            return;
        }

        if (file is not null && RefusedIn(file, file.Profiles.Count == 1 ? file.Profiles[0] : null))
            return;

        if (file is { Profiles.Count: 1 } && FileEnvironments.GivenBy(file, file.Profiles[0]) is { } given)
        {
            _flow.Set("environment", FlowValue.Of(given, Provenance.Evidence,
                new[] { new FlowEvidence(file.File, null, FileEnvironments.Source(file, file.Profiles[0])) }));
            return;
        }

        // Uten profiler gir fila et fast miljø, eller standardverdien i ${VAR:-dev}, som plan og apply bruker når variabelen
        // ikke er satt.
        if (file is not null && FileEnvironments.WithoutProfile(file) is { } fromFile)
        {
            _flow.Set("environment", FlowValue.Of(fromFile, Provenance.Evidence,
                new[] { new FlowEvidence(file.File, null, FileEnvironments.Source(file, null)) }));
            return;
        }

        // En ny fil er for dev, og advise skriver det inn. En fil som finnes uten miljø, gjelder et umerket workspace, som Queuey
        // regner som prod, og advise skriver ikke et antatt miljø inn i den; det samme gjelder en fil som tar miljøet fra en
        // variabel ingen profil eller standardverdi gir. Før tag (oppfølging av #58, 2026-10-06): advise antok dev også her, og
        // designet ble en blanding av localForward og et ekte Stripe-endepunkt i et workspace Queuey regner som prod.
        _flow.Set("environment", FlowValue.Of(file is null ? "dev" : "prod", Provenance.Assumed));
    }

    /// <summary>
    /// The environment for the profile <c>advise --profile</c> names: the one the profile gives, which a stated environment
    /// must not contradict, or the stated one when the profile gives none, unless that is dev, since Queuey treats the
    /// workspace of a profile that gives none as prod. A profile the file does not have is a conflict.
    /// </summary>
    private void ChosenProfile(ExistingDeployFile file, string profile, string? existing, FlowEvidence[] evidence)
    {
        if (!file.Profiles.Contains(profile, StringComparer.Ordinal))
        {
            Conflict("missing", "environment", null, FileEnvironments.Found(file), ProfileEvidence(file),
                file.Profiles.Count == 0
                    ? $"--profile {profile} names a profile, and {file.File} has none. advise adds no profile beside a file's own."
                    : $"--profile {profile} names a profile {file.File} does not have: it has {FileEnvironments.Describe(file)}.",
                file.Profiles.Count == 0
                    ? "Leave --profile out: advise designs with the file's fixed values."
                    : $"Name one of its profiles with --profile ({string.Join(", ", file.Profiles)}), or leave --profile out.");
            return;
        }

        if (RefusedIn(file, profile))
            return;

        string? given = FileEnvironments.GivenBy(file, profile);
        if (_flow["environment"] is { } stated)
        {
            if (given is not null && given != stated.AsString)
                Conflict("contradiction", "environment", stated.Value, JsonValue.Create(given),
                    new[] { new FlowEvidence(file.File, null, FileEnvironments.Source(file, profile)) },
                    $"The profile {profile} in {file.File} gives {given}, and the intent states {stated.AsString}.",
                    $"Is this flow for {given}, as the profile is? State {given} in the intent, or name another profile with --profile.");
            else if (given is null && stated.AsString == "dev")
                NoEnvironmentForDev(file, profile);
            else if (existing is not null)
                _flow.Set("environment", stated.WithEvidence(evidence));
            return;
        }

        _flow.Set("environment", given is not null
            ? FlowValue.Of(given, Provenance.Evidence, new[] { new FlowEvidence(file.File, null, FileEnvironments.Source(file, profile)) })
            : FlowValue.Of("prod", Provenance.Assumed));
    }

    /// <summary>
    /// A conflict, and true, when the value the file gives the environment with <paramref name="profile"/>, or without a
    /// profile, is one apply refuses (review of #60): never a fallback to the default. Only the value in use counts.
    /// </summary>
    private bool RefusedIn(ExistingDeployFile file, string? profile)
    {
        if (FileEnvironments.Refused(file, profile) is not { } refused)
            return false;

        Conflict("unsupported", "environment", null, null, new[] { new FlowEvidence(file.File, null, refused.Where) },
            $"{refused.Where} in {file.File} is {Shown(refused.Value)}, which apply refuses: workspace.environment must be one of " +
            $"{string.Join(", ", DeploymentWorkspace.EnvironmentValues)}.",
            $"Set it to one of {string.Join(", ", DeploymentWorkspace.EnvironmentValues)} in {file.File}, then run advise --intent again.");
        return true;
    }

    /// <summary>
    /// A conflict for a stated dev and a profile that gives no environment: Queuey treats its workspace as prod, so advise
    /// neither takes it for dev nor writes dev into it (re-review of #60).
    /// </summary>
    // Re-review av #60, B1: fila hadde én profil, main, uten miljø (prod-tenanten), og intensjonen sa dev. advise gjenbrukte
    // main, skrev QUEUEY_WORKSPACE_ENVIRONMENT=dev og localForward inn i den, og foreslo testmodus med credentials set
    // --profile main mot prod-tilkoblingen. Og prod-profilen ble skrevet om til dev, så hver CI-deploy med den ble nektet.
    // Et miljø som ikke er dev, kan fortsatt gjenbruke profilen: det er den forsiktige veien.
    private void NoEnvironmentForDev(ExistingDeployFile file, string profile)
    {
        string variable = FileEnvironments.Variable(file)?.Name ?? DeploymentTemplate.EnvironmentVariable;
        Conflict("contradiction", "environment", JsonValue.Create("dev"), null, ProfileEvidence(file),
            $"The profile {profile} in {file.File} gives no environment, so Queuey treats its workspace as prod, and the intent " +
            "states dev.",
            $"If {profile} is the dev profile, give it {variable}=dev in {file.File}" +
            (FileEnvironments.Variable(file) is null ? $", with workspace.environment ${{{variable}}}" : "") +
            ". Or name a profile that gives dev with --profile. Then run advise --intent again.");
    }

    /// <summary>A value from the file as a conflict shows it: quoted when it is plain, else in words.</summary>
    private static string Shown(string value)
        => string.IsNullOrWhiteSpace(value) ? "blank"
           : value.Length <= 32 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-') ? $"\"{value}\""
           : "a value that is not shown";

    /// <summary>
    /// The profile a stated environment goes into: the one profile that gives it, or the file's only profile when that gives
    /// none, which then gets the stated one unless it is dev, since Queuey treats the workspace of a profile that gives none
    /// as prod. Anything else is a conflict, never a new profile beside the file's own.
    /// </summary>
    // Før tag (etter #59): med prod oppgitt og profilene local og production laget advise profiles.prod ved siden av production,
    // med --profile prod. En profil velges etter miljøet den gir, aldri etter navnet.
    private void StatedProfile(ExistingDeployFile file, string environment)
    {
        string[] giving = file.Profiles.Where(p => FileEnvironments.GivenBy(file, p) == environment).ToArray();
        if (giving.Length == 1)
            return;
        if (giving.Length == 0 && file.Profiles.Count == 1 && FileEnvironments.GivenBy(file, file.Profiles[0]) is null)
        {
            if (!RefusedIn(file, file.Profiles[0]) && environment == "dev")
                NoEnvironmentForDev(file, file.Profiles[0]);
            return;
        }

        Conflict(giving.Length > 1 ? "ambiguous" : "contradiction", "environment", JsonValue.Create(environment), FileEnvironments.Found(file),
            ProfileEvidence(file),
            giving.Length > 1
                ? $"{file.File} has {giving.Length} profiles that give {environment} ({string.Join(", ", giving)}), so advise cannot tell " +
                  "which one the flow belongs in."
                : $"No profile in {file.File} gives {environment}: {FileEnvironments.Describe(file)}. advise adds a flow to a profile " +
                  "by the environment it gives, never by its name.",
            ProfileQuestion(file));
    }

    private static FlowEvidence[] ProfileEvidence(ExistingDeployFile file)
        => new[] { new FlowEvidence(file.File, null, $"the deployment file's profiles: {FileEnvironments.Describe(file)}") };

    /// <summary>
    /// What to do about profiles advise cannot choose between: state the environment one of them gives, or first give each
    /// profile an environment of its own.
    /// </summary>
    private static string ProfileQuestion(ExistingDeployFile file)
    {
        string names = string.Join(", ", file.Profiles);
        string?[] given = file.Profiles.Select(p => FileEnvironments.GivenBy(file, p)).ToArray();
        if (given.All(g => g is not null) && given.Distinct().Count() == given.Length)
            return "Which environment is this flow for? State it in the intent as environment: " +
                   string.Join(", ", file.Profiles.Select((p, i) => $"{given[i]} for {p}")) +
                   $". advise adds the flow to the profile that gives it. Or name the profile with advise --profile ({names}).";

        // Profiler som deler et miljø (review av #60), som eu og us i prod, skilles ikke av et oppgitt miljø: da er --profile svaret.
        if (FileEnvironments.Fixed(file) is { } fixedEnvironment)
            return $"Which profile is this flow for? Every profile gives {fixedEnvironment}, so the environment cannot tell them apart: " +
                   $"name the profile with advise --profile ({names}).";

        return FileEnvironments.Variable(file) is { } variable
            ? $"Which profile is this flow for? Name it with advise --profile ({names}). To choose by environment instead, give each " +
              $"profile its own environment in {variable.Name}, the variable workspace.environment comes from, and state the one this " +
              "flow is for in the intent as environment."
            : $"Which profile is this flow for? Name it with advise --profile ({names}). To choose by environment instead, take " +
              $"workspace.environment from ${{{DeploymentTemplate.EnvironmentVariable}}} in {file.File}, give each profile its value " +
              $"for {DeploymentTemplate.EnvironmentVariable}, and state the one this flow is for in the intent as environment.";
    }

    private void ResolveQueue(string kind)
    {
        if (_flow["queue"] is not null)
            return;

        if (_facts.DeployFile?.Queues is { Count: > 0 } queues)
        {
            string[] declared = kind switch
            {
                "stripe" => queues.Where(q => string.Equals(q.Value, "stripe", StringComparison.OrdinalIgnoreCase)).Select(q => q.Key).ToArray(),
                "supabase" when _flow.String("source.table") is { } table => queues.Keys.Where(k => k == TableQueue(table)).ToArray(),
                _ => Array.Empty<string>(),
            };
            if (declared.Length == 1)
            {
                _flow.Set("queue", FlowValue.Of(declared[0], Provenance.Evidence,
                    new[] { new FlowEvidence(_facts.DeployFile.File, null, $"the deployment file declares {declared[0]}") }));
                return;
            }
        }

        string name = kind switch
        {
            "stripe" => "stripe",
            "supabase" => _flow.String("source.table") is { } table && QueueyName.IsValid(TableQueue(table)) ? TableQueue(table) : "supabase",
            _ => ScaffoldPlan.DefaultQueueName(_root),
        };
        _flow.Set("queue", FlowValue.Of(name, Provenance.Assumed));
    }

    private static string TableQueue(string table) => table.Split('.').Last().Trim('"').ToLowerInvariant().Replace('_', '-');

    // ── hjelpere ─────────────────────────────────────────────────────────

    private void Conflict(string kind, string field, JsonNode? stated, JsonNode? found, IReadOnlyList<FlowEvidence> evidence,
        string message, string question)
    {
        if (_flow.Conflicts.Any(c => c.Field == field))
            return;
        _flow.Conflicts.Add(new FlowConflict(kind, field, stated?.DeepClone(), found, evidence, message, question));
    }

    private static JsonArray Values(IEnumerable<string> values)
        => new(values.Distinct().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
