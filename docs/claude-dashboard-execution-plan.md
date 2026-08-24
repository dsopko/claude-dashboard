# Claude Dashboard — Execution Plan

**Draft v0.1 · 2026-08-22 · for the director agent**

## Part 0 — How to use this plan

This plan is written to be consumed by a **director agent** that turns it into prompts for a **coder agent**. It is not itself the architecture — the architecture lives in two companion documents, and this plan points into them rather than repeating them:

- **Technical Specification** (`claude-dashboard-spec.md`, "TS") — the *why*, technology-agnostic.
- **Implementation Specification** (`claude-dashboard-impl-spec.md`, "Impl") — the *how*, C#/.NET 10/WPF.

**The director's loop, per task:**

1. Take the next task whose dependencies are all `Done`.
2. Build one coder prompt from it using the skeleton in Part 5, pasting in the task's spec references and the global working agreements (Part 1).
3. Hand it to the coder; require the task's **acceptance criteria** to be met and its named tests to pass before marking it `Done`.
4. Do not batch multiple tasks into one prompt, and do not advance past a task whose acceptance criteria are unmet. One task ≈ one coder prompt ≈ one commit/PR.

**Scope of this draft:** Phase 1 is specified at task level (Part 3) because it is what gets built now. Phases 2–7 are task *outlines* (Part 4) — enough to sequence and plan, to be expanded into full task blocks when their phase is reached.

---

## Part 1 — Global working agreements (inject into every coder prompt)

These hold for every task. The director should include them (or a link to them) in every prompt so the coder never has to re-derive them.

**Architecture & dependencies**
- Three projects (Impl §1.2). `ClaudeDashboard.Core` has **no** WPF, Win32, COM, or ASP.NET references. `ClaudeDashboard.App` may reference Core; **nothing references App**. `ClaudeDashboard.Remote` (later) references Core only.
- All OS-specific behavior lives in App **behind port interfaces** declared for Core (Impl §1.3). The coder implements adapters, never calls Win32/UIA from Core.

**Domain invariants**
- State transitions are **idempotent** and **timestamp-guarded**: re-applying an event is a no-op; an event older than the session's last-applied stamp is dropped (TS §I.2, §IV.1).
- The `SessionRegistry` is mutated by **exactly one thread** (the event consumer); therefore **no locks** inside it (Impl §4).
- Ingress hooks are **pure observers**: `/hook` returns `200` empty and never a decision field, so the dashboard can never block or alter a Claude turn (Impl §3.3).
- **All hook text is data** — prompt/answer strings are stored and rendered, never executed or interpreted (Impl §3.4).

**Safety & degradation**
- OS adapters (UIA, WinEvent, virtual desktop) **degrade, never crash**: a failure downgrades a feature to a coarser fallback (TS §IV.7). Wrap them; surface faults to logs, not to the process.
- The app runs at **normal integrity, never elevated** (Impl §6.5).
- **No secrets in committed files** — the ingress token comes from an environment variable (Impl §3.4, §9.2).

**Engineering standards**
- Nullable reference types enabled; warnings-as-errors on Core at minimum; analyzers on.
- `async` end to end on I/O paths; never block the WPF Dispatcher thread.
- Every Core behavior (state machine, attention ordering, grouping, sound policy) ships with **xUnit tests**. Adapters get contract tests against fakes where feasible; genuinely OS-bound behavior is verified by a documented manual smoke test.
- Small, single-purpose commits, one per task.

**Definition of Done (global):** builds clean; named tests green; acceptance criteria met; no cross-layer leakage (verified by the dependency rule); logs on the new paths.

---

## Part 2 — Milestones

| Phase | Outcome | Exit criteria |
|---|---|---|
| **1 — See clearly** | Resident tray app shows every live session's state, banded and grouped, with sound and manual/auto ack — no Windows integration. | Real Claude Code sessions across ~15 terminals light up the dashboard and tray correctly; notices/nudges fire; ack tiers 1–2 work; survives logon restart. |
| **2 — Go there** | Click a row → its terminal tab comes forward. | Navigation resolves the correct tab via content-matching for the common case; degrades to window-level otherwise. |
| **3 — It notices** | Looking at a terminal acknowledges it; on-screen sessions don't beep. | Focus inference acks at window (then tab) granularity; suppression works. |
| **4 — Task lens** | Grouping by virtual desktop, with desktop names. | Sessions group by desktop; degrades to cwd grouping if VD breaks. |
| **5 — Memory** | Searchable history + wait-time stats; warm restart. | 30-day event history queryable; restart rebuilds recent state. |
| **6 — Polish** | Settings UI, sound editor, themes, task/hook repair. | Settings editable in-app; setup repairable. |
| **7 — Anywhere** | Authenticated phone read/ack surface. | Remote consumer reads state and acks over an authenticated channel. |

