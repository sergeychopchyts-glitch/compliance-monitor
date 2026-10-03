# Ease.io Back End Code Exercise: Compliance Monitor

## Objective

Build a Compliance Monitor API that evaluates whether actions taken during a process comply with established guidelines using a simple AI model integration.

## Scenario

Organizations rely on repeatable processes — like processing support tickets, running IT checks, or completing safety steps. But sometimes actions don't follow guidelines, and processes are not carried out correctly.

Your task is to build a Compliance Monitor API that:

- Accepts a reported action (what someone did).
- Decides whether the action complies with guidelines (**COMPLIES**) or deviates from them (**DEVIATES**).
- Stores the results and exposes them via simple backend endpoints.

For example:
If the action is "Closed ticket #48219 and sent confirmation email" and the guideline is "All closed tickets must include a confirmation email", then the expected result should be **COMPLIES**.

## Backend API

### Analyze

Expose a **POST /analyze** endpoint that accepts:

```json
{
    "action": "Closed ticket #48219 and sent confirmation email",
    "guideline": "All closed tickets must include a confirmation email"
}
```

Use the Hugging Face model **facebook/bart-large-mnli (Zero-Shot NLI)** to classify the input and return:

```json
{
    "action": "Closed ticket #48219 and sent confirmation email",
    "guideline": "All closed tickets must include a confirmation email",
    "result": "COMPLIES",
    "confidence": 0.94,
    "timestamp": "2025-10-03T10:15:00Z"
}
```

**Save results in a database** (e.g., Postgres, SQLite, or similar).

#### Calling the Inference API

1. Create a free Hugging Face account at [huggingface.co](https://huggingface.co).
2. In **Profile → Access Tokens**, create a new **Read** token.
3. Refer to the official Zero-Shot Classification API docs for details on the JSON payload structure and authentication.

**Hint:** Use candidate labels that map reliably to COMPLIES, DEVIATES, or UNCLEAR.

### History

Expose a **GET /history** endpoint that returns a list of past analyses and results.

### Summary

Expose a **GET /summary** endpoint that returns aggregated stats over all stored analyses:

- Total number of analyses, and
- Total number of analyses by result (COMPLIES, DEVIATES, UNCLEAR).

## Client

Include a simple client that exercises all three endpoints — console app, script, or minimal UI, your choice. Your client should handle all errors gracefully.

## Test Cases

Use the following cases to validate your system:

**Case 1**

- Action: Closed ticket #48219 and sent confirmation email
- Guideline: All closed tickets must include a confirmation email
- Should return: **COMPLIES**

**Case 2**

- Action: Closed ticket #48219 without sending confirmation email
- Guideline: All closed tickets must include a confirmation email
- Should return: **DEVIATES**

**Case 3**

- Action: Rebooted the server and checked logs
- Guideline: Servers must be rebooted weekly and logs reviewed after restart
- Should return: **DEVIATES**

**Case 4**

- Action: Skipped torque confirmation at Station 3
- Guideline: No guidelines exist for this case.
- Should return: **UNCLEAR**

## Working with Coding Agents

We expect you'll use coding agents; we will evaluate how you direct, review, and verify their work. Alongside your code, include a **PROCESS.md** answering:

1. What context, constraints, or instructions did you give your agent before it coded?
2. What did you decide to delegate vs write yourself, and why?
3. What did the agent do wrong that you had to fix? How did you fix it?

We prefer you commit as you work so we can see a history of changes.

## Hints & Expectations

- .NET Core/C# required.
- Include tests.
- Include a README with setup, run, and test instructions.
- Exercise scoped for ~5–6 hours of work.

## What to Submit

GitHub repository link.

README with:

- Setup instructions.
- How to run the project.
- How to run tests.

Plus **PROCESS.md** (see above).
