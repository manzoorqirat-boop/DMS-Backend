# Deploying to Railway

This backend was designed for Railway from the start (see the project plan's original stack
note). This is the ordered path to a working deployment — follow it in order, since a couple
of steps produce confusing failures if skipped or reordered.

## Step 0 — generate the migration (on your own machine, before anything else)

**Nothing in this repo has ever been through `dotnet ef migrations add`.** Railway can build
and run the container fine without one, but the database will have no tables, and every
request will fail. This has to happen locally, where you have the .NET SDK and `dotnet-ef`:

```bash
dotnet tool install --global dotnet-ef   # if you don't have it
dotnet ef migrations add InitialCreate \
  --project src/Dms.Infrastructure \
  --startup-project src/Dms.Api
```

**Then open the generated migration file** (`src/Dms.Infrastructure/Persistence/Migrations/
*_InitialCreate.cs`) **and add one line to its `Up` method:**

```csharp
migrationBuilder.Sql(File.ReadAllText(
    Path.Combine(AppContext.BaseDirectory, "Persistence", "Migrations", "AuditImmutability.sql")));
```

EF's model diffing has no way to know about the append-only triggers in
`AuditImmutability.sql` — they're raw SQL, not part of the C# model — so this step is the only
way they actually get created. Skipping it means the audit trail and signature tables are
technically writable by an UPDATE or DELETE statement, which defeats the entire point of them
being append-only. Commit the migration (including this edit) to source control; Railway
builds from what's committed, not from your local working copy.

## Step 1 — create the Railway project, database, and volume

1. Create a new Railway project from this repository.
2. Add a **PostgreSQL** plugin to the project. Railway provisions it and — once you link the
   two services — injects `DATABASE_URL` into the API service automatically. You don't type a
   connection string in anywhere; `DatabaseConnectionStringResolver` reads `DATABASE_URL`
   directly and translates it into the keyword-value format Npgsql expects.
3. Railway should detect `railway.json` at the repo root and build from the Dockerfile
   automatically. If it doesn't, set the build method to Dockerfile explicitly in the
   service's settings.
4. **Add a Railway Volume to the API service, mounted at `/app/storage`.** This is
   configured in Railway's own dashboard (the service's Volumes tab), not in the Dockerfile —
   Railway's builder rejects the Docker `VOLUME` instruction outright (`"docker VOLUME ... is
   not supported, use Railway Volumes"`), which is why the Dockerfile only creates the
   directory and doesn't declare it. `/app/storage` is already the directory the Dockerfile
   creates and hands to the non-root `dms` user, so mounting there needs no further
   permission changes. Skip this step and every uploaded template and every working document
   is gone on the next deploy.

## Step 2 — environment variables

Set these on the **API service** (not the Postgres plugin — Railway sets `DATABASE_URL` on
that side automatically).

