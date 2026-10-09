# Claude Dashboard — project context for Claude Code

Claude Dashboard is a Windows tray app (C# / .NET 10 / WPF) that shows a developer, at a glance, which of their many concurrent Claude Code sessions need attention — what needs them now, what finished unseen, what's still working. Its world is event-sourced from Claude Code hooks; it never polls, and it never blocks a Claude turn.

## Read these first (authoritative — never contradict)

The documents live in `docs/`:

- `docs/claude-dashboard-spec.md` — **Technical Specification** (TS): technology-agnostic architecture and the *why*. Appendix C lists everything that is specified and not built.
- `docs/claude-dashboard-impl-spec.md` — **Implementation Specification** (Impl): the C#/.NET/WPF *how*, with exact values. §3.5 is the `/state` contract, §5.6 is what the window shows, Part 8 is the data folder, Part 9 is the Claude Code hook contract.
- `docs/claude-dashboard-design.md` — **Design Document**: the product shape and the *what*. §9 is the authority on row anatomy and the motion rule.
- `docs/claude-dashboard-event-flow.md` — **Event flow**: one hook event, step by step, from Claude Code to the window, the speaker and the database, with the file that holds each step.
- `docs/claude-dashboard-core-and-app.md` — **Core and App**: which project holds which rule, and what a second interface (web or phone) needs.
- `docs/claude-code-hooks-reference.md` — **Hook events reference**: all 33 Claude Code hook events transcribed from the source documentation, the eight we consume first. Consult this before asserting anything about what a hook does or what fields it carries — including the Discrepancies section, which records where the documentation and the real payload disagree. Where they disagree, the payload is the authority.
- `docs/claude-dashboard-execution-plan.md` — **Execution Plan**: the phased task graph and acceptance criteria, the agent role prompts (Appendix B), and the orchestration runbook (Appendix C). It is a plan and a record: where it disagrees with the specifications about what the product does today, the specifications are right.
- `docs/claude-dashboard-usage-mod-guide.md` — **Usage Mod Development Guide**: the mod that runs inside Claude Code and posts the plan's usage (`session.measure`) to the dashboard: what it does, why, and the lab that proved it.
- `docs/claude-dashboard-usage-mod-execution-plan.md` — **Usage Mod Execution Plan**: the side plan that built it (MOD.1 to MOD.6, rulings R1 to R7), a plan and a record like the Execution Plan; the specifications are right where they disagree.
- `docs/quiet-scheduled-jobs.md` — a guide for the operator. A test holds its opt-in line to the code, word for word.
- `docs/claude-dashboard-mockups.html` — UI reference. **Visuals only, never ordering** — its flat view is drawn in the superseded pre-ruling order.

**Keep the documents true.** A change of behaviour changes the affected sections in the same change. Rewrite the statement in place and keep the reason beside it; add one row to that document's change history (TS Appendix D, Impl Appendix C, Design §13). Mark an intent that the code does not meet *not built*, and list it in TS Appendix C. Keep section numbers stable: code comments cite them.

## How this repo is built

Development runs as three independent Claude Code sessions coordinating over **cross-session messaging**: a **director** drives the Execution Plan, dispatching one task at a time to a **coder** and routing each completed change to a **reviewer**; the human is the escalation point. The sessions are named `director`, `coder`, and `reviewer`, and message each other with the `SendMessage` and `ListAgents` tools (the director uses `SendMessage`'s `notify_when_idle` to learn when a task is done). Full protocol: Execution Plan **Appendix B**; setup and launch: **Appendix C**. Messages are **text only — code moves through git**, so every hand-off references a commit and files, not attachments.

## Non-negotiable working agreements

Every session follows these (full list: Execution Plan Part 1):

- **Dependency rule:** `ClaudeDashboard.Core` contains no WPF, Win32, or ASP.NET; nothing references `ClaudeDashboard.App`. OS-specific code lives in App behind interfaces.
- **Domain invariants:** state transitions are idempotent and timestamp-guarded; the Registry has exactly one writer and no locks. A click never changes the Registry directly: an Ack, a mute and a pause are events in the same channel as the hooks.
- **Pure-observer ingress:** hook endpoints return `200` empty and never a decision field — the dashboard can never block or alter a Claude turn.
- **Text is data:** hook and message text is stored and rendered, never executed.
- **The log file may hold any text:** a title, a name, a prompt or an answer may appear in a log line; the same text is in `dashboard.db` beside it (the operator's ruling, 2026-10-05). **Never log the token:** it is a credential (Impl §3.4).
- **Never write Claude Code's settings:** the dashboard reads `~/.claude/settings.json` and never writes it. It connects through its plugin only (Impl §9.3, §9.4).
- **Degrade, never crash:** an adapter that fails — sound, storage, virtual desktop, and later UI Automation and WinEvent — downgrades a feature rather than throwing.
- **Never run elevated. No secrets in committed files.** Every Core behavior ships with xUnit tests.

## Status

**Phase 1 is built and released as a pre-release** (0.0.x; the Execution Plan's tasks run through T1.83, with Milestone 1F covering GitHub milestones 3, 4 and 5, "Observability 1" to "Observability 3"; the Usage Mod Execution Plan's MOD.1 to MOD.5 are built, and MOD.6, the operator's check of the mod in a real Windows session, is open). The tray app receives Claude Code hooks through its plugin, shows the panel and the tray light, plays notices and nudges, and keeps an event log. A row that just made a sound shows a still speaker sign for a minute. The **Activity window** lists, in plain words, what the dashboard did since it started (state changes, sounds played, and sounds not played with the cause); it is kept in memory, never read from the database, and a click on a line opens its row. The dashboard also reports on itself: it tests the path from Claude Code at each start, shows notices when history, sound, settings or messages fail, answers its counts and timings in the `health` object of `/state`, and writes a summary each hour. The history database records each run, stores its times in UTC, has indexes, stores the session's name and path on each row, and keeps history for Claude Code's `cleanupPeriodDays` (30 days when the key is absent; `history.retentionDays` is no longer used). A roster (an orchestration of several sessions) counts once in the counts strip, the tray and `/state`. The plugin also carries the **usage mod**: at the end of each turn it posts the plan's usage (the 5-hour and the weekly percentage, with the reset time) to `/usage`, and `/state` answers the newest reading of each limit as `usage`; the window and the tray do not show it yet. **Phases 2 to 7 are not built:** click-to-navigate, focus acknowledgment, virtual-desktop grouping, history search, the full settings interface, and the remote surface. `ClaudeDashboard.Remote` is an empty project.
