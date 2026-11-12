# Aspire.Hosting.Queuey

A self-hosted Queuey in your Aspire AppHost, for local development.

```csharp
var queuey = builder.AddQueuey("queuey", "1.0.0");

builder.AddProject<Projects.Orders>("orders")
    .WithReference(queuey)
    .WaitFor(queuey);
```

`WithReference` gives the app what Queuey.Client reads: `QUEUEY_API_BASE`, `QUEUEY_INGRESS_BASE`, `QUEUEY_API_KEY`,
`QUEUEY_TENANT` and `QUEUEY_LICENSE`. With `new QueueyOptions().UseEnvironmentVariables()` (or `services.AddQueuey(o =>
o.UseEnvironmentVariables())`) the app publishes and declares its queues without any other setup. A container gets the
addresses as `host.docker.internal`.

When the AppHost runs, the resource makes sure the local installation runs, the one `queuey server` uses
(`~/.queuey/server`), and makes it the first time: the Compose package from Queuey's image for the version you pin, you in
the bundled Keycloak, your account, workspace and a key for it. The dashboard links to the console, the API and the
ingress. The installation is not one of the AppHost's containers: it keeps running, with its data, when the AppHost stops,
and `queuey server status`, `logs`, `stop` and `upgrade` work on it.

`AddQueuey(name, version, options => …)` sets another folder, other ports or the developer's email.

It needs Docker with Compose v2. Nothing starts on restore, build or publish, and the resource is not in the manifest. It
contains no Queuey server code. The package is MIT; the images it runs are Queuey's, under Queuey's own terms (see
LICENSE).
