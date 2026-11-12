# Queuey.Server.Hosting

Runs a self-hosted Queuey on this machine for local development, and gives your app what Queuey.Client needs to use it.

```csharp
using Queuey.Server.Hosting;

QueueyServerConnection queuey = await QueueyServer.EnsureRunningAsync(new QueueyServerOptions("1.0.0"));

foreach (var (name, value) in queuey.ToEnvironment())
    Environment.SetEnvironmentVariable(name, value);   // QUEUEY_API_BASE, QUEUEY_INGRESS_BASE, QUEUEY_API_KEY, …
```

It is the installation `queuey server` makes and runs (the [Queuey CLI](https://www.nuget.org/packages/Queuey.Cli)), with the
same folder (`~/.queuey/server`, or `QUEUEY_SERVER_DIR`) and the same defaults:

- **The first call** copies the Compose package out of Queuey's image for the version you pin, starts it with Docker
  Compose and waits until it is healthy. It creates you in the bundled Keycloak (the password is in `developer.json` in the
  folder), your account and workspace, and a key for that workspace that may publish and administer its queues and
  streams.
- **Later calls** start it if it is stopped and return the same addresses and key. They never change the version: upgrading
  is `queuey server upgrade --version <v>`, which takes a backup first.
- Everything listens on 127.0.0.1. An app in a container gets the addresses as `host.docker.internal` with
  `ToEnvironment(fromContainer: true)`. Docker Desktop forwards that; a Linux host does not yet.

It needs Docker with Compose v2. It builds nothing, and nothing runs on restore or build: only a call to
`EnsureRunningAsync` starts anything. It contains no Queuey server code.

For an Aspire AppHost, use [Aspire.Hosting.Queuey](https://www.nuget.org/packages/Aspire.Hosting.Queuey).

The package is MIT. The images it runs are Queuey's, under Queuey's own terms; see LICENSE.