---

## Part 3 — Phase 1 tasks (detailed)

Grouped into four sub-milestones. Each task lists Goal · Depends · Realizes · Deliverables · Acceptance · Guardrails. Tests named in Acceptance are required.

### Milestone 1A — Core (portable domain, no host)

**T1.0 — Solution & project scaffolding**
- **Goal:** create the four-project solution with correct TFMs, references, and analyzer/nullable settings.
- **Depends:** —
- **Realizes:** Impl §1.1–1.2
- **Deliverables:** solution; `Core` (`net10.0`), `App` (`net10.0-windows`, WPF), `Remote` stub (`net10.0`), `Tests` (`net10.0`, xUnit); reference wiring; nullable + analyzers; a build script.
- **Acceptance:** solution builds; `dotnet test` runs an empty suite; an architecture test (or documented reference check) fails if Core gains a WPF/Win32/ASP.NET reference or anything references App.
- **Guardrails:** dependency rule (Part 1).

**T1.1 — Core domain types**
- **Goal:** the immutable domain vocabulary.
- **Depends:** T1.0
- **Realizes:** TS §4; Impl §2.1
- **Deliverables:** `SessionId`, `Exchange`, `SessionState` enum, `Session`, `Group`, and the `InboundEvent` record hierarchy (one variant per consumed event, Impl §9.1).
- **Acceptance:** tests for construction, value-equality, and the `InboundEvent` variants carrying the fields from Impl §9.1.
- **Guardrails:** Core-only; no behavior yet.

**T1.2 — SessionRegistry & state machine**
- **Goal:** apply events to sessions per the state machine.
- **Depends:** T1.1
- **Realizes:** TS §IV.1; Impl §2.2
- **Deliverables:** `SessionRegistry` with `Apply(InboundEvent)`; the full transition table; a change-notification event.
- **Acceptance:** tests for every transition (Working→Unread on Stop; →NeedsPermission/NeedsQuestion on the Notification variants; →Error on StopFailure; →Ended on SessionEnd; →Working + **auto-ack** of prior Unread/Needs-You on UserPromptSubmit; manual/synthetic Ack→Acked); **idempotency** (same event twice = one effect); **stale-drop** (older-timestamp event ignored).
- **Guardrails:** single-writer assumption (no locks); `IClock` for time.

**T1.3 — Attention engine**
- **Goal:** band and order sessions for display.
- **Depends:** T1.2
- **Realizes:** TS §IV.2; Impl §2.3
- **Deliverables:** pure `Order(sessions) → banded ordered list`.
- **Acceptance:** tests proving Needs-You **oldest-first**, Unread **newest-first**, then Working/Quiet/Ended; band precedence; and the grouped case ordering within groups with groups sorted by most-urgent member.
- **Guardrails:** pure/deterministic; no side effects.

**T1.4 — Group resolver**
- **Goal:** derive groups from `cwd`.
- **Depends:** T1.2
- **Realizes:** TS §IV.3; Impl §2.1
- **Deliverables:** grouping by `Cwd`; worst-member-state and most-recent-activity per group; re-derivation when a session's `cwd` changes.
- **Acceptance:** tests for grouping, worst-state roll-up, recency, and re-grouping on cwd change.
- **Guardrails:** Phase 1 key is `cwd` only (desktop grouping is Phase 4).

**T1.5 — Sound-policy engine**
- **Goal:** decide when notices and nudges fire.
- **Depends:** T1.2
- **Realizes:** TS §IV.5; Impl §2.4
- **Deliverables:** engine emitting `PlayNotice`/`PlayNudge(gain)` intents against `ISoundPlayer`, driven by Registry state and `IClock`; per-session/group mute honored.
- **Acceptance:** tests for notice-on-entry; nudge after T₁ with **widening** intervals (2→5→10 min); **cancel on Acked**; Unread at most one soft nudge; mute suppresses.
- **Guardrails:** emits intents only; never touches audio APIs.

**T1.6 — Port interfaces**
- **Goal:** declare the host seam.
- **Depends:** T1.1
- **Realizes:** Impl §1.3
- **Deliverables:** `IClock`, `ISoundPlayer`, `IEventSink`, and the (as-yet-unimplemented) `ITerminalLocator`, `IFocusSource`, `ITerminalNavigator`, `IVirtualDesktopService`.
- **Acceptance:** compiles; Core references none of their implementations; fakes exist in Tests for the ones Phase 1 uses (`IClock`, `ISoundPlayer`, `IEventSink`).
- **Guardrails:** interfaces only.

