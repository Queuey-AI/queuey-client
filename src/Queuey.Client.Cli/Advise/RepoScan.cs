using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Queuey.Client.Cli.Advise;

/// <summary>One thing we found, and where — so a recommendation can show its work.</summary>
/// <param name="What">The finding, in the reader's words: "MassTransit", "a persistent volume".</param>
/// <param name="Where">Repo-relative path it was found in.</param>
public sealed record Evidence(string What, string Where)
{
    public override string ToString() => $"{What} ({Where})";
}

/// <summary>What the repository says about itself. Facts only; the judgement lives in <see cref="Recommendation"/>.</summary>
public sealed record RepoFacts
{
    public IReadOnlyList<string> Ecosystems { get; init; } = Array.Empty<string>();

    /// <summary>Somewhere events already survive a crash: an outbox, a bus, a job queue.</summary>
    public IReadOnlyList<Evidence> Durability { get; init; } = Array.Empty<Evidence>();

    /// <summary>Storage that survives a restart, which is what Edge's spool needs.</summary>
    public IReadOnlyList<Evidence> DurableDisk { get; init; } = Array.Empty<Evidence>();

    /// <summary>Hosting that throws local disk away: scale-to-zero, emptyDir, functions.</summary>
    public IReadOnlyList<Evidence> EphemeralHosting { get; init; } = Array.Empty<Evidence>();

    /// <summary>Code that takes webhooks IN, which is the other half of the flow.</summary>
    public IReadOnlyList<Evidence> Receiving { get; init; } = Array.Empty<Evidence>();

    /// <summary>Queuey is already here in some form.</summary>
    public IReadOnlyList<Evidence> QueueyAlready { get; init; } = Array.Empty<Evidence>();

    /// <summary>
    /// Where code runs on a server: edge and serverless functions, route
    /// handlers, an HTTP server. The only places an ingress key can live.
    /// </summary>
    public IReadOnlyList<Evidence> ServerSide { get; init; } = Array.Empty<Evidence>();

    /// <summary>A browser bundle is built here, and everything compiled into one is public.</summary>
    public IReadOnlyList<Evidence> BrowserApp { get; init; } = Array.Empty<Evidence>();

    public bool IsDotNet => Ecosystems.Contains("dotnet");
    public bool HasDurability => Durability.Count > 0;
    public bool HasDurableDisk => DurableDisk.Count > 0;
    public bool IsEphemeral => EphemeralHosting.Count > 0 && DurableDisk.Count == 0;
    public bool Receives => Receiving.Count > 0;
    public bool HasServerSide => ServerSide.Count > 0;
    public bool IsBrowserApp => BrowserApp.Count > 0;
}

/// <summary>
/// Reads a repository and reports what it found, with the file that says so.
///
/// Two of the three questions a recommendation needs can be answered from the
/// code: does durability already exist somewhere, and does this thing run on
/// storage that survives a restart. The third — whether the network can be
/// trusted — is not in the repository, and is asked rather than guessed.
///
/// Everything here is a signal, not a verdict. The evidence travels with the
/// finding so a human can see what it was read from and say "no, not that".
/// </summary>
public static class RepoScan
{
    private const int MaxFilesScanned = 4000;

    /// <summary>Packages that mean "events already survive a crash here".</summary>
    private static readonly (string Token, string Name)[] DurabilityPackages =
    {
        ("MassTransit", "MassTransit"),
        ("NServiceBus", "NServiceBus"),
        ("Rebus", "Rebus"),
        ("WolverineFx", "Wolverine"),
        ("Paramore.Brighter", "Brighter"),
        ("Hangfire", "Hangfire"),
        ("Quartz", "Quartz"),
        ("Coravel", "Coravel"),
        ("Azure.Messaging.ServiceBus", "Azure Service Bus"),
        ("RabbitMQ.Client", "RabbitMQ"),
        ("Confluent.Kafka", "Kafka"),
        ("\"bullmq\"", "BullMQ"),
        ("\"bull\"", "Bull"),
        ("\"agenda\"", "Agenda"),
        ("\"bee-queue\"", "Bee-Queue"),
        ("\"kafkajs\"", "KafkaJS"),
        ("\"amqplib\"", "amqplib"),
        ("celery", "Celery"),
        ("dramatiq", "Dramatiq"),
        ("\"rq\"", "RQ"),
    };

