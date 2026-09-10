# Evidence to gather before deciding

Collect what applies; note the path for each. Read files rather than guessing
from names. Nothing here has side effects.

## Where does the process run?

| Look for | Tells you |
| --- | --- |
| `Dockerfile`, `docker-compose*.yml` | container; check for `volumes:` on the service that will publish |
| `*.bicep`, `azure.yaml`, `containerapp*.yaml`, `main.tf` | Azure Container Apps / infra — look for `minReplicas: 0`, `scale`, `volumeMounts`, `persistentVolumeClaim` |
| `k8s/`, `helm/`, `deployment.yaml` | Kubernetes — a `PersistentVolumeClaim` mounted at the spool path is durable; `emptyDir` is not |
| `serverless.yml`, `function.json`, `host.json`, `template.yaml` (SAM) | serverless — ephemeral disk, always |
| `*.service` (systemd), `install.sh`, `deploy.sh` with `scp` | a box with a real disk — likely durable |
| `appsettings.*.json` with paths under `/var/lib`, `C:\ProgramData` | the app expects local persistent state |
| `README`, `docs/`, ADRs mentioning "gateway", "edge", "on-prem", "plant", "vehicle", "kiosk", "offline" | the network is not a given |
| `.github/workflows/*.yml` publishing self-contained binaries per RID | targets machines without a runtime — IoT or on-prem |

## Is the network to Queuey a given?

Signals of an unreliable path, in rough order of weight:

- Documentation or code comments about offline operation, reconnect loops,
  "when the link comes back", store-and-forward, buffering to disk.
- Existing retry code around outbound HTTP with long backoffs or a local
  queue file.
- Deployment to a physical site: factory, vessel, vehicle, retail, hospital
  ward, mine, remote sensor.
- A corporate egress proxy with documented drops, or mTLS through an
  appliance.

Absence of all of these in a cloud-deployed service is a reasonable "reliable".

## What reliability mechanism already exists?

### .NET
Read `*.csproj` `PackageReference`s and grep for usage:

| Package | Meaning |
| --- | --- |
| `MassTransit*`, `NServiceBus*`, `Rebus*`, `Wolverine*` | a bus with its own delivery guarantees — publish from a consumer |
| `Microsoft.EntityFrameworkCore` + a table named like `Outbox*`, `IntegrationEvent*` | an outbox — publish from its drainer |
| `Hangfire*`, `Quartz*` with a persistent store | a durable job queue — enqueue the publish |
| `Polly*` on its own | retry in memory only; not a durability mechanism |
| `Azure.Messaging.ServiceBus`, `Confluent.Kafka`, `RabbitMQ.Client` | a broker they already own |
| `MQTTnet` or a running Mosquitto | an MQTT source — see the MQTT override |
| `Queuey.*` already referenced | read how it is used before proposing anything |

Also note `TargetFramework`: `net8.0`+ can host Edge in-process;
`netstandard2.0` / `net48` cannot.

### Node / TypeScript
`package.json` dependencies: `bullmq`, `bull`, `bee-queue`, `agenda`
(persistent job queue); `kafkajs`, `amqplib`, `@azure/service-bus` (broker);
`pg-boss` (Postgres job queue); an `outbox` table in migrations.

### Python
`pyproject.toml` / `requirements*.txt`: `celery` with a durable broker,
`dramatiq`, `rq`, `arq`, `huey` (job queues); `kafka-python`, `confluent-kafka`,
`pika`, `aio-pika` (brokers); `tenacity` alone is in-memory retry only.

### Java / Kotlin
`pom.xml` / `build.gradle`: Spring Cloud Stream, `spring-kafka`,
`spring-rabbit`, Quartz with JDBC store, Axon, an `outbox` entity.

### Go
`go.mod`: `segmentio/kafka-go`, `rabbitmq/amqp091-go`, `hibiken/asynq`,
`riverqueue/river`.

## Direction signals

- **Sending:** outbound HTTP to `ingress.queuey.ai` or `/events/`, use of
  `Queuey.Client`, a config key like `QUEUEY_API_KEY`, or a stated wish to
  "publish", "emit", "push events".
- **Receiving:** a route that reads `X-Queuey-Signature` / `X-Queuey-Timestamp`
  / `X-Queuey-Nonce`, a handler under `/webhooks`, a queue whose destination in
  `queuey.deploy.json` is this service's URL.

## The two questions to ask when evidence is thin

1. Where does this process run, and does a file written before a restart
   exist after it?
2. What happens today if the network is down for an hour?

Their answers place the codebase in the two-by-two; nothing else is strictly
required.
