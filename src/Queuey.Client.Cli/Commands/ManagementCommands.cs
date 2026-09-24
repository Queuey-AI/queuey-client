using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

internal static class CreateTenantCommand
{
    internal static readonly CommandOptions Options = new(
        "create-tenant", flags: new[] { "as-producer", "with-default-queue", "json" }, values: new[] { "name" }, positionals: 1);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string? name = map.Get("name") ?? map.FirstPositional;
        if (string.IsNullOrWhiteSpace(name)) return CliErrors.Usage(map, "missing_argument", "create-tenant requires --name <display>.");

        using ServiceProvider sp = CliHost.BuildProvider(CliHost.Resolve(map));
        var svc = sp.GetRequiredService<IQueueyService>();
        TenantResult t = await svc.Management.CreateTenantAsync(name!, map.Has("as-producer"), map.Has("with-default-queue"));

        if (map.Has("json")) Console.WriteLine(JsonSerializer.Serialize(t, CliHost.JsonOut));
        else Console.WriteLine($"Created tenant {t.PublicId} ({t.DisplayName}, kind={t.Kind})");
        return ExitCodes.Success;
    }
}

internal static class CreateQueueCommand
{
    internal static readonly CommandOptions Options = new("create-queue", flags: new[] { "json" }, values: new[] { "name" }, positionals: 1);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string? tenant = map.Get("tenant");
        string? name = map.Get("name") ?? map.FirstPositional;
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(name))
            return CliErrors.Usage(map, "missing_argument", "create-queue requires --tenant <ten_…> and --name <display>.");

        using ServiceProvider sp = CliHost.BuildProvider(CliHost.Resolve(map));
        var svc = sp.GetRequiredService<IQueueyService>();
        QueueResult q = await svc.Management.CreateQueueAsync(tenant!, name!);

        if (map.Has("json")) Console.WriteLine(JsonSerializer.Serialize(q, CliHost.JsonOut));
        else Console.WriteLine($"Created queue {q.PublicId} ({q.DisplayName}) under {q.TenantPublicId}");
        return ExitCodes.Success;
    }
}

internal static class MetricsCommand
{
    internal static readonly CommandOptions Options = new("metrics", flags: new[] { "json" }, values: new[] { "queue" }, positionals: 1);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string? queue = map.FirstPositional ?? map.Get("queue");
        if (string.IsNullOrWhiteSpace(queue)) return CliErrors.Usage(map, "missing_argument", "metrics requires <que_…>.");

        using ServiceProvider sp = CliHost.BuildProvider(CliHost.Resolve(map));
        var svc = sp.GetRequiredService<IQueueyService>();
        QueueMetricsSnapshot s = await svc.Management.GetQueueMetricsSnapshotAsync(queue!);

        if (map.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(s, CliHost.JsonOut));
        else
            Console.WriteLine($"{queue}: received={s.Received} delivered={s.Delivered} failed={s.Failed} " +
                              $"success={s.SuccessRate:P1} p95={(s.P95LatencyMs?.ToString() ?? "-")}ms depth={s.DepthSample}");
        return ExitCodes.Success;
    }
}

internal static class IssuesCommand
{
    internal static readonly CommandOptions Options = new(
        "issues", flags: new[] { "json" }, values: new[] { "status", "severity", "queue", "limit", "cursor" }, positionals: 1);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string? tenant = map.FirstPositional ?? map.Get("tenant");
        if (string.IsNullOrWhiteSpace(tenant)) return CliErrors.Usage(map, "missing_argument", "issues requires <ten_…>.");

        var query = new IssueQuery
        {
            Status = ParseEnum<IssueStatus>(map.Get("status")),
            Severity = ParseEnum<IssueSeverity>(map.Get("severity")),
            QueuePublicId = map.Get("queue"),
            Limit = int.TryParse(map.Get("limit"), out int l) ? l : (int?)null,
            Cursor = map.Get("cursor"),
        };

        using ServiceProvider sp = CliHost.BuildProvider(CliHost.Resolve(map));
        var svc = sp.GetRequiredService<IQueueyService>();
        IssueListPage page = await svc.Management.ListIssuesAsync(tenant!, query);

        if (map.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(page, CliHost.JsonOut));
            return ExitCodes.Success;
        }

        foreach (IssueSummary i in page.Items)
            Console.WriteLine($"  {i.Severity,-8} {i.Status,-8} {i.PublicId}  {i.Title}");
        Console.WriteLine($"{page.Items.Count} issue(s){(page.NextCursor is null ? "" : $" — more: --cursor {page.NextCursor}")}");
        return ExitCodes.Success;
    }

    private static T? ParseEnum<T>(string? value) where T : struct
        => Enum.TryParse<T>(value, ignoreCase: true, out T v) ? v : (T?)null;
}
