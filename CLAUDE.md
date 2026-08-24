# Claude Dashboard — project context for Claude Code

Claude Dashboard is a Windows tray app (C# / .NET 10 / WPF) that shows a developer, at a glance, which of their many concurrent Claude Code sessions need attention — what needs them now, what finished unseen, what's still working. Its world is event-sourced from Claude Code hooks; it never polls, and it never blocks a Claude turn.

## Read these first (authoritative — never contradict)

Planning docs live in `docs/`:

- `docs/claude-dashboard-spec.md` — **Technical Specification** (TS): technology-agnostic architecture and the *why*.
- `docs/claude-dashboard-impl-spec.md` — **Implementation Specification** (Impl): the C#/.NET/WPF *how*, libraries, and the Claude Code hook contract.
- `docs/claude-dashboard-execution-plan.md` — **Execution Plan**: the phased task graph and acceptance criteria, the agent role prompts (Appendix B), and the orchestration runbook (Appendix C).
- `docs/claude-dashboard-mockups.html` — UI reference.

## How this repo is built

Development runs as three independent Claude Code sessions coordinating over **cross-session messaging**: a **director** drives the Execution Plan, dispatching one task at a time to a **coder** and routing each completed change to a **reviewer**; the human is the escalation point. The sessions are named `director`, `coder`, and `reviewer`, and message each other with the `SendMessage` and `ListAgents` tools (the director uses `SendMessage`'s `notify_when_idle` to learn when a task is done). Full protocol: Execution Plan **Appendix B**; setup and launch: **Appendix C**. Messages are **text only — code moves through git**, so every hand-off references a commit and files, not attachments.

## Non-negotiable working agreements

Every session follows these (full list: Execution Plan Part 1):

- **Dependency rule:** `ClaudeDashboard.Core` contains no WPF, Win32, or ASP.NET; nothing references `ClaudeDashboard.App`. OS-specific code lives in App behind interfaces.
- **Domain invariants:** state transitions are idempotent and timestamp-guarded; the Registry has exactly one writer and no locks.
- **Pure-observer ingress:** hook endpoints return `200` empty and never a decision field — the dashboard can never block or alter a Claude turn.
- **Text is data:** hook and message text is stored and rendered, never executed.
- **Degrade, never crash:** UI Automation, WinEvent, and virtual-desktop adapters downgrade a feature on failure rather than throwing.
- **Never run elevated. No secrets in committed files.** Every Core behavior ships with xUnit tests.

## Status

Planning complete; implementation begins at Execution Plan task **T1.0** (solution scaffolding). Until T1.0 lands, this repo holds planning artifacts and orchestration config only.
