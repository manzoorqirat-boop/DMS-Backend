# DMS-Backend

GxP/pharma controlled-document management system. Handles the document lifecycle —
initiation, review, approval, issuance, distribution, retrieval, revision, obsolescence —
with its own inbuilt electronic-signature module. **DMS does not call ERES/Hastakshar** —
review, approval and the signing audit trail are implemented here.

## Layout

Clean Architecture, mirroring `eres-backend`'s conventions.

| Project | Depends on | Notes |
|---|---|---|
| `Dms.Domain` | — | Entities, enums, pure domain services incl. PBKDF2 password hashing. **No package references, by design.** |
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
| 5 | Review & approval — **inbuilt**, no ERES | **Done** |
| 6 | Template governance | Not decided |

## Phase 5 — review & approval (inbuilt)

A draft is submitted with a **sequential signature route** — the whole route is fixed up
front, because letting a reviewer choose their own approver at signing time would let the two
be the same conversation. Only the lowest-numbered pending step is signable. Submitting locks
the draft, which is what makes the content hash recorded against each signature meaningful.

| Method | Route | Purpose |
|---|---|---|
| `GET`/`POST` | `/api/users` | User master data |
| `POST` | `/api/users/me/change-password` | Self-service only |
| `POST` | `/api/documents/{id}/submit` | Start the route |
| `GET` | `/api/documents/{id}/route` | Route with applied signatures |
| `POST` | `/api/documents/{id}/sign` | Apply a signature |
| `POST` | `/api/documents/{id}/make-effective` | Issue with an effective date |
| `GET` | `/api/my/pending-signatures` | The caller's signing queue |

### How Part 11 is discharged

| Requirement | Where |
|---|---|
| §11.50(a) — printed name, timestamp, meaning shown with the signature | Copied onto `ElectronicSignature` at signing, never resolved live, so a role change doesn't rewrite old approvals |
| §11.70 — signature linked to its record | `ContentHash`: SHA-256 of the exact bytes in front of the signer |
| §11.200(a)(1) — signing credential distinct from session | Password re-entered on every `/sign`; being logged in is not sufficient |
| §11.200(a)(2) — signature used only by its owner | No administrator password reset exists |
| §11.300(d) — unauthorised attempts detected | `SignatureAuthenticationFailed` audited; account locks after 3 failures |

Signatures are append-only through the same three layers as the audit trail: no mutators, the
`SaveChanges` guard, and database triggers.

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

## Configuration & access control

Two things that started as code constants are now master data.

**Numbering patterns.** `DocumentNumberPattern` renders a configurable token string —
`{SITE}-{DEPT}-{TYPE}-{SEQ:0000}` or `SOP/{DEPT}/{YYYY}/{SEQ:000}`. Tokens: `SITE`, `DEPT`,
`TYPE`, `SEQ`, `REV`, `YYYY`, `YY`, `MM`. A `NumberingRule` sets the pattern per document
type, optionally overridden per site; resolution is most-specific-wins with a built-in
default so the system works before anything is configured.

Patterns are validated **when the rule is saved**, not when a document is created — a bad
pattern should be a form error for an administrator, not an outage for an author.
`POST /api/numbering-rules/preview` shows the rendered result first.

A pattern containing a year or month token implies the sequence restarts each period, so
`DocumentNumberSequence` is keyed by `PeriodKey` as well as site/department/type. Changing a
pattern never renumbers documents already issued.

**Role & privilege matrix.** `Permission` is a fixed enum — every value corresponds to a check
in code, so a permission that exists but nothing enforces would be a matrix that lies.
`Role` bundles permissions and is composed by administrators at runtime.
`UserRoleAssignment` grants a role at Global, Site or Department scope, which is what makes
"QA Head at Site A" different from "QA Head at Site B".

`RoleManage` is deliberately **not** scopable: a site-scoped role administrator could grant
themselves `RoleManage` and escalate to anything, so scoping it would be scoping that does
nothing.

**Review routes.** `WorkflowDefinition` declares the review chain per document type, with an
optional per-site override — "QA reviews, then HOD reviews, then Plant Head approves". Steps
name **roles**, not people.

At submission the submitter no longer supplies a route. They call
`GET /api/documents/{id}/route-template`, which returns the fixed chain plus, for each step,
the people who actually hold that role at *this document's* site and department. They nominate
one candidate per step; `ReviewWorkflowService` validates every nomination against that
eligible list, so a submitter can't add a step, drop one, reorder the chain, or name a
friendly approver who was never meant to be in it.

Definitions are immutable while active — steps can only be edited after deactivating, so the
route can't change between a submitter loading the form and posting it. A partial unique index
enforces at most one active definition per (type, site). Documents already in review keep the
route they were submitted under, because the chain is materialised into `SignatureRequest`
rows at submission.

### What is deliberately NOT configurable

In a regulated system, configuration that changes computational behaviour is itself subject to
validation. These stay in code on purpose — an admin-editable version of any of them would
mean a validated *engine* running an unvalidated config:

- Append-only audit and signature tables
- Sequential signing, and that only the named signer may sign a step
- Hash-to-record binding of signatures
- Which document status transitions are legal
- That a number is issued once, gap-free, and never reissued
- That a route must contain at least one approver, and that one person cannot occupy two steps

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
- **Revision cycle is not built.** `Supersede()` and `MakeObsolete()` exist on
  `ControlledDocument`, but nothing yet creates revision *n+1* from an effective document,
  carries its number forward, or supersedes the predecessor when the successor takes effect.
  That's the next lifecycle gap, and it's the one an inspector reaches fastest.
- **Four-digit sequence ceiling** — 9,999 documents per site/department/type before numbers
  grow a fifth digit and stop sorting lexically. Pinned in `DocumentNumberFormat` rather than
  left configurable, since changing it mid-life would make old and new numbers inconsistent.
