# Quality Lab Service

Part of the **Wonrich Dairy Milk Quality Monitoring and Traceability System** (SE3022 Case Study Project, Group 16).

The Quality Lab Service manages laboratory testing of milk: recording test panels, clearing or holding batches based on results, and publishing the outcome as Kafka events for the other services.

---

## Tech stack

| Area | Technology |
|---|---|
| API | ASP.NET Core Web API (.NET 10) |
| Database | Azure Database for MySQL – Flexible Server, EF Core (Pomelo) |
| Messaging | Apache Kafka (shared broker from [`wonrich-infra`](../wonrich-infra)) |
| Containerisation | Docker (multi-stage build) |
| Hosting | Azure App Service (Linux, container) |
| Image registry | Azure Container Registry (`wonrichacr`) |

---

## Project structure

```
quality-lab-service/
├── src/
│   └── QualityLab.Api/
│       ├── Data/
│       │   ├── QualityLabDbContext.cs
│       │   └── Migrations/            # EF Core migrations
│       ├── Health/
│       │   └── KafkaHealthCheck.cs    # Broker reachable + topic exists
│       ├── Program.cs
│       ├── appsettings.json
│       └── QualityLab.Api.csproj
├── docs/
│   └── database.md                    # Database, access, migrations, backups
├── QualityLab.slnx
├── Dockerfile                         # Multi-stage build (SDK → ASP.NET runtime)
├── docker-compose.yml                 # Local API container on the shared wonrich-net network
├── .env.example                       # Template for local secrets
├── .dockerignore
├── .gitignore
└── README.md
```

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- The [`wonrich-infra`](../wonrich-infra) repository cloned next to this one
- Your public IP added to the Azure MySQL firewall (ask the DevOps member)

---

## Run locally

### 1. Start the shared infrastructure

Kafka is shared by all services and runs from `wonrich-infra`. Start it first:

```bash
cd ../wonrich-infra
docker compose up -d
```

### 2. Configure secrets

```bash
cd ../quality-lab-service
cp .env.example .env        # then fill in QLS_DB_CONNECTION (ask the DevOps member)
```

### 3. Start the service

```bash
docker compose up --build -d
```

### 4. Verify

```bash
curl -sS http://localhost:5003/health; echo
```

| Service | Address |
|---|---|
| API | http://localhost:5003 |
| Health check | http://localhost:5003/health |
| Kafka UI (from wonrich-infra) | http://localhost:8085 |
| Database | Azure Database for MySQL (remote) |

Useful commands:

```bash
docker compose ps
docker compose logs quality-lab
docker compose down
```

### Running with `dotnet run`

Store the connection string in user secrets (kept outside the repository). Kafka is reached at `localhost:29092`, the default in `appsettings.json`.

```bash
cd src/QualityLab.Api
dotnet user-secrets set "ConnectionStrings:QualityLabDb" '<connection-string>'
dotnet run
```

---

## Configuration

| Setting | Environment variable | Description |
|---|---|---|
| `ConnectionStrings:QualityLabDb` | `ConnectionStrings__QualityLabDb` | MySQL connection string |
| `Kafka:BootstrapServers` | `Kafka__BootstrapServers` | Kafka broker address (`kafka:9092` in Docker, `localhost:29092` outside) |
| `Kafka:Topics:BatchDeterminations` | `Kafka__Topics__BatchDeterminations` | Topic the service publishes batch outcomes to |

> **Never commit secrets.** Use `.env` (git-ignored) for Docker Compose, user secrets for `dotnet run`, App Service settings for Azure, and GitHub Secrets for CI/CD.

See [docs/database.md](docs/database.md) for database details.

---

## Kafka

Topics are defined once in `wonrich-infra` (`kafka/topics.env`); see its `docs/kafka.md` for all services' topics and conventions.

| Topic | Role | Events | Key |
|---|---|---|---|
| `wonrich.quality-lab.batch-determinations.v1` | **Produces** | `BatchCleared`, `BatchFailed` | `batchId` |