### Milestone 1B — Host, ingress, pipeline

**T1.7 — Generic Host bootstrap**
- **Goal:** the app process skeleton.
- **Depends:** T1.0
- **Realizes:** Impl §3.1, §5.1, §10.1
- **Deliverables:** .NET Generic Host with DI; config loaded from `%LOCALAPPDATA%\ClaudeDashboard\settings.json`; Serilog rolling logs; global exception handlers (`AppDomain`, `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`); `ShutdownMode.OnExplicitShutdown`.
- **Acceptance:** app starts headless and logs; a thrown test exception is caught and logged, not fatal; settings round-trip.
- **Guardrails:** no business logic here.

**T1.8 — Ingress endpoint + payload mapping**
- **Goal:** receive hooks and normalize them.
- **Depends:** T1.7, T1.1
- **Realizes:** Impl §3.2, §9.1
- **Deliverables:** Kestrel minimal API bound to loopback fixed port; `POST /hook` (deserialize by `hook_event_name`, map event-specific fields per Impl §9.1 to an `InboundEvent`, enqueue, return `200` empty); `POST /show`; `GET /health`; `X-Dashboard-Token` check.
- **Acceptance:** tests mapping **real sample payloads** for each consumed event (using the exact field names — `prompt`, `last_assistant_message`, the `Notification`/`StopFailure` matcher values, `source`, `cwd`) to the right `InboundEvent`; `/hook` returns `200` empty; bad/missing token rejected; binds loopback only.
- **Guardrails:** no Registry work on the request thread; pure-observer (no decision fields).

**T1.9 — Event pipeline (Channel + consumer + Dispatcher marshalling)**
- **Goal:** the one crossing point.
- **Depends:** T1.8, T1.2
- **Realizes:** Impl §4
- **Deliverables:** `Channel<InboundEvent>`; `EventConsumer : BackgroundService` (single reader) applying to the Registry; marshalling of Registry change-notifications onto `Application.Current.Dispatcher` into an `ObservableCollection`.
- **Acceptance:** concurrency test — many concurrent producers, single consumer, timestamp order preserved, no corruption; marshalling smoke test; a burst of simultaneous events doesn't stall the producers.
- **Guardrails:** exactly one consumer; no locks in the Registry.

### Milestone 1C — UI, tray, ack, sound

**T1.10 — ViewModels**
- **Goal:** expose the banded model to WPF.
- **Depends:** T1.9, T1.3, T1.4
- **Realizes:** Impl §5.5; TS §5, §7
- **Deliverables:** `SessionViewModel`, `GroupViewModel`, `MainViewModel` (banded `ObservableCollection`, grouped/flat toggle, counts strip) via CommunityToolkit.Mvvm.
- **Acceptance:** tests/harness showing the VM reflects Registry changes and orders via the attention engine; grouped/flat toggle re-projects the same data.
- **Guardrails:** no OS calls; ordering comes from Core.

**T1.11 — Main window & rows**
- **Goal:** the dashboard UI matching the mockups.
- **Depends:** T1.10
- **Realizes:** TS §5–§9; mockups
- **Deliverables:** main window; session row (status LED, monospace prompt snippet, state+age line, ack affordance on Unread); grouped & flat views with labeled bands; **expanded row** showing the You-asked/Claude-answered exchange; collapse rules (stale group → one line; acked → "+ k quiet" footer; **Unread always full row**).
- **Acceptance:** renders from the live Registry; structure matches the mockups; **motion only** red-blink and working-breathe; collapse rules behave.
- **Guardrails:** presentation only; honor reduced-motion.

**T1.12 — Ack tiers 1–2**
- **Goal:** mark results seen.
- **Depends:** T1.11, T1.9
- **Realizes:** TS §4 (Acknowledgment), §IV.1
- **Deliverables:** auto-ack (already in the state machine via next `UserPromptSubmit`) verified end-to-end; **manual ack** via the row/expanded button emitting a synthetic ack `InboundEvent` into the Channel.
- **Acceptance:** manual ack → Acked and the row greys/collapses; a new prompt auto-acks a prior Unread; both travel the same pipeline.
- **Guardrails:** ack is an event through the Channel, not a direct Registry poke from the UI.

