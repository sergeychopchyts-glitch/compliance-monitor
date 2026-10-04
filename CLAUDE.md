# Compliance Monitor — instructions for the coding agent

## Goal
Take-home exercise for Ease.io; the brief is in docs/exercise.md.
Build a .NET 10 Web API plus a console client. The API classifies whether
an action COMPLIES with, DEVIATES from, or is UNCLEAR against a guideline,
using Hugging Face facebook/bart-large-mnli (zero-shot), stores every result,
and exposes POST /analyze, GET /history, GET /summary.
Reviewers judge code quality, tests, and how I direct and check your work.

## Hard constraints
- .NET 10, C#, ASP.NET Core. Nullable enabled, TreatWarningsAsErrors.
- HF endpoint: POST https://router.huggingface.co/hf-inference/models/facebook/bart-large-mnli
  The real response shape is in docs/hf-sample-response.json. Code against
  that file. Never use api-inference.huggingface.co (retired).
- HF returns labels sorted by score. Map results by label text, never by index.
- Token comes only from config key HuggingFace:ApiToken (dotnet user-secrets
  locally, env var HuggingFace__ApiToken elsewhere). Never put it in
  appsettings*.json, code, tests, logs, or exception messages.
- Storage: EF Core + SQLite. Connection string in config.
- Result values are exactly COMPLIES, DEVIATES, UNCLEAR, serialized as
  uppercase strings, never integers.
- Timestamps: UTC, ISO 8601 with a trailing Z, from an injected TimeProvider.
  Never DateTime.Now or DateTime.UtcNow directly.
- If classification fails, do NOT store a result and do NOT fall back to a
  default label. Return RFC 7807 ProblemDetails with a fitting status code.
- Plain `dotnet test` makes no network calls. Tests that call Hugging Face
  carry [Trait("Category", "Live")] and skip when no token is configured.

## Packages
Allowed: Microsoft.EntityFrameworkCore.Sqlite, Microsoft.EntityFrameworkCore.Design,
Microsoft.AspNetCore.OpenApi, Scalar.AspNetCore, Microsoft.Extensions.Http.Resilience,
Microsoft.AspNetCore.Mvc.Testing, Microsoft.Extensions.TimeProvider.Testing, xunit.v3.
Anything else: ask first. No FluentAssertions (v8 is commercially licensed);
use plain xUnit asserts. No MediatR, no AutoMapper, no repository-of-repositories.

## How we work
- Before each step, give me a short plan. Wait for my OK if it changes the design.
- One step per commit. When a step is done, propose a Conventional Commit
  message. Commit only when I say "commit".
- Done means: `dotnet build` with zero warnings and `dotnet test` green, and
  you paste the summary lines. Never claim success without running them.
- Every new behaviour gets a test in the same change.
- Never weaken or delete a test to make it pass. If a test is wrong, say so first.
- If the brief is ambiguous, ask or list your assumption. Don't silently guess.
- Don't edit docs/exercise.md, docs/hf-sample-response.json, PROCESS-LOG.md or PROCESS.md.
- Keep it proportionate: this is a 5-6 hour exercise, not a platform.

## Commands
- Build: dotnet build
- Unit + integration tests: dotnet test
- Live tests: dotnet test --filter "Category=Live"
- Run API: dotnet run --project src/ComplianceMonitor.Api
- Run client: dotnet run --project src/ComplianceMonitor.Client -- <command>
- Measure label strategies (live HF, cached in .cache/hf/, writes docs/label-tuning.md):
  dotnet run --project tools/LabelLab [-- --strategy <name>]

## Acceptance cases (from the brief)
These are the Live tests. Do not special-case them in production code, except the
agreed `NoGuidelineRule` precondition (docs/plan.md, section 3).

| # | Action | Guideline | Expected |
|---|--------|-----------|----------|
| 1 | Closed ticket #48219 and sent confirmation email | All closed tickets must include a confirmation email | COMPLIES |
| 2 | Closed ticket #48219 without sending confirmation email | All closed tickets must include a confirmation email | DEVIATES |
| 3 | Rebooted the server and checked logs | Servers must be rebooted weekly and logs reviewed after restart | DEVIATES |
| 4 | Skipped torque confirmation at Station 3 | No guidelines exist for this case. | UNCLEAR |

## Agreed design
See docs/plan.md
