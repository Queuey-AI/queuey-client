<Query Kind="Program">
  <NuGetReference Version="0.1.0-preview.5" Prerelease="true">Queuey.Client.Waas</NuGetReference>
  <NuGetReference>Microsoft.Extensions.DependencyInjection</NuGetReference>
  <Namespace>Microsoft.Extensions.DependencyInjection</Namespace>
  <Namespace>Queuey.Client</Namespace>
  <Namespace>Queuey.Client.Waas</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// Queuey onboarding in LINQPad — decorate, sync, publish.
//
// Prereqs:
//  1. In LINQPad: F4 → "Add NuGet" → gear/Settings → add package source =
//     /Users/Kenneth/Repos/queuey-client/artifacts   (then tick "Show prerelease")
//  2. A Queuey backend running locally (Queuey.Api on :5223, Queuey.Ingress on :5084)
//     and a tenant + license-wide API key.

async Task Main()
{
    var services = new ServiceCollection();

    services.AddQueuey(
        o =>
        {
            o.ApiBaseAddress     = new Uri("http://localhost:5223"); // Queuey.Api
            o.IngressBaseAddress = new Uri("http://localhost:5084"); // Queuey.Ingress
            o.TenantPublicId  = "ten_dhz_lILIgoEB";
            o.ApiKey          = "qak_your.key";
            o.LicensePublicId = "lic_5Gtf0EdUqphS";
        },
        b => b.AddStream<Order>().AddStream<Customer>());

    using var sp = services.BuildServiceProvider();
    var queuey = sp.GetRequiredService<IQueueyService>();

	// What would sync do? (network-free)
	queuey.Plan().Dump("Plan");

	// On deploy: create/converge the stream(s) + their packages + memberships in Queuey.
	var sync = await queuey.SyncModelsAsync();
	sync.Applied.Dump("SyncModels — streams");
	sync.Packages.Dump("SyncModels — packages");   // "standard" + "premium", each with the stream assigned

	// Create a package explicitly (idempotent) — e.g. one with a description, independent of a model.
	var pkg = await queuey.ApplyPackageAsync("enterprise", "Enterprise tier");
	pkg.Dump("ApplyPackage");

    // On save: publish an event to the stream.
    var published = await queuey.PushEventAsync(
        stream: "order-events",
        eventType: "created",
        key: "cust_42",
        data: new Order { Id = "ord_1", Total = 4200 });
    published.Dump("PushEvent");

	var publishedC = await queuey.PushEventAsync(
	stream: "customer-events",
	eventType: "created",
	key: "cust_42",
	data: new Customer { Id = "ord_1", Name = "TestNavn" });
	published.Dump("PushEvent");

	// ── Partner enablement (integration partner, NOT the platform partner-relations) ──
	// Uncomment to run the full distribution chain: invite → grant a package → activate a group key.
	// Delivery also needs the integration's own subscription + an active activation (two gates).
	//
	// var integration = await queuey.Integrations.InviteAsync("partner@acme.io");
	// var premiumId   = sync.Packages.First(p => p.Name == "premium").PublicId!;
	// await queuey.Integrations.GrantPackageAsync(integration.PublicId!, premiumId);          // access boundary
	// var activation  = await queuey.Integrations.ActivateAsync(integration.PublicId!, "cust_42"); // routing gate
	// activation.Dump("Activate");
}

// Packages = which partner package(s) this stream is published in (a stream can be in several).
// On sync each package is created (idempotent) and the stream assigned to it.
[QueueyModel("order-events",
    EventTypes = new[] { "created", "updated" },
    Packages = new[] { "standard", "premium" })]
public class Order
{
    public string Id { get; set; } = "";
    public decimal Total { get; set; }
}

[QueueyModel("customer-events",
	EventTypes = new[] { "created", "updated" },
	Packages = new[] { "standard", "premium" })]
public class Customer
{
	public string Id { get; set; } = "";
	public string Name { get; set; }
}