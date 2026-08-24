# DMS-Backend

GxP/pharma controlled-document management system. Handles the document lifecycle —
initiation, review, approval, issuance, distribution, retrieval, revision, obsolescence —
and delegates e-signature and signing audit trail to **ERES/Hastakshar** over HTTP as a
separate service.

## Layout

Clean Architecture, mirroring `eres-backend`'s conventions.

| Project | Depends on | Notes |
|---|---|---|
| `Dms.Domain` | — | Entities, enums, pure domain services. **No package references, by design.** |
| `Dms.Application` | Domain | Use-case services, DTOs, and the abstractions Infrastructure implements. **No package references either** — persistence and storage sit behind interfaces so this layer never sees EF Core. |
| `Dms.Infrastructure` | Application | EF Core / Npgsql, repositories, blob store, DI wiring. |
| `Dms.Api` | Infrastructure | Minimal-API endpoints, HTTP error mapping, current-user resolution. |

Conventions carried over from ERES: UUIDv7 primary keys assigned at construction, enums
persisted as name-strings rather than ordinals, snake_case database naming applied by a
local `ModelBuilder` convention rather than a package, central package management,
`TreatWarningsAsErrors`.

## Build status

| # | Phase | Status |
|---|---|---|
| 1 | Template registry + upload/validation | **Done** — Domain, Application, Infrastructure, and API |
| 2 | Numbering service + draft creation | **Done** |
| 3 | OnlyOffice/Collabora integration + check-in/out | Not started |
| 4 | Audit wiring + server-side protected-field revalidation | **Done** |
| 5 | Handoff to Review/Approval → ERES | Not started (mapping designed) |
| 6 | Template governance | Not decided |

## Phase 4 — what it does

Every state-changing operation records an `AuditEvent` in the **same transaction** as the
change it describes — an audit entry written separately can record an approval that then
failed to persist, or miss one that succeeded, and both are worse than no trail because
they're wrong rather than absent.

`GET /api/audit?entityId=&entityType=&limit=` reads it. There is deliberately no write
endpoint.

**Immutability is enforced in three layers**, because application-level immutability alone is
not immutability — anyone with the connection string can still rewrite history:

1. `AuditEvent` has no mutators, no setters, and no delete method anywhere in the codebase.
2. `DmsDbContext.SaveChanges` throws if any audit entry is marked Modified or Deleted —
   catching reflection, a stray `ExecuteUpdate`, or someone adding a setter later.
3. `Persistence/Migrations/AuditImmutability.sql` installs BEFORE UPDATE/DELETE triggers that
   reject the operation at the database. **Apply this via `migrationBuilder.Sql(...)` in the
   migration that creates `dms.audit_events`** — it is not applied automatically. Note the
   trailing comments there about `TRUNCATE` and table ownership; a role that owns the table
   can drop the triggers, so migrations must run as a different role from the application.

`DocxProtectionVerifier` re-checks a saved working copy: is protection still enforced, are
the content controls still present, and do the system-populated fields still hold the values
the server wrote. A lock enforced only by the editor is a lock enforced by the client.
`POST /api/documents/{id}/verify` runs it and records the outcome either way — a trail that
only shows failures can't demonstrate that checking happens at all.

## Phase 2 — what it does

An author creates a controlled document by picking a site, department and type. The service
allocates the next sequence number for that combination, composes a document number
(`MNK-QA-SOP-0001`), fetches the Active template, fills its content controls with the
system-populated metadata via `DocxMetadataWriter`, stores the resulting working copy, and
writes the register row.

Numbering is **gap-free**. Allocation is a single `INSERT ... ON CONFLICT DO UPDATE ...
RETURNING` that takes a row lock held to the end of the transaction, and the register insert
shares that transaction — so a failure anywhere after allocation returns the number rather
than burning it. Failures inside the transaction throw `DraftAbortedException` rather than
returning a failed `Result`, because a normal return would commit.