| Variable | Required | Notes |
|---|---|---|
| `Jwt__SigningKey` | **Yes** | ≥32 random characters. `openssl rand -base64 32` works. This is the only thing standing between a guessed key and a token that passes every permission check in the system — do not reuse the dev placeholder from `appsettings.Development.json`. |
| `Bootstrap__AdminUserName` | Yes, for first login | Only takes effect while the user table is empty. |
| `Bootstrap__AdminPassword` | Yes, for first login | Same. **Remove both `Bootstrap__*` variables once you've logged in and changed the password** — `BootstrapSeeder` logs a warning to that effect on every startup they're still set. |
| `Deploy__RunMigrationsOnStartup` | For first deploy only | See Step 3. Set to `true` for the first deploy, then back to `false` (or unset). |
| `TemplateStorage__RootPath` | **Yes** | Set to `/app/storage/templates` — a subdirectory under the Railway Volume mounted in Step 1. |
| `DocumentStorage__RootPath` | **Yes** | Set to `/app/storage/documents`, same volume. |
| `OpenApi__Enabled` | Recommended: `true` initially | Defaults to on only in the `Development` environment, which Railway doesn't set by default — so Swagger is off unless you turn it on. Useful for verifying the deployment; consider turning it back off once a frontend is the only real consumer, since an unauthenticated map of every endpoint is a reconnaissance aid on a regulated system. |
| `Cors__AllowedOrigins__0` | Once a frontend exists | The frontend's deployed origin (e.g. `https://your-app.vercel.app`). Leave unset for a backend-only deploy — CORS only matters for browser-based cross-origin calls, and Swagger served from the API's own origin doesn't need it. |
| `DocumentServer__Url` | No | Leave unset. This deployment doesn't include OnlyOffice Document Server — in-browser editing stays disabled until that's stood up separately (it's a heavier, stateful service that deserves its own Railway service or its own decision to host elsewhere). |
| `Scheduler__Enabled` | No | Leave `false` (the default). The reminder sweep would run, but `LoggingNotificationSender` only logs — no mail transport exists yet, so there's nothing to gain from turning it on before that's addressed. |

Everything else (`Jwt__TokenMinutes`, `RateLimiting__*`, `Signing__*`) has a sensible default
and doesn't need setting for a first deployment.

## Step 3 — apply the migration to the live database

Three ways to do this. In order of preference:

**Recommended: the `migrate` GitHub Action** (`.github/workflows/migrate.yml`). Manually
triggered (`workflow_dispatch`), not automatic on push — a schema change against production
should be a deliberate act, not a side effect of merging code, the same reasoning applied
everywhere else in this codebase to anything irreversible (see `RetentionService`,
`DispositionAction`).

Set up once:
1. In the repo's **Settings → Environments**, create an environment named `production`.
   Optionally add required reviewers here — doing so means `apply` runs won't execute until
   someone approves them, which is the actual enforcement mechanism; the workflow file alone
   can't require a human, only GitHub's environment protection rules can.
2. Add `DATABASE_URL` as a secret **on that environment specifically** (not a repository-wide
   secret) — get the value from Railway's Postgres plugin dashboard, same connection string
   the API itself reads.

To use it: **Actions → migrate → Run workflow**, choose `plan` first. This generates the exact
SQL the migration would execute (via `dotnet ef migrations script --idempotent`) without
touching the database, prints it in the log, and uploads it as a downloadable artifact. Read
it. Then run again with `apply` to actually execute it. Every run's summary records who
triggered it and which commit it ran against — the attributable record this whole approach is
for.

**Manual alternative: run it from your own machine.** Get the Postgres plugin's public
connection details from Railway's dashboard (or `railway variables` via the CLI):

```bash
DATABASE_URL="postgres://user:pass@host:port/railway" \
  dotnet ef database update --project src/Dms.Infrastructure --startup-project src/Dms.Api
```

Still leaves an attributable record (you, in your own shell history), just not one anyone else
can see without asking you — the GitHub Action's real advantage is that the record is visible
to the whole team by default, not that the underlying command is any different.

**Convenience alternative: `Deploy__RunMigrationsOnStartup=true`.** The API applies any
pending migration itself the moment it starts. Simplest for a genuine first deploy, but read
`StartupMigrator`'s own comments before leaving it on: it's a single-instance convenience.
The moment this API runs as more than one instance, multiple containers could race to apply
the same migration on boot, and nothing in EF Core's migration history table protects against
that. Turn it back off after the first successful deploy.

## Step 4 — deploy and verify

1. Deploy (push to the connected branch, or `railway up` from the CLI).
2. Check `GET /health/ready` — it checks the database, the blob store, and (if configured)
   the document server, and reports `Degraded` rather than `Unhealthy` for the document
   server specifically, since editing is one feature among many and shouldn't take the whole
   instance out of rotation.
3. If `OpenApi__Enabled=true`, open `/swagger` and try `POST /api/auth/login` with the
   bootstrap credentials.
4. Change that password (`POST /api/users/me/change-password`), then remove
   `Bootstrap__AdminUserName` / `Bootstrap__AdminPassword` from the service's variables.

## What this deployment does *not* include

- **OnlyOffice Document Server** — in-browser editing stays off. `docker-compose.yml` shows
  the full three-service local shape (API + Postgres + OnlyOffice) if that's wanted later;
  each would need its own Railway service, or the document server could be hosted elsewhere
  entirely with `DocumentServer__Url` pointed at it.
- **A real mail transport** — reminders queue but nothing sends. See
  `LoggingNotificationSender` in the main README's known gaps.
- **The frontend.** This is the API only. Once the frontend is deployed somewhere (Railway
  can host a static site too, or Vercel/Netlify are the more typical fit for a Vite app),
  come back to `Cors__AllowedOrigins__0` and point it there.
