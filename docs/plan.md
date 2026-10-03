# Design plan

Draft by the agent, 2026-10-03. Items marked **Assumption** fill gaps in the
brief and were approved by Sergey ("go with your assumptions").

## Solution layout

```
ComplianceMonitor.slnx
Directory.Build.props              net10.0, Nullable, TreatWarningsAsErrors, ImplicitUsings
src/ComplianceMonitor.Api          minimal-API web app
  Classification/                  ComplianceResult, IComplianceClassifier,
                                   HuggingFaceClassifier, HuggingFaceOptions, LabelMapper
  Persistence/                     AppDbContext, AnalysisRecord, Migrations/
  Endpoints/                       AnalyzeEndpoints, HistoryEndpoints, SummaryEndpoints
  Errors/                          ClassificationException, exception -> ProblemDetails handler
src/ComplianceMonitor.Client       console app, typed HttpClient, no extra packages
tests/ComplianceMonitor.Tests      xunit.v3; unit, integration (WebApplicationFactory), Live
```

One test project covers both API and client to keep it small.

## Classification

Request to `POST https://router.huggingface.co/hf-inference/models/facebook/bart-large-mnli`:

```json
{
  "inputs": "Action: <action>. Guideline: <guideline>",
  "parameters": {
    "candidate_labels": ["complies with the guideline", "violates the guideline", "is unrelated to the guideline"],
    "hypothesis_template": "This action {}.",
    "multi_label": false
  }
}
```

Response shape (per docs/hf-sample-response.json): `[{ "label": string, "score": number }, ...]`,
sorted by score.

Mapping (`LabelMapper`, pure function, unit-tested):
- Labels and their result are config (`HuggingFace:Labels`), so wording can be tuned
  without code changes. Lookup is by label text, never by index.
- Pick the highest-scoring entry. If its label maps to COMPLIES/DEVIATES and
  score >= `HuggingFace:MinConfidence` (default 0.5) → that result.
- **Assumption:** top label is the "unrelated" label, or top score below
  threshold → UNCLEAR. This is a real classification, not a fallback.
- `confidence` returned = score of the winning label.
- Any configured label missing from the response, an unknown label, empty or
  malformed JSON → classification failure (nothing stored).

**Assumption:** no special-casing of the brief's cases (e.g. no string match on
"No guidelines exist"). Case 3 may fail live; if so we tune labels/template and
record it, we don't hardcode it.

## HTTP client and errors

- Typed `HttpClient` for `HuggingFaceClassifier`, bearer token from
  `HuggingFace:ApiToken`, `AddStandardResilienceHandler` (retries 429/5xx,
  including HF 503 "model loading"; total timeout 30 s).
- Token is read per call; if missing → failure. The app still boots without a
  token so plain tests and `/history` work.
- Error mapping (RFC 7807 via `AddProblemDetails` + one `IExceptionHandler`):

| Situation | Status |
|-----------|--------|
| action/guideline missing, blank, or > 2000 chars | 400 (ValidationProblemDetails) |
| token not configured | 503 |
| HF auth rejected (401/403) | 502 |
| HF other non-success, malformed or unmappable response | 502 |
| HF timeout after retries | 504 |

ProblemDetails `detail` never contains the token or the raw HF body.

## Persistence

- EF Core + SQLite, connection string `ConnectionStrings:ComplianceMonitor`
  (default `Data Source=compliance-monitor.db`; `*.db` gitignored).
- `AnalysisRecord { long Id; string Action; string Guideline; ComplianceResult Result
  (stored as string); double Confidence; DateTime TimestampUtc }`.
- Migrations checked in; `Database.Migrate()` at startup.
- Tests use SQLite in-memory with a connection held open per factory.
- Timestamp from injected `TimeProvider` (`FakeTimeProvider` in tests). Stored
  and returned as `DateTime` with `Kind=Utc`, so JSON ends in `Z`.

## API contract

- `POST /analyze` → 200 with `{ action, guideline, result, confidence, timestamp }`.
  The row is saved only after a successful classification.
- `GET /history?limit=N` → 200, array of the same shape plus `id`, newest first.
  **Assumption:** `limit` defaults to 100, range 1–1000; out of range → 400.
- `GET /summary` → 200 `{ total, byResult: { COMPLIES, DEVIATES, UNCLEAR } }`,
  all three keys always present (0 when none).
- `result` serialized as uppercase strings via `JsonStringEnumConverter` +
  `[JsonStringEnumMemberName]`.
- OpenAPI document + Scalar UI in Development.

## Client

`dotnet run --project src/ComplianceMonitor.Client -- <command>`

- `analyze "<action>" "<guideline>"`, `history [limit]`, `summary`,
  `cases` (runs the four brief cases and prints expected vs actual).
- Base URL: `--url` or `COMPLIANCE_API_URL`, default `http://localhost:5000`.
- Graceful errors: API down, timeout, ProblemDetails (prints title/detail),
  unexpected JSON. Non-zero exit code, no stack traces, usage text on bad args.

## Tests

- Unit: `LabelMapper` (order independence, threshold, unrelated label,
  missing/unknown labels); `HuggingFaceClassifier` with a stub
  `HttpMessageHandler` (request body, auth header, status → error mapping,
  token never in exception text).
- Integration (`WebApplicationFactory`, fake classifier, `FakeTimeProvider`):
  analyze happy path + persisted, validation 400, classifier failure → ProblemDetails
  and no row, history order/limit, summary counts, JSON shape (uppercase strings,
  trailing Z).
- Client: command parsing and error handling against a stub handler.
- Live (`[Trait("Category","Live")]`, skipped without token): the four brief cases.

## Steps (one commit each)

0. docs: CLAUDE.md + this plan
1. Scaffold solution, projects, smoke test
2. `ComplianceResult` + `LabelMapper` + tests
3. `HuggingFaceClassifier` + options + resilience + tests
4. Persistence + `POST /analyze` + ProblemDetails handling + tests
5. `GET /history`, `GET /summary` + tests
6. Live tests for the four cases; tune labels if needed
7. Console client + tests
8. README (setup, run, test). PROCESS.md is Sergey's.