    /// <summary>
    /// Dependencies that mean a browser bundle is built here. Next.js is on the
    /// list even though it also runs on a server: its NEXT_PUBLIC_ variables are
    /// compiled into the bundle, which is exactly the mistake worth warning about.
    /// </summary>
    private static readonly (string Token, string Name)[] BrowserBundlers =
    {
        ("\"vite\"", "Vite"),
        ("\"react-scripts\"", "Create React App"),
        ("\"next\"", "Next.js"),
        ("\"nuxt\"", "Nuxt"),
        ("\"@sveltejs/kit\"", "SvelteKit"),
    };

    /// <summary>Dependencies that mean an HTTP server runs this code.</summary>
    private static readonly (string Token, string Name)[] HttpServers =
    {
        ("\"express\"", "Express"),
        ("\"fastify\"", "Fastify"),
        ("\"hono\"", "Hono"),
        ("\"koa\"", "Koa"),
        ("\"@nestjs/core\"", "NestJS"),
    };

    private static readonly string[] ProjectFiles =
    {
        "*.csproj", "package.json", "pyproject.toml", "requirements.txt", "go.mod",
    };

    public static RepoFacts Scan(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A repository path is required.", nameof(root));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"No such directory: {root}");

        var files = EnumerateInterestingFiles(root).Take(MaxFilesScanned).ToList();

        var ecosystems = new SortedSet<string>(StringComparer.Ordinal);
        var durability = new List<Evidence>();
        var durableDisk = new List<Evidence>();
        var ephemeral = new List<Evidence>();
        var receiving = new List<Evidence>();
        var queueyAlready = new List<Evidence>();
        var serverSide = new List<Evidence>();
        var browserApp = new List<Evidence>();

        foreach (var file in files)
        {
            var relative = Relative(root, file);
            var name = Path.GetFileName(file);
            var text = ReadTextOrEmpty(file);
            if (text.Length == 0) continue;

            DetectEcosystem(name, ecosystems);
            DetectQueuey(name, text, relative, queueyAlready, IsManifest(name));
            DetectDurability(name, text, relative, durability, IsManifest(name));
            DetectHosting(name, text, relative, durableDisk, ephemeral);
            DetectReceiving(text, relative, receiving);
            DetectWhereCodeRuns(name, text, relative, serverSide, browserApp);
        }

        return new RepoFacts
        {
            Ecosystems = ecosystems.ToArray(),
            Durability = Dedupe(durability),
            DurableDisk = Dedupe(durableDisk),
            EphemeralHosting = Dedupe(ephemeral),
            Receiving = Dedupe(receiving),
            QueueyAlready = Dedupe(queueyAlready),
            ServerSide = Dedupe(serverSide),
            BrowserApp = Dedupe(browserApp),
        };
    }

