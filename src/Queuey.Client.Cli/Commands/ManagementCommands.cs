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
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "as-producer", "with-default-queue", "json", "help", "h" };

    public static async Task<int> RunAsync(string[] args)
    {
        ArgMap map = ArgMap.Parse(args, Flags);
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string? name = map.Get("name") ?? map.FirstPositional;
        if (string.IsNullOrWhiteSpace(name)) { Console.Error.WriteLine("create-tenant requires --name <display>."); return ExitCodes.Usage; }

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
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "json", "help", "h" };

    public static async Task<int> RunAsync(string[] args)
    {
        ArgMap map = ArgMap.Parse(args, Flags);
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string? tenant = map.Get("tenant");
        string? name = map.Get("name") ?? map.FirstPositional;
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("create-queue requires --tenant <ten_…> and --name <display>.");
            return ExitCodes.Usage;
        }

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
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "json", "help", "h" };

    public static async Task<int> RunAsync(string[] args)
    {
        ArgMap map = ArgMap.Parse(args, Flags);
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string? queue = map.FirstPositional ?? map.Get("queue");
        if (string.IsNullOrWhiteSpace(queue)) { Console.Error.WriteLine("metrics requires <que_…>."); return ExitCodes.Usage; }

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
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "json", "help", "h" };

    public static async Task<int> RunAsync(string[] args)
    {
        ArgMap map = ArgMap.Parse(args, Flags);
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        string? tenant = map.FirstPositional ?? map.Get("tenant");
        if (string.IsNullOrWhiteSpace(tenant)) { Console.Error.WriteLine("issues requires <ten_…>."); return ExitCodes.Usage; }

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
