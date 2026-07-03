using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Examples;

/// <summary>Each method demonstrates one capability of the SDK, printing what it does and the result.</summary>
public static class Examples
{
    public static async Task Onboarding(IQueueyService queuey)
    {
        Header("Onboarding — create a tenant + a queue");
        TenantResult tenant = await queuey.Management.CreateTenantAsync("Example Integrations", asProducer: true, withDefaultQueue: true);
        Step($"tenant {tenant.PublicId} ({tenant.Kind})");
        QueueResult queue = await queuey.Management.CreateQueueAsync(tenant.PublicId!, "example-orders");
        Step($"queue  {queue.PublicId} ({queue.DisplayName})");
        Note("In a real integration hub you'd copy the tenant's API key into that service's config, then sync + publish.");
    }

    public static async Task Sync(IQueueyService queuey)
    {
        Header("Sync models — apply streams + packages (idempotent; safe on every deploy)");
        foreach (StreamPlan p in queuey.Plan())
            Step($"plan: {p.Name}  packages=[{string.Join(", ", p.Packages)}]  schema={p.HasPayloadSchema}");

        SyncResult r = await queuey.SyncModelsAsync();
        foreach (StreamApplyResult s in r.Applied) Step($"stream {s.Name}: {s.PublicId} ({s.Status})");
        foreach (PackageApplyResult pk in r.Packages) Step($"package {pk.Name}: {pk.PublicId} (+{pk.AssignedStreams} stream)");
        Note($"{r.Succeeded} stream(s), {r.Packages.Count} package(s). Re-run — it converges, no duplicates.");
    }

    public static async Task Publish(IQueueyService queuey)
    {
        Header("Publish an event");
        PublishResult r = await queuey.PushEventAsync(
            stream: "order-events",
            eventType: "order.created",
            key: "cust_42",
            data: new OrderCreated { OrderId = "ord_1", Total = 4200 });
        Step($"published {r.EventId} → {r.QueuePublicId} (mode={r.Mode}, replayed={r.Replayed})");
        Note("eventType + key are extracted because SyncModels configured the queue's context headers.");
    }

    public static async Task Packages(IQueueyService queuey)
    {
        Header("Packages — create / update / assign / archive");
        PackageApplyResult pkg = await queuey.ApplyPackageAsync("enterprise", "Enterprise tier");
        Step($"created {pkg.PublicId}");
        await queuey.UpdatePackageAsync(pkg.PublicId!, description: "Enterprise tier (updated)");
        Step("updated description");

        SyncResult sync = await queuey.SyncModelsAsync();
        string? cat = sync.Applied.FirstOrDefault(s => s.Name == "order-events")?.PublicId;
        if (cat is not null)
        {
            await queuey.AssignStreamToPackageAsync(pkg.PublicId!, cat);
            Step($"assigned stream {cat}");
        }

        await queuey.ArchivePackageAsync(pkg.PublicId!);
        Step("archived (its streams stop being visible to partners)");
    }

    public static async Task Integrations(IQueueyService queuey)
    {
        Header("Integrations — invite a partner → grant a package → activate a group key");
        IntegrationResult integ = await queuey.Integrations.InviteAsync("partner@example.com");
        Step($"invited {integ.PublicId} ({integ.Status})");

        PackageApplyResult pkg = await queuey.ApplyPackageAsync("standard");
        await queuey.Integrations.GrantPackageAsync(integ.PublicId!, pkg.PublicId!);
        Step($"granted package {pkg.PublicId} (the access boundary)");

        ActivationResult act = await queuey.Integrations.ActivateAsync(integ.PublicId!, "cust_42");
        Step($"activated group key → {act.PublicId} ({act.Status})");
        Note("Delivery needs BOTH gates (grant + active activation) AND the integration's own subscription.");
    }

    public static async Task Observability(IQueueyService queuey, ExampleConfig config)
    {
        Header("Observability — queue metrics + issues");
        QueueResult q = await queuey.Management.CreateQueueAsync(config.Tenant!, "metrics-demo");
        QueueMetricsSnapshot snap = await queuey.Management.GetQueueMetricsSnapshotAsync(q.PublicId!);
        Step($"{q.PublicId}: received={snap.Received} delivered={snap.Delivered} failed={snap.Failed} success={snap.SuccessRate:P0}");

        IssueListPage issues = await queuey.Management.ListIssuesAsync(config.Tenant!, new IssueQuery { Limit = 5 });
        Step($"{issues.Items.Count} issue(s)" + (issues.NextCursor is null ? "" : " (more available via NextCursor)"));
        foreach (IssueSummary i in issues.Items) Step($"  {i.Severity} {i.Status} {i.PublicId} — {i.Title}");
    }

    public static async Task FullWalkthrough(IQueueyService queuey, ExampleConfig config)
    {
        Header("FULL WALKTHROUGH (sync → publish → integrations → observability)");
        await Sync(queuey);
        await Publish(queuey);
        await Integrations(queuey);
        await Observability(queuey, config);
    }

    private static void Header(string title) { Console.WriteLine(); Console.WriteLine($"== {title} =="); }
    private static void Step(string text) => Console.WriteLine($"  ✓ {text}");
    private static void Note(string text) => Console.WriteLine($"  · {text}");
}
