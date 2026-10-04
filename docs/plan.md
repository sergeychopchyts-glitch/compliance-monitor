# Design plan

Status: agreed (2026-10-03). Assumptions are marked **A**, open questions **Q** (section 7).

## 1. Solution layout

| Project | Responsibility |
|---------|----------------|
| `src/ComplianceMonitor.Api` | Minimal-API host. `Classification/`: HF client, label strategies, label→result mapping. `Persistence/`: DbContext, entity, migrations. `Endpoints/`: the three endpoints and request validation. `Errors/`: one `IExceptionHandler` that turns classification failures into ProblemDetails. |
| `src/ComplianceMonitor.Client` | Console app: `analyze`, `history`, `summary`, `cases`. Typed `HttpClient`, prints ProblemDetails, no stack traces, non-zero exit code on failure. |
| `tests/ComplianceMonitor.Tests` | One xunit.v3 project covering unit, integration and Live tests for API and client. |

Shared settings in `Directory.Build.props`, versions in `Directory.Packages.props`.

## 2. POST /analyze flow

1. Bind `AnalyzeRequest { action, guideline }`. Unreadable JSON → 400.
2. Validate: both present, not whitespace, ≤ 2000 chars each, after trimming → else 400 `ValidationProblemDetails`.
3. `IComplianceClassifier.ClassifyAsync(action, guideline, ct)`:
   1. **Rule:** if the guideline says no guideline exists (short case-insensitive phrase list, `NoGuidelineRule`) → UNCLEAR, `DecidedBy=RULE`, confidence 1.0, no HTTP call.
   2. The active `ILabelStrategy` builds the HF payload (`inputs`, `candidate_labels`, `hypothesis_template`, `multi_label`).
   3. POST through a typed `HttpClient` with `AddStandardResilienceHandler`: retries 429 / 5xx / timeouts with backoff (honours `Retry-After`), never 400/401/402/403; 30 s total budget.
   4. Failures throw `HuggingFaceTransientException` (timeout, network, 408, 429, 5xx) or `HuggingFacePermanentException` (other 4xx incl. 400/401/402/403, malformed body). Both carry the upstream status (table in 4).
   5. Parse `[{label, score}]` (also legacy `{labels, scores}`), map **by label text**; the response must contain exactly the labels sent, else `Malformed`. Top score below `ConfidenceFloor` → UNCLEAR, `DecidedBy=LOW_CONFIDENCE`; otherwise the top label's result, `DecidedBy=MODEL`.
