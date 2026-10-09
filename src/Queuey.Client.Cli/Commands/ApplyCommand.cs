using System;
using System.Collections.Generic;
using System.Globalization;
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
    // --repo, --repo-path, --commit og --no-git sier hvor fila ligger, og --adopt hva applyen tar tilbake (Queuey F2.4).
    // --plan plan_… applyer en plan Queuey lagrer (F3.11), og tar en verdi: planen å skrive. Visningen av hva apply ville
    // gjort, er fortsatt verbet `queuey plan` (2026-09-24), og et --plan uten verdi avvises. --wait venter på en persons
    // godkjenning, høyst --timeout sekunder.
    internal static readonly CommandOptions Options = new(
        "apply",
        flags: new[] { "dry-run", "check", "continue-on-error", "json", "no-git", "wait" },
        values: new[] { "file", "repo", "repo-path", "commit", "adopt", "profile", "plan", "timeout" },
        hints: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan"] = "To see what apply would change, run `queuey plan`; `queuey apply --plan plan_…` applies a plan Queuey stores.",
        });

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        // Profilen (F2.7) velger begge halvdelene: verdiene i fila, her, og tilkoblingen hos brukeren, når apply kobler til.
        string? profile = CliHost.Profile(map);
        if (!TryReadDeploymentFile(map, out string path, out DeploymentFile file, out failure, profile)) return failure;
        DeploymentFile target = profile is null ? file : ForProfile(file, profile, path);
        string? fileTenant = profile is null ? file.ResolveTenant() : target.Tenant;
        bool dryRun = map.Has("dry-run");
        if (StoredPlanOptions(map, out string? planId, out TimeSpan wait) is { } refused)
            return refused;

        if (dryRun)
        {
            // Samme regel for tenant som apply, så en dry-run feiler der applyen ville feilet.
            DeploymentTenant.EnsureNoConflict(map, CliHost.Env, fileTenant, path);

            // Network-free: ParseNamed has expanded and resolved the file, so names, policy and an unset ${VAR}
            // already failed there, in the dry run, rather than during the deploy it was meant to protect.

            // En leverings-URL på maskinen eller et privat nett (Queuey F2.3), med de utvidede verdiene: ParseNamed har alt utvidet
            // fila. Blindtesten 2026-10-09 (funn 5): en dry-run kobler ikke til, og vet ikke hva Queuey tillater. En lokal stack
            // leverer til localhost, så det er en advarsel her; plan og apply spør Queuey, og serverens svar gjelder.
            try
            {
                DeploymentDestinations.EnsureReachable(profile is null ? file.Expand() : target, DryRunApiBase(map, profile));
            }
            catch (QueueyConfigurationException unreachable)
            {
                Console.Error.WriteLine($"Warning: {unreachable.Message}");
                Console.Error.WriteLine("  The dry run does not connect, so Queuey decides when you plan or apply: Queuey's hosts refuse " +
                                        "such a URL, and a Queuey on this machine may reach it. " + unreachable.SuggestedAction);
            }

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
        ResolvedConfig config = CliHost.ResolveForDeployment(map, fileTenant, path);

        // Gullflyten 2026-10-09: et dev-workspace kunne ikke lages fra CLI-en. Navngir ingenting et workspace, og fila sier dev
        // eller test, lager apply det først og applyer til det, men aldri i CI eller for en levering med credential
        // (WorkspaceCreation). Et navngitt workspace lages aldri på nytt.
        CreatedWorkspace? created = null;
        if (string.IsNullOrWhiteSpace(config.TenantPublicId) && !map.Has("check") && planId is null)
        {
            DeploymentFile expanded = profile is null ? file.Expand() : target;
            if (expanded.Workspace?.EnvironmentToSend is { } environment)
            {
                if (WorkspaceCreation.Refusal(environment, expanded, path, CliHost.Env) is { } notCreated)
                    throw notCreated;
                created = await CreateWorkspaceAsync(config, path, environment, nameItHint: profile is null);
                config = config.WithTenant(created.PublicId);
                // Blindtest 2 (Kenneth 2026-10-09): workspacet skrives inn i profilen i fila, så neste kommando finner det.
                if (profile is not null)
                    created = created with { FileUpdated = RecordWorkspace(path, profile, created.PublicId) };
            }
        }

        // Etter at workspacet er laget, står det i hver utgang, også en feil: ellers lager neste forsøk et til (review av #65, B1).
        try
        {
            return await ApplyAsync(map, path, file, target, config, planId, wait, created);
        }
        catch (QueueyException ex) when (created is not null)
        {
            bool configuration = ex is QueueyConfigurationException;
            return CliErrors.Write(map.Has("json"), configuration ? "config_error" : ex.ErrorCode ?? "queuey_error", ex.Message,
                (ex.SuggestedAction is null ? "" : ex.SuggestedAction + " ") + created.NameIt, configuration ? null : ex.StatusCode,
                configuration ? ExitCodes.Configuration : ExitCodes.RuntimeError, configuration ? "Config error" : "Queuey error",
                new Dictionary<string, object?> { ["createdWorkspace"] = created.ToJson() });
        }
    }

    private static async Task<int> ApplyAsync(
        ArgMap map, string path, DeploymentFile file, DeploymentFile target, ResolvedConfig config, string? planId, TimeSpan wait,
        CreatedWorkspace? created)
    {
        using ServiceProvider provider = CliHost.BuildProvider(config);
        var service = provider.GetRequiredService<IQueueyService>();

        // Med en profil går fila ut med profilens verdier, utvidet her; uten en utvider biblioteket den fra miljøet, som før.
        file = target;

        if (map.Has("check"))
            return await CheckAsync(service, file, path, map);

        // En plan Queuey lagrer (F3.11): kilden og det den tar tilbake, er planens.
        var storedPlans = provider.GetRequiredService<IQueueyPlans>();
        var run = new ApplyRun(service, storedPlans, file, path, config, map, wait, created);
        if (planId is not null)
            return await run.StoredAsync(await storedPlans.GetStoredPlanAsync(planId, config.TenantPublicId));

        // Hvor fila ligger, til merket på det applyen styrer (Queuey F2.4): flaggene først, så git, med mindre --no-git. En plan
        // apply lager, får også ref, workflow og PR fra CI (F3.11); starten av en apply sender bare repo, sti og commit.
        DeploymentFileSource? source = CiSource.With(
            GitSource.Resolve(path, map.Get("repo"), map.Get("repo-path"), map.Get("commit"), map.Has("no-git")), CliHost.Env);
        IReadOnlyList<string> adopt = DeploymentAdopt.Parse(map.Get("adopt"));
        var options = new SyncOptions { ContinueOnError = map.Has("continue-on-error"), Source = source, Adopt = adopt };

        // --adopt viser diffen fila vil påføre det den tar tilbake, før den skriver: planen for de samme målene, som dry runs.
        // Krever Queuey en lagret plan for applyen (F3.11), viser den lagrede planen det samme, så denne vises ikke.
        DeploymentPlan? adoptPlan = adopt.Count > 0 ? await service.PlanDeploymentAsync(file, options) : null;
        if (adoptPlan is { ApplyRequiresPlan: true })
            adoptPlan = null;
        if (adoptPlan is not null && !map.Has("json"))
            WriteAdoptPlan(adoptPlan, adopt);

        QueueSyncResult result;
        try
        {
            result = await service.ApplyDeploymentAsync(file, options);
        }
        catch (QueueySyncException ex)
        {
            result = ex.Queues!;
        }
        catch (QueueyPlanRequiredException required)
        {
            // Queuey F3.11: en nøkkel applyer til dette workspacet bare gjennom en lagret plan, og ingenting er skrevet. apply
            // lager planen selv; kjører policyen den, applyes den med en gang (regel 3: ingen ekstra steg uten gevinst).
            return await run.ThroughStoredPlanAsync(required, options);
        }

        return run.Report(result, source, adopt, adoptPlan);
    }

    /// <summary>
    /// Reads <c>--plan</c>, <c>--wait</c> and <c>--timeout</c>: the plan's id, and how long to wait for a person. The exit code
    /// of the usage error, or null.
    /// </summary>
    private static int? StoredPlanOptions(ArgMap map, out string? planId, out TimeSpan wait)
    {
        planId = null;
        wait = TimeSpan.Zero;

        if (map.Has("plan"))
        {
            string? raw = map.Get("plan")?.Trim();
            if (!StoredPlan.IsPlanId(raw))
                return CliErrors.Usage(map, "invalid_value",
                    $"--plan takes the id of a plan Queuey stores, plan_…; got '{(raw is null ? "" : CliErrors.Shown(raw))}'.",
                    "`queuey plan` makes one and says its id. To see what apply would change without storing a plan, run `queuey plan --local`.");
            foreach (string other in new[] { "dry-run", "check", "adopt", "repo", "repo-path", "commit" })
                if (map.Has(other))
                    return CliErrors.Usage(map, "conflicting_options",
                        $"--plan applies a plan Queuey stores, which already says where the file is and what it takes back, so --{other} does not go with it.");
            planId = raw!;
        }

        if (map.Has("timeout") && !map.Has("wait"))
            return CliErrors.Usage(map, "conflicting_options", "--timeout says how long --wait waits; give --wait too.");
        if (!map.Has("wait"))
            return null;
        if (map.Has("dry-run") || map.Has("check"))
            return CliErrors.Usage(map, "conflicting_options", "--wait waits for a person to approve a plan, and --dry-run and --check make none.");

        int seconds = StoredPlanText.DefaultWaitSeconds;
        if (map.Has("timeout")
            && (!int.TryParse(map.Get("timeout"), NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds < 1))
            return CliErrors.Usage(map, "invalid_value",
                $"--timeout takes whole seconds, at least 1; got '{CliErrors.Shown(map.Get("timeout") ?? "")}'.",
                $"Leave it out to wait {StoredPlanText.DefaultWaitSeconds} seconds. A plan waits 24 hours in Queuey's inbox.");
        wait = TimeSpan.FromSeconds(seconds);
        return null;
    }

    /// <summary>
    /// Writes the workspace apply created into <paramref name="profile"/> in the file, and says so on stderr; or says why it
    /// did not. Never fails the apply: the workspace is made, and the answer names it either way.
    /// </summary>
    private static DeploymentWorkspaceRecord.Written? RecordWorkspace(string path, string profile, string tenant)
    {
        try
        {
            if (DeploymentWorkspaceRecord.Record(path, profile, tenant, out string? notWritten) is { } written)
            {
                Console.Error.WriteLine(written.Said);
                return written;
            }
            Console.Error.WriteLine($"Did not write {tenant} to {path}: {notWritten}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not write {tenant} to {path}: {ex.Message}");
        }
        Console.Error.WriteLine($"  → Name it, or the next apply creates another: {DeploymentWorkspaceRecord.Variable} for profile {profile}, or --tenant {tenant}.");
        return null;
    }

    /// <summary>A workspace apply created because nothing named one and the file says its environment.</summary>
    private sealed record CreatedWorkspace(string PublicId, string? DisplayName, string Environment)
    {
        /// <summary>The option that names it, ready to use.</summary>
        public string TenantOption => $"--tenant {PublicId}";

        /// <summary>The change apply made to the deployment file to record it, or null when it made none.</summary>
        public DeploymentWorkspaceRecord.Written? FileUpdated { get; init; }

        /// <summary>What to do so the next apply reaches it.</summary>
        public string NameIt => FileUpdated is { } written
            ? $"This apply created workspace {PublicId} and wrote it to profiles.{written.Profile} in {written.Path}: the next command " +
              $"with --profile {written.Profile} reaches it."
            : $"This apply created workspace {PublicId}: name it with {TenantOption}, or the next apply creates another.";

        public object ToJson() => new
        {
            publicId = PublicId, displayName = DisplayName, environment = Environment, tenantOption = TenantOption,
            fileUpdated = FileUpdated?.ToJson(),
        };
    }

    /// <summary>
    /// Creates the workspace the file describes, marked with its environment, and says on stderr how to name it, so the next
    /// apply reaches it instead of creating another.
    /// </summary>
    private static async Task<CreatedWorkspace> CreateWorkspaceAsync(ResolvedConfig config, string path, string environment, bool nameItHint)
    {
        using ServiceProvider provider = CliHost.BuildProvider(config);
        if (provider.GetRequiredService<IQueueyService>().Management is not QueueyManagement management)
            throw new QueueyException("This build's Queuey client can't create a workspace with an environment.", errorCode: "unsupported");

        // Et nøytralt navn, aldri mappens: den kan bære et kundenavn, og navnet vises for hele lisensen (review av #65, K3).
        string name = WorkspaceCreation.NameFor(environment);
        TenantResult tenant = await management.CreateWorkspaceAsync(name, environment);
        string id = tenant.PublicId ?? throw new QueueyException("Queuey created a workspace and did not say its id.", errorCode: "workspace_id_missing");
        // Security-review av #71 (K2): id-en vises og kan skrives i deploy-fila, så den må ha formen til en workspace-id.
        if (!CliErrors.LooksLikeAWorkspaceId(id))
            throw new QueueyException("Queuey created a workspace and answered with an id that is not a workspace id. It is not shown.",
                errorCode: "workspace_id_invalid");

        Console.Error.WriteLine($"No workspace is named, and {path} says environment {environment}: created workspace {id} "
                                + $"({TerminalText.Line(tenant.DisplayName ?? name)}, {environment}).");
        if (nameItHint)
            Console.Error.WriteLine($"  → Name it, or the next apply creates another: \"tenant\": \"{id}\" in {path}, tenant in the profile, or --tenant {id}.");
        return new CreatedWorkspace(id, tenant.DisplayName, environment);
    }

    /// <summary>One apply from the command line: the service, the file, and how the result is written.</summary>
    private sealed class ApplyRun
    {
        private readonly IQueueyService _service;
        private readonly IQueueyPlans _plans;
        private readonly DeploymentFile _file;
        private readonly string _path;
        private readonly ResolvedConfig _config;
        private readonly ArgMap _map;
        private readonly TimeSpan _wait;
        private readonly CreatedWorkspace? _created;

        public ApplyRun(
            IQueueyService service, IQueueyPlans plans, DeploymentFile file, string path, ResolvedConfig config, ArgMap map, TimeSpan wait,
            CreatedWorkspace? created)
        {
            _created = created;
            _service = service;
            _plans = plans;
            _file = file;
            _path = path;
            _config = config;
            _map = map;
            _wait = wait;
        }

        private bool Json => _map.Has("json");

        /// <summary><paramref name="json"/> as apply prints it, with <c>createdWorkspace</c> when this apply created the workspace.</summary>
        private string WithCreated(object json)
        {
            if (_created is null)
                return JsonSerializer.Serialize(json, CliHost.JsonOut);

            var node = (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(json, CliHost.JsonOut)!;
            node["createdWorkspace"] = JsonSerializer.SerializeToNode(_created.ToJson(), CliHost.JsonOut);
            node["fileUpdated"] = _created.FileUpdated is { } updated ? JsonSerializer.SerializeToNode(updated.ToJson(), CliHost.JsonOut) : null;
            return node.ToJsonString(CliHost.JsonOut);
        }

        /// <summary>
        /// Makes the plan Queuey asked for, sends it to the inbox when a person approves it, and applies it once the policy runs
        /// it or a person approved it. Exit 5 while it
        /// waits for a person, and 1 for a refusal in it.
        /// </summary>
        public async Task<int> ThroughStoredPlanAsync(QueueyPlanRequiredException required, SyncOptions options)
        {
            if (!Json)
                Console.WriteLine($"Queuey applies to this workspace from an API key only through a configuration plan "
                                  + $"({TerminalText.Line(required.Message)}). Making one:");

            // apply sender planen til innboksen selv når policyen gir den til en person: det er applyen som trenger den.
            DeploymentPlan plan = await _plans.StorePlanAsync(_file, new SyncOptions { Source = options.Source, Adopt = options.Adopt }, submit: true);
            if (plan.Stored is not { } stored)
                throw new QueueyException(
                    "Queuey asked for a configuration plan, and stored none when one was made, so nothing was applied.", errorCode: "plan_not_stored")
                {
                    SuggestedAction = "Run `queuey plan` to see what Queuey answers.",
                };

            if (!plan.WouldSucceed)
            {
                if (Json)
                    Console.WriteLine(WithCreated(PlanCommand.ToJson(plan, _path, _config.IngressSource())));
                else
                {
                    Console.WriteLine($"Queuey plan — {_path} → {_config.ResolvedApiBase()}  (tenant {plan.Tenant})");
                    PlanCommand.WriteBody(plan, _config);
                }

                return ExitCodes.RuntimeError;
            }

            if (!Json)
            {
                Console.WriteLine($"Queuey plan — {_path} → {_config.ResolvedApiBase()}  (tenant {plan.Tenant})");
                PlanCommand.WriteBody(plan, _config);
            }

            return await StoredAsync(stored, shown: true);
        }

        /// <summary>
        /// Applies <paramref name="plan"/>: sends it to the inbox when it was sealed for a person and not sent, waits for the
        /// approval with <c>--wait</c>, and writes it once it may be applied.
        /// </summary>
        public async Task<int> StoredAsync(StoredPlan plan, bool shown = false)
        {
            if (plan.NeedsSubmitting)
                plan = await _plans.SubmitStoredPlanAsync(plan.PlanId, plan.Tenant);

            if (!shown && !Json)
            {
                Console.WriteLine($"Queuey plan — {plan.PlanId}  (tenant {plan.Tenant})");
                StoredPlanText.Write(plan);
            }

            if (plan.IsPendingApproval && _wait > TimeSpan.Zero)
                plan = await StoredPlanText.WaitAsync(_plans, plan, _wait, Json);

            if (plan.IsPendingApproval)
                return Pending(plan, shown);

            if (StoredPlanText.WhyNotApplicable(plan) is { } why)
                throw why;

            QueueSyncResult result;
            try
            {
                result = await _service.ApplyDeploymentAsync(_file, new SyncOptions { ContinueOnError = _map.Has("continue-on-error"), Plan = plan });
            }
            catch (QueueySyncException ex)
            {
                result = ex.Queues!;
            }

            return Report(result, source: null, adopt: plan.Adopt, adoptPlan: null, plan);
        }

        // Planen venter på en person: lenken, og exit 5. Planen som nettopp ble laget, har alt sagt hvor den godkjennes.
        private int Pending(StoredPlan plan, bool shown)
        {
            if (Json)
            {
                Console.WriteLine(WithCreated(new
                {
                    schemaVersion = ResultJsonSchemaVersion,
                    file = _path,
                    pendingApproval = true,
                    plan = StoredPlanText.ToJson(plan),
                }));
            }
            else
            {
                if (!shown || _wait > TimeSpan.Zero)
                    StoredPlanText.WriteNext(plan);
                Console.WriteLine("Nothing was applied: the plan waits for a person's approval.");
                if (_created is not null)
                    Console.Error.WriteLine($"  → {_created.NameIt}");
            }

            return ExitCodes.PendingApproval;
        }

        public int Report(QueueSyncResult result, DeploymentFileSource? source, IReadOnlyList<string> adopt, DeploymentPlan? adoptPlan, StoredPlan? plan = null)
        {
            string? tenant = _config.TenantPublicId;

            if (Json)
                Console.WriteLine(JsonSerializer.Serialize(ToJsonResult(result, _path, _config, tenant, source, adopt, adoptPlan, plan, _created), CliHost.JsonOut));
            else
                WriteHuman(result, _path, _config, tenant, plan);

            return result.AllSucceeded ? ExitCodes.Success : ExitCodes.RuntimeError;
        }
    }

    /// <summary>The version of <c>apply --json</c>'s and <c>apply --check --json</c>'s shapes, which had none before Queuey F2.4.</summary>
    internal const int ResultJsonSchemaVersion = 1;

    /// <summary>
    /// What apply and plan say when Queuey started no apply (sikkerhetsreviewen 2026-10-06): the run goes as before F2.4, so
    /// nothing is marked as managed by the file, and what a person detached is still skipped.
    /// </summary>
    internal const string NoApplyStarted =
        "Queuey started no apply for this run (it predates managed resources): nothing was marked as managed by this file. "
        + "What a person detached was skipped all the same.";

    /// <summary>The targets an adopt names, as a plan writes them: <c>workspace</c> and <c>queues.&lt;name&gt;</c>.</summary>
    internal static HashSet<string> AdoptTargets(IReadOnlyList<string> adopt)
        => new(adopt.Select(DeploymentAdopt.TargetOf), StringComparer.Ordinal);

    // Diffen for det --adopt tar tilbake, før applyen skriver: hver endring fila påfører, og hvert avslag.
    private static void WriteAdoptPlan(DeploymentPlan plan, IReadOnlyList<string> adopt)
    {
        HashSet<string> targets = AdoptTargets(adopt);
        Console.WriteLine($"Adopting {string.Join(", ", targets)} back into deployment management. What the file changes on it:");
        var steps = plan.Steps.Where(s => targets.Contains(s.Target)).ToList();
        if (steps.Count == 0)
            Console.WriteLine("  nothing: it already matches the file.");
        foreach (DeploymentPlanStep step in steps)
        {
            Console.WriteLine($"  {step.Target} · {step.Aspect}{(step.Changes.Count == 0 && step.Error is null ? "  (no change)" : "")}");
            foreach (PlannedChange change in step.Changes)
                Console.WriteLine($"      ~ {change}");
            if (step.Error is { } error)
                WriteStepError(error);
        }
    }

    /// <summary>A refused step of a plan, as apply and plan write it: the code, Queuey's message, and the way out under it.</summary>
    internal static void WriteStepError(QueueyException error)
    {
        Console.WriteLine($"      ✗ {TerminalText.Line(error.ErrorCode ?? "refused")}: {TerminalText.Line(error.Message)}");
        // Blindtesten 2026-10-09 (funn 2): et plantak skal sies som et plantak, med veien ut.
        if ((error.SuggestedAction ?? Advise.PlanRetention.CapHint(error.ErrorCode)) is { } action)
            Console.WriteLine($"        → {TerminalText.Line(action)}");
    }

    /// <summary>
    /// The deployment file named by <c>--file</c>, or <c>queuey.deploy.json</c> here. A missing file is
    /// a usage error, written the way the caller asked for errors.
    /// </summary>
    internal static bool TryReadDeploymentFile(ArgMap map, out string path, out DeploymentFile file, out int failure, string? profile = null)
    {
        path = map.Get("file") ?? DeploymentFile.DefaultFileName;
        file = null!;
        failure = ExitCodes.Success;

        if (!File.Exists(path))
        {
            failure = CliErrors.Usage(map, "missing_file", $"No deployment file at '{path}'.",
                profile is null
                    ? "Create one, or pass --file <path>."
                    : $"Create one, or pass --file <path>. --profile {profile} needs the file's values for {profile} as well as the connection.");
            return false;
        }

        file = ParseNamed(path, profile);
        return true;
    }

    /// <summary>The file expanded for <paramref name="profile"/>, with the path in front of the error when it fails.</summary>
    internal static DeploymentFile ForProfile(DeploymentFile file, string profile, string path)
    {
        try
        {
            return file.ForProfile(profile, CliHost.Env);
        }
        catch (QueueyConfigurationException ex)
        {
            throw new QueueyConfigurationException($"{path}: {ex.Message}") { SuggestedAction = ex.SuggestedAction };
        }
    }

    /// <summary>
    /// The API host a dry run judges a local destination by. With a profile, its connection's when the user's file has it:
    /// a dry run never connects, so it needs only the file's half of the profile, and says on stderr what is wrong with the
    /// other half.
    /// </summary>
    private static Uri DryRunApiBase(ArgMap map, string? profile)
    {
        if (profile is null)
            return CliHost.Resolve(map).ResolvedApiBase();

        string? problem, action;
        try
        {
            return CliHost.Resolve(map, profiles: true).ResolvedApiBase();
        }
        catch (QueueyConfigurationException ex)
        {
            (problem, action) = (ex.Message, ex.SuggestedAction);
        }
        catch (CliUsageException ex)
        {
            (problem, action) = (ex.Message, ex.Action);
        }

        // Uten brukerens fil (en CI-jobb uten nøkler, for eksempel): --api-base, ellers standardverten. Feilen sies likevel, på
        // stderr (herding før tag, review av #53): ellers fikk brukeren vite det først når en ekte apply stoppet på den.
        Console.Error.WriteLine(
            $"Warning: the dry run went on without profile {profile}'s connection, which apply --profile {profile} needs: {problem}");
        if (!string.IsNullOrWhiteSpace(action))
            Console.Error.WriteLine($"  → {action}");

        return new ResolvedConfig
        {
            ApiBaseOverride = map.Get("api-base") is { } apiBase && Uri.TryCreate(apiBase, UriKind.Absolute, out Uri? uri) ? uri : null,
        }.ResolvedApiBase();
    }

    /// <summary>
    /// The deployment file at <paramref name="path"/>, parsed and checked the way apply checks it — every <c>${VAR}</c>
    /// expanded and every value validated — with the path in front of the error when it fails: the parser and the
    /// checks speak of "the deployment file" or of a field, and a command can read more than one file. The file is
    /// returned as written, with <c>${VAR}</c> unexpanded.
    /// </summary>
    internal static DeploymentFile ParseNamed(string path, string? profile = null)
    {
        try
        {
            DeploymentFile file = DeploymentFile.Parse(CliFiles.ReadAllText(path));
            _ = (profile is null ? file.Expand() : file.ForProfile(profile, CliHost.Env)).Resolve();
            return file;
        }
        catch (QueueyConfigurationException ex)
        {
            // Parseren sier «the deployment file», ikke hvilken (review 2026-10-05). Re-review samme dag: valideringen
            // (en ingress-kilde, en authMode) og en ${VAR} som mangler, fikk ikke stien, så den sjekkes her også.
            throw new QueueyConfigurationException($"{path}: {ex.Message}") { SuggestedAction = ex.SuggestedAction };
        }
    }

    /// <summary>
    /// The CI gate: report what applying would change, write nothing, and exit non-zero on drift so a
    /// divergence is noticed at review time rather than during an incident.
    /// </summary>
    private static async Task<int> CheckAsync(IQueueyService service, DeploymentFile file, string path, ArgMap map)
    {
        // Det en person har løsrevet, hopper apply over (Queuey F2.4): det er ikke drift, men sjekken melder det, med hvem og når.
        DeploymentCheck check = await service.InspectDeploymentAsync(file);
        IReadOnlyList<DriftItem> drift = check.Drift;

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = ResultJsonSchemaVersion,
                file = path,
                inSync = drift.Count == 0,
                drift = drift.Select(d => new { d.Path, d.Declared, d.Actual }),
                detached = check.Detached.Select(ToJson),
            }, CliHost.JsonOut));
        }
        else
        {
            if (drift.Count == 0)
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

            WriteDetached(check.Detached, "apply skips it");
        }

        return drift.Count == 0 ? ExitCodes.Success : ExitCodes.RuntimeError;
    }

    // Hvert løsrevne mål, med hvem, når og hvorfor, og hvordan det tas tilbake.
    internal static void WriteDetached(IReadOnlyList<SkippedResource> detached, string consequence)
    {
        foreach (SkippedResource skipped in detached)
            Console.WriteLine($"  – {skipped.Target}\t{skipped.Management.DetachedText()} — {consequence}; `queuey apply --adopt {skipped.AdoptAs}` takes it back");
    }

    internal static object ToJson(SkippedResource skipped) => new
    {
        target = skipped.Target,
        queue = skipped.QueueName,
        state = skipped.Management.State,
        detachedBy = skipped.Management.DetachedBy is { } by ? new { kind = by.Kind, name = by.Name, apiClientPublicId = by.ApiClientPublicId } : null,
        detachedAtUtc = skipped.Management.DetachedAtUtc,
        detachReason = skipped.Management.DetachReason,
        file = skipped.Management.File,
        adoptAs = skipped.AdoptAs,
    };

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
            if (w.Ingress?.SignedRequest is { } sr) parts.Add(SignedRequestText(sr));
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
            if (p.Kind is { } kind) parts.Add($"kind={kind.ToFileText()}");
            QueuePolicy policy = p.Definition.Policy;
            if (policy.Ordering is not null) parts.Add($"ordering={policy.Ordering}");
            parts.AddRange(Backoff(policy.Backoff));
            if (policy.Filter is { } filter) parts.Add($"filter=({filter})");
            if (p.Ingress?.SignedRequest is { } sr) parts.Add(SignedRequestText(sr));

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

    // Malen og credential-navnet, aldri en hemmelighet: fila har ingen.
    private static string SignedRequestText(DeploymentSignedRequest signed)
        => signed.CredentialRef is { } credential ? $"signedRequest={signed.Template}:{credential}" : $"signedRequest={signed.Template}";

    private static IEnumerable<string> Backoff(RetryBackoff? backoff)
    {
        if (backoff is { } b)
        {
            if (b.BaseDelayMs is { } baseMs) yield return $"backoff.baseDelayMs={baseMs}";
            if (b.MaxDelayMs is { } maxMs) yield return $"backoff.maxDelayMs={maxMs}";
            if (b.Jitter is { } jitter) yield return $"backoff.jitter={jitter}";
        }
    }

    private static void WriteHuman(QueueSyncResult result, string path, ResolvedConfig config, string? tenant, StoredPlan? plan = null)
    {
        Console.WriteLine($"Queuey apply — {path} → {config.ResolvedApiBase()}  (tenant {tenant ?? "?"})");
        if (plan is not null)
            Console.WriteLine($"  plan {TerminalText.Line(plan.PlanId)}, {(plan.Status == StoredPlan.Statuses.Approved ? "approved by a person" : "run by the policy")}");

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
            // feil som stoppet hele kommandoen det; en vanlig apply mistet det. Teksten er Queueys, så den går gjennom
            // TerminalText, som i CliErrors (local forwarding bare i dev, 2026-10-06: avslaget har veien ut her).
            if (!r.Succeeded && (r.Error?.SuggestedAction ?? Advise.PlanRetention.CapHint(r.Error?.ErrorCode)) is { } action)
                Console.WriteLine($"      → {TerminalText.Line(action)}");
        }

        foreach (string skipped in result.NotAttempted)
            Console.WriteLine($"  – {skipped}\tnot attempted (stopped at an earlier failure)");

        // Løsrevet av en person (Queuey F2.4): applyen rørte det ikke.
        WriteDetached(result.Skipped, "skipped");
        if (result.ApplyStarted == false)
            Console.WriteLine($"  ! {NoApplyStarted}");

        foreach (string warning in result.Warnings)
            Console.WriteLine($"  ! {warning}");

        Console.WriteLine($"{result.Succeeded} applied ({result.Created} created), {result.Failed} failed, "
                          + $"{result.NotAttempted.Count} not attempted"
                          + (result.NotAttempted.Count > 0 ? " — re-run to converge (applying is idempotent)" : ""));

        // Planen passet ikke lenger (Queuey F3.11): det som ble skrevet, står, og en ny plan viser resten.
        if (plan is not null && result.Applied.Any(r => StoredPlanText.MeansPlanAgain(r.Error?.ErrorCode)))
            Console.WriteLine(StoredPlanText.PlanAgain);
    }

    internal static string FormatError(QueueyException? e)
        => e is null ? "failed" : TerminalText.Line($"{(e.StatusCode?.ToString() ?? "error")} {e.ErrorCode} {e.Message}".Replace("  ", " ").Trim());

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
        // Typen står for seg, fordi den ikke er en del av leveringens PATCH: en fil som bare sier kind, har delivery null her.
        kind = p.Kind?.ToFileText(),
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
        signedRequest = i.SignedRequest is { } sr ? new { sr.Template, sr.CredentialRef } : null,
    };

    private static object? ToJson(ContextSource? s) => s is null ? null : new { s.From, s.Name };

    private static object ToJsonResult(
        QueueSyncResult result, string path, ResolvedConfig config, string? tenant,
        DeploymentFileSource? source, IReadOnlyList<string> adopt, DeploymentPlan? adoptPlan, StoredPlan? plan, CreatedWorkspace? created) => new
    {
        schemaVersion = ResultJsonSchemaVersion,
        file = path,
        // Workspacet apply laget fordi ingenting navnga et, og fila sa miljøet; null ellers. Navngi det, ellers lager neste
        // apply et til.
        createdWorkspace = created?.ToJson(),
        // Endringen apply gjorde i fila for å huske workspacet (blindtest 2); null når den ikke gjorde noen.
        fileUpdated = created?.FileUpdated?.ToJson(),
        // Planen Queuey lagrer, som applyen skrev (F3.11); null for en apply uten plan.
        plan = plan is null ? null : StoredPlanText.ToJson(plan),
        // Fila slik Queuey merker det applyen styrer med (F2.4): uten userinfo, query og fragment.
        source = source is null ? null : new { repo = source.Repo, path = source.Path, commit = source.Commit },
        enforcement = result.Enforcement,
        applyStarted = result.ApplyStarted,
        skipped = result.Skipped.Select(ToJson),
        adopted = adopt,
        adoptPlan = adoptPlan is null ? null : adoptPlan.Steps.Where(s => AdoptTargets(adopt).Contains(s.Target)).Select(s => new
        {
            target = s.Target,
            aspect = s.Aspect,
            changes = s.Changes.Select(c => new { path = c.Path, from = c.From, to = c.To }),
            error = s.Error is null ? null : new { code = s.Error.ErrorCode, message = s.Error.Message },
        }),
        apiHost = config.ResolvedApiBase().ToString(),
        tenant,
        total = result.Total,
        succeeded = result.Succeeded,
        created = result.Created,
        failed = result.Failed,
        notAttempted = result.NotAttempted,
        // Bare klientens egne: Queueys står i serverWarnings, og sto før i begge (gullflyten 2026-10-09).
        warnings = result.Warnings.Where(w => !result.ServerWarnings.Contains(w, StringComparer.Ordinal)),
        // Advarslene Queuey svarte med (X-Queuey-Warning), som would_require_approval (F3.11). De står også på stderr.
        serverWarnings = result.ServerWarnings,
        queues = result.Applied.Select(r => new
        {
            r.Name, r.Succeeded, r.PublicId, r.Created, r.PolicyApplied, r.Mode,
            error = r.Error?.Message,
            errorCode = r.Error?.ErrorCode,
            action = r.Error?.SuggestedAction ?? Advise.PlanRetention.CapHint(r.Error?.ErrorCode),
            status = r.Error?.StatusCode,
        }),
    };
}