**T1.13 — Tray status light**
- **Goal:** the always-on status glyph.
- **Depends:** T1.9
- **Realizes:** TS §9; Impl §5.2, §5.1
- **Deliverables:** H.NotifyIcon tray icon; worst-state roll-up **Red > Amber > Green > Blue > Grey**; **tooltip carries counts**; **static** (no animation); left-click toggles the window; right-click menu (Open · Mute all / 30 min · Pause monitoring · Settings · Quit); window close → hide to tray.
- **Acceptance:** icon color tracks the worst current state; tooltip shows the counts; close hides; Quit exits; menu items wired (Settings may be a stub until Phase 6).
- **Guardrails:** color carries state, not digits; no elevation.

**T1.14 — Sound adapter (NAudio)**
- **Goal:** play notices and nudges with volume.
- **Depends:** T1.5, T1.7
- **Realizes:** TS §IV.5; Impl §7
- **Deliverables:** `ISoundPlayer` over NAudio — per-sound gain (notice vs nudge), fade-in for nudges, a mixer to coalesce bursts, master volume + per-session/group mute; sound files in the app dir with user-override under the config dir.
- **Acceptance:** notice and nudge play at different gains from the **same** file; a burst coalesces rather than stacking; mute silences.
- **Guardrails:** driven by the policy engine's intents.

**T1.15 — Single instance**
- **Goal:** one resident process.
- **Depends:** T1.8
- **Realizes:** Impl §5.3
- **Deliverables:** named `Mutex` at startup + the loopback port bind as interlock; a second instance `POST /show` to the first, then exits.
- **Acceptance:** launching a second copy surfaces the first's window and the second exits; no port/mutex leak on clean exit.
- **Guardrails:** reuse ingress for the signal (no separate IPC).

**T1.16 — DPI + pin-to-all-desktops + placement**
- **Goal:** correct rendering and always-present window.
- **Depends:** T1.11, T1.6
- **Realizes:** Impl §5.4, §6.3 (documented tier only)
- **Deliverables:** Per-Monitor v2 in `app.manifest`; a **minimal** `IVirtualDesktopService` adapter (MScholtes, documented calls) exposing just `PinToAllDesktops`; pin the window to all desktops; restore last position, else open on the focused monitor; always-on-top toggle (default **off**).
- **Acceptance:** window stays crisp when dragged between differently-scaled monitors; appears on every virtual desktop; position restores.
- **Guardrails:** documented VD calls only (full grouping is Phase 4); adapter degrades if VD is unavailable.

### Milestone 1D — Persistence, integration, packaging

**T1.17 — SQLite event log**
- **Goal:** durably record events.
- **Depends:** T1.9
- **Realizes:** Impl §8
- **Deliverables:** `dashboard.db` (Microsoft.Data.Sqlite); append-only `events(id, session_id, ts, event_type, payload_json, cwd)`; write every `InboundEvent`.
- **Acceptance:** events persist across runs; write path is off the UI thread; **no pruning yet** (retention is Phase 5).
- **Guardrails:** write-only in Phase 1 (no read-back required); don't block the consumer on disk.

**T1.18 — First-run setup (the integration milestone)**
- **Goal:** make Claude Code feed the dashboard.
- **Depends:** T1.8
- **Realizes:** Impl §9.2–9.3, §10.2
- **Deliverables:** register the **logon scheduled task** (restart-on-failure; normal integrity); **merge** the hook config + `allowedHttpHookUrls` + `httpHookAllowedEnvVars` into `~/.claude/settings.json` without clobbering existing hooks; write `port.txt`; ensure `CLAUDE_DASHBOARD_TOKEN` exists (generate + set if absent).
- **Acceptance:** on a clean profile, running setup then starting a Claude Code session causes real events to reach `/hook` and drive the dashboard; existing user hooks in `settings.json` are preserved.
- **Guardrails:** parse-merge-write settings (never overwrite the file); token via env var only.

**T1.19 — Packaging (self-contained single-file)**
- **Goal:** a shippable exe that autostarts.
- **Depends:** T1.7, T1.18
- **Realizes:** Impl §10.2
- **Deliverables:** a `dotnet publish -c Release -r win-x64 --self-contained` single-file profile; the logon task points at the published exe.
- **Acceptance:** the published exe launches at logon via the task and runs headless-to-tray; no machine-wide runtime required. **Not MSIX.**
- **Guardrails:** —

**T1.20 — Phase 1 end-to-end acceptance**
- **Goal:** prove the slice under real load.
- **Depends:** T1.11–T1.19
- **Realizes:** Phase 1 exit criteria (Part 2)
- **Deliverables:** a documented E2E run.
- **Acceptance:** across ~15 real Claude Code terminals: states and bands are correct; the tray light rolls up correctly; notices/nudges fire and coalesce; manual + auto ack behave; the app survives a logon restart and a forced crash (relaunches via the task).
- **Guardrails:** this task gates the phase.