4. `timestamp = TimeProvider.GetUtcNow()`, truncated to whole seconds (**A**, matches the brief's example).
5. Insert `AnalysisRecord`, `SaveChangesAsync`. Only reached on success, so failures store nothing.
6. Return 200 with the response body. (**A** 200 not 201: there is no GET-by-id to point a Location at.)

Logs: HF status code, latency, strategy name. Never the token, never the raw HF body.

## 3. From zero-shot output to a result

The HF pipeline scores, for each label, whether `inputs` (premise) entails `hypothesis_template.format(label)` (hypothesis). With `multi_label=false` the scores are a softmax across labels and sum to 1; with `true` each is an independent entailment probability.

Three strategies, measured against the four cases in Step 3:

**A. Combined premise, three generic labels** (baseline)
- `inputs`: `"Action: {action}. Guideline: {guideline}"`
- `candidate_labels`: `complies with the guideline` → COMPLIES, `violates the guideline` → DEVIATES, `is unrelated to the guideline` → UNCLEAR
- `hypothesis_template`: `"This action {}."`, `multi_label=false`

- **B. Action as premise, guideline in the hypothesis.** This is the textbook NLI framing. `inputs` = action only. The labels embed the guideline: `"fully satisfies the requirement: {guideline}"`, `"fails the requirement: {guideline}"`, `"has nothing to do with the requirement: {guideline}"`. The template is `"This action {}."`. The labels contain user text, so the strategy keeps its own label→result table per request. "Fully" is there to push partial matches (Case 3) towards DEVIATES.
- **C. Two labels, independent scores, abstain band.** Use the same premise as A, but only the comply/violate labels, with `multi_label=true`. The result is COMPLIES if the comply score is ≥ T_high and the violate score is < T_low, and DEVIATES the other way round. Anything else is UNCLEAR, which covers both labels low and both labels high. UNCLEAR comes from the scores themselves, not from a third label.

The strategy is chosen by config (`HuggingFace:Strategy`), and labels and thresholds live in config too. Step 3 runs every strategy against the four cases and records the scores in `docs/label-experiments.md`. The strategy that passes the most cases with the widest margin wins. Ties are broken by fewest moving parts.

**Every way UNCLEAR can arise** (never because something failed; `DecidedBy` says which)
1. `RULE`: the guideline states that no guideline exists (decided 2026-10-03; supersedes the original A1).
2. `MODEL`: the strategy's "unrelated" label wins (A, B), or strategy C's abstain band.
3. `LOW_CONFIDENCE`: the top score is below `HuggingFace:ConfidenceFloor` (default 0.5). At the floor is not below.

`MinMargin` was dropped (2026-10-03) to keep the knobs few.

A classification *failure* (HF error, unknown or missing label, empty array, score that isn't a number) is never UNCLEAR. It becomes a ProblemDetails error and nothing is stored.

## 4. JSON contracts and failure codes

`POST /analyze` request `{ "action": "...", "guideline": "..." }` → 200
```json
{ "action": "...", "guideline": "...", "result": "COMPLIES", "confidence": 0.9238, "timestamp": "2025-10-03T10:15:00Z" }
```
`GET /history?limit=100` → 200, newest first (`limit` 1–1000, default 100):
```json
[ { "id": 7, "action": "...", "guideline": "...", "result": "DEVIATES", "confidence": 0.81, "timestamp": "2025-10-03T10:16:02Z" } ]
```
`GET /summary` → 200, all three keys always present:
```json
{ "total": 4, "byResult": { "COMPLIES": 1, "DEVIATES": 2, "UNCLEAR": 1 } }
```
Errors are RFC 7807 (`application/problem+json`) with `type`, `title`, `status`, `detail`, and `traceId`.

| Failure | Status | Notes |
|---------|--------|-------|
| Invalid input (missing, blank, too long, bad JSON, bad `limit`) | 400 | `errors` map per field |
| HF 401 / 403 (auth) | 502 | our credentials are wrong; the caller can't fix it |
| HF timeout (after retries) | 504 | |
| HF 503 (model loading/unavailable, after retries) | 503 | `Retry-After` passed through if present |
| HF 429 (after retries) | 503 | our quota, not the caller's (Q1); `Retry-After` passed through |
| HF 402 (credits exhausted) | 503 | not retried; distinct `type` so operators can tell |
| HF other 5xx / network failure (after retries) | 503 | transient |
| HF other 4xx, malformed body, unmappable labels | 502 | permanent |

A missing token is not a runtime error: `ValidateOnStart` stops the API from booting, with a message naming `HuggingFace:ApiToken`.
| Unexpected (e.g. DB error) | 500 | generic detail |

## 5. Data model

Entity `AnalysisRecord`, table `Analyses` (EF Core migration, SQLite):

| Column | Type | Constraints |
|--------|------|-------------|
| `Id` | INTEGER | PK, autoincrement |
| `Action` | TEXT | NOT NULL, max 2000 |
| `Guideline` | TEXT | NOT NULL, max 2000 |
| `Result` | TEXT | NOT NULL, `COMPLIES`/`DEVIATES`/`UNCLEAR` (enum→string converter) |
| `Confidence` | REAL | NOT NULL, 0–1 |
| `TimestampUtc` | TEXT | NOT NULL; converter re-applies `DateTimeKind.Utc` on read |
| `Strategy` | TEXT | NOT NULL, label strategy name (A/B/C), for audit |
| `DecidedBy` | TEXT | NOT NULL, `MODEL`/`RULE`/`LOW_CONFIDENCE`, for audit |
| `ScoresJson` | TEXT | NOT NULL, raw `[{label, score}]` from HF, for audit; not in API responses |

History orders by `Id` desc, so there's no date sorting in SQLite. The summary is a single `GROUP BY Result`. `Database.Migrate()` runs at startup.

## 6. Test strategy

- **Unit** (no I/O):
  - Each strategy's payload builder and its mapper. Shuffled order gives the same result, and each UNCLEAR rule is covered.
  - Missing, unknown or empty labels give a failure, not UNCLEAR.
  - `HuggingFaceClassifier` against a stub `HttpMessageHandler`: URL, bearer header and body are correct. Each HF status maps to the right exception kind. The token never appears in exception text or logs.
  - The client's argument parsing and its rendering of errors.
- **Integration** (`WebApplicationFactory`, fake `IComplianceClassifier`, `FakeTimeProvider`, in-memory SQLite):
  - Each endpoint's happy path, with the stored row checked.
  - Every row of the failure table returns the right status and ProblemDetails shape, and the database stays empty.
  - JSON shape: uppercase result strings, a trailing `Z`, and all three summary keys.
  - History order and limit.
  - Client against an in-process API, including "API down".
- **Live** (`[Trait("Category","Live")]`, skipped without a token): the four brief cases through the real classifier. Plain `dotnet test` makes no network calls.

## 7. Assumptions and questions

Assumptions:
- **A1** ~~Nothing special-cases "No guidelines exist".~~ Revised 2026-10-03: an explicit, visible `NoGuidelineRule` precondition handles it (`DecidedBy=RULE`); everything else is decided by the model.
- **A2** Case 3 is hard for NLI, because the action doesn't contradict "weekly". If no strategy gets it, I'll report that rather than force it.
- **A3** 2000-char limit per field; inputs are trimmed before storage.
- **A4** `confidence` = score of the winning label. For UNCLEAR via `LOW_CONFIDENCE` it's the top score; via `RULE` it's 1.0. Full precision, not rounded.
- **A5** No auth on our API, no paging beyond `limit`, no delete endpoint.

Questions (resolved 2026-10-03: "go with your assumptions"):
- **Q1** HF 429 → **503** to the caller; it is our quota, not theirs.
- **Q2** `confidence` → **full precision**, not rounded.
- **Q3** Store raw scores and strategy name → **yes**, columns `Strategy` and `ScoresJson` (section 5). Not exposed in the API.
- **Q4** Measuring strategies against the real HF API in Step 3 (about 16 calls) → **yes**.

## Steps (one commit each)
1. Scaffold ✅ · 2. Result type + mapping/strategies (unit) · 3. HF client + strategy measurement · 4. Persistence + POST /analyze + errors · 5. /history, /summary · 6. Live tests · 7. Client · 8. README
