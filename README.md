# Compliance Monitor

A .NET 10 Web API that classifies whether an action **COMPLIES** with, **DEVIATES** from, or is **UNCLEAR** against a guideline, using Hugging Face `facebook/bart-large-mnli` (zero-shot NLI).
Every successful classification is stored in SQLite and exposed through `/history` and `/summary`. A console client exercises all three endpoints.

## Quick start

**Prerequisites:** .NET SDK 10.0.4xx (pinned in `global.json`) and a Hugging Face account. No database setup: SQLite migrations run on startup.

**1. Create a token.** Create a **fine-grained** token with the **"Make calls to Inference Providers"** permission.
[This link](https://huggingface.co/settings/tokens/new?ownUserPermissions=inference.serverless.write&tokenType=fineGrained) opens the form with both already selected (from the [Inference Providers docs](https://huggingface.co/docs/inference-providers/index#authentication)).

**2. Store the token** with user-secrets. Never put it in `appsettings*.json`; a test fails if you do.

```bash
git clone https://github.com/sergeychopchyts-glitch/compliance-monitor.git && cd compliance-monitor
dotnet user-secrets set HuggingFace:ApiToken <your-token> --project src/ComplianceMonitor.Api
```

Outside Development, set the environment variable `HuggingFace__ApiToken` instead. Without a token the API refuses to start, with a message naming the config key.

**3. Run the API** (http://localhost:5080):

```bash
dotnet run --project src/ComplianceMonitor.Api
```

Interactive API docs (Development only): http://localhost:5080/scalar, which redirects to `/scalar/v1`. The raw OpenAPI document is at `/openapi/v1.json`.
`src/ComplianceMonitor.Api/ComplianceMonitor.Api.http` has every request ready to send from VS Code or Rider.

**4. Run the client demo** in a second terminal. It sends the brief's four cases, then shows history and summary.

```bash
dotnet run --project src/ComplianceMonitor.Client
```

```
Brief cases
  Case 1  expected COMPLIES  actual COMPLIES  (0.88, MODEL)  PASS
  Case 2  expected DEVIATES  actual DEVIATES  (0.97, MODEL)  PASS
  Case 3  expected DEVIATES  actual COMPLIES  (0.62, MODEL)  FAIL
  Case 4  expected UNCLEAR   actual UNCLEAR   (1.00, RULE)  PASS
3/4 cases passed.
```

Case 3 is a known limitation (see below). Other client commands:

```bash
dotnet run --project src/ComplianceMonitor.Client -- analyze \
  --action "Wore safety goggles while operating the lathe" \
  --guideline "Eye protection must be worn when operating machinery"
```
```
Result:      COMPLIES
Confidence:  0.92
Decided by:  MODEL
Timestamp:   2026-10-04T01:27:51Z
Id:          10
```

```bash
dotnet run --project src/ComplianceMonitor.Client -- run-cases              # only the four brief cases (exit 1 while Case 3 fails)
dotnet run --project src/ComplianceMonitor.Client -- summary
dotnet run --project src/ComplianceMonitor.Client -- history --limit 500    # the API's 400, shown as a field error (exit 3)
dotnet run --project src/ComplianceMonitor.Client -- --help                 # all commands, options and exit codes
```

The base URL comes from `--base-url`, else `COMPLIANCE_API_URL`, else `http://localhost:5080`.
Exit codes: `0` ok · `1` a brief case failed · `2` API unreachable or timed out · `3` API returned an error · `64` bad usage · `130` Ctrl+C.

## API reference

All responses are JSON with camelCase properties and enums as uppercase strings. Errors are RFC 7807 `application/problem+json`.

**`POST /analyze`**: classify and store.

```bash
curl -s -X POST localhost:5080/analyze -H 'Content-Type: application/json' \
  -d '{"action":"Closed ticket #48219 and sent confirmation email","guideline":"All closed tickets must include a confirmation email"}'
```
```json
{"id":1,"action":"Closed ticket #48219 and sent confirmation email","guideline":"All closed tickets must include a confirmation email","result":"COMPLIES","confidence":0.88,"decidedBy":"MODEL","timestamp":"2026-10-04T00:40:32Z"}
```

Both fields are required, trimmed, and at most 2000 characters. A bad request gets 400 with a ValidationProblemDetails body that names each bad field.
`confidence` is rounded to 2 decimals in the response; the database keeps the full score.

**`GET /history`**: newest first. Takes `limit` (1–200, default 50), `offset` (≥ 0) and `result` (optional filter).

```bash
curl -s "localhost:5080/history?limit=2&result=DEVIATES"
```
```json
[{"id":2,"action":"Closed ticket #48219 without sending confirmation email","guideline":"All closed tickets must include a confirmation email","result":"DEVIATES","confidence":0.97,"decidedBy":"MODEL","timestamp":"2026-10-04T00:40:33Z"}]
```

**`GET /summary`**: all three keys are always present.

```bash
curl -s localhost:5080/summary
```
```json
{"total":4,"byResult":{"COMPLIES":2,"DEVIATES":1,"UNCLEAR":1}}
```

**Failures:** when classification fails, nothing is stored and there is no fallback label.

| Cause | Status |
|---|---|
| Invalid input | 400 |
| HF rejected our token (401/403), other 4xx, unreadable response | 502 |
| HF 429 / 503 / 5xx / network error, after retries | 503, with `Retry-After` when HF sent one |
| HF 402 (credits exhausted) | 503, with its own `type` |
| HF timeout (10 s per attempt, 30 s total) | 504 |
| Anything else | 500, generic detail, no stack trace |

## How classification works

1. **The no-guideline rule.** If the guideline itself says that no guideline exists (a short, case-insensitive phrase list such as "no guidelines exist"), there is nothing to judge against. The result is UNCLEAR with `decidedBy: RULE`, and HF is not called.
2. **The label strategy** turns (action, guideline) into a zero-shot request: `inputs`, `candidate_labels`, `hypothesis_template` and `multi_label`.
   The current `PlaceholderLabelStrategy` sends `"Action: …\nGuideline: …"` with the labels *complies with the guideline* → COMPLIES and *violates the guideline* → DEVIATES, using the template `"This action {}."`.
3. **Mapping.** HF returns labels sorted by score. They are mapped **by label text, never by position**. If any label is unknown, missing or duplicated, the response is treated as a failure, not as UNCLEAR.
4. **Confidence floor.** If the top score is below `HuggingFace:ConfidenceFloor` (default 0.5), the result is UNCLEAR with `decidedBy: LOW_CONFIDENCE`. Otherwise it's the top label's result with `decidedBy: MODEL`.

`decidedBy` tells a reviewer *why* a result is UNCLEAR. It is stored with the strategy name and the raw HF scores for audit.
`tools/LabelLab` measures strategies against the brief's cases plus 11 extra ones. Results: [docs/label-tuning.md](docs/label-tuning.md).

## Tests

```bash
dotnet test                                                    # unit + integration; no network calls
dotnet test -- --filter-trait "Category=Live" --explicit on    # the 4 brief cases against real HF
dotnet run --project tools/LabelLab                            # strategy measurement (cached in .cache/hf/)
```

- `tests/ComplianceMonitor.Tests` holds the API, persistence and LabelLab tests. Integration tests use `WebApplicationFactory` with in-memory SQLite (real SQLite, not the EF InMemory provider), a fake classifier or a fake HF handler, and `FakeTimeProvider`. A guard handler fails any test that tries to reach the network.
- `tests/ComplianceMonitor.Client.Tests` drives the client end to end over a fake API, covering every command, error path and exit code.
- **Live tests** are `[Theory(Explicit = true)]` with `[Trait("Category","Live")]`. Plain `dotnet test` never runs them, even with a token configured, and they skip when no token is set. Don't use `dotnet test --filter "Category=Live"`: it selects them but runs nothing.
- **CI** (`.github/workflows/ci.yml`) restores, builds in Release, runs the non-live tests and checks `dotnet format --verify-no-changes`.

## Design decisions

| Decision | Why | Trade-off |
|---|---|---|
| Labels mapped by text; the label set must match exactly | HF sorts by score; position is meaningless | A changed label wording surfaces as a 502, not a silent wrong answer |
| `NoGuidelineRule` as an explicit precondition | No guideline means nothing to judge; the model alone said DEVIATES (0.945) for Case 4 | A phrase list is a rule, not ML; it's visible as `decidedBy: RULE` |
| Failures never become UNCLEAR | UNCLEAR is a judgment, not an error; nothing is stored on failure | Callers must handle 502/503/504 |
| Fail fast without a token (`ValidateOnStart`) | A misconfigured API should not start | `/history` can't be served without a token either |
| Standard resilience handler: 3 retries, backoff with jitter, `Retry-After` | HF returns 503 while the model loads, and 429 on quota | Retrying the POST is safe: inference is idempotent |
| SQLite + EF Core migrations, `CreatedAt` as a UTC `DateTime` | Zero setup; EF's SQLite provider can't order `DateTimeOffset` | Single node only |
| Enums stored by API names (`COMPLIES`, `LOW_CONFIDENCE`) | The table reads like the API | A custom converter instead of `HasConversion<string>()` |
| Minimal APIs and one `AnalysisStore` class | Three endpoints don't need MediatR or repositories | — |
| Live tests explicit, plus a no-network guard | `dotnet test` must be free and deterministic everywhere | Live runs need the longer command |
| Hand-rolled client argument parsing | Five commands; no extra package | No auto-generated help per option |

## Known limitations and next steps

- **Case 3 fails.** "Rebooted the server and checked logs" against a *weekly* reboot rule scores COMPLIES (0.62). NLI detects contradiction, not a missing requirement, and the placeholder strategy has no way to say "not fully satisfied". **Next:** the three strategies in `docs/plan.md` §3: a third "unrelated" label, the guideline written into the hypothesis ("fully satisfies…"), and independent scores with an abstain band. Measure each with LabelLab and pick the one with the best brief and extra-case accuracy.
- **The confidence floor can't fire today.** With two labels scored against each other, the top score is always at least 0.5. It becomes meaningful with three labels, or with a floor above 0.5.
- **The extra LabelLab cases** were written by the coding agent and need human review before their accuracy numbers mean much.
- **No auth or rate limiting** on the API, and the history response has no total count.
- **Unparsable query values** (e.g. `limit=abc`) return a framework 400 that doesn't name the parameter. Out-of-range values do name it.
- **macOS:** port 5000 belongs to the AirPlay Receiver, which is why the API uses 5080.
- **Stale migration lock:** if the API is killed mid-migration, EF Core leaves a lock row behind and the next start hangs. Delete the local `src/ComplianceMonitor.Api/compliance.db*` files.

## Project layout

```
src/ComplianceMonitor.Api/          Minimal API host
  Classification/                   HF client, options, label strategy, classifier, NoGuidelineRule
  Endpoints/                        /analyze, /history, /summary (one MapGroup)
  Errors/                           IExceptionHandler → ProblemDetails
  Persistence/                      DbContext, AnalysisStore, migrations
src/ComplianceMonitor.Client/       Console client (demo, analyze, run-cases, history, summary)
tests/ComplianceMonitor.Tests/      API, persistence, LabelLab and Live tests
tests/ComplianceMonitor.Client.Tests/
tools/LabelLab/                     Label-strategy measurement against live HF
docs/                               Brief, design plan, label-tuning results, recorded HF response
CLAUDE.md                           Instructions given to the coding agent
```

Migrations (only needed when the model changes):

```bash
dotnet tool restore   # installs dotnet-ef 10.0.12 from dotnet-tools.json
dotnet ef migrations add <Name> --project src/ComplianceMonitor.Api --output-dir Persistence/Migrations
```
