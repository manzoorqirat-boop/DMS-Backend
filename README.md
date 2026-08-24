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
| 2 | Numbering service + draft creation | Not started |
| 3 | OnlyOffice/Collabora integration + check-in/out | Not started |
| 4 | Audit wiring + server-side protected-field revalidation | Not started |
| 5 | Handoff to Review/Approval → ERES | Not started (mapping designed) |
| 6 | Template governance | Not decided |

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
- **No audit trail yet** — Phase 4.
