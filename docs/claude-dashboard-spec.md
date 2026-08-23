# Claude Dashboard — Technical Specification

**Draft v0.2 · 2026-08-22**

*v0.2: session↔tab identification is now content-matching; the terminal title is left untouched (the earlier title-stamping approach is removed).*

This specification describes *how the system works and what mechanisms make it possible*. It is deliberately agnostic to programming language, UI framework, and storage engine: every component is named by the **role** it plays, not the technology that fills it. Where it names concrete APIs, those are operating-system and Claude Code capabilities — the fixed external surfaces the system must integrate with — not implementation choices. A companion business-level design document (`claude-dashboard-design.md`) covers product intent; this document covers the machinery.

Reading order: Part I is the architecture at a glance. Part II is the Claude side (how the system learns what agents are doing). Part III is the Windows side (how the system observes and acts on the desktop). Part IV is the cross-cutting logic that ties them together. Part V maps each mechanism to a delivery phase.

---

## Part I — System architecture (agnostic)

### I.1 Logical components

The system is a set of roles. One deployment might fold several into one process; a later phase might split one across a network. The roles are stable regardless.

| Role | Responsibility | Depends on |
|---|---|---|
| **Ingress** | Receive lifecycle signals from Claude Code sessions and hand them to Intake | a local receiving endpoint |
| **Intake** | Normalize raw signals into internal *events*; deduplicate; timestamp | Ingress |
| **Session Registry** | The world model: the set of known sessions and each one's current state, latest exchange, workspace, and group | Intake |
| **Attention Engine** | Compute band membership and ordering; decide what is urgent | Session Registry |
| **Group Resolver** | Derive a session's group from observable facts (workspace now; virtual desktop later) | Session Registry, (Windows layer, later) |
| **Notifier** | Sound policy: schedule notices and nudges; honor mutes and suppression | Session Registry (state changes) |
| **Presenter** | Render the list, bands, expanded exchanges, counts, tray presence; accept Ack and view-mode input | Attention Engine, Group Resolver |
| **Navigator** *(later)* | Bring a session's terminal window and tab to the foreground on request | Windows layer |
| **Focus Observer** *(later)* | Detect which terminal the operator is actually looking at, to infer acknowledgment | Windows layer |

### I.2 The core principle: the world is event-sourced

**There is no API that lists "all Claude Code sessions currently running on this machine."** Claude Code does not expose a running-session registry to outside observers. The dashboard's entire world model is therefore *reconstructed from a stream of lifecycle events*. A session exists in the Registry because the system saw an event from it; it has a state because the last event told it so.

Three consequences follow, and they drive several design decisions:

1. **Sessions that started before the app was running are invisible** until their next event. Mitigations: register a session-start signal that also fires on resume (so a `--continue` surfaces the session), and optionally run a *reconciliation sweep* (Part III.6) that scans the OS for running Claude Code processes and shows a placeholder "known, awaiting first event" row.
2. **Restart must rebuild the world** either from persisted state or by waiting for the next events. The Registry's durable snapshot (Part IV.5) exists for this.
3. **Delivery is at-least-once and possibly out of order.** Every state transition must be *idempotent* and *timestamp-ordered* (Part IV.1), so applying the same "finished" signal twice, or a stale one late, is harmless.

### I.3 Data flow

```
 Claude Code session ──hook──▶ Ingress ──▶ Intake ──▶ Session Registry
                                                          │
                        ┌─────────────────────────────────┼───────────────┐
                        ▼                                  ▼               ▼
                  Attention Engine                     Notifier        Group Resolver
                        │                                  │               │
                        └──────────────▶ Presenter ◀───────┴───────────────┘
                                             │
                                    operator ▲│▼ (Ack, toggle, click)
                                             │
                          (later) Navigator ─┘   Focus Observer ──▶ Intake (as ack events)
```

Note the loop at the bottom: the Focus Observer feeds *back into* Intake as a synthetic "acknowledged" event, so acknowledgment from any source (new prompt, manual click, inferred focus) flows through one path.

### I.4 Deployment shape

