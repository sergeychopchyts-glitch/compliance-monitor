# Process

How I directed, reviewed and verified the coding agent (Claude Code) on this exercise.
The commit history is the record: each commit is one step I approved after seeing its build and test output.

## 1. Context, constraints and instructions I gave the agent

### Ground truth before any code
- **The brief** went into `docs/exercise.md`. When the agent extracted it from the PDF, it dropped the hyperlink to the Zero-Shot API docs (see section 3).
- **A real Hugging Face call.** Before writing instructions I ran one zero-shot request myself and saved the raw response as `docs/hf-sample-response.json`.
  That paid off immediately: the router returns `[{label, score}]`, **not** the `{labels, scores}` shape that older docs and most examples show.
  `CLAUDE.md` tells the agent to code against that file. A test parses the real file (`ClassifyAsync_ParsesRecordedSampleResponse`).

### `CLAUDE.md`: standing constraints, each with a reason
| Constraint | Why |
|---|---|
| Use `router.huggingface.co/hf-inference/...`, never `api-inference.huggingface.co` | The old endpoint is retired; agents still suggest it from training data |
| Map results **by label text, never by index** | HF sorts labels by score, so index 0 is "the winner", not "COMPLIES" |
| Token only from `HuggingFace:ApiToken` (user-secrets or env var); never in config, code, tests, logs or exception messages | A secret leak is the easiest mistake to make and the hardest to undo |
| Results are exactly `COMPLIES` / `DEVIATES` / `UNCLEAR` as strings | The brief's contract; enums serialize as integers by default |
| UTC timestamps with `Z`, from an injected `TimeProvider` | Testable time; `DateTime.Now` is untestable and timezone-dependent |
| If classification fails: store nothing, no default label, return ProblemDetails | A fabricated "UNCLEAR" on an outage would corrupt the history and the summary |
| Plain `dotnet test` makes no network calls; live tests carry a trait and skip without a token | Fast, free, deterministic CI |
| Package allowlist; no FluentAssertions (commercial licence), MediatR or AutoMapper | Keep it proportionate; ask before adding dependencies |

### How we worked (also in `CLAUDE.md`)
- **Before each step:** a short plan, and wait for my OK if it changes the design.
- **One step per commit**, with a proposed Conventional Commit message. The agent commits **only when I say "commit"**.
- **"Done"** means `dotnet build` with zero warnings and `dotnet test` green, with the summary lines pasted. No claiming success without running them.
- **Testing rules:** every new behaviour gets a test. Never weaken or delete a test to make it pass.
- **When the brief is ambiguous:** ask, or state the assumption.

### Plan first, then one prompt per step
- **Design plan.** I had the agent draft `docs/plan.md` (`388221f`) covering seven things:
  - layout;
  - the `/analyze` flow;
  - how zero-shot output becomes a result, with three alternative label strategies;
  - JSON contracts and a status code for every failure;
  - the data model;
  - the test strategy;
  - open questions.

  I answered the questions, and the plan became the reference for every later step.
- **Step prompts.** Each step got its own prompt with acceptance criteria:
  - the classification layer;
  - a LabelLab tool for measuring label strategies;
  - persistence, where I named the two SQLite traps up front;
  - the endpoints, with exact JSON;
  - resilience;
  - the client, with exact error messages and exit codes;
  - the README.
- **Spec conflicts.** When a new prompt contradicted an earlier decision, the agent stopped and asked rather than picking one.

**Decisions I made along the way** (each recorded in `docs/plan.md`):

| Decision | Choice |
|---|---|
| Guideline says "no guideline exists" | An explicit rule returns UNCLEAR with `decidedBy: RULE`, so it's visible rather than hidden |
| Missing token | Fail at startup (`ValidateOnStart`), not at the first request |
| HF failure codes | Kept 502/503/504 distinct. 402 (credits exhausted) gets its own `type`. HF 429 becomes 503, because it's our quota, not the caller's |
| Confidence | Rounded to 2 decimals in the API, full precision in the database |
| Enum storage | API names (`COMPLIES`, `LOW_CONFIDENCE`) rather than C# names |
| Margin rule | Dropped, to keep the knobs few |
| Migrations | Pinned `dotnet-ef` as a local tool; regenerated the never-deployed `InitialCreate` instead of stacking a second migration |
| Client tests | A separate test project |

## 2. What I delegated and what I kept

| Area | Who | Why |
|---|---|---|
| `CLAUDE.md`, the plan's questions and every step's acceptance criteria | Me | The constraints are the product; the agent can't know them |
| Ground truth: the manual curl and the recorded response | Me | Gave the agent a fixed reference to code against |
| Product decisions: error semantics, what gets stored, UNCLEAR sources, rounding, failing at startup | Me (the agent proposed options) | These are product choices, not code |
| Scaffold, EF Core, endpoints, HF client and resilience, console client, CI | Agent | Well-trodden code that's easy to verify with tests and a live run |
| Unit, integration and client tests | Agent, to my specs | I specified *what* each step must prove; the agent wrote them, and I reviewed the coverage audit |
| LabelLab extra cases (`tools/LabelLab/cases.json`) | Agent drafted, I edited | I replaced a duplicate with a compliant counterpart to Case 3. Agent-written expected labels are flagged in the README as needing review |
| Label strategies (the core NLI judgment) | Me | This is where overfitting to the four brief cases is the main risk. **At the time of writing the placeholder strategy is still in place**, which is why Case 3 fails |
| Final review of README and `PROCESS.md` | Me | Must reflect what actually happened |