---

## Part 4 — Phases 2–7 task outlines

Expand each into full task blocks (Part 3 format) when the phase is reached.

**Phase 2 — Go there** *(TS §III.2, §III.7, §III.8; Impl §6.1, §6.4)*
- T2.1 `ITerminalLocator` via FlaUI: enumerate Windows Terminal windows/tabs, read pane text (UIA Text pattern), match against Registry exchange text; ambiguity → unresolved. Acceptance: finds the right tab for the common case; degrades to window-level.
- T2.2 `ITerminalNavigator`: `wt.exe -w <window> focus-tab -t <index>` when resolved, else `SetForegroundWindow` + UIA invoke. Acceptance: brings the correct tab forward; click-initiated so foreground isn't blocked.
- T2.3 Wire the expanded-row "Open terminal" action to the navigator. Acceptance: clicking navigates; failures degrade, don't throw.
- T2.4 Per-terminal locate strategy (WT vs classic console). Acceptance: classic-console path uses process-tree location.

**Phase 3 — It notices** *(TS §III.5; Impl §6.2)*
- T3.1 `IFocusSource`: `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` on a message-pumped thread; raise window-focus events.
- T3.2 Focus→session via `ITerminalLocator.IdentifyForegroundTab`; dwell threshold → synthetic ack into the Channel.
- T3.3 Tab-level focus via UIA selection events (refinement over window-level).
- T3.4 On-screen notice suppression: mute the notice for the session currently focused.

**Phase 4 — Task lens** *(TS §III.9; Impl §6.3)*
- T4.1 Extend `IVirtualDesktopService` to read a window's desktop id (documented tier).
- T4.2 Swap the group key to virtual desktop; desktop names as labels; degrade to cwd grouping if VD breaks.
- T4.3 Harden the undocumented-tier calls behind the adapter; version-pin.

**Phase 5 — Memory** *(Impl §8)*
- T5.1 30-day rolling prune of `events`.
- T5.2 History search over past exchanges.
- T5.3 Wait-time stats (how long agents sit blocked).
- T5.4 Warm-restart: rebuild recent Registry state from `dashboard.db`.

**Phase 6 — Polish**
- T6.1 Settings UI (thresholds, sounds, mutes, view, always-on-top, port).
- T6.2 Sound editor.
- T6.3 Themes.
- T6.4 Task/hook repair from within the app.

**Phase 7 — Anywhere** *(TS §I.4, §11; Impl §1.2)*
- T7.1 `ClaudeDashboard.Remote` (ASP.NET Core + SignalR) as a second Core consumer.
- T7.2 Authenticated channel; remote **read** of states.
- T7.3 Remote **ack**.

---

## Part 5 — Per-task prompt skeleton (for the director)

Fill the placeholders from the task block and Part 1, then hand to the coder:

```
You are implementing ONE task for the Claude Dashboard (C# / .NET 10 / WPF).

Read first (authoritative — do not contradict):
  • Technical Spec sections: <task "Realizes" TS refs>
  • Implementation Spec sections: <task "Realizes" Impl refs>

Global rules (must hold):
  <paste Part 1 working agreements, or the relevant subset>

Task <ID> — <name>
Goal: <task goal>
Build: <task deliverables>
Constraints: <task guardrails>

Done when (all required):
  <task acceptance criteria, as a checklist>
Write the tests named above and make them pass.

Do NOT:
  • touch anything outside this task's deliverables
  • add NuGet dependencies beyond those in Impl Appendix A for this layer
  • violate the dependency rule (Core has no WPF/Win32/ASP.NET; nothing references App)

Deliver: the code + tests, plus a one-line summary of what changed and any assumption you made.
```

**Director guidance:** keep tasks in dependency order (Part 3 milestones 1A→1D); never merge two tasks into one prompt; if the coder's output misses an acceptance criterion, send it back with the specific unmet criterion rather than proceeding.

---

## Appendix A — Phase 1 dependency order

```
T1.0
 ├─ T1.1 ─ T1.2 ─┬─ T1.3 ─┐
 │               ├─ T1.4 ─┤
 │               └─ T1.5 ─┤
 ├─ T1.6         (Core)   │
 └─ T1.7 ─ T1.8 ─ T1.9 ───┼─ T1.10 ─ T1.11 ─ T1.12
                          │            └─ T1.16
                          ├─ T1.13
                          ├─ T1.14 (needs T1.5)
                          ├─ T1.15
                          └─ T1.17
 T1.8 ─ T1.18 ─ T1.19
 (all) ─ T1.20   ← phase gate
```

---

## Appendix B — Agent role prompts (over cross-session messaging)