A single resident presence that (a) is always running so it never misses events, (b) exposes a receiving endpoint on the loopback interface only, (c) owns a persistent tray indicator, and (d) can show or hide a main window without losing state. The main window is one *consumer* of the Registry; this separation is what lets a remote surface (Phase 7) become a second consumer without disturbing the core.

---

## Part II — The Claude Code event layer

This is how the system learns what every agent is doing. The mechanism is **Claude Code hooks**: user-defined handlers that Claude Code runs at fixed points in a session's lifecycle. Hooks fire wherever Claude Code runs — terminal, IDE extension, desktop app — and every hook receives a JSON payload describing the event.

### II.1 Transport: how a hook reaches Ingress

Claude Code supports several hook *handler types*. Three are relevant, and the system can use them interchangeably or in combination:

- **HTTP handler** — Claude Code POSTs the event's JSON payload to a URL. This is the natural primary transport: the resident presence exposes a loopback endpoint, and each hook is configured to POST to it. No per-event script process, no file to poll. Payload arrives as the request body; the handler can carry custom headers (e.g., a shared secret), with an explicit allowlist of environment variables permitted in those headers.
- **Command handler** — Claude Code runs a shell command and passes the payload on standard input. Useful as a fallback where an HTTP listener isn't desired. (The command inherits the session's environment and *could* forward extra variables, but the system needs none for identification — see II.4.)
- **MCP tool handler** — Claude Code calls a tool on an already-connected server. Mentioned for completeness; not needed for this system.

The transport choice is an implementation detail. The specification requires only that *every relevant lifecycle event reaches Ingress with its payload intact*.

Hook registration lives in Claude Code's settings, which merge across scopes (per-user, per-project, per-machine-managed). For a machine-wide dashboard, the per-user scope covers every project automatically. Registration is declarative configuration; the system may ship it or help the operator install it, but the spec does not mandate hand-editing.

### II.2 The events consumed, and what each means

The dashboard subscribes to a small subset of the available lifecycle events. Each maps to a state or an enrichment.

| Claude Code event | Fires when | Dashboard meaning | Key payload fields consumed |
|---|---|---|---|
| **SessionStart** | a session begins or resumes | create/refresh a Registry entry; on resume, surface a pre-existing session | `session_id`, `cwd`, `source` (startup/resume/clear/compact/fork), `transcript_path` |
| **UserPromptSubmit** | operator submits a prompt, before processing | state → **Working**; store the prompt text as the session's context line; **auto-acknowledge** any prior unread/needs-you state (proof the answer was seen) | `session_id`, prompt text, `cwd`, `prompt_id` |
| **Notification** (matcher: `permission_prompt`) | a permission dialog is raised | state → **Needs You — Permission** | `session_id`, notification type |
| **Notification** (matcher: `idle_prompt` / agent-needs-input) | Claude is waiting on the operator | state → **Needs You — Question** | `session_id`, notification type |
| **Stop** | Claude finishes responding | state → **Unread**; store the answer text as the exchange result | `session_id`, `last_assistant_message` (final answer text, provided so the handler need not read the transcript) |
| **StopFailure** (matchers: `rate_limit`, `overloaded`, `authentication_failed`, …) | the turn dies on an error | state → **Error**; store the reason | `session_id`, error type |
| **SessionEnd** | a session terminates | state → **Ended**; schedule removal | `session_id`, end reason |

Two payload properties make the product possible and deserve emphasis:

- **UserPromptSubmit carries the prompt text.** This is why the session's identifying line — the thing the operator recognizes it by — populates at the source, with no scrollback scraping.
- **Stop carries the final assistant message directly.** This is why an expanded row can show the *answer* beside the *question*: both halves of the exchange arrive in events, so many checks resolve inside the dashboard without opening the terminal at all.

### II.3 Correlation and identity

