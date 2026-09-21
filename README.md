# Quality Lab Service

Part of the **Wonrich Dairy Milk Quality Monitoring and Traceability System** (SE3022 Case Study Project, Group 16).

The Quality Lab Service manages laboratory testing of milk: recording test panels, clearing or holding batches based on results, and publishing lab events to Kafka for the Traceability & QC Dashboard Service.

---

## Tech stack

| Area | Technology |
|---|---|
| API | ASP.NET Core Web API (.NET 10) |
| Database | Azure Database for MySQL – Flexible Server |
| Messaging | Apache Kafka |
| Containerisation | Docker (multi-stage build) |
| Hosting | Azure App Service (Linux, container) |
| Image registry | Azure Container Registry (`wonrichacr`) |

---

## Project structure

```
quality-lab-service/
├── src/
│   └── QualityLab.Api/          # ASP.NET Core Web API
│       ├── Program.cs
│       ├── appsettings.json
│       └── QualityLab.Api.csproj
├── QualityLab.slnx              # Solution file
├── Dockerfile                   # Multi-stage build (SDK → ASP.NET runtime)
├── docker-compose.yml           # Local environment (API + Kafka)
├── .env.example                 # Template for local secrets
├── .dockerignore
├── .gitignore
└── README.md
```

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- Your public IP added to the Azure MySQL firewall (ask the DevOps member)

---

## Run locally

### Option A — Docker Compose (recommended)

Starts the API and a local Kafka broker. The API connects to the remote Azure MySQL database.

1. Create your `.env` file from the template and fill in the connection string (ask the DevOps member):

   ```bash
   cp .env.example .env
   ```

2. Start the environment:

   ```bash
   docker compose up --build -d
   ```

3. Verify:

   ```bash
   curl http://localhost:5003/health    # → Healthy
   ```

| Service | Address |
|---|---|
| API | http://localhost:5003 |
| Health check | http://localhost:5003/health |
| Kafka (from host) | localhost:29092 |
| Kafka (from containers) | kafka:9092 |
| Database | Azure Database for MySQL (remote) |

Useful commands:

```bash
docker compose ps                  # container status
docker compose logs quality-lab    # API logs
docker compose down                # stop everything
```

### Option B — `dotnet run`

Store the connection string in user secrets (kept outside the repository):

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
| `Kafka:BootstrapServers` | `Kafka__BootstrapServers` | Kafka broker address |

Connection string format:

```
Server=<host>.mysql.database.azure.com;Port=3306;Database=quality_lab;User=<user>;Password=<password>;SslMode=Required;
```

> **Never commit secrets.** Use `.env` (git-ignored) for Docker Compose, user secrets for `dotnet run`, App Service settings for Azure, and GitHub Secrets for CI/CD.

---

## Endpoints

| Method | Path | Description |
|---|---|---|
| GET | `/health` | Health check (API and database connectivity) |

Feature endpoints will be documented here as they are implemented.

---

## Deployment

| Item | Value |
|---|---|
| App Service | `wonrich-quality-lab` (Linux, container) |
| URL | `https://wonrich-quality-lab-bvega5acfkgpb5gx.southeastasia-01.azurewebsites.net` |
| Container image | `wonrichacr.azurecr.io/quality-lab:latest` |
| Image pull | System-assigned managed identity with `AcrPull` role |
| Database | Azure Database for MySQL, database `quality_lab` |

### Manual deployment

```bash
az acr login -n wonrichacr
docker build --platform linux/amd64 -t wonrichacr.azurecr.io/quality-lab:latest .
docker push wonrichacr.azurecr.io/quality-lab:latest
az webapp restart -g rg-wonrich -n wonrich-quality-lab
```

> `--platform linux/amd64` is required when building on Apple Silicon Macs.

Automated deployment through GitHub Actions is tracked in SCRUM-109.

---

## Branching and commits

| Branch | Purpose |
|---|---|
| `main` | Production-ready code |
| `develop` | Integration branch |
| `feature/<name>`, `devops/<name>` | Work branches, merged into `develop` via pull request |

- Start commit messages and PR titles with the Jira key, e.g. `SCRUM-108: Add docker-compose local environment`.
- Every pull request needs at least one approving review.
- Use **Squash and merge**.

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