These operationalize the workflow over **Claude Code cross-session messaging**: three independent sessions you start yourself in separate terminals — named `director`, `coder`, and `reviewer` — that message each other with the `SendMessage` and `ListAgents` tools. Setup and launch are in **Appendix C**. All three share the four project documents (mockups, TS, Impl, this plan); the Handoff Contract (B.0) defines the message payloads. Paste each role block as that session's instructions (Appendix C shows how to attach it) and set `<DOCS_DIR>`.

> **Because cross-session messaging is very new, each role prompt names the tools explicitly.** Don't assume the model reaches for `SendMessage`/`ListAgents` on its own — the prompts below tell it exactly when to send, to whom, and what.

### B.0 — Handoff contract

The agents coordinate by sending each other plain-text messages with `SendMessage`, addressed by session name (`director`, `coder`, `reviewer`); `ListAgents` (or the `/list-agents` command) shows who's reachable. A message carries **only text — never files or conversation history** — so all code and artifacts move through the **git repository**, and these messages carry just the coordination text below. The **Task/Fix Prompt**, **Status Report**, **Review Request**, and **Verdict** travel over `SendMessage`. The **Resurface** and **Progress Update** are *not* messages — they are the director speaking in its own terminal, where you're watching.

**Director → `coder` — Task Prompt** (via `SendMessage`) — the Part 5 skeleton, placeholders filled from the task block. Because the message is text-only, it points the coder at the task by ID and spec refs; the coder reads the docs from the repo itself.

**Director → `coder` — Fix Prompt** (via `SendMessage`) — the same task, with an added `Fix these (from review):` list quoting the reviewer's required changes verbatim.

**Coder → `director` — Status Report** (via `SendMessage`)
```
STATUS REPORT
Task: <ID> — <name>
Status: DONE | BLOCKED | QUESTION
Summary: <what was built, 1–3 sentences>
Commit/Files: <commit ref + changed files, so the director and reviewer can find it in the repo>
Tests: <named tests> → <n passed / n failed>
Assumptions: <any assumption made because the spec left a gap>
Deviations: <anything done differently from the task block, and why> | none
Problem: <only if BLOCKED/QUESTION — the exact blocker or question>
```

**Director → `reviewer` — Review Request** (via `SendMessage`)
```
REVIEW REQUEST
Task: <ID> — <name>
Realizes: <TS refs> · <Impl refs>
Acceptance: <the checklist from the task block>
Guardrails: <from the task block>
Change: <commit ref + files to review — the reviewer reads the actual diff from the repo>
Coder notes: <the coder's Summary + Tests + Assumptions + Deviations>
```

**Reviewer → `director` — Verdict** (via `SendMessage`)
```
VERDICT
Task: <ID> — <name>
Verdict: APPROVE | CHANGES_REQUESTED | ESCALATE
Findings:
  - Plan adherence: <pass | issue>
  - Spec compliance: <pass | issue, with TS/Impl §>
  - Working agreements: <pass | issue>
  - Tests: <pass | issue>
  - Code quality: <pass | notes>
Required changes: <numbered, specific, each tied to a criterion or spec § — only if CHANGES_REQUESTED>
Escalate because: <the spec conflict / ambiguity / cross-task design concern — only if ESCALATE>
```