| Method | Route | Purpose |
|---|---|---|
| `GET`/`POST` | `/api/sites` | Site master data |
| `GET`/`POST` | `/api/departments?siteId=` | Department master data, scoped per site |
| `GET` | `/api/documents?siteId=&departmentId=&documentTypeId=` | The master register |
| `POST` | `/api/documents` | Create a draft from the Active template |
| `GET` | `/api/documents/{id}` | One document |
| `POST` | `/api/documents/{id}/withdraw` | Abandon a draft; number stays burned, not reused |
| `GET` | `/api/documents/{id}/working-copy` | Download for inspection — **not** the authoring path |

## Phase 1 — what it does

An admin registers a `.docx` template against a document type. The upload is checked by
`DocxTemplateValidator` for the two invariants the authoring flow depends on: every
system-populated metadata field exists as a content control, and document protection is
genuinely enforced with an editable body range carved out. A template that fails is stored
and inspectable but can never be activated, so a malformed template can't silently become
the thing every new document of that type is cloned from.

### Endpoints

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/health/live`, `/health/ready` | Liveness; readiness includes a DB connectivity probe |
| `GET` | `/api/document-types?includeInactive=` | List document types |
| `POST` | `/api/document-types` | Create one (`{ "code": "SOP", "name": "Standard Operating Procedure" }`) |
| `POST` | `/api/document-types/{id}/deactivate` | Hide from pickers; existing documents untouched |
| `POST` | `/api/document-types/{id}/reactivate` | Reverse the above |
| `GET` | `/api/templates?documentTypeId=` | List registered templates |
| `GET` | `/api/templates/{id}` | One template, including `validationIssues` |
| `POST` | `/api/templates` | Register — `multipart/form-data`: `documentTypeId`, `file`, optional `name` |
| `POST` | `/api/templates/{id}/activate` | Promote a validated template; retires the incumbent |
| `POST` | `/api/templates/{id}/retire` | Retire outright |
| `GET` | `/api/templates/{id}/file` | Download the stored `.docx` for inspection |

Registration returns **201 even when structural validation failed** — the registration
succeeded and produced a record to inspect. Read `status` and `validationIssues` to see
whether it's activatable.

### Concurrency

Two invariants are enforced by database indexes, not just by service logic, because
read-then-write across two concurrent admins is not atomic:

- `ux_document_templates_type_version` — one row per (document type, version).
- `ux_document_templates_one_active_per_type` — a **partial** unique index filtered on
  `status = 'Active'`. This is what makes the loser of an activation race fail loudly rather
  than leaving a document type with two live templates.

Both are translated back into `409 Conflict` with a distinguishable `code`.

## Running locally

```bash
dotnet restore
dotnet list package --outdated      # package versions in Directory.Packages.props are UNVERIFIED
dotnet ef migrations add InitialCreate -p src/Dms.Infrastructure -s src/Dms.Api
dotnet ef database update -p src/Dms.Infrastructure -s src/Dms.Api
dotnet run --project src/Dms.Api
```

## Known gaps

- **No authentication.** `ICurrentUser` resolves from the authenticated principal, which
  doesn't exist yet, so every write returns `400 actor_unknown` outside Development. That is
  the deliberate default for a Part 11 system — no attributable identity, no record. A
  development-only impersonation setting (`Development:ImpersonateUser`, gated on
  `IHostEnvironment.IsDevelopment()`) exists so Phase 1 can be exercised meanwhile.
- **No CORS.** Waiting on a decided frontend origin.
- **Template blobs are on local disk.** `ITemplateFileStore` is the seam; the disk
  implementation needs a mounted persistent volume, or replacement with an object store,
  before it's anything other than a dev store.
- **No tests.** `DocxTemplateValidator` is pure and I/O-free specifically so it can be
  covered against a folder of good and bad sample `.docx` files without a database.
- **Only Draft-stage transitions exist** on `ControlledDocument`. Review and approval are
  ERES's to drive (Phase 5), so the states between `InReview` and `Approved` are declared in
  the enum but have no transition methods — writing speculative ones would mean rewriting
  them once real envelope callbacks exist.
- **Four-digit sequence ceiling** — 9,999 documents per site/department/type before numbers
  grow a fifth digit and stop sorting lexically. Pinned in `DocumentNumberFormat` rather than
  left configurable, since changing it mid-life would make old and new numbers inconsistent.
