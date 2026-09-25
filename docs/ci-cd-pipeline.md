# CI/CD pipeline

Quality Lab Service is built, tested and deployed by GitHub Actions (SCRUM-109).

| Workflow | File | Runs on |
|---|---|---|
| CI/CD | `.github/workflows/ci-cd.yml` | Every push to `develop` / `main`, every pull request, manual |
| Rollback | `.github/workflows/rollback.yml` | Manual only |

---

## Environments

The team uses a **single deployed environment, `staging`** (team decision: no separate production environment or database).

| Item | Value |
|---|---|
| GitHub environment | `staging` |
| App Service | `wonrich-quality-lab` (Linux container) in `rg-wonrich` |
| URL | `https://<default-domain>` |
| Image | `wonrichacr.azurecr.io/quality-lab` |
| Deploys from | `develop` |

---

## Pipeline stages

```
push / PR
   │
   ▼
┌───────────────────────────── build ─────────────────────────────┐
│ restore → build → unit tests → migration script → docker build  │
└────────────────────────────────┬────────────────────────────────┘
                                 │ only on push to develop, only if build passed
                                 ▼
┌───────────────────────── deploy-staging ────────────────────────┐
│ tag current image "previous" → build image (latest + SHA)       │
│ → push to ACR → (webhook) App Service pulls → verify /health    │
└─────────────────────────────────────────────────────────────────┘
```

### 1. Build and test (every push and PR)

| Step | Purpose |
|---|---|
| Restore, build | Compile in Release mode |
| Unit tests | `tests/QualityLab.UnitTests`. **A failing test fails the job, and the deploy job does not run.** Results are uploaded as the `test-results` artifact |
| Migration script | `dotnet ef migrations script --idempotent`, uploaded as the `migration-script` artifact. Safe to run against a database at any migration state |
| Docker build | Confirms the image builds before anything is deployed |

The build status appears on every pull request. Branch protection on `develop` and `main` requires the **Build and test** check to pass before merging.

### 2. Deploy to staging (push to `develop` only)

| Step | Purpose |
|---|---|
| Keep current image as `previous` | Records the last successful deployment as the rollback target |
| Build image | Tagged with the commit SHA and `latest`; the SHA is baked in as `GIT_SHA` |
| Push | To Azure Container Registry, using a push token scoped to the `quality-lab` repository |
| App Service update | Pushing `latest` fires the ACR webhook; App Service continuous deployment pulls the image (managed identity with `AcrPull`) and restarts the container |
| Verify | `scripts/verify-health.sh`: waits until `/version` reports the new commit, then requires `/health` not `Unhealthy` and the `mysql` check `Healthy` |

EF Core migrations are applied by the service on startup; the post-deploy database check confirms they succeeded.

Kafka is reported but does not block: Degraded does not fail the deployment, so a broker outage cannot stop a deploy.

---

## Rollback

Rollback restores a previous image without rebuilding.

1. GitHub → **Actions** → **Rollback staging** → **Run workflow**.
2. `image_tag`:
   - `previous`: the deployment before the current one (default)
   - a commit SHA: any earlier deployed commit (see **Container registry → quality-lab → Tags**, or the commit list)
3. The workflow points `latest` at that image and pushes it; the webhook makes App Service pull it. The same `/health` verification then runs against the rolled-back commit.

Check the running version at any time:

```bash
curl https://<default-domain>/version
```

To make a rollback permanent, revert the faulty commit on `develop`; the next deployment then builds from fixed code.

---

## Secrets and configuration

No credentials are stored in workflow files. Registry credentials are **GitHub environment secrets** on `staging`, so only jobs that declare `environment: staging` can read them.

| Name | Type | Scope | Value |
|---|---|---|---|
| `ACR_LOGIN_SERVER` | Secret | `staging` environment | `wonrichacr.azurecr.io` |
| `ACR_USERNAME` | Secret | `staging` environment | Name of the ACR token (`gh-quality-lab-push`) |
| `ACR_PASSWORD` | Secret | `staging` environment | Password generated for that token |
| `APP_HOST` | Variable | `staging` environment | App Service default domain, without `https://` |

### Why an ACR token instead of an Azure service principal

The Azure for Students subscription sits in the university's Microsoft Entra directory, where students cannot register applications, so `az ad sp create-for-rbac` is not permitted. The pipeline therefore never logs in to Azure:

- It authenticates only to the container registry, with a **token scoped to the `quality-lab` repository** (read + write on that repository only; it cannot touch other repositories or any other Azure resource).
- App Service **continuous deployment** is enabled on the main container, so ACR notifies the App Service through a webhook whenever `latest` is pushed.

Application settings (database connection string, Kafka) are App Service settings, not pipeline secrets.

---

## Verification evidence (Definition of Done)

| Item | How it was demonstrated |
|---|---|
| Pipeline runs green | CI/CD run on `develop`: `<link to run>` |
| Failing test blocks deployment | Deliberate failing test pushed; **Build and test** failed and **Deploy to staging** was skipped: `<link to run>`. Reverted in `<commit>` |
| Rollback executed | Rollback to `previous`; verification passed on the earlier commit: `<link to run>` |