### How I verified the agent's work
- **Pasted output every step.** Each step ended with pasted build and test summaries; no commit without them.
- **Live runs.** I had the agent run the real API, the client and the Live tests against Hugging Face, not just the fakes.
- **A coverage audit:** "every behaviour promised in the plan, the test that covers it, or GAP", plus a list of weak tests. Fixing that audit is `622b932`.
- **"Unverified means TODO."** The README had to contain only commands the agent had actually run; anything else was marked TODO. All 7 TODOs were later verified and replaced (`5892f76`).
- **A cold review.** I asked the agent to review the finished repo as an Ease.io grader would, without modifying anything. Its open findings are listed at the end of section 3.

## 3. What the agent got wrong and how it was fixed

Most of these were caught *by the verification rules above* rather than by reading code. That's the point of "done = pasted test output". Where it matters, the table says who caught what.

| # | What went wrong | How it was caught | Fix | Commit |
|---|---|---|---|---|
| 1 | When extracting the brief from the PDF, the agent dropped the "Zero-Shot Classification API docs" hyperlink | My review | The recorded live response became the reference instead | (before code) |
| 2 | A startup test expected one `OptionsValidationException`; the host actually throws an `AggregateException` of two | Test run | Test unwraps and checks each inner exception | `3019bd8` |
| 3 | The agent's own `.editorconfig` required `_` on `static readonly` fields; `dotnet build` didn't flag it | `dotnet format --verify-no-changes` | Corrected the naming rule | `3019bd8` |
| 4 | The test project referenced the API and LabelLab, and both had a public `Program` type (CS0433) | Build | LabelLab got an explicit `Main` in its own namespace | `b949eaa` |
| 5 | The agent reported a LabelLab "exit 0" that was really `tail`'s exit code | Agent re-checked | Real exit code was 2 (no token) | `b949eaa` |
| 6 | `HasConversion<string>()` would have stored `Complies`, contradicting the plan's `COMPLIES` | Reviewing the generated migration | Custom converter. I later re-confirmed this when a prompt asked for `HasConversion<string>()` | `f2061d5` |
| 7 | UTC round-trip test asserted `EndsWith("Z")` on JSON output, which ends in `"` | Test run | Exact-string assertion (stricter) | `e2f9761` |
| 8 | The first live check on port 5000 "worked" by chance; macOS AirPlay owns that port and answers 403 | Later live runs got empty bodies; `lsof` showed Control Center | API moved to 5080 | `25427c4` |
| 9 | The agent killed the API with `pkill` mid-migration, leaving an EF Core lock row; the next start hung silently | Startup log showed endless lock retries | Deleted the local dev DB, stopped processes gracefully afterwards, documented in the README | `25427c4`, `2d48262` |
| 10 | The resilience package silently reset `HttpClient.Timeout` to infinite, discarding the backstop | A test asserting the configured value | Configure the backstop after the pipeline is registered | `1802480` |
| 11 | A caller cancelling between retries surfaced as "HF unavailable" (503), because Polly returns the last response instead of throwing | Test `CallerCancels_StopsWithoutRetrying` | Check the caller's token after the call | `1802480` |
| 12 | The plan and `CLAUDE.md` had drifted from the code: logs promised a strategy name that wasn't logged, and the documented Live command (`--filter "Category=Live"`) selected the tests but silently ran none | My coverage audit | Implemented the log line; corrected the docs and the command | `622b932` |
| 13 | A new end-to-end 504 test was flaky (about 1 run in 12): a fake clock advanced faster than the retry ran | Repeated suite runs | Event-driven clock advancing; 15 consecutive green runs | `622b932` |
| 14 | Timing tests had upper bounds tighter than the test harness allows (2.8 s measured vs a 2.2 s bound) | Test run | Kept the exact lower bound, which proves the mechanism. Upper bound now only rules out the next mechanism. **A loosened assertion, recorded deliberately** | `622b932` |
| 15 | A test assumed the framework's 400 for `limit=abc` names the parameter; outside Development it doesn't | Test run | Test asserts a complete ProblemDetails; limitation documented, no contract change | `622b932` |
| 16 | Leftover code: an unused helper and a needless lambda in the client, a stray line in a test | Agent self-review before building | Removed | `2a88d3c`, `622b932` |
| 17 | README listed commands the agent had never run | My "unverified means TODO" rule | Each verified live, or replaced | `2d48262`, `5892f76` |

**Where the agent pushed back correctly:**
- When there was no `CLAUDE.md` draft and no curl had been run, it refused to invent the content.
- It flagged that my first curl omitted the guideline, so Cases 3 and 4 couldn't be judged.
- It noticed when I re-sent prompts for work already committed, and listed where each requirement lived instead of redoing it.

**Still open after the cold review** (not fixed at the time of writing):
- **No-guideline rule:** it matches substrings, so a guideline *mentioning* "no guideline exists" is wrongly short-circuited to UNCLEAR.
- **Polly rejections:** circuit-breaker and rate-limiter rejections fall through to a generic 500 instead of 503.
- **Long inputs:** two 2000-character fields can exceed the model's 1024-token limit.
- **Case 3:** fails with the placeholder strategy (COMPLIES, 0.62). NLI detects contradiction, not a missing requirement.

These, and the label-strategy work, are listed as next steps in the README.