**Director → You — Resurface** (spoken in the director's own terminal, not a message)
```
NEEDS YOU
Where: Task <ID> — <name>
What happened: <the blocker, escalation, repeated failure, phase gate, or spec conflict>
Options: <the choices, if it's a decision>
Recommendation: <the director's suggested course>
```

**Director → its own terminal — Progress Update** (the always-visible heartbeat, one line per exchange)
```
[Dashboard ▸ <ID>] <who> <what> → <next action>
```

### B.1 — Coder  · session name `coder`

You are the **Coder** on the Claude Dashboard project. You implement **one task at a time**, exactly to spec, with tests, and report back to the director. You do not pick your own tasks and you do not start work beyond the task you were handed.

**Messaging.** You receive tasks as incoming cross-session messages from the session named `director`. You report back by sending a message to `director` with `SendMessage` (Claude Code exposes `ListAgents` to find it and `SendMessage` to deliver). A message is **text only** — it can't carry files — so you do the work as **commits in the git repo**, and your Status Report names the commit and changed files so the director and reviewer can find them. You never message the reviewer; the director routes review.

**The project.** Claude Dashboard is a Windows tray app that shows a developer, at a glance, which of their many concurrent Claude Code sessions need attention. Its world is event-sourced from Claude Code hooks. Full context is in four documents in `<DOCS_DIR>`, which are **authoritative — never contradict them**: `claude-dashboard-spec.md` (Technical Spec, "TS"), `claude-dashboard-impl-spec.md` (Implementation Spec, "Impl"), `claude-dashboard-execution-plan.md` (this plan), `claude-dashboard-mockups.html` (UI reference).

**Tech stack.** C# on .NET 10 (LTS), WPF. Three projects: `ClaudeDashboard.Core` (portable domain, **no** WPF/Win32/ASP.NET), `ClaudeDashboard.App` (WPF host + ingress + Windows integration), `ClaudeDashboard.Remote` (later), `ClaudeDashboard.Tests` (xUnit). Add no dependencies beyond Impl Appendix A for your layer without flagging it.

**Working agreements** (Part 1): Core free of WPF/Win32/ASP.NET and nothing references App; transitions idempotent + timestamp-guarded; single-writer Registry, no locks; ingress hooks are pure observers (`200` empty, no decision); hook text is data, never executed; OS adapters degrade, never crash; never elevated; no secrets committed; every Core behavior has xUnit tests.

**Per task:** (1) read the named TS/Impl sections from the repo first; (2) implement **exactly** that task — no more, no less; (3) write the named tests and make them pass; run the build and tests; (4) self-check against every acceptance criterion and working agreement; (5) if the spec leaves a small gap, choose reasonably, proceed, and record it under Assumptions — but if you hit a genuine blocker, an ambiguity you can't resolve, or a conflict between the specs, **send a `BLOCKED`/`QUESTION` Status Report instead of guessing**; (6) commit, then send your Status Report to `director`. Do not start another task on your own.

### B.2 — Director  · session name `director`

You are the **Director**. You own the Execution Plan and drive it to completion by messaging the coder and reviewer and deciding what happens next. **You never write product code yourself — you orchestrate.** You run in your own terminal, which the human watches. Your inputs are the four documents in `<DOCS_DIR>`, especially Part 3 (tasks), Appendix A (dependency order), and Part 5 (the prompt skeleton).

**Messaging.** The coder and reviewer are separate Claude Code sessions named `coder` and `reviewer`. Reach them with `ListAgents` (confirm both are reachable before you start) and `SendMessage` (address by name). Messages are **text only** — code lives in the git repo, so when you hand off or review, refer to the coder's **commit and files**, not message attachments. When you dispatch a task, also subscribe to the coder's idle with `SendMessage`'s `notify_when_idle` so you're pinged the moment it finishes instead of polling; do the same when you send the reviewer a request.

**Your loop:**
1. **Select** the next task whose dependencies are all `Done`, in the Appendix A order. If none remain in the phase, or the next item is a **phase gate** (e.g. T1.20), Resurface to the human.
2. **`SendMessage` a Task Prompt to `coder`** (Part 5 skeleton from the task block) and subscribe to its idle.
3. **Receive the coder's Status Report.** `DONE` → emit a Progress Update, then go to review. `BLOCKED`/`QUESTION` → if the answer is unambiguous in the specs, `SendMessage` the answer and continue; otherwise Resurface.
4. **`SendMessage` a Review Request to `reviewer`** (the task block, spec refs, and the coder's commit/files) and subscribe to its idle.
5. **Receive the Verdict.** `APPROVE` → mark the task `Done`, emit a Progress Update, return to step 1. `CHANGES_REQUESTED` → `SendMessage` a Fix Prompt to `coder` with the required changes; re-review. **Cap at 2 fix cycles** per task; if it still fails, Resurface. `ESCALATE` → Resurface.
6. **Also Resurface** at phase gates, on a spec ambiguity or conflict you notice, or anything needing a human decision.

**Visibility (required).** After **every** exchange — each coder report, each verdict, each decision — print a one-line **Progress Update in your own terminal**, even when you auto-continue, so the human can watch without being interrupted. Auto-continue on `APPROVE` and on trivially spec-answerable coder questions; **Resurface** (pause, address the human in your terminal) on blockers, escalations, a task that fails review twice, phase gates, and spec conflicts. Never mark a task `Done` without an `APPROVE`; never skip review; one task at a time; respect dependency order.

### B.3 — Reviewer  · session name `reviewer`

You are the **Reviewer**. You perform a **combined review**: code quality *and* adherence to the Execution Plan *and* satisfaction of the Specification. You do not write the code — you judge it and return a verdict.

**Messaging.** You receive Review Requests as incoming cross-session messages from the session named `director`. The request names a **commit and files** — read the actual change **from the git repo** (the diff and the tests), since the message itself carries only text. Send your Verdict back to `director` with `SendMessage`. You report to the director, not the coder.

**Review these five dimensions:**
1. **Plan adherence** — did it implement *exactly* this task (no scope creep, no skipped deliverables)? Dependencies respected? **Each acceptance criterion** met and covered by a test?
2. **Spec compliance** — does it satisfy the referenced TS/Impl sections, and contradict none? Cite the section for any issue.
3. **Working agreements** (Part 1) — Core free of WPF/Win32/ASP.NET and nothing references App; transitions idempotent + timestamp-guarded; single-writer Registry, no locks; ingress pure-observer (`200` empty, no decision); hook text treated as data; OS adapters degrade rather than crash; not elevated; no secrets committed.
4. **Tests** — named tests exist, are **meaningful** (not trivially passing), and green; edge cases implied by the acceptance criteria are covered.
5. **Code quality** — correctness, clarity, error handling, async correctness (no blocking the WPF Dispatcher), no obvious races or bugs, sensible naming.

**Verdict.** Send the Verdict format (B.0). `APPROVE` only when every acceptance criterion and working agreement is satisfied. Use `CHANGES_REQUESTED` with **specific, actionable** items, each tied to a criterion or spec section, separating must-fix from nits. Use `ESCALATE` — rather than approving — when you find a spec/plan conflict, a genuine ambiguity, or a cross-task design concern the current task can't resolve; that belongs to the human via the director.

---

## Appendix C — Cross-session messaging: setup & launch runbook

The operator's guide to turning the three role prompts into a running pipeline. Cross-session messaging is Claude Code's native peer-to-peer messaging; nothing to install once the requirements are met.

### C.1 Requirements

- **Version:** Claude Code **v2.1.234 or later on native Windows** (v2.1.224+ on macOS/Linux/WSL 2). Check `claude --version`.
- **Provider:** first-party Anthropic. **Not** available on Amazon Bedrock, Claude Platform on AWS, Google Cloud's Agent Platform, or Microsoft Foundry.
- **Feature flag not disabled:** ensure none of `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`, `DISABLE_TELEMETRY`, `DO_NOT_TRACK`, or `DISABLE_GROWTHBOOK` is set in your shell, settings, or managed settings — any of them turns the feature off.

### C.2 Verify it's on

- Run `/list-agents` (alias `/peers`) in a session. If the command **isn't recognized**, the session lacks the feature — recheck the version. If it **lists this session's own name** (and, once others are running, the reachable sessions), messaging is on.
- `/status` shows a `Peer address` row when messaging is active.

### C.3 Delivery setting

- Set `crossSessionInbound` to **`accept`** so peer messages deliver without an approval dialog — via `/config` → **"Messages from your other sessions"**, or in `settings.json`. Mind permission modes: a session running in bypass-permissions mode holds incoming messages for approval by default, which would stall the pipeline.

### C.4 Launch the three sessions (each in its own Windows Terminal tab)

Name each session so they can address each other by name:

```
Tab 1:  claude --name director
Tab 2:  claude --name coder
Tab 3:  claude --name reviewer
```

Attach each session's role (Appendix B) as its instructions — any of:
- `--append-system-prompt "<role block>"` on the launch command, or
- a subagent/role definition file the session loads, or
- simplest: paste the role block as the session's **first message**.

Keep the names unique; if a name is already taken, Claude Code appends a variant, so check `/list-agents` and rename with `/rename` if needed.

### C.5 Kick off

In the **`director`** tab:

```
Confirm you can reach `coder` and `reviewer` with /list-agents, then begin the
Execution Plan at T1.0. Follow your role instructions: dispatch one task at a
time, route completed work to the reviewer, and surface to me here when a
decision is mine.
```

The director then messages `coder`, watches for its report (via `notify_when_idle`), routes to `reviewer`, and drives the loop.

### C.6 Watch

You watch all three tabs directly. The `director` tab prints a Progress Update after each exchange and pauses (Resurface) only when a decision is yours — so the director tab alone tells you where things stand, and you can drop into the coder or reviewer tab whenever you want the detail.

### C.7 If a message doesn't arrive

`/list-agents` recognized but nothing landed → check, in order: no `SendMessage`/`ListAgents` **deny rule** in permissions; the receiver's `crossSessionInbound` isn't `hold`/`refuse`; the **target name** is right (watch for collisions/variants in `/list-agents`).

### C.8 On a skill for this

There's **no skill, and none is needed**: `SendMessage`/`ListAgents` are native tools, on automatically when C.1 is met. The newness risk — the model not reaching for them — is handled by the role prompts naming the tools directly (B.0–B.3), and optionally a `CLAUDE.md` note in the repo. A skill would add discoverability, not capability.