- **Primary key: `session_id`.** Present on every event; it is the Registry's key. All state transitions are keyed by it.
- **Grouping input: `cwd`.** The working directory arrives on the session-defining events and is the Phase 1 grouping key (Part IV.3). It can change mid-session (the operator or Claude runs `cd`); the current directory events reflect the change, so the group is re-derived, not fixed at start.
- **Turn correlation: `prompt_id`.** Ties a specific prompt to its outcome; useful for matching a `Stop` to the `UserPromptSubmit` that caused it, and for de-duplicating.
- **Fallback context: `transcript_path`.** The full conversation on disk. The system generally should *not* need it — the events carry what's shown — but it exists as a recovery source. Note it is written asynchronously and can lag the live turn, so it is unsuitable for reading the latest message in real time; that is precisely why `Stop`'s inline answer field is preferred.

### II.4 What is *not* available, and how the system compensates

- **No session enumeration** — addressed in I.2 (event-sourced world, reconciliation sweep).
- **No "which window/tab is this session in" from Claude Code.** The event tells you the session and its working directory, not its on-screen location. Locating the window and tab is entirely the Windows layer's job (Part III), done by **content-matching**: reading a tab's visible content via UI Automation and matching it against the prompt/answer text the Registry already holds (III.2).
- **No guaranteed ordering or exactly-once delivery.** Addressed by idempotent, timestamp-ordered transitions (IV.1).

### II.5 Delivery security at the boundary

The receiving endpoint is a local attack surface and hook payloads are untrusted content:

