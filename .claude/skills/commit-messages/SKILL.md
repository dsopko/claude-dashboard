---
name: commit-messages
description: Write git commit messages in a controlled Simplified Technical English (ASD-STE100) style. Use this skill every time a commit is about to be created — before any `git commit` or `git commit --amend`, and whenever the user asks to commit, draft, improve, or review a commit message, even if they only say "commit this."
---

# Commit Messages — Simplified Technical English

Commit messages follow a controlled language modeled on ASD-STE100. The goal: a future reader — human or agent walking `git log` — gets the what and the why in one pass, with no ambiguity about which change did what.

This skill governs the message text only. It takes no position on what to stage, what to commit, or when — those decisions belong to the user and to the session that made the changes.

## Format

**Summary line**
- Imperative mood, 50 characters or fewer, no trailing period.
- Start with a verb from the approved list below.
- Name the component or behavior, not the file: `Fix session timeout in quote editor`, not `Fix QuoteController.cs`.
- Articles may be dropped here only, to fit the length limit.

**Body** (blank line after the summary)
- One bullet per logical change; one sentence per bullet. A change is one logical modification, not one file.
- 20 words maximum per sentence.
- Active voice, present tense: `Replace X with Y`, never `X was replaced`.
- No *-ing* form as the main verb: `Add validation`, not `Adding validation`.
- Keep articles (a, an, the) in the body. Telegraphic style creates ambiguity.
- No noun cluster longer than 3 nouns: `the snapshot job for inventory levels`, not `the inventory level snapshot close-and-append job`.

**Why paragraph** (blank line after the bullets)
- 1–3 sentences, 25 words maximum each.
- State cause or effect: what problem this solves, what it unblocks, or what breaks without it.
- Name the related work unit or issue here if one exists (for example, `WU-014`).
- When referencing issue identifiers give a description or micro summary of the issue. For example 'WU-014  Layout, partials, and static files', any further references to the issue in the commit only use the identifier. Don't repeat the summary.

A trivial commit (typo, single-line config change) may use the summary line alone. Everything else gets all three parts.

## Approved verbs — one verb, one meaning

STE's core rule is one meaning per word. The summary line must start with one of these verbs, used with exactly this meaning:

| Verb | Meaning |
|---|---|
| Add | Create something that did not exist |
| Remove | Delete something entirely |
| Replace | Remove one thing and add another that serves the same purpose |
| Fix | Correct a defect |
| Rename | Change a name only; behavior unchanged |
| Move | Change a location only; behavior unchanged |
| Extract | Pull code into a new unit; behavior unchanged |
| Refactor | Restructure code; behavior unchanged (when Extract, Move, or Rename is too narrow) |
| Upgrade | Raise a dependency, framework, or platform version |
| Configure | Change settings or configuration values |
| Enable | Turn a feature or code path on |
| Disable | Turn a feature or code path off |
| Revert | Undo a previous commit |
| Document | Change documentation or comments only |
| Test | Add or change tests only |

`Update`, `improve`, `enhance`, `clean up`, and `misc` are forbidden as summary verbs. Each has many meanings, so it has none.

In the body, any verb is allowed if it has one clear meaning in context (`Migrate`, `Convert`, `Register`). Use the same verb for the same action throughout one message — do not alternate `remove` and `delete` for the same operation.

## Technical Names

Identifiers, SQL keywords, table names, and package names are Technical Names. Write them exactly as they appear in the code: `SignInManager`, `DATETIME2(3)`, `Microsoft.AspNetCore.Identity`, `fct.InventoryLevelDaily`. Vocabulary rules never override an identifier's real spelling.

## Example

```
Replace SimpleMembership with ASP.NET Core Identity

- Remove the WebMatrix packages and the SimpleMembership initializer.
- Add Identity services in Program.cs with cookie authentication.
- Migrate webpages_Membership rows to AspNetUsers with a SQL script.
- Replace the SimpleMembership calls in AccountController with SignInManager.

SimpleMembership does not run on .NET 10. This change unblocks
WU-012 and keeps the existing password hashes valid.
```

## Do not write

- Vague summaries: `Update files`, `Fix issues`, `Changes`.
- Passive voice anywhere in the message.
