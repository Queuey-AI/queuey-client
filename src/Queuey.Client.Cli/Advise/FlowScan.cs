using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli.Advise;

/// <summary>One thing found in the repository, at a line, in words.</summary>
public sealed record Finding(string File, int Line, string What)
{
    public FlowEvidence ToEvidence() => new(File, Line, What);
}

/// <summary>A route that takes POST requests, where it is registered, and what serves it.</summary>
/// <param name="FromPath">True when the route is where the file is, as with Next.js and Supabase functions.</param>
public sealed record RouteFinding(string File, int Line, string Route, string Framework, bool FromPath)
{
    public FlowEvidence ToEvidence() => new(File, Line, $"POST {Route}");
}

/// <summary>A prefix a router, group or blueprint is mounted under, and the text after it on that line.</summary>
public sealed record MountFinding(string File, int Line, string Prefix, string Target);

/// <summary>A port a receiver listens on. <paramref name="HardCoded"/> is false when configuration can change it.</summary>
public sealed record PortFinding(string File, int Line, int Port, bool HardCoded, string What)
{
    public FlowEvidence ToEvidence() => new(File, Line, What);
}

/// <summary>A call that verifies Stripe's signature, and the name the handler reads its secret from, when it shows.</summary>
public sealed record StripeVerificationFinding(string File, int Line, string Api, string? SecretName, int? SecretLine)
{
    public FlowEvidence ToEvidence() => new(File, Line, $"{Api} checks the Stripe-Signature header");
}

/// <summary>A handler that compares a header with a secret, and the name the secret is read from, when it shows.</summary>
public sealed record SecretCheckFinding(string File, int Line, string Header, string? SecretName)
{
    public bool IsBearer => Header.Equals("authorization", StringComparison.OrdinalIgnoreCase);

    public FlowEvidence ToEvidence() => new(File, Line,
        SecretName is null ? $"checks the {Header} header" : $"checks the {Header} header against {SecretName}");
}

/// <summary>
/// A Supabase Database Webhook declared in SQL: the table, the operations, and where it posts — a path only when it is a
/// Supabase function's or one of this repository's routes, since a URL can carry a secret.
/// </summary>
public sealed record SupabaseTriggerFinding(
    string File, int Line, string? Table, IReadOnlyList<string> Events, string? TargetPath, bool TargetsQueuey, string? RawPath)
{
    public FlowEvidence ToEvidence() => new(File, Line,
        $"a Supabase Database Webhook on {Table ?? "a table"} ({string.Join(", ", Events.Select(e => e.ToLowerInvariant()))})" +
        (TargetsQueuey ? " that posts to Queuey's ingress" : TargetPath is not null ? $" that posts to {TargetPath}" : ""));
}

/// <summary>An event type a handler compares with.</summary>
public sealed record EventTypeFinding(string File, int Line, string Type);

/// <summary>A name read from a file: an environment variable in a .env file, a Supabase function in config.toml.</summary>
public sealed record NameFinding(string File, int Line, string Name);

/// <summary>The deployment file in the repository's root, as far as advise needs it.</summary>
public sealed class ExistingDeployFile
{
    public string File { get; init; } = DeploymentFile.DefaultFileName;

    /// <summary>Null when the file parses and holds together; otherwise why it does not, in the parser's words.</summary>
    public string? Problem { get; init; }

    /// <summary>
    /// Why advise did not read the file that is there, as the end of a sentence, when it is in a form advise does not read: a
    /// link, a folder, larger than it reads, or unreadable. Null when it was read. advise then proposes nothing for it: a new
    /// file would replace the one that is there (review of #56, B-1).
    /// </summary>
    public string? Unread { get; init; }

    /// <summary>What to do about a file advise did not read, for the conflict it gives.</summary>
    public string? WayOut { get; init; }

    public string? Environment { get; init; }

    public string? BaseUrl { get; init; }

    /// <summary>Each queue it declares, with the signed-request template its ingress verifies, when it names one.</summary>
    public IReadOnlyDictionary<string, string?> Queues { get; init; } = new Dictionary<string, string?>();

    /// <summary>The profiles it has, by name: the values its ${VAR} references take per environment.</summary>
    public IReadOnlyList<string> Profiles { get; init; } = Array.Empty<string>();

    /// <summary>The file as JSON, when it reads: what a proposal for it starts from and keeps.</summary>
    public JsonObject? Json { get; init; }

    /// <summary>Whether it has comments or trailing commas, which JSON written from it does not keep.</summary>
    public bool HasComments { get; init; }
}

/// <summary>What the repository shows about the flows it takes part in, each finding with its file and line.</summary>
public sealed class FlowFacts
{
    public IReadOnlyList<RouteFinding> Routes { get; init; } = Array.Empty<RouteFinding>();
    public IReadOnlyList<MountFinding> Mounts { get; init; } = Array.Empty<MountFinding>();
    public IReadOnlyList<StripeVerificationFinding> StripeVerifications { get; init; } = Array.Empty<StripeVerificationFinding>();
    public IReadOnlyList<Finding> RawBodyReads { get; init; } = Array.Empty<Finding>();
    public IReadOnlyList<Finding> JsonBodyParsers { get; init; } = Array.Empty<Finding>();
    public IReadOnlyList<PortFinding> Ports { get; init; } = Array.Empty<PortFinding>();
    public IReadOnlyList<SupabaseTriggerFinding> SupabaseTriggers { get; init; } = Array.Empty<SupabaseTriggerFinding>();
    public IReadOnlyList<Finding> SupabasePayloadReads { get; init; } = Array.Empty<Finding>();
    public IReadOnlyList<SecretCheckFinding> SecretChecks { get; init; } = Array.Empty<SecretCheckFinding>();
    public IReadOnlyList<Finding> QueueySignatureChecks { get; init; } = Array.Empty<Finding>();
    public IReadOnlyList<Finding> Deduplication { get; init; } = Array.Empty<Finding>();
    public IReadOnlyList<EventTypeFinding> EventTypes { get; init; } = Array.Empty<EventTypeFinding>();
    public IReadOnlyList<NameFinding> EnvNames { get; init; } = Array.Empty<NameFinding>();
    public IReadOnlyList<NameFinding> SupabaseFunctionsWithoutJwt { get; init; } = Array.Empty<NameFinding>();
    public IReadOnlyList<Finding> Queuey { get; init; } = Array.Empty<Finding>();
    public ExistingDeployFile? DeployFile { get; init; }

    /// <summary>What the scan left out because of a limit, in words. Empty when it read everything it wanted.</summary>
    public IReadOnlyList<string> Limits { get; init; } = Array.Empty<string>();

    /// <summary>Every file the scan read, repository-relative.</summary>
    public IReadOnlyList<string> FilesRead { get; init; } = Array.Empty<string>();

    /// <summary>The project each file belongs to: the nearest folder above it with a manifest, repository-relative.</summary>
    internal Func<string, string> ProjectOf { get; init; } = _ => "";

    /// <summary>What serves the routes in a file.</summary>
    internal Func<string, string> FrameworkOf { get; init; } = _ => "node";
}

/// <summary>
/// Reads a repository for the parts of a flow: routes that take POST, Stripe's signature check, the raw body, the port,
/// Supabase's webhooks and functions, a secret compared in a header, deduplication, and Queuey where it is already used.
/// Each finding keeps its file and line, so a person can check it.
/// </summary>
/// <remarks>
/// Secrets stay where they are. From a <c>.env</c> file only the names are read; a value is never kept. A URL in a
/// migration is reduced to a path, and only when the path is a function's or a route's. What a finding says is written
/// here, never copied from the file, beyond a route, a name, a header or an event type.
/// </remarks>
public static class FlowScan
{
    private static readonly string[] SkippedDirectories =
    {
        "node_modules", "bin", "obj", ".git", "dist", "build", "out", "coverage", "venv", ".venv", "__pycache__", "vendor",
        ".next", ".vercel", ".netlify", ".turbo", ".supabase",
        // Tester og fixtures er ikke appens handlere: en mock av constructEvent er ikke en Stripe-handler.
        "test", "tests", "__tests__", "spec", "specs", "fixtures", "__fixtures__", "__mocks__", "testdata", "e2e", "cypress", "playwright",
        // Og en kodesnutt i dokumentasjonen er ikke en rute (funnet mot queuey.ai sitt eget repo, 2026-10-06).
        "docs", "doc", "examples", "example",
    };