    /// <summary>
    /// Where a publish call can go in a JavaScript repository. The question is
    /// not academic: the call carries the ingress key, and a key in a browser
    /// bundle is readable by anyone who loads the page. So the advice has to
    /// name the server-side place — and say plainly when there is none.
    ///
    /// Function directories are recognised by where they sit, because their
    /// route is the directory, not a string in the code.
    /// </summary>
    private static void DetectWhereCodeRuns(
        string name, string text, string relative, List<Evidence> serverSide, List<Evidence> browserApp)
    {
        if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (token, display) in BrowserBundlers)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                    browserApp.Add(new Evidence(display, relative));
            }

            foreach (var (token, display) in HttpServers)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                    serverSide.Add(new Evidence(display, relative));
            }

            return;
        }

        if (relative.StartsWith("supabase/functions/", StringComparison.Ordinal))
            serverSide.Add(new Evidence("Supabase Edge Functions", "supabase/functions/"));
        else if (relative.StartsWith("netlify/functions/", StringComparison.Ordinal))
            serverSide.Add(new Evidence("Netlify Functions", "netlify/functions/"));
        else if (relative.StartsWith("functions/", StringComparison.Ordinal))
            serverSide.Add(new Evidence("serverless functions", "functions/"));
        else if (relative.StartsWith("api/", StringComparison.Ordinal))
            serverSide.Add(new Evidence("API functions", "api/"));
        else if (NextRouteHandler.IsMatch(relative))
            serverSide.Add(new Evidence("Next.js route handlers", relative));
        else if (NextApiRoute.IsMatch(relative))
            serverSide.Add(new Evidence("Next.js API routes", relative));
    }

    /// <summary>A Next.js App Router handler: <c>app/**/route.ts</c>, optionally under <c>src/</c>.</summary>
    private static readonly System.Text.RegularExpressions.Regex NextRouteHandler = new(
        @"^(src/)?app/(.+/)?route\.(ts|js)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A Next.js Pages Router API route: <c>pages/api/**</c>, optionally under <c>src/</c>.</summary>
    private static readonly System.Text.RegularExpressions.Regex NextApiRoute = new(
        @"^(src/)?pages/api/",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void DetectEcosystem(string name, ISet<string> ecosystems)
    {
        if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) ecosystems.Add("dotnet");
        else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase)) ecosystems.Add("node");
        else if (name is "pyproject.toml" or "requirements.txt") ecosystems.Add("python");
        else if (name == "go.mod") ecosystems.Add("go");
    }

    private static void DetectQueuey(string name, string text, string relative, List<Evidence> into, bool isManifest)
    {
        if (name is "queuey.json" or "queuey.deploy.json")
            into.Add(new Evidence(name, relative));

        // Only a manifest counts. Source and docs MENTION package names all the
        // time — a page describing Queuey.Edge is not a repository using it.
        if (!isManifest) return;

        if (text.Contains("Queuey.Edge", StringComparison.Ordinal))
            into.Add(new Evidence("Queuey.Edge is a dependency", relative));
        else if (text.Contains("Queuey.Client", StringComparison.Ordinal))
            into.Add(new Evidence("Queuey.Client is a dependency", relative));
    }

    /// <summary>A file that DECLARES dependencies, as opposed to one that mentions them.</summary>
    private static bool IsManifest(string name) =>
        name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
        || name is "pyproject.toml" or "requirements.txt" or "go.mod";

    private static void DetectDurability(string name, string text, string relative, List<Evidence> into, bool isManifest)
    {
        // A package counts when it is DEPENDED ON. Source that names MassTransit
        // in a comment, or docs describing it, is not this repository using it.
        if (isManifest)
        {
            foreach (var (token, display) in DurabilityPackages)
            {
                if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                    into.Add(new Evidence(display, relative));
            }
        }

        // An outbox is the pattern itself, whatever library is around it — but
        // the WORD is not the pattern. Prose that mentions an outbox (a comment,
        // a help text, these very lines) is not an outbox, so require a file
        // named for one or a declaration of one.
        var declaresOutbox =
            name.Contains("outbox", StringComparison.OrdinalIgnoreCase)
            || OutboxDeclaration.IsMatch(text);

        if (declaresOutbox)
            into.Add(new Evidence("an outbox", relative));
    }

    private static void DetectHosting(
        string name, string text, string relative, List<Evidence> durableDisk, List<Evidence> ephemeral)
    {
        if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) && text.Contains("VOLUME", StringComparison.Ordinal))
            durableDisk.Add(new Evidence("a VOLUME", relative));

        if (name.EndsWith(".service", StringComparison.OrdinalIgnoreCase))
            durableDisk.Add(new Evidence("a systemd unit, so this runs on a machine with its own disk", relative));

        if (IsYaml(name) || IsCompose(name))
        {
            if (text.Contains("persistentVolumeClaim", StringComparison.Ordinal) || text.Contains("PersistentVolumeClaim", StringComparison.Ordinal))
                durableDisk.Add(new Evidence("a persistent volume claim", relative));

            // emptyDir LOOKS like storage and is thrown away with the pod. That
            // confusion is exactly what makes a naive "is there a volume?" wrong.
            if (text.Contains("emptyDir", StringComparison.Ordinal))
                ephemeral.Add(new Evidence("emptyDir, which does not survive the pod", relative));

            if (IsCompose(name) && text.Contains("volumes:", StringComparison.Ordinal))
                durableDisk.Add(new Evidence("a compose volume", relative));
        }

        if (name is "host.json")
            ephemeral.Add(new Evidence("Azure Functions", relative));
        if (name is "serverless.yml" or "serverless.yaml")
            ephemeral.Add(new Evidence("Serverless Framework", relative));
        if (name is "template.yaml" && text.Contains("AWS::Serverless", StringComparison.Ordinal))
            ephemeral.Add(new Evidence("AWS SAM", relative));

        if ((name.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tf", StringComparison.OrdinalIgnoreCase))
            && (text.Contains("minReplicas: 0", StringComparison.Ordinal)
                || text.Contains("minReplicas = 0", StringComparison.Ordinal)
                || text.Contains("min_replicas = 0", StringComparison.Ordinal)))
        {
            ephemeral.Add(new Evidence("scale-to-zero (minReplicas 0)", relative));
        }
    }

    private static void DetectReceiving(string text, string relative, List<Evidence> into)
    {
        // A route literal, not the word. "/webhook" inside a quote is somebody
        // serving that path; the same letters in prose are not.
        if (WebhookRoute.IsMatch(text))
            into.Add(new Evidence("an endpoint that takes webhooks", relative));

        if (text.Contains("X-Queuey-Signature", StringComparison.OrdinalIgnoreCase))
            into.Add(new Evidence("Queuey signature verification", relative));
    }

    /// <summary>A declaration of an outbox, rather than a mention of the idea.</summary>
    private static readonly System.Text.RegularExpressions.Regex OutboxDeclaration = new(
        @"(class|record|interface|struct)\s+\w*Outbox|DbSet<\s*\w*Outbox|ToTable\(\s*""outbox|CREATE\s+TABLE\s+\W?outbox",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A quoted route that serves webhooks. The literal has to END as a route
    /// does — at its closing quote, a query or a template placeholder — because
    /// a file name is a quoted path too: a site whose article image is called
    /// "/…-before-webhook.webp" does not take webhooks. That repository was the
    /// one the first version of this pattern got wrong.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex WebhookRoute = new(
        @"[""'`]/[\w/:{}$.-]*webhook[\w/:{}$-]*[""'`?]",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static IReadOnlyList<Evidence> Dedupe(List<Evidence> found) =>
        found
            .GroupBy(e => e.What, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.What, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsYaml(string name) =>
        name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompose(string name) =>
        name.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase) || name.Equals("compose.yaml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The files worth opening: project manifests, infrastructure, and source.
    /// Build output and dependencies are skipped — a package in node_modules is
    /// not a statement about this repository.
    /// </summary>
    private static IEnumerable<string> EnumerateInterestingFiles(string root)
    {
        var skipped = new[] { "node_modules", "bin", "obj", ".git", "dist", "build", "venv", ".venv", "__pycache__", "vendor", ".next" };
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] entries;
            try { entries = Directory.GetFiles(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in entries)
            {
                if (IsInteresting(Path.GetFileName(file))) yield return file;
            }

            string[] subdirectories;
            try { subdirectories = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var sub in subdirectories)
            {
                var name = Path.GetFileName(sub);
                if (name.Length > 0 && skipped.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                stack.Push(sub);
            }
        }
    }

    private static bool IsInteresting(string name)
    {
        if (ProjectFiles.Any(pattern => pattern.StartsWith("*", StringComparison.Ordinal)
                ? name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
                : name.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (name is "Dockerfile" or "host.json" or "queuey.json" or "queuey.deploy.json"
            or "serverless.yml" or "serverless.yaml" or "template.yaml") return true;

        return name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".go", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tf", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".service", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadTextOrEmpty(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 512 * 1024) return string.Empty; // a generated blob says nothing useful
            return File.ReadAllText(path);
        }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    private static string Relative(string root, string file)
    {
        var full = Path.GetFullPath(file);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal) ? full[prefix.Length..].Replace('\\', '/') : full;
    }
}