- Both outcomes are published to the same topic, so events for one batch stay in order. Each message has an `eventType` field (`BatchCleared` or `BatchFailed`).
- Consuming Processing's stage events (consumer group `quality-lab-stage-events`, dead-letter topic `wonrich.dlq.quality-lab-stage-events.v1`) is defined in `wonrich-infra` and will be configured here when the consumer is implemented.

---

## Endpoints

| Method | Path | Description |
|---|---|---|
| GET | `/health` | Health of the service and its dependencies |

`/health` returns JSON with an overall status and one entry per check:

```json
{
  "status": "Healthy",
  "checks": [
    { "name": "mysql", "status": "Healthy", "description": null },
    { "name": "kafka", "status": "Healthy", "description": "Kafka reachable at kafka:9092; topic 'wonrich.quality-lab.batch-determinations.v1' has 3 partitions." }
  ]
}
```

| Check | On failure | HTTP |
|---|---|---|
| `mysql` | `Unhealthy` (the service cannot work without its database) | 503 |
| `kafka` | `Degraded` (during a broker outage in Azure) | 200 |

Feature endpoints will be documented here as they are implemented.

---

## Database

- Database `quality_lab` on the shared Azure MySQL server, accessed as the scoped user `qls_app`.
- EF Core migrations are applied automatically on startup.
- Details, access rules and backups: [docs/database.md](docs/database.md).

---

## Deployment

| Item | Value |
|---|---|
| App Service | `wonrich-quality-lab` (Linux, container) |
| URL | `https://wonrich-quality-lab-bvega5acfkgpb5gx.southeastasia-01.azurewebsites.net` |
| Container image | `wonrichacr.azurecr.io/quality-lab` (`latest` + commit SHA tags) |
| Image pull | System-assigned managed identity with `AcrPull` role |

Automated build and deployment through GitHub Actions is tracked in SCRUM-109.

### Manual deployment

```bash
az acr login -n wonrichacr
docker build --platform linux/amd64 -t wonrichacr.azurecr.io/quality-lab:latest .
docker push wonrichacr.azurecr.io/quality-lab:latest
az webapp restart -g rg-wonrich -n wonrich-quality-lab
```

> `--platform linux/amd64` is required when building on Apple Silicon Macs.

---

## Branching and commits

| Branch | Purpose |
|---|---|
| `main` | Production-ready code |
| `develop` | Integration branch |
| `feature/<name>`, `devops/<name>` | Work branches, merged into `develop` via pull request |

- Start commit messages and PR titles with the Jira key, e.g. `SCRUM-110: Add Kafka health check`.
- Every pull request needs at least one approving review.
- Use **Squash and merge**.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `network wonrich-net not found` | Shared infrastructure not running | `cd ../wonrich-infra && docker compose up -d` |
| `/health` → kafka `Degraded` | Broker not running or topic missing | Start `wonrich-infra`; check `docker compose logs kafka-init` there |
| `/health` → mysql `Unhealthy` | Database unreachable | Check your IP in the MySQL firewall and `QLS_DB_CONNECTION` in `.env` |
| `Set QLS_DB_CONNECTION in .env` on start | `.env` missing | `cp .env.example .env` and fill it in |
| Port 29092 already allocated | An old Kafka container from this repo is still running | `docker rm -f qls-kafka qls-kafka-init` |

---

## Related work items (Sprint 3)

| Key | Item |
|---|---|
| SCRUM-107 | Provision a remote MySQL database |
| SCRUM-108 | Containerise the service and compose a local development environment |
| SCRUM-109 | CI/CD pipeline to Azure App Service |
| SCRUM-110 | Kafka topics and consumer groups for lab events |
| SCRUM-111 | Prometheus scrape config and Grafana dashboard |
| SCRUM-112 | Dependency and security scanning in CI |
| SCRUM-42 | Unit and integration test stages with code coverage |
| SCRUM-43 | JMeter performance test run |
