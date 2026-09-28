# Quality Lab Service – Database

This document describes the database used by the Quality Lab Service: where it is hosted, how to connect, how access is controlled, how schema migrations work, and how backups are handled.

---

## 1. Overview

| Item | Value |
|---|---|
| Engine | MySQL 8.0 |
| Hosting | Azure Database for MySQL – Flexible Server |
| Server | `wonrichmysql.mysql.database.azure.com` |
| Region | Southeast asia |
| Database | `quality_lab` |
| Character set / collation | `utf8mb4` / `utf8mb4_0900_ai_ci` |
| Application user | `qls_app` |
| ORM | Entity Framework Core with the Pomelo MySQL provider |

The server is shared by the Wonrich microservices. Each service has **its own database** on the server (`mcc_intake`, `wonrich_processing`, `quality_lab`, …) so that each service can evolve its schema and deploy independently.

**Environment decision:** the team uses a **single remote database** for both local development and the deployed App Service. There is no separate staging or production database.

---

## 2. Access control

The service connects as `qls_app`, a user whose privileges are limited to the `quality_lab` database.

```sql
CREATE USER 'qls_app'@'%' IDENTIFIED BY '<password>';
GRANT ALL PRIVILEGES ON quality_lab.* TO 'qls_app'@'%';
FLUSH PRIVILEGES;
```

Verification (run as `qls_app`):

```sql
SHOW GRANTS;               -- only quality_lab.* is listed
USE mcc_intake;            -- Access denied
USE wonrich_processing;    -- Access denied
```

The server administrator login is **not** used by the application.

### Network access

| Rule | Purpose |
|---|---|
| Allow public access from Azure services | Lets the App Service connect |
| Individual client IP rules | Lets each developer connect from their machine |

To add your IP: MySQL server → **Networking** → **Add current client IP address** → **Save**. Home IP addresses change; if you get a connection timeout, check this first.

All connections require SSL (`SslMode=Required`).

---

## 3. Connection settings

Connection string format:

```
Server=mcc-db.mysql.database.azure.com;Port=3306;Database=quality_lab;User=qls_app;Password=<password>;SslMode=Required;
```

The application reads it from the configuration key `ConnectionStrings:QualityLabDb`. The value is supplied differently in each environment:

| Environment | Where the value lives | Key |
|---|---|---|
| Docker Compose (local) | `.env` file (git-ignored) | `QLS_DB_CONNECTION` |
| `dotnet run` (local) | .NET user secrets | `ConnectionStrings:QualityLabDb` |
| Azure App Service | App Service → Environment variables | `ConnectionStrings__QualityLabDb` |
| GitHub Actions | Repository secret | `QLS_DB_CONNECTION` |

Local setup:

```bash
# Docker Compose
cp .env.example .env        # then fill in QLS_DB_CONNECTION

# dotnet run
cd src/QualityLab.Api
dotnet user-secrets set "ConnectionStrings:QualityLabDb" '<connection-string>'
```

> **Never commit connection strings or passwords.** Share credentials with teammates privately, not through the repository or Jira.

If the connection string is missing, the service fails at startup with a message naming the missing setting.

---

## 4. Schema migrations

Migrations are managed with Entity Framework Core and stored in `src/QualityLab.Api/Data/Migrations`.

### Applying migrations

Migrations are applied **automatically when the service starts** (`Database.Migrate()` in `Program.cs`). No manual step is needed after deployment. Applied migrations are recorded in the `__EFMigrationsHistory` table.

### Creating a migration

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet ef migrations add <MigrationName> \
  --project src/QualityLab.Api --output-dir Data/Migrations
```

Requires the EF Core CLI: `dotnet tool install --global dotnet-ef`.

### Rules for a shared database

Because local development and the deployed service use the same database, a migration is applied to the shared database as soon as anyone runs the service with it.

- Create migrations only on a feature branch.
- Get the migration reviewed in a pull request **before** running the service with it.
- Never edit or delete a migration that has already been applied; add a new migration instead.
- Coordinate with the team before running destructive migrations (dropping tables or columns).

### Verifying

```sql
USE quality_lab;
SHOW TABLES;
SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory;
```

---

## 5. Backups and restore

Azure Database for MySQL – Flexible Server takes **automatic backups** of the whole server.

| Item | Value |
|---|---|
| Backup type | Automatic, managed by Azure |
| Retention period | `7` days (see server → **Backup and restore**) |
| Restore method | Point-in-time restore to a new server |
| Scope | Entire server (all service databases) |

### Restoring

1. MySQL server → **Backup and restore** → **Restore**.
2. Choose the restore point (latest or a specific time).
3. Enter a name for the new server and confirm.
4. After the restore completes, point the connection string at the new server, or copy the needed data back with `mysqldump`.

Restoring creates a **new** server; the original is not overwritten.

### Manual backup (optional)

Before risky changes, take a logical backup of just this service's database:

```bash
mysqldump -h <server-name>.mysql.database.azure.com -u qls_app -p \
  --ssl-mode=REQUIRED --single-transaction quality_lab > quality_lab_$(date +%F).sql
```

Do not commit dump files to the repository.

---

## 6. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Connection timeout | Your IP is not in the firewall | Add your IP under **Networking** |
| `Access denied for user 'qls_app'` | Wrong password or user | Check the connection string |
| `/health` returns `Unhealthy` | Database unreachable | Check firewall, connection string and server status |
| Service fails at startup | Missing connection string or failed migration | Check logs: `docker compose logs quality-lab` or App Service **Log stream** |
| Server not responding | Server stopped to save credit | Start it: MySQL server → **Overview** → **Start** |

---

## 7. Ownership

| Item | Owner |
|---|---|
| MySQL server (Azure subscription) | `<account owner>` |
| `quality_lab` database and `qls_app` user | Quality Lab Service DevOps |
| Migrations | Quality Lab Service developer |