    private static readonly string[] SkippedDirectorySuffixes = { ".Tests", ".Test", ".UnitTests", ".IntegrationTests", ".Specs" };

    // .tsx og .jsx er komponenter i nettleseren, og en dokumentasjonsside med en Stripe-snutt i en streng ble en
    // Stripe-handler (2026-10-06). En handler er .ts eller .js.
    private static readonly string[] SourceExtensions = { ".cs", ".js", ".mjs", ".cjs", ".ts", ".mts", ".cts", ".py", ".go" };

    public static FlowFacts Scan(string root) => Scan(root, ScanBudget.Default);

    /// <summary>How long opening the root's deployment file may take: a pipe swapped in for it waits for a writer.</summary>
    internal static readonly TimeSpan DeployFileOpenTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Scans within <paramref name="budget"/>: a limit it reaches stops the scan and is named in <see cref="FlowFacts.Limits"/>.</summary>
    public static FlowFacts Scan(string root, ScanBudget budget) => Scan(root, budget, DeployFileOpenTimeout);

    internal static FlowFacts Scan(string root, ScanBudget budget, TimeSpan deployFileOpenTimeout)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A repository path is required.", nameof(root));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"No such directory: {root}");

        var scan = new Scanner(Path.GetFullPath(root), budget, deployFileOpenTimeout);
        scan.Run();
        return scan.Facts();
    }

    private sealed class Scanner
    {
        private readonly string _root;
        private readonly ScanBudget _budget;
        private readonly RepoWalk _walk;
        private readonly List<string> _files = new();
        private int _longLines;
        private int _slowFiles;
        private readonly Dictionary<string, string> _manifests = new(StringComparer.Ordinal);   // mappe → manifest-tekst
        private readonly Dictionary<string, string> _nodeFrameworks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _pythonFrameworks = new(StringComparer.Ordinal);

        private readonly List<RouteFinding> _routes = new();
        private readonly List<MountFinding> _mounts = new();
        private readonly List<StripeVerificationFinding> _stripe = new();
        private readonly List<Finding> _rawBody = new();
        private readonly List<Finding> _jsonParsers = new();
        private readonly List<PortFinding> _ports = new();
        private readonly List<SupabaseTriggerFinding> _triggers = new();
        private readonly List<Finding> _supabasePayload = new();
        private readonly List<SecretCheckFinding> _secretChecks = new();
        private readonly List<Finding> _queueySignature = new();
        private readonly List<Finding> _dedup = new();
        private readonly List<EventTypeFinding> _eventTypes = new();
        private readonly List<NameFinding> _envNames = new();
        private readonly List<NameFinding> _noJwt = new();
        private readonly List<Finding> _queuey = new();
        private ExistingDeployFile? _deployFile;

        private readonly TimeSpan _deployFileOpenTimeout;

        public Scanner(string root, ScanBudget budget, TimeSpan deployFileOpenTimeout)
        {
            _root = root;
            _budget = budget;
            _deployFileOpenTimeout = deployFileOpenTimeout;
            _walk = new RepoWalk(root, budget, SkipDirectory, IsInteresting);
        }

        public void Run()
        {
            // queuey.deploy.json i roten avgjør om forslaget er en ny fil eller den som er der. Den sjekkes for seg, med lstat,
            // og leses før og utenfor budsjettet: en lenke, en frist som gikk ut eller et brukt budsjett ga før «ingen fil», og
            // agenten fikk beskjed om å skrive en ny fil over den som var der (review av #56, B-1).
            _deployFile = RootDeployFile();
            _files.AddRange(_walk.Files());
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);

            // Manifestene først: rammeverket til en fil avhenger av package.json-en over den.
            foreach (string file in _files)
            {
                string name = Path.GetFileName(file);
                if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || name is "pyproject.toml" or "requirements.txt" or "go.mod")
                {
                    string dir = Relative(Path.GetDirectoryName(file)!);
                    string text = texts[file] = _walk.Read(file);
                    _manifests[dir] = _manifests.TryGetValue(dir, out string? before) ? before + "\n" + text : text;
                }
            }

            foreach (string file in _files)
            {
                if (_walk.Expired)
                    break;

                string relative = Relative(file);
                string name = Path.GetFileName(file);
                string text = texts.TryGetValue(file, out string? read) ? read : _walk.Read(file);
                if (text.Length == 0)
                    continue;

                try
                {
                    Read(relative, name, text);
                }
                catch (RegexMatchTimeoutException)
                {
                    // En linje som tar for lang tid å matche, er ingen evidens. Fila hoppes over, og grensen står i svaret.
                    _slowFiles++;
                }
            }

            ResolveTriggerTargets();
        }

        private void Read(string relative, string name, string text)
        {
            if (IsEnvFile(name))
            {
                ReadEnvNames(relative, text);
                return;
            }

            string[] lines = Lines(text);

            if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                ReadPackageJson(relative, lines);
            else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                ReadProject(relative, lines);
            else if (name.Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase))
                ReadLaunchSettings(relative, lines);
            else if (relative.EndsWith("supabase/config.toml", StringComparison.Ordinal))
                ReadSupabaseConfig(relative, lines);
            else if (name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                ReadSql(relative, text, lines);
            else if (SourceExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                ReadSource(relative, name, lines);
        }

        /// <summary>The file's lines, with a line longer than the budget allows left empty, so the numbering holds.</summary>
        private string[] Lines(string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > _budget.MaxLineLength)
                {
                    lines[i] = string.Empty;
                    _longLines++;
                }
            }
            return lines;
        }

        private IReadOnlyList<string> Limits()
        {
            var limits = new List<string>(_walk.Limits);
            if (_longLines > 0)
                limits.Add($"{(_longLines == 1 ? "1 line" : $"{_longLines} lines")} longer than {_budget.MaxLineLength} characters skipped, such as minified code");
            if (_slowFiles > 0)
                limits.Add($"{(_slowFiles == 1 ? "1 file" : $"{_slowFiles} files")} skipped because a line took too long to match");
            return limits;
        }

        public FlowFacts Facts() => new()
        {
            Routes = _routes.Distinct().ToArray(),
            Mounts = _mounts.Distinct().ToArray(),
            StripeVerifications = _stripe.ToArray(),
            RawBodyReads = _rawBody.ToArray(),
            JsonBodyParsers = _jsonParsers.ToArray(),
            Ports = _ports.ToArray(),
            SupabaseTriggers = _triggers.ToArray(),
            SupabasePayloadReads = _supabasePayload.ToArray(),
            SecretChecks = _secretChecks.ToArray(),
            QueueySignatureChecks = _queueySignature.ToArray(),
            Deduplication = _dedup.ToArray(),
            EventTypes = _eventTypes.Distinct().ToArray(),
            EnvNames = _envNames.ToArray(),
            SupabaseFunctionsWithoutJwt = _noJwt.ToArray(),
            Queuey = _queuey.ToArray(),
            DeployFile = _deployFile,
            Limits = Limits(),
            FilesRead = _walk.FilesRead,
            ProjectOf = ProjectOf,
            FrameworkOf = FrameworkOf,
        };

        // ── kildekode ────────────────────────────────────────────────────

        private void ReadSource(string relative, string name, string[] lines)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            bool cs = ext == ".cs", py = ext == ".py", go = ext == ".go";
            bool js = !cs && !py && !go;
            string framework = FrameworkOf(relative);

            FileRoute(relative, lines, framework);

            var csharp = cs ? new CSharpRoutes(relative, this) : null;
            var nest = js ? new NestRoutes(relative, this, framework) : null;
            var goGroups = new Dictionary<string, string>(StringComparer.Ordinal);

            // Navnet handleren leser hemmeligheten fra: det første som ser ut som en webhook-hemmelighet i fila.
            (string Name, int Line)? stripeSecret = null, sharedSecret = null;
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i], py))
                    continue;
                foreach (string secretName in SecretNames(lines[i]))
                {
                    if (stripeSecret is null && LooksLikeAWebhookSecret(secretName, stripe: true))
                        stripeSecret = (secretName, i + 1);
                    if (LooksLikeAWebhookSecret(secretName, stripe: false)
                        && (sharedSecret is null || Rank(secretName) < Rank(sharedSecret.Value.Name)))
                        sharedSecret = (secretName, i + 1);
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (IsComment(line, py))
                    continue;
                int n = i + 1;

                if (cs) csharp!.Read(line, n);
                else if (js) ReadJsRoutes(relative, line, n, framework, nest!);
                else if (py) ReadPythonRoutes(relative, line, n, framework);
                else ReadGoRoutes(relative, line, n, goGroups);

                if (StripeCall(line, ext) is { } api)
                    _stripe.Add(new StripeVerificationFinding(relative, n, api, stripeSecret?.Name, stripeSecret?.Line));

                if (RawBodyRead(line, ext) is { } raw)
                    _rawBody.Add(new Finding(relative, n, raw));

                if (js && JsonParser.IsMatch(line))
                    _jsonParsers.Add(new Finding(relative, n, "parses JSON bodies before the handler sees them"));

                if (PortIn(line, ext) is { } port)
                    _ports.Add(new PortFinding(relative, n, port.Port, port.HardCoded, port.What));

                if (OldRecord.IsMatch(line))
                    _supabasePayload.Add(new Finding(relative, n, "reads old_record, the shape of a Supabase Database Webhook"));

                foreach (string header in CheckedHeaders(line, ext))
                    _secretChecks.Add(new SecretCheckFinding(relative, n, header, sharedSecret?.Name));

                if (QueueySignature.IsMatch(line))
                    _queueySignature.Add(new Finding(relative, n, "verifies Queuey's signature"));

                if (Dedup.IsMatch(line))
                    _dedup.Add(new Finding(relative, n, "keeps track of the events it has handled"));

                foreach (Match m in StripeEventType.Matches(line))
                {
                    string type = m.Groups[1].Value;
                    if (!LooksLikeAFileName(type))
                        _eventTypes.Add(new EventTypeFinding(relative, n, type));
                }

                foreach (Match m in RowEventType.Matches(line))
                    _eventTypes.Add(new EventTypeFinding(relative, n, m.Groups[1].Value.ToUpperInvariant()));

                if (cs && QueueyRegistration.Match(line) is { Success: true } registration)
                    _queuey.Add(new Finding(relative, n, $"registers Queuey with {registration.Groups[1].Value}"));
            }
        }

        /// <summary>A route that is where the file is: Next.js route handlers and API routes, and Supabase functions.</summary>
        private void FileRoute(string relative, string[] lines, string framework)
        {
            Match supabase = SupabaseFunctionFile.Match(relative);
            if (supabase.Success)
            {
                int line = FirstLine(lines, ServeCall) ?? 1;
                _routes.Add(new RouteFinding(relative, line, "/functions/v1/" + supabase.Groups["name"].Value, "supabase-edge", FromPath: true));
                return;
            }

            if (framework != "nextjs")
                return;

            Match app = NextRouteHandlerFile.Match(relative);
            if (app.Success)
            {
                int? post = FirstLine(lines, NextPostExport);
                if (post is null)
                    return;
                string route = "/" + string.Join("/", app.Groups["segs"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => !(s.StartsWith("(", StringComparison.Ordinal) && s.EndsWith(")", StringComparison.Ordinal)) && !s.StartsWith("@", StringComparison.Ordinal))
                    .Select(NextSegment));
                _routes.Add(new RouteFinding(relative, post.Value, route, "nextjs", FromPath: true));
                return;
            }

            Match pages = NextApiFile.Match(relative);
            if (pages.Success)
            {
                string path = pages.Groups["path"].Value;
                if (path == "index") path = "";
                else if (path.EndsWith("/index", StringComparison.Ordinal)) path = path[..^"/index".Length];
                string route = "/api" + (path.Length > 0 ? "/" + string.Join("/", path.Split('/').Select(NextSegment)) : "");
                _routes.Add(new RouteFinding(relative, FirstLine(lines, ExportDefault) ?? 1, route, "nextjs", FromPath: true));
            }
        }

        private static string NextSegment(string segment)
        {
            string s = segment.Trim('[', ']');
            if (s.StartsWith("...", StringComparison.Ordinal)) s = s[3..];
            return segment.StartsWith("[", StringComparison.Ordinal) ? "{" + s + "}" : segment;
        }

        private void ReadJsRoutes(string relative, string line, int n, string framework, NestRoutes nest)
        {
            if (ServesHttp(relative, framework))
            {
                foreach (Match m in JsPost.Matches(line))
                {
                    // axios.post('/api/orders', …) i en nettleserapp er et kall, ikke en rute.
                    if (!HttpClientName.IsMatch(m.Groups[1].Value))
                        AddRoute(relative, n, m.Groups[4].Value, framework);
                }
                foreach (Match m in JsRoutePost.Matches(line))
                    AddRoute(relative, n, m.Groups[2].Value, framework);
            }
            foreach (Match m in JsMount.Matches(line))
                _mounts.Add(new MountFinding(relative, n, NormalizeRoute(m.Groups[2].Value), m.Groups[3].Value.Trim()));
            foreach (Match m in HonoMount.Matches(line))
                _mounts.Add(new MountFinding(relative, n, NormalizeRoute(m.Groups[2].Value), m.Groups[3].Value.Trim()));
            if (NestGlobalPrefix.Match(line) is { Success: true } global)
                _mounts.Add(new MountFinding(relative, n, NormalizeRoute("/" + global.Groups[1].Value), "*"));
            nest.Read(line, n);
        }

        private void ReadPythonRoutes(string relative, string line, int n, string framework)
        {
            Match m = PythonRoute.Match(line);
            if (m.Success)
            {
                string verb = m.Groups[2].Value;
                bool post = verb == "post" || PostInMethods.IsMatch(m.Groups[4].Value);
                if (post)
                    AddRoute(relative, n, m.Groups[3].Value, framework);
            }

            foreach (Match prefix in PythonPrefix.Matches(line))
                _mounts.Add(new MountFinding(relative, n, NormalizeRoute(prefix.Groups[1].Value), line.Trim()));
        }

        private void ReadGoRoutes(string relative, string line, int n, Dictionary<string, string> groups)
        {
            Match group = GoGroup.Match(line);
            if (group.Success)
            {
                string prefix = (groups.TryGetValue(group.Groups["recv"].Value, out string? outer) ? outer : "") + group.Groups["path"].Value;
                if (group.Groups["var"].Success)
                    groups[group.Groups["var"].Value] = prefix;
                _mounts.Add(new MountFinding(relative, n, NormalizeRoute(prefix), line.Trim()));
            }

            foreach (Match m in GoRoute.Matches(line))
            {
                string recv = m.Groups["recv"].Value;
                string prefix = groups.TryGetValue(recv, out string? p) ? p : "";
                AddRoute(relative, n, prefix + m.Groups["path"].Value, "go");
            }
        }

        /// <summary>
        /// Whether <c>.post('/x', …)</c> in this file registers a route. In a browser app (Vite, Create React App) it is a
        /// call to a server; Next.js and Supabase functions take their routes from where the file is.
        /// </summary>
        private bool ServesHttp(string relative, string framework)
        {
            if (framework is "express" or "fastify" or "hono" or "koa" or "nestjs")
                return true;
            if (framework != "node" || SupabaseFunctionFile.IsMatch(relative))
                return false;
            string manifest = _manifests.TryGetValue(ProjectOf(relative), out string? m) ? m : "";
            return !BrowserBundler.IsMatch(manifest);
        }

        internal void AddRoute(string relative, int line, string route, string framework)
        {
            string normalized = NormalizeRoute(route);
            if (normalized.Length > 0 && Showable(normalized) && !TerminalText.HasUnsafeCharacters(normalized))
                _routes.Add(new RouteFinding(relative, line, normalized, framework, FromPath: false));
        }

        // ── andre filer ──────────────────────────────────────────────────

        private void ReadEnvNames(string relative, string text)
        {
            // Bare navnet før '='. Resten av linjen er verdien, og den leses aldri videre.
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = EnvAssignment.Match(lines[i]);
                if (m.Success)
                    _envNames.Add(new NameFinding(relative, i + 1, m.Groups[1].Value));
            }
        }

        private void ReadPackageJson(string relative, string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                Match port = ScriptPort.Match(lines[i]);
                if (port.Success && int.TryParse(port.Groups[1].Value, out int p) && p is >= 1 and <= 65535)
                    _ports.Add(new PortFinding(relative, i + 1, p, HardCoded: false, $"a script serves on port {p}"));
            }
        }

        private void ReadProject(string relative, string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = QueueyPackage.Match(lines[i]);
                if (m.Success)
                    _queuey.Add(new Finding(relative, i + 1, $"{m.Groups[1].Value} is a package reference"));
            }
        }

        private void ReadLaunchSettings(string relative, string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("\"applicationUrl\"", StringComparison.Ordinal))
                    continue;
                foreach (Match m in HttpLocalPort.Matches(lines[i]))
                {
                    if (int.TryParse(m.Groups[1].Value, out int p))
                        _ports.Add(new PortFinding(relative, i + 1, p, HardCoded: false, $"the launch profile serves http on port {p}"));
                }
            }
        }

        private void ReadSupabaseConfig(string relative, string[] lines)
        {
            string section = "";
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("#", StringComparison.Ordinal) || line.Length == 0)
                    continue;

                Match header = TomlSection.Match(line);
                if (header.Success)
                {
                    section = header.Groups[1].Value.Trim();
                    continue;
                }

                if (section == "api" && TomlPort.Match(line) is { Success: true } port && int.TryParse(port.Groups[1].Value, out int p))
                    _ports.Add(new PortFinding(relative, i + 1, p, HardCoded: false, $"the Supabase API serves functions on port {p}"));

                if (section.StartsWith("functions.", StringComparison.Ordinal) && NoVerifyJwt.IsMatch(line)
                    && FunctionName.IsMatch(section["functions.".Length..].Trim('"')))
                    _noJwt.Add(new NameFinding(relative, i + 1, section["functions.".Length..].Trim('"')));
            }
        }

        private void ReadSql(string relative, string text, string[] lines)
        {
            foreach (Match m in SupabaseTrigger.Matches(text))
            {
                int line = text[..m.Index].Count(c => c == '\n') + 1;
                string table = m.Groups["table"].Value.Replace("\"", "");
                string[] events = OrSeparator.Split(m.Groups["events"].Value.Trim())
                    .Select(e => e.Trim().ToUpperInvariant()).Where(e => e.Length > 0).ToArray();
                string url = m.Groups["url"].Value;

                bool queuey = false;
                string? path = null;
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                {
                    queuey = uri.Host.EndsWith("queuey.ai", StringComparison.OrdinalIgnoreCase)
                             || uri.AbsolutePath.StartsWith("/events/ten_", StringComparison.Ordinal);
                    path = uri.AbsolutePath;
                }

                _triggers.Add(new SupabaseTriggerFinding(relative, line, table, events, TargetPath: null, queuey, path));
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("--", StringComparison.Ordinal))
                    continue;
                if (Dedup.IsMatch(lines[i]))
                    _dedup.Add(new Finding(relative, i + 1, "keeps track of the events it has handled"));
            }
        }

        /// <summary>The most of the root's deployment file advise reads.</summary>
        internal const int MaxDeployFileBytes = 512 * 1024;

        /// <summary>
        /// The deployment file in the root, checked on its own: null when there is none, a file advise did not read (with why
        /// and what to do) when it is a link, a folder, too large, unreadable, not a regular file, slow to open or a link by the
        /// time it was read, and otherwise the file as it reads.
        /// </summary>
        private ExistingDeployFile? RootDeployFile()
        {
            const string name = DeploymentFile.DefaultFileName;
            string path = Path.Combine(_root, name);
            var info = new FileInfo(path);

            string? target;
            try
            {
                target = info.LinkTarget;   // readlink på selve oppføringen; lenken følges ikke
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Unread(name, $"could not be checked ({ex.Message}), so advise cannot merge the flow into it.",
                    "Check that this user can read it, then run advise --intent again.");
            }

            if (target is not null)
                return LinkedDeployFile(name, target);

            if (Directory.Exists(path))
                return Unread(name, "is a folder, not a file.",
                    "apply reads a file by that name: move the folder, then run advise --intent again.");

            if (!info.Exists)
                return null;

            string tooLarge = $"is larger than {RepoWalk.SizeText(MaxDeployFileBytes)}, the most advise reads of it, so it cannot merge " +
                              "the flow into it.";
            string tooLargeWayOut = "Check that it is the file apply should read, since a deployment file is rarely that large. Or add the " +
                                    "flow's queue to it by hand.";
            if (info.Length > MaxDeployFileBytes)
                return Unread(name, tooLarge, tooLargeWayOut);

            string? text;
            ReadRefusal refusal;
            try
            {
                // Det som åpnes, kan være noe annet enn det som ble sjekket: åpningen har en tidsgrense, og en strøm som ikke
                // kan søke (et rør), avvises før noe leses (review av #57, K-a).
                text = RepoWalk.ReadBounded(path, MaxDeployFileBytes, _deployFileOpenTimeout, out refusal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Unread(name, $"could not be read ({ex.Message}), so advise cannot merge the flow into it.",
                    "Check that this user can read it, then run advise --intent again.");
            }

            switch (refusal)
            {
                case ReadRefusal.TooLarge:
                    return Unread(name, tooLarge, tooLargeWayOut);
                case ReadRefusal.NotRegular:
                    return Unread(name, "is not a regular file (a pipe or a socket, for instance), so advise cannot merge the flow into it.",
                        $"Put the deployment file itself at {name}, then run advise --intent again.");
                case ReadRefusal.TimedOut:
                    return Unread(name, $"did not open within {RepoWalk.DurationText(_deployFileOpenTimeout)}, as a pipe without a " +
                                        "writer never does, so advise cannot merge the flow into it.",
                        $"Put the deployment file itself at {name}. If it is there and the disk was only slow, run advise --intent again.");
            }

            // Ble fila byttet ut med en lenke mens den ble lest, er det som ble lest, ikke fila som står der nå.
            try
            {
                if (new FileInfo(path).LinkTarget is { } swapped)
                    return LinkedDeployFile(name, swapped);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Unread(name, $"could not be checked ({ex.Message}), so advise cannot merge the flow into it.",
                    "Check that this user can read it, then run advise --intent again.");
            }

            ReadDeployFile(name, text!);
            return _deployFile;
        }

        /// <summary>A link where the deployment file is: never followed, and named by where it points when that is in the repository.</summary>
        private ExistingDeployFile LinkedDeployFile(string name, string target)
        {
            string resolved = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(_root, target));
            string? inside = resolved.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? Relative(resolved)
                : null;

            // Målet står i en vei ut en agent kan lime inn i et skall (review av #57, K-b, skallregelen fra #52). Bare en vanlig
            // sti vises: bokstaver, sifre og . _ / -, uten .. som ledd og uten - først, så ln og cp aldri leser den som et
            // flagg. Et annet mål regnes som en lenke ut av repoet.
            if (inside is not null && !IsPlainPath(inside))
                inside = null;

            return inside is null
                ? Unread(name, "is a symbolic link out of the repository, and advise follows no link, so it cannot read the file it " +
                               "would merge the flow into.",
                    $"Put the deployment file itself at {name}, then run advise --intent again.")
                : Unread(name, $"is a symbolic link to {inside}, and advise follows no link, so it cannot read the file it would merge " +
                               "the flow into.",
                    $"Put a copy of {inside} in the link's place while advise runs, write the content it proposes to {inside}, and " +
                    $"bring the link back: git checkout -- {name} when git tracks it, or ln -sf {inside} {name} when it does not. Or " +
                    $"add the flow's queue to {inside} by hand.");
        }

        /// <summary>A path a shell reads as it is and a command never as a flag: letters, digits and . _ / -, without .. as a segment, not starting with -.</summary>
        private static bool IsPlainPath(string path)
            => PlainPath.IsMatch(path) && !path.StartsWith('-') && !path.Split('/').Contains("..");

        private ExistingDeployFile Unread(string name, string reason, string wayOut)
        {
            _queuey.Add(new Finding(name, 1, "a deployment file advise did not read"));
            return new ExistingDeployFile { File = name, Unread = reason, WayOut = wayOut };
        }

        private void ReadDeployFile(string relative, string text)
        {
            try
            {
                DeploymentFile file = DeploymentFile.Parse(text);
                file.Resolve();
                _deployFile = new ExistingDeployFile
                {
                    File = relative,
                    Environment = file.Workspace?.Environment,
                    BaseUrl = file.Workspace?.Delivery?.BaseUrl,
                    Queues = file.Queues.ToDictionary(q => q.Key, q => q.Value?.Ingress?.SignedRequest?.Template, StringComparer.Ordinal),
                    Profiles = file.Profiles?.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>(),
                    // En tom fil er en deploy-fil uten innhold for leseren, og et tomt objekt her.
                    Json = string.IsNullOrWhiteSpace(text)
                        ? new JsonObject()
                        : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                        {
                            CommentHandling = JsonCommentHandling.Skip,
                            AllowTrailingCommas = true,
                        }) as JsonObject,
                    HasComments = !string.IsNullOrWhiteSpace(text) && !IsPlainJson(text),
                };
                _queuey.Add(new Finding(relative, 1, file.Queues.Count == 0
                    ? "a deployment file that declares no queues"
                    : $"a deployment file that declares {string.Join(", ", file.Queues.Keys)}"));
            }
            catch (QueueyConfigurationException ex)
            {
                _deployFile = new ExistingDeployFile { File = relative, Problem = ex.Message };
                _queuey.Add(new Finding(relative, 1, "a deployment file apply cannot read as it is"));
            }
        }

        /// <summary>
        /// A trigger's target as a path, once the routes are known: a Supabase function's, or one of this repository's.
        /// Any other path is left out, since a webhook URL can carry a secret in its path.
        /// </summary>
        private void ResolveTriggerTargets()
        {
            for (int i = 0; i < _triggers.Count; i++)
            {
                SupabaseTriggerFinding t = _triggers[i];
                if (t.TargetsQueuey || t.RawPath is null)
                {
                    _triggers[i] = t with { RawPath = null };
                    continue;
                }

                string path = NormalizeRoute(t.RawPath);
                bool known = path.StartsWith("/functions/v1/", StringComparison.Ordinal) && Showable(path)
                             || _routes.Any(r => SameRoute(r.Route, path));
                _triggers[i] = t with { TargetPath = known ? path : null, RawPath = null };
            }
        }

        // ── rammeverk og prosjekt ────────────────────────────────────────

        /// <summary>The nearest folder at or above the file's that has a manifest, repository-relative.</summary>
        internal string ProjectOf(string relative)
        {
            string dir = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
            while (true)
            {
                if (_manifests.ContainsKey(dir))
                    return dir;
                if (dir.Length == 0)
                    return "";
                dir = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "";
            }
        }

        internal string FrameworkOf(string relative)
        {
            if (SupabaseFunctionFile.IsMatch(relative))
                return "supabase-edge";

            string ext = Path.GetExtension(relative).ToLowerInvariant();
            if (ext == ".cs") return "aspnet";
            if (ext == ".go") return "go";

            string project = ProjectOf(relative);
            string manifest = _manifests.TryGetValue(project, out string? m) ? m : "";

            if (ext == ".py")
            {
                if (!_pythonFrameworks.TryGetValue(project, out string? python))
                {
                    python = manifest.Contains("fastapi", StringComparison.OrdinalIgnoreCase) ? "fastapi"
                        : manifest.Contains("flask", StringComparison.OrdinalIgnoreCase) ? "flask"
                        : "python";
                    _pythonFrameworks[project] = python;
                }
                return python;
            }

            if (!_nodeFrameworks.TryGetValue(project, out string? node))
            {
                node = manifest.Contains("\"@nestjs/core\"", StringComparison.Ordinal) ? "nestjs"
                    : manifest.Contains("\"next\"", StringComparison.Ordinal) ? "nextjs"
                    : manifest.Contains("\"fastify\"", StringComparison.Ordinal) ? "fastify"
                    : manifest.Contains("\"hono\"", StringComparison.Ordinal) ? "hono"
                    : manifest.Contains("\"koa\"", StringComparison.Ordinal) ? "koa"
                    : manifest.Contains("\"express\"", StringComparison.Ordinal) ? "express"
                    : "node";
                _nodeFrameworks[project] = node;
            }
            return node;
        }

        // ── filene ───────────────────────────────────────────────────────

        /// <summary>Whether the file is JSON as it stands, without the comments or trailing commas the parser allows.</summary>
        private static bool IsPlainJson(string text)
        {
            try
            {
                using JsonDocument _ = JsonDocument.Parse(text);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // En skjult mappe (.claude, .github, .vscode) er verktøy, og .claude/worktrees er hele kopier av repoet.
        private static bool SkipDirectory(string name)
            => name.StartsWith(".", StringComparison.Ordinal)
               || SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)
               || SkippedDirectorySuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

        private static bool IsInteresting(string name)
            => IsEnvFile(name)
               || name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
               || name is "pyproject.toml" or "requirements.txt" or "go.mod" or "launchSettings.json" or "config.toml"
               || name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
               || SourceExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        /// <summary><c>.env</c>, <c>.env.local</c>, <c>.env.example</c> and the like.</summary>
        private static bool IsEnvFile(string name)
            => name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase);

        private string Relative(string path)
        {
            string full = Path.GetFullPath(path);
            string prefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full == _root.TrimEnd(Path.DirectorySeparatorChar)) return "";
            return full.StartsWith(prefix, StringComparison.Ordinal) ? full[prefix.Length..].Replace('\\', '/') : full;
        }
    }

    // ── C#: kontrollere og minimal API ───────────────────────────────────

    /// <summary>Reads ASP.NET Core routes line by line: a controller's [Route] and each [HttpPost], and MapPost with MapGroup.</summary>
    private sealed class CSharpRoutes
    {
        private readonly string _file;
        private readonly Scanner _scanner;
        private readonly Dictionary<string, string> _groups = new(StringComparer.Ordinal);
        private string? _pendingRoute;
        private string _classPrefix = "";
        private string _className = "";
        private bool _inClass;

        public CSharpRoutes(string file, Scanner scanner)
        {
            _file = file;
            _scanner = scanner;
        }

        public void Read(string line, int n)
        {
            Match group = CsMapGroup.Match(line);
            if (group.Success)
            {
                string outer = _groups.TryGetValue(group.Groups["recv"].Value, out string? o) ? o : "";
                _groups[group.Groups["var"].Value] = Combine(outer, group.Groups["path"].Value);
            }

            foreach (Match post in CsMapPost.Matches(line))
            {
                string prefix = _groups.TryGetValue(post.Groups["recv"].Value, out string? p) ? p : "";
                _scanner.AddRoute(_file, n, Combine(prefix, post.Groups["path"].Value), "aspnet");
            }

            Match httpPost = CsHttpPost.Match(line);
            Match route = CsRouteAttribute.Match(line);

            Match cls = CsClass.Match(line);
            if (cls.Success)
            {
                _className = cls.Groups[1].Value;
                _classPrefix = _pendingRoute ?? "";
                _pendingRoute = null;
                _inClass = true;
            }

            if (httpPost.Success && _inClass)
            {
                string template = httpPost.Groups[1].Success ? httpPost.Groups[1].Value
                    : route.Success ? route.Groups[1].Value
                    : _pendingRoute ?? "";
                _pendingRoute = null;
                _scanner.AddRoute(_file, n, Resolve(template), "aspnet");
                return;
            }

            if (route.Success && !cls.Success)
                _pendingRoute = route.Groups[1].Value;
        }

        private string Resolve(string template)
        {
            string controller = _className.EndsWith("Controller", StringComparison.Ordinal) ? _className[..^"Controller".Length] : _className;
            string combined = template.StartsWith("/", StringComparison.Ordinal) || template.StartsWith("~/", StringComparison.Ordinal)
                ? template.TrimStart('~')
                : Combine(_classPrefix, template);
            return combined.Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Reads NestJS routes: a controller's prefix and each @Post under it.</summary>
    private sealed class NestRoutes
    {
        private readonly string _file;
        private readonly Scanner _scanner;
        private readonly string _framework;
        private string? _prefix;

        public NestRoutes(string file, Scanner scanner, string framework)
        {
            _file = file;
            _scanner = scanner;
            _framework = framework;
        }

        public void Read(string line, int n)
        {
            Match controller = NestController.Match(line);
            if (controller.Success)
                _prefix = controller.Groups[1].Value;

            Match post = NestPost.Match(line);
            if (post.Success && _prefix is not null)
                _scanner.AddRoute(_file, n, Combine(_prefix, post.Groups[1].Value), _framework);
        }
    }

    // ── hjelpere ─────────────────────────────────────────────────────────

    /// <summary>A route as advise compares it: leading slash, no trailing one, parameters as {name}.</summary>
    internal static string NormalizeRoute(string route)
    {
        string r = route.Trim();
        r = TemplateParameter.Replace(r, m => "{" + m.Groups[1].Value + "}");
        r = ColonParameter.Replace(r, m => "{" + m.Groups[1].Value + "}");
        r = r.Replace("//", "/", StringComparison.Ordinal);
        if (!r.StartsWith("/", StringComparison.Ordinal)) r = "/" + r;
        if (r.Length > 1) r = r.TrimEnd('/');
        return r;
    }

    /// <summary>Whether two routes are the same, a parameter matching any one segment, letters in any case.</summary>
    internal static bool SameRoute(string a, string b)
    {
        string[] x = NormalizeRoute(a).Split('/'), y = NormalizeRoute(b).Split('/');
        if (x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++)
        {
            bool parameter = x[i].StartsWith("{", StringComparison.Ordinal) || y[i].StartsWith("{", StringComparison.Ordinal);
            if (!parameter && !x[i].Equals(y[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    internal static string Combine(string prefix, string path)
    {
        string p = prefix.Trim().Trim('/'), s = path.Trim().Trim('/');
        return "/" + string.Join("/", new[] { p, s }.Where(x => x.Length > 0));
    }

    /// <summary>Whether a route may be shown: none of its segments looks like a secret.</summary>
    internal static bool Showable(string route)
        => route.Split('/', StringSplitOptions.RemoveEmptyEntries).All(segment =>
            segment.StartsWith("{", StringComparison.Ordinal) || (!CredentialNameRules.LooksLikeASecret(segment) && segment.Length <= 64));

    private static bool IsComment(string line, bool hashComments)
    {
        string t = line.TrimStart();
        return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal)
               || t.StartsWith("* ", StringComparison.Ordinal) || t == "*" || t.StartsWith("*/", StringComparison.Ordinal)
               || (hashComments && t.StartsWith("#", StringComparison.Ordinal));
    }

    private static int? FirstLine(string[] lines, Regex pattern)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (pattern.IsMatch(lines[i]))
                return i + 1;
        }
        return null;
    }

    private static bool LooksLikeAFileName(string value)
        => FileNameEnding.IsMatch(value);

    private static IEnumerable<string> SecretNames(string line)
    {
        foreach (Regex pattern in SecretReads)
        {
            foreach (Match m in pattern.Matches(line))
            {
                string name = m.Groups[1].Value;
                if (name.Length > 0 && CredentialNameRules.Showable(name) is not null)
                    yield return name;
            }
        }
    }

    private static bool LooksLikeAWebhookSecret(string name, bool stripe)
    {
        string upper = name.ToUpperInvariant();
        if (stripe)
            return upper.Contains("WHSEC", StringComparison.Ordinal)
                   || (upper.Contains("WEBHOOK", StringComparison.Ordinal) && (upper.Contains("SECRET", StringComparison.Ordinal) || upper.Contains("KEY", StringComparison.Ordinal)))
                   || (upper.Contains("STRIPE", StringComparison.Ordinal) && upper.Contains("SIGNING", StringComparison.Ordinal));

        if (upper.Contains("STRIPE", StringComparison.Ordinal) || upper.Contains("SERVICE_ROLE", StringComparison.Ordinal)
            || upper.Contains("ANON", StringComparison.Ordinal) || upper.EndsWith("_URL", StringComparison.Ordinal))
            return false;
        return upper.Contains("SECRET", StringComparison.Ordinal) || upper.Contains("TOKEN", StringComparison.Ordinal)
               || upper.Contains("HOOK", StringComparison.Ordinal);
    }

    /// <summary>How well a name fits a webhook's shared secret: lower is better.</summary>
    private static int Rank(string name)
    {
        string upper = name.ToUpperInvariant();
        return upper.Contains("HOOK", StringComparison.Ordinal) ? 0 : upper.Contains("SECRET", StringComparison.Ordinal) ? 1 : 2;
    }

    private static string? StripeCall(string line, string ext)
    {
        if (ext == ".cs")
            return CsStripe.IsMatch(line) ? "Stripe.net's EventUtility.ConstructEvent" : null;
        if (ext == ".py")
            return PyStripe.IsMatch(line) ? "stripe.Webhook.construct_event" : null;
        if (ext == ".go")
            return GoStripe.IsMatch(line) ? "webhook.ConstructEvent" : null;
        Match m = JsStripe.Match(line);
        return m.Success ? (m.Groups[1].Success ? "stripe.webhooks.constructEventAsync" : "stripe.webhooks.constructEvent") : null;
    }

    private static string? RawBodyRead(string line, string ext)
    {
        bool raw = ext switch
        {
            ".cs" => CsRawBody.IsMatch(line),
            ".py" => PyRawBody.IsMatch(line),
            ".go" => GoRawBody.IsMatch(line),
            _ => JsRawBody.IsMatch(line),
        };
        return raw ? "reads the raw request body" : null;
    }

    private static (int Port, bool HardCoded, string What)? PortIn(string line, string ext)
    {
        (Regex Pattern, bool HardCoded)[] patterns = ext switch
        {
            ".cs" => new[] { (CsUseUrls, true) },
            ".py" => new[] { (PyRunPort, true) },
            ".go" => new[] { (GoListen, true) },
            _ => new[] { (JsListen, true), (JsListenObject, true), (JsPortDefault, false) },
        };

        foreach ((Regex pattern, bool hardCoded) in patterns)
        {
            Match m = pattern.Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int port) && port is >= 1 and <= 65535)
                return (port, hardCoded, hardCoded ? $"listens on port {port}" : $"listens on port {port} unless configured otherwise");
        }
        return null;
    }

    private static IEnumerable<string> CheckedHeaders(string line, string ext)
    {
        Regex[] patterns = ext switch
        {
            ".cs" => new[] { CsHeader },
            ".py" => new[] { PyHeader },
            ".go" => new[] { GoHeader },
            _ => new[] { JsHeaderGet, JsHeaderIndex },
        };

        foreach (Regex pattern in patterns)
        {
            foreach (Match m in pattern.Matches(line))
            {
                string header = m.Groups[1].Value;
                if (SecretHeader.IsMatch(header) && !NotASecretHeader.IsMatch(header))
                    yield return header.ToLowerInvariant();
            }
        }
    }

    // ── mønstrene ────────────────────────────────────────────────────────

    private const RegexOptions Compiled = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private const RegexOptions CompiledIgnoreCase = Compiled | RegexOptions.IgnoreCase;

    // Hvert mønster har en tidsgrense: en linje som får et mønster til å gå i ring, hopper fila over (RegexMatchTimeoutException),
    // i stedet for å holde skanningen. Linjer over budsjettets lengde matches ikke i det hele tatt.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex OrSeparator = new(@"\s+or\s+", CompiledIgnoreCase, MatchTimeout);
    private static readonly Regex FunctionName = new(@"^[A-Za-z0-9_-]{1,64}$", Compiled, MatchTimeout);
    private static readonly Regex PlainPath = new(@"^[A-Za-z0-9._/-]+$", Compiled, MatchTimeout);
    private static readonly Regex FileNameEnding = new(
        @"\.(js|mjs|cjs|ts|tsx|jsx|json|cs|py|go|html|css|md|txt|ya?ml|sql|toml|env|lock|config)$", Compiled, MatchTimeout);

    private static readonly Regex TemplateParameter = new(@"\$\{\s*(\w+)\s*\}", Compiled, MatchTimeout);
    private static readonly Regex ColonParameter = new(@"(?<=/):(\w+)", Compiled, MatchTimeout);

    // C#
    private static readonly Regex CsMapPost = new(@"\b(?<recv>\w+)\s*\.\s*MapPost\s*\(\s*@?""(?<path>[^""]*)""", Compiled, MatchTimeout);
    private static readonly Regex CsMapGroup = new(@"\b(?<var>\w+)\s*=\s*(?<recv>\w+)\s*\.\s*MapGroup\s*\(\s*@?""(?<path>[^""]*)""", Compiled, MatchTimeout);
    private static readonly Regex CsRouteAttribute = new(@"\bRoute\s*\(\s*@?""([^""]*)""", Compiled, MatchTimeout);
    private static readonly Regex CsHttpPost = new(@"\[\s*(?:[^\]]*,\s*)?HttpPost\b(?:\s*\(\s*@?""([^""]*)"")?", Compiled, MatchTimeout);
    private static readonly Regex CsClass = new(@"\bclass\s+(\w+)", Compiled, MatchTimeout);
    private static readonly Regex CsStripe = new(@"\bEventUtility\s*\.\s*ConstructEvent\s*\(", Compiled, MatchTimeout);
    private static readonly Regex CsRawBody = new(@"\bRequest\s*\.\s*Body\b|\brequest\s*\.\s*Body\b", Compiled, MatchTimeout);
    private static readonly Regex CsUseUrls = new(@"\bUseUrls\s*\(\s*""[^""]*:(\d{2,5})\b", Compiled, MatchTimeout);
    private static readonly Regex CsHeader = new(@"\bHeaders\s*(?:\[\s*|\.TryGetValue\s*\(\s*)""([\w-]+)""", Compiled, MatchTimeout);
    private static readonly Regex QueueyRegistration = new(@"\b(AddQueueyClient|AddQueueyEdge|AddQueueyEdgeMqttSource|AddQueuey)\s*\(", Compiled, MatchTimeout);
    private static readonly Regex QueueyPackage = new(@"Include\s*=\s*""(Queuey\.(?:Client(?:\.Waas)?|Edge(?:\.Mqtt)?))""", Compiled, MatchTimeout);

    // JavaScript og TypeScript
    private static readonly Regex JsPost = new(@"\b([\w$]+)\s*\.\s*(post)\s*\(\s*(['""`])(/[^'""`]*)\3", Compiled, MatchTimeout);
    private static readonly Regex HttpClientName = new(
        @"^(?:axios|https?|client|request|ky|got|superagent|fetcher|httpClient|apiClient|\$http|supertest)$", CompiledIgnoreCase, MatchTimeout);
    private static readonly Regex BrowserBundler = new(@"""(?:vite|react-scripts|@sveltejs/kit|nuxt|@vue/cli-service)""", Compiled, MatchTimeout);
    private static readonly Regex JsRoutePost = new(@"\.\s*route\s*\(\s*(['""`])(/[^'""`]*)\1\s*\)\s*\.\s*post\s*\(", Compiled, MatchTimeout);
    private static readonly Regex JsMount = new(@"\.\s*use\s*\(\s*(['""`])(/[^'""`]*)\1\s*,\s*([^\n]*)", Compiled, MatchTimeout);
    private static readonly Regex HonoMount = new(@"\.\s*route\s*\(\s*(['""`])(/[^'""`]*)\1\s*,\s*(\w+)", Compiled, MatchTimeout);
    private static readonly Regex NestController = new(@"@Controller\s*\(\s*(?:['""`]([^'""`]*)['""`])?", Compiled, MatchTimeout);
    private static readonly Regex NestPost = new(@"@Post\s*\(\s*(?:['""`]([^'""`]*)['""`])?\s*\)", Compiled, MatchTimeout);
    private static readonly Regex NestGlobalPrefix = new(@"\bsetGlobalPrefix\s*\(\s*['""`]([^'""`]*)['""`]", Compiled, MatchTimeout);
    private static readonly Regex JsStripe = new(@"\.\s*webhooks\s*\.\s*constructEvent(Async)?\s*\(", Compiled, MatchTimeout);
    private static readonly Regex JsRawBody = new(
        @"\bawait\s+(?:\w+\.)*(?:req|request)\s*\.\s*(?:text|arrayBuffer)\s*\(\s*\)|\b(?:express|bodyParser)\s*\.\s*raw\s*\(|\brawBody\b|\bbuffer\s*\(\s*req\s*\)|\bgetRawBody\s*\(|\bbodyParser\s*:\s*false\b",
        Compiled, MatchTimeout);
    private static readonly Regex JsonParser = new(@"\b(?:express|bodyParser)\s*\.\s*json\s*\(", Compiled, MatchTimeout);
    private static readonly Regex JsListen = new(@"\.\s*listen\s*\(\s*(\d{2,5})\b", Compiled, MatchTimeout);
    private static readonly Regex JsListenObject = new(@"\.\s*listen\s*\(\s*\{[^}]*\bport\s*:\s*(\d{2,5})\b", Compiled, MatchTimeout);
    private static readonly Regex JsPortDefault = new(@"\bPORT\b[^\n;]{0,40}?(?:\|\||\?\?)\s*['""]?(\d{2,5})\b", Compiled, MatchTimeout);
    private static readonly Regex JsHeaderGet = new(
        @"(?:\bheaders?\s*\.\s*get|\.\s*header|\breq\s*\.\s*get)\s*\(\s*['""`]([\w-]+)['""`]\s*\)", Compiled, MatchTimeout);
    private static readonly Regex JsHeaderIndex = new(@"\bheaders\s*\[\s*['""`]([\w-]+)['""`]\s*\]", Compiled, MatchTimeout);
    private static readonly Regex NextPostExport = new(@"\bexport\s+(?:async\s+)?function\s+POST\b|\bexport\s+const\s+POST\b|\bexport\s*\{[^}]*\bPOST\b", Compiled, MatchTimeout);
    private static readonly Regex ExportDefault = new(@"\bexport\s+default\b", Compiled, MatchTimeout);
    private static readonly Regex ServeCall = new(@"\b(?:Deno\.)?serve\s*\(", Compiled, MatchTimeout);
    private static readonly Regex NextRouteHandlerFile = new(@"^(?:.*/)?(?:src/)?app/(?<segs>(?:[^/]+/)*)route\.(?:ts|js|mts|mjs)$", Compiled, MatchTimeout);
    private static readonly Regex NextApiFile = new(@"^(?:.*/)?(?:src/)?pages/api/(?<path>.+)\.(?:ts|js|mts|mjs)$", Compiled, MatchTimeout);
    private static readonly Regex SupabaseFunctionFile = new(@"^(?:.*/)?supabase/functions/(?<name>[^/_.][^/]*)/index\.(?:ts|js|mts|mjs)$", Compiled, MatchTimeout);

    // Python
    private static readonly Regex PythonRoute = new(@"@(\w+)\s*\.\s*(post|route)\s*\(\s*['""](/[^'""]*)['""]([^\n]*)", Compiled, MatchTimeout);
    private static readonly Regex PostInMethods = new(@"methods\s*=\s*[\[(][^\])]*['""]POST['""]", CompiledIgnoreCase, MatchTimeout);
    private static readonly Regex PythonPrefix = new(@"\b(?:APIRouter|include_router|Blueprint|register_blueprint)\s*\([^\n]*?\b(?:url_)?prefix\s*=\s*['""](/[^'""]*)['""]", Compiled, MatchTimeout);
    private static readonly Regex PyStripe = new(@"\bWebhook\s*\.\s*construct_event\s*\(", Compiled, MatchTimeout);
    private static readonly Regex PyRawBody = new(@"\brequest\s*\.\s*data\b|\brequest\s*\.\s*get_data\s*\(|\bawait\s+request\s*\.\s*body\s*\(\s*\)", Compiled, MatchTimeout);
    private static readonly Regex PyRunPort = new(@"\b(?:uvicorn\s*\.\s*run|\w+\s*\.\s*run)\s*\([^\n]*\bport\s*=\s*(\d{2,5})\b", Compiled, MatchTimeout);
    private static readonly Regex PyHeader = new(@"\bheaders\s*(?:\.\s*get\s*\(\s*|\[\s*)['""]([\w-]+)['""]", Compiled, MatchTimeout);

    // Go
    private static readonly Regex GoRoute = new(
        @"\b(?<recv>\w+)\s*\.\s*(?:HandleFunc|Handle|POST)\s*\(\s*""(?:POST\s+)?(?<path>/[^""]*)""", Compiled, MatchTimeout);
    private static readonly Regex GoGroup = new(@"(?:\b(?<var>\w+)\s*:?=\s*)?\b(?<recv>\w+)\s*\.\s*Group\s*\(\s*""(?<path>/[^""]*)""", Compiled, MatchTimeout);
    private static readonly Regex GoStripe = new(@"\bwebhook\s*\.\s*ConstructEvent(?:WithOptions)?\s*\(", Compiled, MatchTimeout);
    private static readonly Regex GoRawBody = new(@"\b(?:io|ioutil)\s*\.\s*ReadAll\s*\(\s*[\w.]*\bBody\b", Compiled, MatchTimeout);
    private static readonly Regex GoListen = new(@"\b(?:ListenAndServe(?:TLS)?|Run)\s*\(\s*""[^""]*:(\d{2,5})""", Compiled, MatchTimeout);
    private static readonly Regex GoHeader = new(@"\bHeader\s*\.\s*Get\s*\(\s*""([\w-]+)""", Compiled, MatchTimeout);

    // Navn på hemmeligheter, slik koden leser dem fra miljøet eller konfigurasjonen.
    private static readonly Regex[] SecretReads =
    {
        new(@"\bprocess\.env\.([A-Za-z_][A-Za-z0-9_]*)", Compiled, MatchTimeout),
        new(@"\bprocess\.env\[\s*['""]([A-Za-z_][A-Za-z0-9_]*)['""]\s*\]", Compiled, MatchTimeout),
        new(@"\bDeno\.env\.get\(\s*['""]([A-Za-z_][A-Za-z0-9_]*)['""]", Compiled, MatchTimeout),
        new(@"\bGetEnvironmentVariable\(\s*""([A-Za-z_][A-Za-z0-9_]*)""", Compiled, MatchTimeout),
        new(@"\b(?:Configuration|configuration|config|_config\w*|_configuration)\s*\[\s*""([A-Za-z][\w:.-]*)""\s*\]", Compiled, MatchTimeout),
        new(@"\bGetValue<string>\(\s*""([A-Za-z][\w:.-]*)""", Compiled, MatchTimeout),
        new(@"\bos\.environ(?:\.get)?\s*[\[(]\s*['""]([A-Za-z_][A-Za-z0-9_]*)['""]", Compiled, MatchTimeout),
        new(@"\bos\.getenv\(\s*['""]([A-Za-z_][A-Za-z0-9_]*)['""]", Compiled, MatchTimeout),
        new(@"\bos\.Getenv\(\s*""([A-Za-z_][A-Za-z0-9_]*)""", Compiled, MatchTimeout),
    };

    private static readonly Regex SecretHeader = new(@"secret|token|signature|auth|api[-_]?key|hook", CompiledIgnoreCase, MatchTimeout);
    private static readonly Regex NotASecretHeader = new(@"^(?:stripe-signature|x-queuey-[\w-]*|webhook-id|webhook-timestamp)$", CompiledIgnoreCase, MatchTimeout);
    private static readonly Regex QueueySignature = new(@"\bQueueyDeliveryVerifier\b|x-queuey-signature", CompiledIgnoreCase, MatchTimeout);
    // Uten ordgrenser med vilje: HasProcessedEventAsync og processed_events er det samme mønsteret.
    private static readonly Regex Dedup = new(
        @"(?:processed|handled|seen)_?(?:stripe_?)?(?:webhook_?)?events?|stripe_?event_?ids?|webhook_?events?|idempotency_?keys?",
        CompiledIgnoreCase, MatchTimeout);
    private static readonly Regex StripeEventType = new(@"(?:\bcase\s+|===?\s*|\bEquals\(\s*)['""`]([a-z][a-z_]*(?:\.[a-z][a-z_]*){1,4})['""`]", Compiled, MatchTimeout);
    private static readonly Regex RowEventType = new(@"(?:\bcase\s+|===?\s*|\bEquals\(\s*)['""`](INSERT|UPDATE|DELETE)['""`]", Compiled, MatchTimeout);
    private static readonly Regex OldRecord = new(@"\bold_record\b", Compiled, MatchTimeout);
    private static readonly Regex EnvAssignment = new(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=", Compiled, MatchTimeout);
    private static readonly Regex ScriptPort = new(@"""[\w:-]+""\s*:\s*""[^""]*(?:-p|--port)[ =](\d{2,5})\b", Compiled, MatchTimeout);
    private static readonly Regex HttpLocalPort = new(@"http://[^;""/]*:(\d{2,5})\b", Compiled, MatchTimeout);
    private static readonly Regex TomlSection = new(@"^\[\s*([^\]]+)\s*\]$", Compiled, MatchTimeout);
    private static readonly Regex TomlPort = new(@"^port\s*=\s*(\d{2,5})\b", Compiled, MatchTimeout);
    private static readonly Regex NoVerifyJwt = new(@"^verify_jwt\s*=\s*false\b", Compiled, MatchTimeout);
    private static readonly Regex SupabaseTrigger = new(
        @"create\s+(?:or\s+replace\s+)?trigger\s+""?[\w-]+""?\s+(?:after|before)\s+(?<events>(?:insert|update|delete)(?:\s+or\s+(?:insert|update|delete))*)\s+on\s+(?<table>(?:""?\w+""?\.)?""?\w+""?)[\s\S]{0,600}?""?supabase_functions""?\s*\.\s*""?http_request""?\s*\(\s*'(?<url>[^']*)'",
        CompiledIgnoreCase, MatchTimeout);
}