- **Bind to loopback only.** The endpoint listens on the local interface; nothing off-machine can reach it. (Phase 7 remote access is a *separate, authenticated* surface layered on top of the Registry, never this raw ingress exposed to the network.)
- **Optional shared secret.** A token in a hook header, checked at Ingress, guards against other local processes posting spoofed events.
- **Treat all event text as data, never instruction.** Prompt text and answer text are displayed and stored; they are never interpreted as commands by the system. (Claude Code itself flags text shaped like out-of-band system commands; the dashboard's own rule is simpler — it renders and escapes, and does nothing executable with event content.)

---

## Part III — The Windows integration layer

This is how the system observes the desktop (to infer that the operator looked at a terminal) and acts on it (to jump to a terminal). Everything here is **later-phase** work; Phase 1 needs none of it. It is specified now because the mechanisms constrain the earlier design — and because the identification approach (content-matching, III.2) shapes what the Registry must retain: enough of each exchange's text to recognize a tab by its content.

The APIs named below are Windows platform capabilities. Every general-purpose language on Windows can bind to them; naming them commits to nothing about implementation language.

### III.1 The two hard problems

1. **Observation** — "Is the operator currently looking at session S's terminal?" (Focus Observer, for inferred acknowledgment, Phase 3.)
2. **Action** — "Bring session S's terminal window and tab to the foreground." (Navigator, Phase 2.)

Both reduce to a mapping problem: **session ⇄ on-screen window+tab.** The system holds the session side (Registry). Windows holds the on-screen side. Bridging them is the crux, and it is genuinely awkward for tabbed terminals, for reasons III.3 explains. The adopted approach is **content-matching**: recognize a session's tab by reading its visible content and comparing against the exchange text the Registry already holds (III.2). It needs no cooperation from the terminal title and no identifier injected anywhere.

### III.2 The content-matching join

The system recognizes a session's on-screen tab by **what the tab contains**, not by any injected marker. The Registry already holds each session's latest exchange (the prompt text from `UserPromptSubmit`, the answer text from `Stop`). The Windows layer reads a tab's visible buffer through UI Automation (III.4) and matches that text against the Registry. A confident match maps the tab to a session; that mapping is what both navigation and focus-inference need.

- **What to match on:** the most recent prompt line is the strongest signal — distinctive and operator-authored; the answer snippet and the working directory shown in the prompt are corroborating signals. Matching on several beats matching on one.
- **Ambiguity:** if two tabs genuinely present the same recent text (e.g., the identical prompt typed in two sessions), the match is unresolved and degrades to **window-level** behavior (III.7) — activate or observe the window, skip precise tab selection — rather than guessing.
- **Cost:** reading buffer text per tab is heavier than reading a title, but it happens only on demand (a navigation click, or a focus change), not continuously, so the cost is negligible in practice.

The decisive property: content-matching requires **no cooperation from the terminal title, no identifier written anywhere, and nothing from Claude Code beyond the event text the Registry already has.** This is why the title is left entirely untouched (III.3).

### III.3 Identifiers the join deliberately avoids

Three more obvious handles were considered and rejected; content-matching exists because none of them holds up:

- **The terminal title.** Tempting as a place to stamp a session id, but Claude Code actively rewrites the whole title on every render cycle and exposes no append or template hook, so any injected suffix is erased almost immediately. Setting it from a hook doesn't work either — escape sequences in hook output are captured by Claude Code's interface rather than passed to the terminal emulator. And the only actor that could re-stamp it after each overwrite would have to already know which tab belongs to which session — the very fact the id was meant to establish. The title is therefore left completely alone; the system writes nothing to it.
- **The pane session GUID.** Windows Terminal gives each pane a session GUID in its environment, which a hook could report. But Windows offers no public way to ask "which visible tab has this GUID," so the GUID can *correlate* but cannot *locate* a tab on screen.
- **The process tree.** From a session's process one might hope to find its window. This works for classic single-window consoles but fails for Windows Terminal, where one process backs many windows and tabs and the shells are grandchildren, with no clean path from process to a specific window-and-tab.

Content-matching sidesteps all three: it reads something the tab genuinely displays and that the Registry independently knows.

### III.4 Reading the desktop: UI Automation

Windows Terminal renders its own tab strip; tabs are not separate OS windows. To inspect them, the system uses **UI Automation (UIA)**, the accessibility API, treating the terminal's UI as an element tree:

- The tab strip exposes which tab is **selected**; each tab's **pane content** is reachable as text through UIA's text pattern.
- To find a tab: walk the terminal window's UIA subtree, read each tab's visible content, and match against the Registry's exchange text (III.2).
- To read the active tab: query the selected element of the tab strip, then read its content.

UIA is the workhorse for anything tab-level, and it is the fragile part: it depends on the terminal's accessibility tree, which can shift between terminal versions. The spec therefore isolates all UIA behind an adapter with a hard rule that **UIA failure degrades to window-level behavior**, never a crash (III.8).

### III.5 Observing focus: event hooks vs. tab switches

To know when the operator switches to a window, the system installs a **system-wide event hook** for the foreground-changed event, registered out-of-process with a callback in the resident presence. (Out-of-process event hooks are delivered as messages, so the registering thread must run a message loop — a concrete constraint on however the resident presence is built.)

A crucial subtlety: **switching tabs *within* one terminal window does not change the foreground window.** The foreground-changed event fires when the operator moves *between* windows, not between tabs of the same window. Tab-level focus therefore cannot be inferred from the foreground event alone; it requires UIA selection/focus events on the tab strip. The Focus Observer thus has two tiers:

- **Window focus** (foreground event) — cheap, reliable, tells you the operator is in *some* terminal window.
- **Tab focus** (UIA selection change) — richer, fragile, tells you *which tab* — and, via content-matching, which session.

Phase 3 can ship window-level inference first (already enough to soft-acknowledge when only one session lives in a window) and add tab-level inference as a refinement.

Acknowledgment inference then reads: foreground settles on a terminal window → read the focused tab's content via UIA → match to session (III.2) → if it dwells for a short threshold, emit a synthetic acknowledgment event into Intake (I.3). Dwell-thresholding avoids acking everything the operator merely tabs past.

### III.6 Reconciliation sweep (optional, supports I.2)

To surface sessions that predate the app, the system may periodically enumerate running processes, identify Claude Code instances and their hosting terminals, and create placeholder Registry entries ("known, awaiting first event"). These rows carry no exchange text until a real event arrives. This is a supplement to the event stream, not a replacement, and it is best-effort.

### III.7 Per-terminal locate strategy

The rationale for content-matching over titles, GUIDs, and process trees is in III.3. One actionable nuance remains: for **non-tabbed** terminals (classic single-window consoles), process-tree location is a valid and cheaper path — enumerate top-level windows and match by owning process id, since there the process maps cleanly to one window. It is only **tabbed** terminals (Windows Terminal) where that breaks and content-matching is required. The locate strategy is therefore selected per terminal type; window-level activation (III.8) is the common fallback when neither resolves a specific tab.

### III.8 Acting on the desktop: activation and navigation

Two levers, preferred in this order:

1. **Delegate to the terminal.** Windows Terminal accepts a command to focus a tab in a specific existing window by window id and zero-based tab index (`-w <window> focus-tab -t <index>` in its command-line vocabulary). When the system can determine the window id and index (via UIA enumeration + content-matching, III.2), delegating activation to the terminal is the cleanest path — the terminal handles its own foreground and tab selection.
2. **Direct activation.** Otherwise, the system brings the located window to the foreground via the OS activation call and, if needed, invokes the target tab element through UIA.

**Foreground-lock note.** Windows restricts which process may take the foreground; a background process generally cannot steal it. This system is largely exempt *by construction*: navigation is triggered by the operator clicking a row in the dashboard's own window, so the dashboard has just received user input and is permitted to set the foreground at that moment. This is a real advantage of click-initiated navigation over autonomous window-raising, and the spec relies on it rather than on foreground-lock workarounds.

### III.9 Virtual desktop awareness (Phase 4 grouping)

The operator organizes tasks as one virtual desktop per task, so virtual desktop is the truest grouping key. Windows exposes desktop capability at two tiers:

- **Documented tier** — a public manager interface answers *which* desktop a given window is on, and whether a window is on the current desktop, and can move a window to another desktop. This is enough to **group sessions by desktop** (read each session's window's desktop id) and to know if a target is off-screen.
- **Undocumented tier** — *enumerating* desktops, *switching* to a desktop, and reading/setting desktop *names* are not in the public interface. They require an internal COM interface whose identifier changes between Windows builds. The spec's rule: isolate this tier behind an adapter, depend on a maintained community wrapper, pin versions, and **degrade gracefully** — if a Windows update breaks desktop switching, navigation still activates the window (which itself triggers the desktop switch on most builds) and grouping still works via the documented tier. Desktop *names* may be read from their per-user storage as a pragmatic, non-contractual source; absent a name, groups fall back to the workspace key.

### III.10 Tray presence and audio

- **Tray indicator** — a persistent status icon owned by the resident presence, reflecting the worst current state (quiet / working / needs-you with a count badge), clickable to show the window. This is the always-on layer that survives closing the main window.
- **Audio** — an output capability for notices and nudges. The scheduling is OS-independent (Part IV.4); only playback touches the platform.

### III.11 Housekeeping

Single-instance enforcement (only one resident presence), start-with-Windows registration, and **no elevation**: all target processes (terminals, shells) run at the operator's normal integrity level, so UIA inspection and foreground activation work without administrator rights. Running elevated would actually *impede* UIA against non-elevated windows and is to be avoided.

---

## Part IV — Cross-cutting logic

Platform-independent rules that govern behavior regardless of how Parts II and III are realized.

### IV.1 The session state machine

States: `Working`, `NeedsYou.Question`, `NeedsYou.Permission`, `Error`, `Unread`, `Acked`, `Ended`.

Transitions are triggered by events (Part II) and by acknowledgment sources (Part I.3). All transitions are **idempotent** and **timestamp-guarded**: an incoming event older than the state's last-applied timestamp is ignored; re-applying the current state is a no-op.

```
            UserPromptSubmit
   (any) ───────────────────────▶ Working        [also: auto-ack prior Unread/NeedsYou]
 Working ──Stop──────────────────▶ Unread
 Working ──Notification(perm)────▶ NeedsYou.Permission
 Working ──Notification(idle)────▶ NeedsYou.Question
 Working ──StopFailure───────────▶ Error
 Unread        ──Ack*────────────▶ Acked
 NeedsYou.*     ──Ack*────────────▶ Acked          (rare; usually a new prompt supersedes)
 NeedsYou.* / Error ──UserPromptSubmit─▶ Working    (operator answered/retried)
 (any)   ──SessionEnd────────────▶ Ended ──(timer)──▶ removed

 Ack* = { new UserPromptSubmit in session | manual Ack | inferred focus (Phase 3) }
```

Every state carries: the latest exchange (prompt text; answer text once known), entry timestamp (for age display and nudge timing), workspace, and derived group.

### IV.2 Attention banding and ordering

Bands, top to bottom, with intra-band order:

| Band | Members | Order within band | Rationale |
|---|---|---|---|
| Needs You | `NeedsYou.*`, `Error` | **oldest first** | longest-blocked agent = most wasted capacity |
| Unread | `Unread` | **newest first** | freshest finish is the one being chased after a beep |
| Working | `Working` | most recent activity first | — |
| Quiet | `Acked`, idle | recency | sinks; collapsible |
| Ended | `Ended` | recency | dim; auto-removed after a short window |

The ordering asymmetry (reds by *ascending* age, greens by *descending* recency) is intentional and is the heart of the attention model. Pseudocode:

```
def render_order(sessions):
    needs = [s for s in sessions if s.state in (Question, Permission, Error)]
    unread = [s for s in sessions if s.state == Unread]
    working = [s for s in sessions if s.state == Working]
    quiet = [s for s in sessions if s.state == Acked or s.idle]
    ended = [s for s in sessions if s.state == Ended]

    needs.sort(key=lambda s: s.entered_at)            # oldest first
    unread.sort(key=lambda s: s.entered_at, reverse=True)  # newest first
    working.sort(key=lambda s: s.last_activity, reverse=True)
    quiet.sort(key=lambda s: s.last_activity, reverse=True)

    return needs + unread + working + quiet + ended
```

In **grouped** view this ordering runs *within* each group, and groups are ordered by their most-urgent member (tie-break: latest activity), so active groups float up. In **flat** view the bands are global and labeled.

### IV.3 Grouping and derivation

- **Phase 1 key:** workspace (`cwd`). Re-derived on directory-change events, not fixed at session start.
- **Phase 4 key:** virtual desktop id (III.9), with desktop name as the group label.
- **Group state** = worst member state (Needs You > Error > Unread > Working > Quiet).
- **Group recency** = most recent member event.

The operator never assigns groups by hand; grouping mirrors observable reality. A manual label or per-group checklist is a candidate refinement only if it later earns its place.

### IV.4 Space, staleness, collapse

Rows are the scarce resource (a narrow panel). Rules, in priority order:

1. **A fully quiet group collapses to one line** after N minutes idle (default 15): name, member count, idle age. Expandable; never pushes active work down.
2. **Acked rows collapse within their group** to a "+ k quiet" footer.
3. **Unread rows always keep a full row** — finished-but-unseen work is exactly what gets lost today and is never summarized away.

Rule 3 supersedes the earlier "show only the first finished session per group when space is tight" idea, which would have hidden the very thing the tool exists to surface. Collapsing only *already-handled* work is simpler and safe.

### IV.5 Sound policy engine

Vocabulary: a **notice** is the first sound for an event; a **nudge** is a reminder.

- **Notices** fire on state entry: distinct sounds for finished, permission, question, error (the existing sound language).
- **Nudges** fire for a `NeedsYou.*` session still unacknowledged past T₁ (default 2 min): the *same melody, softer and quieter*, repeating at widening intervals (2 → 5 → 10 min). Never louder, never faster.
- **Unread** gets at most one soft nudge (default 5 min) or none — per-state configurable.
- **Mute** is available per-session and per-group.
- **Suppression (Phase 3):** when focus inference reports the operator is looking at a session, suppress that session's notice — they'll see it finish.

Timing model: each session in a nudge-eligible state holds a scheduled next-nudge time; entering `Acked` (from any ack source) cancels it. This is a pure timer over Registry state and touches no platform code except playback.

### IV.6 Persistence

- **Ephemeral (in memory):** the live Registry, band computations, nudge timers.
- **Durable (survives restart):** a Registry snapshot for warm restart (I.2), operator settings (thresholds, sound choices, mutes, default view), and — Phase 5 — an event/exchange history for search and stats.
- A durable append-only event log is the natural substrate for both warm restart and later history; the spec requires the *capability*, not a specific store.

### IV.7 Degradation ladder

Every platform capability fails soft; the product keeps working with less precision:

| If this breaks… | …the system falls back to | Product still does |
|---|---|---|
| Tab-level UIA | window-level focus/activation | acks and navigates at window granularity |
| Content match ambiguous (look-alike tabs) | window-level activation | navigates to the window, skips exact tab |
| Desktop switching (build change) | window activation (triggers switch) + documented-tier grouping | jumps to the session; groups correctly |
| Focus inference entirely | manual + auto (new-prompt) ack | Phase 1 acknowledgment, fully usable |
| Reconciliation sweep | event stream only | shows sessions from their next event on |
| HTTP ingress | command-hook transport | same events, different pipe |

Phase 1 sits at the bottom of every ladder and is fully functional on its own.

### IV.8 Threat surface summary

- Loopback-only ingress; optional shared-secret header.
- Event text is display data, never executed.
- No elevation.
- Phase 7 remote access is a distinct authenticated surface over the Registry, never the raw ingress exposed outward.

---

## Part V — Mechanism-to-phase map

| Phase | Product theme | Claude-side mechanisms | Windows-side mechanisms | Cross-cutting |
|---|---|---|---|---|
| **1** | See clearly | HTTP/command hook intake for SessionStart, UserPromptSubmit, Notification, Stop, StopFailure, SessionEnd | tray presence; audio playback | full state machine; banding; grouping by workspace; collapse rules; notices + nudges; warm-restart snapshot; retain exchange text for later matching |
| **2** | Go there | — | UIA tab enumeration + content-matching; terminal-delegated `focus-tab`; direct activation with click-exempt foreground | navigator wiring; per-terminal locate strategy |
| **3** | It notices | synthetic ack events from focus | foreground event hook; UIA selection events; dwell thresholding | ack-source unification; on-screen notice suppression |
| **4** | Task lens | — | virtual-desktop grouping (documented tier); desktop names; isolated undocumented adapter | grouping key swap to desktop |
| **5** | Memory | richer event retention | — | durable history; search; wait-time stats |
| **6** | Polish | — | — | settings UI; sound editor; themes |
| **7** | Anywhere | — | — | authenticated remote surface as a second Registry consumer |

---

## Appendix A — External surfaces this system depends on (and their stability)

| Surface | Kind | Stability | Isolation strategy |
|---|---|---|---|
| Claude Code hook events & payloads | documented product API | evolving but documented | thin intake adapter; tolerate unknown fields |
| UI Automation tree + text of the terminal | OS API over app UI | app-version-sensitive | adapter; window-level fallback; used for both content-matching and tab selection |
| Foreground-changed event hook | OS API | very stable | — |
| Terminal window/tab command-line addressing | documented | stable | delegate when possible |
| Virtual desktop — documented interface | OS API | stable | primary for grouping |
| Virtual desktop — internal interface | undocumented | build-sensitive | pinned community wrapper; graceful degradation |
| Tray & activation calls | OS API | very stable | — |

## Appendix B — Open technical questions

- **Content-match disambiguation.** If two tabs ever present identical recent text, the join is unresolved and falls back to window level. Is that acceptable in practice, or is a lightweight disambiguator (e.g., a hidden per-session marker read only during troubleshooting) worth adding later? Not built now.
- **Answering from the dashboard.** Typing a reply into a session from the panel would need a write path back into the terminal (injected input, or a terminal automation surface). This pulls the tool toward being a terminal frontend and is deferred past Phase 3 pending appetite.
- **Subagents.** Roll subagent lifecycle up into the parent session row, or ignore entirely? Affects which events are subscribed.
- **Queued prompts.** Claude Code lets the operator queue messages; surface a "queued" hint on Working rows, and if so, from which signal?
- **Unread that is never acked and never revisited.** Auto-fade after some hours, or persist until acted on?
- **Relationship to ClaudeSessions.** Does this absorb that project? A session-addressing URI scheme from it would slot directly into Phase 2 navigation as an alternative to UIA location.
