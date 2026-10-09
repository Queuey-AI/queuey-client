using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Queuey.Edge;
using Queuey.Edge.Demo;

// ─────────────────────────────────────────────────────────────────────────
// Queuey Edge demo: a "sensor" publishing one reading per interval.
//
// The entire delivery story in this application is ONE line, in
// SensorPublisher: await queuey.PublishAsync(...). No retries, no buffering,
// no reconnect, no restart recovery — kill Queuey Cloud, pull the network,
// kill THIS process: accepted events survive on disk and drain, in order,
// when the world comes back.
//
// Configuration (environment):
//   QUEUEY_SIGNING_KEY_ID the queue's signing key                 (from `queuey keys mint --queue sensor-readings --write .env`)
//   QUEUEY_SIGNING_SECRET its secret                              (the same command writes both; never shown)
//   QUEUEY_API_KEY        alternative: a publish-only key         (read only without the signing pair)
//   QUEUEY_TENANT         ten_... public id                      (required)
//   QUEUEY_INGRESS_BASE   e.g. http://localhost:5084             (optional; default production)
//   QUEUEY_DEMO_QUEUE     queue display name                     (optional; default "sensor-readings")
//   QUEUEY_DEMO_INTERVAL  seconds between readings               (optional; default 5)
// ─────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Warning);          // keep the stage clear for the dashboard
builder.Logging.AddFilter("Queuey", LogLevel.Information);  // ...but let Edge tell its story

builder.Services.AddQueueyEdge(o =>
{
    // The signing pair, the tenant and the ingress base from the environment — and in Development the pair from .env,
    // where `queuey keys mint --write .env` put it. Validate() at startup says what is missing.
    o.UseEnvironmentVariables();

    o.Source = "edge-demo";
    // A durable path next to the executable — survives process AND machine
    // restarts. (The default would work too; explicit here so the demo can
    // point at the file.)
    o.Storage.Path = Path.Combine(AppContext.BaseDirectory, "queuey-edge", "spool.db");
});

builder.Services.AddHostedService<SensorPublisher>();
builder.Services.AddHostedService<ConsoleDashboard>();

await builder.Build().RunAsync();
