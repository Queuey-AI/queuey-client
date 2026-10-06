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
    public static FlowAdvice Advise(DesiredFlow intent, FlowFacts facts, string root, string? queue = null, Advice? sending = null)
    {
        if (intent is null) throw new ArgumentNullException(nameof(intent));
        if (facts is null) throw new ArgumentNullException(nameof(facts));

        DesiredFlow flow = intent.Intent();
        if (queue is not null)
            flow.Set("queue", FlowValue.Of(queue, Provenance.Stated));

        new Enrichment(flow, facts, root, seed: null).Run();
        return new FlowAdvice
        {
            Flow = flow,
            Existing = facts.Queuey.Select(f => f.ToEvidence()).ToArray(),
            Design = flow.Conflicts.Count == 0 ? FlowDesigner.Design(flow, facts, sending) : null,
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
    private readonly List<string> _handlerFiles = new();
    private RouteFinding? _route;

    public Enrichment(DesiredFlow flow, FlowFacts facts, string root, Handler? seed)
    {
        _flow = flow;
        _facts = facts;
        _root = root;
        _seed = seed;
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
        string? existing = _facts.DeployFile?.Environment is { } env && env.IndexOf("${", StringComparison.Ordinal) < 0
            ? env.Trim().ToLowerInvariant()
            : null;
        FlowEvidence[] evidence = existing is null
            ? Array.Empty<FlowEvidence>()
            : new[] { new FlowEvidence(_facts.DeployFile!.File, null, $"the deployment file's workspace is {existing}") };

        if (_flow["environment"] is { } stated)
        {
            if (existing is not null && existing != stated.AsString)
                Conflict("contradiction", "environment", stated.Value, JsonValue.Create(existing), evidence,
                    $"{_facts.DeployFile!.File} sets up a {existing} workspace.",
                    $"Is this flow for {existing}, as the deployment file is, or for {stated.AsString}, in a deployment file of its own?");
            else if (existing is not null)
                _flow.Set("environment", stated.WithEvidence(evidence));
            return;
        }

        _flow.Set("environment", existing is not null
            ? FlowValue.Of(existing, Provenance.Evidence, evidence)
            : FlowValue.Of(AssumedEnvironment(), Provenance.Assumed));
    }

    /// <summary>
    /// The environment advise assumes when neither the intent nor the file names one. A new file is for dev, which advise
    /// writes into it. A file that is there without one applies to an unmarked workspace, which Queuey counts as prod, and
    /// advise writes no assumed environment into it; so does a file that takes it from a variable without profiles, which
    /// only the environment where apply runs sets. A file that takes it from a variable with profiles gets it from the
    /// profile advise writes for dev (the design reads one the file already has).
    /// </summary>
    // Før tag (oppfølging av #58, 2026-10-06): advise antok dev også for en fil som fantes uten miljø. Designet ble da en
    // blanding: localForward og queuey listen, mens Stripe-stegene pekte et ekte endepunkt mot køen, i et workspace Queuey
    // regner som prod. Et umerket workspace er prod (Kenneths beslutning), og nå følger hele designet det.
    private string AssumedEnvironment()
        => _facts.DeployFile is not { } file ? "dev"
           : file.Environment is { } env && env.IndexOf("${", StringComparison.Ordinal) >= 0 && file.Profiles.Count > 0 ? "dev"
           : "prod";

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
