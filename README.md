# Compliance Monitor

A .NET 10 Web API that classifies whether an action **COMPLIES** with, **DEVIATES** from, or is **UNCLEAR** against a guideline, using Hugging Face `facebook/bart-large-mnli` (zero-shot NLI) plus two explicit application policies.
Every successful analysis is stored with an audit trail of how it was decided, and exposed through `/history` and `/summary`. A console client exercises all three endpoints.

All four cases from the brief pass against the real model (verified 2026-10-04):

| # | Action → guideline | Result | Decided by |
|---|---|---|---|
| 1 | Closed ticket and sent confirmation email → confirmation email required | COMPLIES (0.98) | model |
| 2 | Closed ticket without sending confirmation email → same | DEVIATES (0.92) | model |
| 3 | Rebooted the server and checked logs → reboot *weekly*, review logs | DEVIATES | policy: no evidence of the weekly frequency |
| 4 | Skipped torque confirmation → "No guidelines exist for this case." | UNCLEAR | policy: no applicable guideline |

## Quick start

**Prerequisites:** .NET SDK 10.0.401 or a later 10.0 feature band (pinned in `global.json`), and a Hugging Face account.

**1. Create a token.** Create a **fine-grained** token with the **"Make calls to Inference Providers"** permission.
[This link](https://huggingface.co/settings/tokens/new?ownUserPermissions=inference.serverless.write&tokenType=fineGrained) opens the form with both selected (from the [Inference Providers docs](https://huggingface.co/docs/inference-providers/index#authentication)).

**2. Store it** with user-secrets. Never put it in `appsettings*.json`; a test fails if you do.

```bash
git clone https://github.com/sergeychopchyts-glitch/compliance-monitor.git && cd compliance-monitor
dotnet user-secrets set HuggingFace:ApiToken <your-token> --project src/ComplianceMonitor.Api
```

Outside Development, set `HuggingFace__ApiToken` in the environment, or better, the platform's secret manager. Without a token the API refuses to start, with a message naming the key.

**3. Run the API** on http://localhost:5080. The SQLite database is created and migrated on first start (`Database:MigrateOnStartup`, on by default for local use).

```bash
dotnet run --project src/ComplianceMonitor.Api
```

API docs in Development: http://localhost:5080/scalar (it redirects to `/scalar/v1`), and the raw document at `/openapi/v1.json`.
`src/ComplianceMonitor.Api/ComplianceMonitor.Api.http` has every request ready to send.

**4. Run the client demo** in a second terminal: the four brief cases, then history and summary.

```bash
dotnet run --project src/ComplianceMonitor.Client
```
```
Brief cases
  Case 1  expected COMPLIES  actual COMPLIES  (0.98, MODEL_CLASSIFICATION)  PASS
  Case 2  expected DEVIATES  actual DEVIATES  (0.92, MODEL_CLASSIFICATION)  PASS
  Case 3  expected DEVIATES  actual DEVIATES  (—, MISSING_TEMPORAL_EVIDENCE)  PASS
  Case 4  expected UNCLEAR   actual UNCLEAR   (—, NO_APPLICABLE_GUIDELINE)  PASS
4/4 cases passed.
```

Other commands: `analyze --action "…" --guideline "…"`, `run-cases`, `history [--limit N] [--result COMPLIES|DEVIATES|UNCLEAR]`, `summary` and `--help`.
The base URL comes from `--base-url`, else `COMPLIANCE_API_URL`, else `http://localhost:5080`.
Exit codes: `0` ok · `1` a brief case failed · `2` API unreachable or timed out · `3` API returned an error (including 429) · `64` bad usage · `130` Ctrl+C.

## API

JSON uses camelCase, with enums as uppercase strings. Errors are RFC 7807 `application/problem+json`.

**`POST /analyze`**: classify and store.

```bash
curl -s -X POST localhost:5080/analyze -H 'Content-Type: application/json' \
  -d '{"action":"Rebooted the server and checked logs","guideline":"Servers must be rebooted weekly and logs reviewed after restart"}'
```
```json
{"id":23,"action":"Rebooted the server and checked logs","guideline":"Servers must be rebooted weekly and logs reviewed after restart","result":"DEVIATES","confidence":null,"decisionSource":"RULE","decisionReason":"MISSING_TEMPORAL_EVIDENCE","timestamp":"2026-10-04T05:14:39Z"}
```

- **Fields returned:**
  - `confidence` is the model's score for the result, rounded to 2 decimals. It's `null` when a policy decided; there is no meaningful "confidence in UNCLEAR".
  - `decisionSource` is `MODEL` or `RULE`.
  - `decisionReason` is `MODEL_CLASSIFICATION`, `NO_APPLICABLE_GUIDELINE`, `MISSING_TEMPORAL_EVIDENCE` or `INSUFFICIENT_MODEL_CONFIDENCE`.
- **Validation:** both fields are required, not blank, and at most 2000 characters (.NET 10 `AddValidation` with DataAnnotations). Together they must also fit the model's 1024-token input, about 950 bytes of text. Hugging Face silently truncates longer input, which can flip the answer.

**`GET /history?limit=50&offset=0&result=DEVIATES`**: newest first. `limit` is 1–200 (default 50), `offset` ≥ 0, `result` optional.

```json
[{"id":23,"action":"Rebooted the server and checked logs","guideline":"Servers must be rebooted weekly and logs reviewed after restart","result":"DEVIATES","confidence":null,"decisionSource":"RULE","decisionReason":"MISSING_TEMPORAL_EVIDENCE","timestamp":"2026-10-04T05:14:39Z"}]
```

**`GET /summary`**: all three keys are always present. Cached for 30 s; every new analysis invalidates the cache.

```json
{"total":23,"byResult":{"COMPLIES":10,"DEVIATES":8,"UNCLEAR":5}}
```

**Health:** `GET /health/live` (the process answers) and `GET /health/ready` (database reachable and migrated; never calls the model).

**Failures:** when classification fails, nothing is stored and there's no fallback label.

| Cause | Status |
|---|---|
| Invalid input, or too long for the model | 400, with field errors |
| Too many concurrent analyses | 429 |
| Model rejected our credentials or request, or answered with something unusable | 502 |
| Model unavailable or rate-limited after retries, or our circuit breaker is open | 503, with `Retry-After` when known |
| Model credits exhausted | 503, with its own `type` |
| Model timeout (10 s per attempt, 30 s total) | 504 |
| Anything else | 500, generic detail, no stack trace |

## How a decision is made

1. **No-applicable-guideline policy.** If the whole guideline states that no guideline exists ("No guidelines exist for this case."), the result is UNCLEAR and the model isn't called. A guideline that merely mentions the phrase ("If no guideline exists for a station, escalate…") goes to the model.
2. **The model.** One production prompt, `combined-three-label-v1`: premise `"Action: … Guideline: …"`, with candidate labels *complies with* / *violates* / *is unrelated to* the guideline, mapped to COMPLIES / DEVIATES / UNCLEAR **by label text, never by position**.
3. **Confidence threshold.** A top score below `Compliance:ConfidenceThreshold` (0.5) gives UNCLEAR (`INSUFFICIENT_MODEL_CONFIDENCE`).
4. **Temporal-evidence policy.** If the model says COMPLIES but the guideline requires an explicit frequency the action shows no evidence of, the result is DEVIATES (`MISSING_TEMPORAL_EVIDENCE`).
   - **Frequencies recognised:** daily, weekly, monthly, annually, and "every / each / once a" plus a unit.
   - **Evidence:** the same unit stated in the action; a weekday name also counts for "weekly".
   - **Narrow on purpose:** it never overrides a model DEVIATES or UNCLEAR, so an unrelated action stays UNCLEAR.
   - **Why it's needed:** NLI detects contradiction, not a missing requirement. Case 3 doesn't contradict "weekly"; it just never shows it.
5. **Audit trail.** Every analysis stores the final result, source and reason, plus the model's provider, model ID, prompt name, top result, top score, every raw score and the threshold in force.

**Choosing the prompt.** `tools/LabelLab` measures prompts against the 4 brief cases and 11 extra ones, all decided through the same policies as the API ([docs/label-tuning.md](docs/label-tuning.md)):

| Prompt | Brief | Extra |
|---|---|---|
| **combined-three-label-v1** (production) | 4/4 | 7/11 |
| placeholder (two labels) | 4/4 | 8/11 |
| guideline-hypothesis | 3/4 | 6/11 |
| independent-scores | 4/4 | 7/11 |

The three-label prompt ships because it gives the model an explicit UNCLEAR, as the brief suggests, even though the two-label placeholder scores one extra case higher. Only the production prompt is in the API; the others live in LabelLab.

## Tests

```bash
dotnet test                                                    # deterministic; no network calls
dotnet test tests/ComplianceMonitor.Tests -- --filter-trait "Category=Live" --explicit on    # the 4 brief cases against real HF (needs a token)
dotnet run --project tools/LabelLab                            # prompt measurement, cached in .cache/hf/
```

- `tests/ComplianceMonitor.Tests` has one folder per layer:
  - `Application/`: policies, decisions and the service with fakes.
  - `Infrastructure/`: the HF client, gateway, resilience, input budget, the repository on real in-memory SQLite (including the upgrade migration), and the cache.
  - `Api/`: `WebApplicationFactory` with a fake model gateway; also contracts, validation, error mapping, rate limiting, health and OpenAPI.
  - `Live/`, `LabelLab/`, and architecture tests that enforce the dependency rule.
- A guard handler fails any non-Live test that tries to reach the network.
- `tests/ComplianceMonitor.Client.Tests` drives the client end to end over a fake API.
- **Live tests** are `[Theory(Explicit = true)]`, so plain `dotnet test` never runs them, even with a token configured. Don't use `--filter "Category=Live"`: it selects them but runs nothing. Target the test project as shown: run solution-wide, the client test project matches zero Live tests and the run exits with code 8 even though all four pass.
- **CI** (`.github/workflows/ci.yml`) restores, builds in Release, runs the non-live tests, checks formatting, and publishes any failing test as a public annotation.

## Architecture

```
src/ComplianceMonitor.Api/             HTTP only: Features/{Analyze,History,Summary}, shared Contracts, Errors, rate limiting, health, composition root
src/ComplianceMonitor.Application/     The use case: ComplianceAnalysisService, CompliancePolicy and rules, models,
                                       IAnalysisRepository and IComplianceModelGateway, provider-neutral ModelGatewayException
src/ComplianceMonitor.Infrastructure/  Persistence/ (EF Core + SQLite, migrations), Integrations/HuggingFace/ (gateway,
                                       client, resilience, prompt, input budget), Caching/ (HybridCache summary)
src/ComplianceMonitor.Client/          Console client
tools/LabelLab/                        Prompt measurement against live HF, including the non-production prompts
```

Dependencies point inwards: **Api → Application ← Infrastructure**. The API references Infrastructure only from the composition root (`Program.cs`): dependency registration plus infrastructure startup concerns (migrations, the readiness check).
`Application` references no ASP.NET Core, EF Core or Hugging Face code (`ArchitectureTests` checks this). Switching model provider means a new gateway in Infrastructure; the HTTP error layer only knows `ModelGatewayException`.

**Why Minimal APIs.** Three endpoints don't need controllers. The handlers are thin: they bind, call the service and map the result.

**Caching.** Only `/summary` is cached, with `HybridCache`. `/analyze` is an audited write and is never cached; history is a cheap indexed page.
HybridCache currently uses local in-memory storage. A horizontally scaled production deployment can add a distributed backing store such as Redis through infrastructure configuration/registration without requiring an application-layer redesign.

**Rate limiting.** `POST /analyze` has a concurrency limit (`RateLimiting:Analyze:PermitLimit` 4, `QueueLimit` 2), because each request holds an external model call open for up to 30 s. The other endpoints aren't limited.

**Migrations.** EF Core migrations live in `Infrastructure/Persistence/Migrations`. To add one:
```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/ComplianceMonitor.Infrastructure --output-dir Persistence/Migrations
```

## Production considerations: deliberately deferred

This is a 5–6 hour exercise, so these are documented, not built. The required routes `/analyze`, `/history` and `/summary` stay exactly as the brief specifies.

- **Authentication and authorisation.** Integrate with the organisation's identity provider (OIDC/OAuth/JWT, or gateway-managed authentication).
  - Authorise by policy or scope, not just "signed in": e.g. `Compliance.Analyze`, `Compliance.Read`, `Compliance.Admin`.
  - Nothing is faked here; a home-grown identity system would be worse than none.
- **Multi-tenancy and user identity.** Each analysis would carry `TenantId` and the actor's identity, taken from validated claims, never from the request body. Persistence, history, summary, cache keys (`analysis:{tenantId}:summary:v1`), authorisation and rate limits would all become tenant-aware.
- **AI usage metering, quotas and cost.** `/analyze` spends an external AI resource. Track usage per user and per tenant, including provider calls *and retries*, since one analysis can cost several calls. Add quotas, monthly budgets and provider-cost alerts.
- **Distributed, user-aware rate limiting.** The in-process concurrency limit protects a single instance. Several instances need per-user and per-tenant limits, coordinated globally or at the API gateway.
- **Decision reproducibility.** Rows already store the provider, model ID, prompt name, threshold, raw scores and decision reason. Add the exact model revision where the provider exposes it, and a `DecisionPolicyVersion` for the application rules, so a historical decision stays explainable after code changes.
- **Immutable audit history.** Treat stored decisions as append-only. Highly regulated settings may need restricted update permissions or tamper-evident audit storage.
- **Production database and distributed cache.** Move from local SQLite to a managed relational database (PostgreSQL or SQL Server):
  - with backups, point-in-time recovery, high availability, and migrations run as a deployment step (`Database:MigrateOnStartup=false`; `/health/ready` reports pending migrations);
  - with a distributed second-level cache behind `HybridCache` once several instances share the summary;
  - with large histories paged by keyset on `(CreatedAt, Id)` instead of `offset`.
- **Observability.** Structured logs and trace IDs exist today. Add OpenTelemetry traces and metrics for model latency, provider errors and retries, the result and UNCLEAR distribution, 429s, cache hits and misses, and database latency, with alerts and SLOs.
- **API versioning.** Before long-lived external consumers depend on the API, introduce an explicit versioning strategy (e.g. `/api/v1`) with compatibility, deprecation and migration policies. Health endpoints stay unversioned.

Also worth knowing:
- **Idempotency.** A lost response after a successful `/analyze` makes a client retry store a duplicate. Production would accept an `Idempotency-Key` with a uniqueness guarantee.
- **Data handling.** Action and guideline text goes to an external model provider; organisations must approve that data flow and the provider's retention policy. Logs never contain that text, the token, or provider response bodies. Keep secrets in the platform's secret manager.
- **Known model limits.** Measured on the extra cases:
  - Unrelated actions are often still called COMPLIES.
  - Missing requirements other than frequency (a missed deadline, an unmentioned step) aren't caught. The temporal policy is deliberately not a rules engine.
- **Local quirks.**
  - macOS reserves port 5000 for AirPlay, hence 5080.
  - If the API is killed mid-migration, EF Core's lock row can make the next start wait; deleting `src/ComplianceMonitor.Api/compliance.db*` resets it.
