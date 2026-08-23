# Claude Dashboard — Implementation Specification

**C# / .NET / WPF · Draft v0.1 · 2026-08-22**

## Part 0 — Scope and relationship to the Technical Specification

This document pins the **Technical Specification** (`claude-dashboard-spec.md`, v0.2) to a concrete stack: C#, .NET, WPF. It is the *implementation* companion, not a replacement. The two divide cleanly:

- The **Technical Specification** stays technology-agnostic and owns the *rationale* — why attention bands sort the way they do, why content-matching over titles, why the world is event-sourced. It remains the reference for any future port (a Linux tray build, an iOS remote surface); those builds start from it, not from this file.
- This **Implementation Specification** owns the *realization* — which project holds what, which library binds which OS call, which type carries which field. Where the Technical Spec says "a role," this says "a class/project"; where it says "an OS capability," this names the API and the wrapper. For any *why*, it defers to the Technical Spec by section number rather than re-arguing it.

Section numbering here is independent, but cross-references to the Technical Spec are written as "TS §IV.2" and so on.

Everything the Technical Spec marks as later-phase is later-phase here too. Phase 1 builds only what Parts 1–5 and Part 9 describe; Parts 6–7 are the Windows-integration phases.

---

## Part 1 — Platform and solution structure

### 1.1 Targets

- **Runtime:** .NET 10 (current LTS). Pin to the current LTS and stay on it; this is a long-lived personal tool, not a place to chase STS releases.
- **Language:** C# 14 (ships with .NET 10).
- **UI:** WPF (`net10.0-windows`, `<UseWPF>true</UseWPF>`).
- **Architecture / RID:** `win-x64` (dev box is x64). Self-contained, single-file publish so "starts with Windows" doesn't depend on a machine-wide runtime install.
- **DPI:** Per-Monitor v2 (Part 5).

### 1.2 Projects

A hexagonal split: a portable domain core with **ports** (interfaces), and a Windows host supplying **adapters**. This is what makes the Phase 7 remote surface nearly free — it becomes a second consumer of the same Core — and what makes the domain unit-testable without a desktop.

| Project | TFM | Role | References |
|---|---|---|---|
| **ClaudeDashboard.Core** | `net10.0` | Domain: Registry, state machine, attention engine, sound-policy engine, and the port interfaces. **Zero** WPF, zero Win32, zero ASP.NET. | — |
| **ClaudeDashboard.App** | `net10.0-windows` | The Windows host: WPF UI, ingress (Kestrel), and every Win32/UIA/COM adapter. | Core |
| **ClaudeDashboard.Remote** *(Phase 7)* | `net10.0` | ASP.NET Core + SignalR read/ack surface for the phone. A second consumer of Core. | Core |
| **ClaudeDashboard.Tests** | `net10.0` | xUnit. Exercises Core (pure) and adapter contracts against fakes. | Core, App |

**Dependency rule:** everything points *at* Core; nothing points at App. All OS-specific code lives in App behind interfaces declared in Core. Enforced by project references and, optionally, an architecture test.

### 1.3 The ports Core declares (host implements)

These interfaces are the seam. Core is written entirely against them; App provides the Windows implementations; Tests provide fakes; Remote ignores the desktop-only ones.

- `IClock` — `Now`; injectable so state-machine timing and nudge scheduling are testable.
- `ISoundPlayer` — `Play(SoundId, gain, fade)`; NAudio adapter in App.
- `ITerminalLocator` — `Task<TabRef?> FindTab(SessionId)` and `TabRef? IdentifyForegroundTab()`; the content-matching adapter (Phase 2/3).
- `IFocusSource` — raises `ForegroundChanged`/`TabFocusChanged`; the WinEvent+UIA adapter (Phase 3).
- `ITerminalNavigator` — `Activate(TabRef)`; the `wt.exe`/UIA adapter (Phase 2).
- `IVirtualDesktopService` — `GetDesktop(hwnd)`, `PinToAllDesktops(hwnd)`, later `Switch`/`Name` (Phase 4).
- `IEventSink` — how ingress hands normalized events to the pipeline.

---

## Part 2 — The Core library (portable domain)

Realizes TS Part IV. No behavior here is new; this is the C# shape of it.

### 2.1 Domain types

- `SessionId` — a wrapper over Claude Code's `session_id` string; Registry key.
- `Exchange` — `{ Prompt: string, Answer: string?, PromptId: string?, StartedAt, AnsweredAt? }`. The latest `Exchange` is the session's context line and the payload of an expanded row.
- `SessionState` — enum: `Working`, `NeedsPermission`, `NeedsQuestion`, `Error`, `Unread`, `Acked`, `Ended`.
- `Session` — `{ Id, State, Latest: Exchange, Cwd, Group, EnteredAt, LastActivity, ErrorKind? }` plus a small transition log.
- `Group` — derived container keyed by `Cwd` (Phase 1) or virtual-desktop id (Phase 4); exposes worst-member state and most-recent activity.
- `InboundEvent` — the normalized internal event (see 3.2), a discriminated shape (record hierarchy or a tagged struct) the pipeline applies.

### 2.2 SessionRegistry and the state machine

`SessionRegistry` holds `IReadOnlyDictionary<SessionId, Session>` and applies `InboundEvent`s. It realizes TS §IV.1 with two invariants baked in:

- **Idempotent:** re-applying the same event is a no-op.
- **Timestamp-guarded:** an event older than the session's last-applied stamp is dropped (delivery is at-least-once and can reorder — TS §I.2).

Mutation happens on exactly one thread (Part 4), so the Registry needs no internal locking. It raises change notifications (a plain `event` or `INotifyPropertyChanged`) that the UI layer subscribes to; Core does not know those subscribers are a UI.

### 2.3 Attention engine

A pure function `Order(sessions) → banded, ordered list`, realizing TS §IV.2 exactly — Needs-You oldest-first, Unread newest-first, then Working, Quiet, Ended. Pure and deterministic, so it is straightforward to unit-test the ordering asymmetry that is the heart of the model.

### 2.4 Sound-policy engine

Realizes TS §IV.5 as a timer model over Registry state: each session in a nudge-eligible state carries a scheduled next-nudge time; entering `Acked` cancels it. The engine emits *intents* (`PlayNotice`, `PlayNudge(gain)`) against `ISoundPlayer` — it never touches audio APIs itself, so it runs in Tests with a fake player. Uses `IClock`.

---

## Part 3 — Ingress (event intake)

Realizes TS §II.1 and TS §II.7.

### 3.1 Host and binding

An in-process **ASP.NET Core minimal API on Kestrel**, started by the .NET **Generic Host** that also owns the WPF app, logging, and background services. Kestrel binds to **loopback only** — `http://127.0.0.1:<port>` — at a **fixed default port** in the private range (e.g. `52789`), configurable via settings. Fixed because the hook URL must be stable (Part 9); loopback because nothing off-machine may post events (TS §II.7).

### 3.2 Endpoints

- `POST /hook` — the single ingest endpoint. Body is the hook's JSON. The handler reads `hook_event_name`, deserializes the event-specific fields (Part 9), maps to an `InboundEvent`, writes it to the Channel (Part 4), and returns **`200` with an empty body immediately**. It does no Registry work on the request thread.
- `POST /show` — single-instance signal (Part 5.3): tells the running instance to surface its window. Returns `200`.
- `GET /health` — liveness for diagnostics.

### 3.3 Pure-observer property

Every hook the dashboard registers is **observational**: the endpoint returns `200`/empty and never returns a decision field. Per the hooks reference, a `2xx` empty body is "success, no decision," so the dashboard **cannot block, delay, or alter** any Claude Code turn. This is a hard design constraint — the monitor must never become able to interfere with the thing it monitors — and it also means a crashed or stopped dashboard degrades Claude Code to "no hooks fire," never to "Claude is stuck."

### 3.4 Boundary security

- Loopback bind (above).
- **Optional shared secret:** a token in a hook header, checked at ingress, to stop other local processes spoofing events. Delivered via Claude Code's `headers` + `allowedEnvVars` (Part 9); the token lives in an environment variable, never in the committed settings.
- **All event text is data.** Prompt and answer strings are stored and rendered, never executed or interpreted as commands. WPF binding renders them as text; no `eval`-like path exists.

---

## Part 4 — Threading and data flow

Realizes the "background-thread ingress, UI-thread rendering, one crossing point" model.

```
 Kestrel request threads (many, concurrent)
        │  write
        ▼
 Channel<InboundEvent>            (System.Threading.Channels; single-reader)
        │  read (one consumer)
        ▼
 EventConsumer : BackgroundService   ── applies to ──▶ SessionRegistry (single-thread mutation, lock-free)
                                                              │ raises change notifications
                                                              ▼
                                        UI marshal via Application.Current.Dispatcher
                                                              │
                                                              ▼
                                   ObservableCollection<SessionViewModel> → WPF bindings
```

- **Channel:** `System.Threading.Channels.Channel<InboundEvent>` (bounded, with a generous capacity; drop-oldest or block-writer policy chosen so a burst of fifteen simultaneous events can't stall Kestrel). Producers = Kestrel handlers; single consumer = `EventConsumer`.
- **EventConsumer:** a `BackgroundService` reading the channel in a loop, applying each event to the Registry. Because it is the *only* writer, no locks are needed and events are serialized into one orderly stream even though they arrived in parallel — this is where the timestamp guard (2.2) runs.
- **UI hop:** the Registry's change notifications are marshalled onto the WPF `Dispatcher` (captured `SynchronizationContext` or `Application.Current.Dispatcher.InvokeAsync`) and applied to the `ObservableCollection` the ViewModels expose. This single marshalling point is the only place background work touches the UI thread.

Synthetic acknowledgment events (from focus inference, Phase 3) enter the **same** Channel, so all ack sources — new prompt, manual click, inferred focus — travel one path (TS §I.3).

---

## Part 5 — WPF host and tray

### 5.1 Application lifecycle

- `ShutdownMode.OnExplicitShutdown` — the app does not exit when the window closes.
- Main window `Closing` is intercepted: cancel the close, hide the window. The window is shown/hidden, never recreated, so it keeps state.
- The process exits only via the tray's **Quit**.

### 5.2 Tray icon = the overall status light

The tray icon **is** the status light of TS §9 — a worst-state roll-up across all sessions, same "worst wins" as groups (TS §IV.3). Via **H.NotifyIcon.Wpf**.

Precedence (top wins), color carries state:

| Color | Meaning |
|---|---|
| **Red** | ≥1 session in Needs-You (question or permission) |
| **Amber** | ≥1 session in Error (distinct from red so "a turn died" reads differently from "it's asking you") |
| **Green** | ≥1 Unread finish waiting; nothing above |
| **Blue** | ≥1 Working; nothing above |
| **Grey** | all quiet |

- **Counts live in the tooltip**, not the glyph ("3 need you · 2 unread · 1 working") — a 16px icon can't render legible digits. H.NotifyIcon can generate the icon bitmap per state; if a digit is drawn, cap at `9+`.
- **No animation.** The tray is static per state (TS reserves motion for needs-you rows inside the window, not the tray).
- **Left-click:** toggle the dashboard window. **Right-click:** context menu — Open · Mute all (and "for 30 min") · Pause monitoring · Settings · **Quit**.

### 5.3 Single instance

A named `Mutex` created at startup; if already held, this process is the second instance. The **loopback port bind** is the belt-and-suspenders interlock — a second instance can't bind the same port. The second instance signals the first by `POST /show` to the loopback endpoint (reusing ingress; no separate IPC), then exits.

### 5.4 DPI and monitor placement

- **Per-Monitor v2** declared in `app.manifest` (`<dpiAwareness>PerMonitorV2</dpiAwareness>`) so the window re-renders crisply when moved between monitors at different scale — directly relevant to sliding the dashboard between the top status monitor and the main monitor.
- The dashboard **pins itself to all virtual desktops** via `IVirtualDesktopService.PinToAllDesktops` (Part 6.3) so it is always present on the status monitor regardless of the active desktop.
- Restore position on the monitor it was last on; open on the focused monitor if that position is gone.
- Optional **always-on-top** toggle (default off) for when it lives on the status monitor.

### 5.5 MVVM

**CommunityToolkit.Mvvm** — `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` source generators. ViewModels observe the Registry (via the marshalled notifications) and expose the banded `ObservableCollection` the views bind to. Grouped vs Flat is a view-mode toggle over the same collection (TS §7).

---

## Part 6 — Windows integration adapters (Phases 2–4)

All behind Core ports (1.3); Phase 1 ships none of these. Rationale for the approach is in the Technical Spec; this is the binding.

### 6.1 Content-matching locator — `ITerminalLocator` (Phase 2/3)

Realizes TS §III.2. Uses **FlaUI (UIA3)**:

- Enumerate `WindowsTerminal.exe` top-level windows; walk each window's UIA subtree to its tab elements and the terminal content control.
- Read a pane's visible text via the UIA **Text pattern** (`TextPattern.DocumentRange.GetText`), and match it against the Registry's `Exchange` text — latest prompt line strongest, answer snippet and shown `cwd` corroborating.
- `FindTab(session)` → the matching tab's `TabRef` (window handle + tab index/element). `IdentifyForegroundTab()` → read the selected tab of the foreground terminal window, match, return its session.
- Ambiguity (identical recent text) → return "unresolved," and callers fall back to window-level (TS §III.7).
- All UIA behind this adapter; **UIA failure degrades to window-level, never throws to the app** (TS §IV.7).

### 6.2 Focus observer — `IFocusSource` (Phase 3)

Realizes TS §III.5:

- `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, …, WINEVENT_OUTOFCONTEXT)` via P/Invoke. Out-of-process delivery is message-based, so the registering thread runs a message loop — a dedicated STA thread, or the WPF UI thread's existing loop.
- **Window focus** first (cheap, reliable). **Tab focus** later via UIA selection/structure events, because switching tabs inside one terminal window does *not* raise the foreground event (same HWND).
- On foreground settling on a terminal window → `ITerminalLocator.IdentifyForegroundTab()` → if it dwells past a short threshold, emit a synthetic ack event into the Channel (Part 4). Dwell-thresholding avoids acking tabs merely passed through.

### 6.3 Virtual desktop — `IVirtualDesktopService` (Phase 4, plus pin-to-all now)

Realizes TS §III.9. The **MScholtes VirtualDesktop** wrapper, vendored as source behind this adapter and version-pinned:

- **Documented-tier** calls (which desktop a window is on; pin a window to all desktops) drive Phase 4 grouping and the §5.4 pin-to-all-desktops behavior.
- **Undocumented-tier** calls (enumerate, switch, name) are isolated here; the interface GUID shifts between Windows builds, so this adapter is the one expected to need occasional maintenance. Degrades per TS §IV.7 — if switching breaks on an update, window activation still jumps to the session and grouping still works.

### 6.4 Navigation — `ITerminalNavigator` (Phase 2)

Realizes TS §III.8. Preferred order:

1. **Delegate to Windows Terminal:** `wt.exe -w <window> focus-tab -t <index>` via `Process.Start`, when the locator resolved window + index.
2. **Direct activation:** `SetForegroundWindow` (P/Invoke) on the located window, then UIA-invoke the target tab.

Navigation is **click-initiated** from the dashboard's own window, so the foreground-lock generally does not bite (TS §III.8) — no foreground-stealing workarounds needed.

### 6.5 Integrity

The process runs at the user's **normal integrity, never elevated** — elevated UIA cannot inspect the non-elevated terminal windows, which would break §6.1–6.2 outright.

---

## Part 7 — Sound (NAudio)

`ISoundPlayer` implemented over **NAudio**, realizing TS §IV.5's need for programmatic volume:

- One sound file per event; play at **gain 1.0 for a notice, lower gain for a nudge** — no duplicate "quiet" files. A short fade-in makes the nudge feel softer rather than merely quieter.
- A mixer (`MixingSampleProvider`) to coalesce a burst instead of stacking fifteen beeps.
- Master volume plus per-session/per-group mute, all as gain — the reason NAudio is required over `System.Media.SoundPlayer`, which has no volume control at all.
- Sound files ship in the app directory; user overrides live under the config directory (Part 8).

---

## Part 8 — Storage and configuration

Location: **`%LOCALAPPDATA%\ClaudeDashboard\`**.

| Artifact | Tech | Purpose |
|---|---|---|
| `settings.json` | System.Text.Json | Human-editable settings: port, thresholds (nudge T₁/intervals, stale-collapse minutes), sound choices, mutes, default view, always-on-top. |
| `dashboard.db` | SQLite (Microsoft.Data.Sqlite) | Event/exchange history for Phase 5 (search, wait-time stats). Also the natural warm-restart substrate. |
| `port.txt` | plain text | The chosen port, so a **command-style** hook can rediscover the URL if ever needed (Part 9). |
| `logs/` | Serilog rolling files | Diagnostics; the only "console" a resident app has. |

The live Registry is **in memory**. Warm restart either replays recent events from `dashboard.db` or simply waits for the next events (TS §I.2); a periodic snapshot is optional, not required for Phase 1.

Event history table sketch (Phase 5): `(id, session_id, ts, event_type, payload_json, cwd)` — append-only; the source for both restart and stats.

---

## Part 9 — Claude Code hook configuration (the integration contract)

This is the wire between Claude Code and the dashboard. Registered in **`~/.claude/settings.json`** (user scope covers every project). All handlers are **HTTP hooks** POSTing to the loopback endpoint; a command-hook variant (reading `port.txt`) is the fallback where an env var must be captured that the HTTP path can't carry.

### 9.1 Events consumed → state, with exact payload fields

Common fields on every event: `session_id`, `prompt_id`, `transcript_path`, `cwd`, `hook_event_name` (plus `permission_mode`, `effort`, and `agent_id`/`agent_type` inside subagents).

| Event (matcher) | → State / action | Event-specific fields used |
|---|---|---|
| `SessionStart` (`startup`,`resume`,`fork`) | create/refresh session; `resume`/`fork` surface a pre-existing one | `source`, `session_title`, `cwd` |
| `UserPromptSubmit` | → **Working**; store `prompt` as the context line; **auto-ack** prior Unread/Needs-You | **`prompt`** (submitted text), `prompt_id`, `cwd` |
| `Notification` (`permission_prompt`) | → **NeedsPermission** | notification type |
| `Notification` (`idle_prompt`, `agent_needs_input`) | → **NeedsQuestion** | notification type |
| `Notification` (`agent_completed`) | corroborating "finished" signal (optional) | notification type |
| `Stop` | → **Unread**; store the answer | **`last_assistant_message`** (final answer text, inline — preferred over the transcript) |
| `StopFailure` (`rate_limit`,`overloaded`,`authentication_failed`,…) | → **Error**; record kind | error type (from matcher) |
| `SessionEnd` (`clear`,`resume`,`logout`,`prompt_input_exit`,`other`) | → **Ended**; schedule removal | end reason (from matcher) |
| `CwdChanged` *(optional)* | re-derive the session's **Group** | `cwd` |
| `SubagentStart` / `SubagentStop` *(optional)* | subagent roll-up, if surfaced | `agent_id`, `agent_type` |

Notes:
- `Notification`, `StopFailure`, `SessionEnd`, `CwdChanged` have **no decision control** — pure observation, which is exactly what the dashboard wants (§3.3).
- `transcript_path` is **fallback only**: it is written asynchronously and may lag the live turn, which is precisely why `prompt` and `last_assistant_message` are read inline instead.
- `UserPromptSubmit`, `Stop`, and `CwdChanged` take **no matcher** (they always fire); `SessionStart`, `Notification`, `StopFailure`, `SessionEnd` filter by the matcher values above.

### 9.2 Example `settings.json` block (HTTP hooks)

```json
{
  "allowedHttpHookUrls": ["http://127.0.0.1:52789/hook"],
  "httpHookAllowedEnvVars": ["CLAUDE_DASHBOARD_TOKEN"],
  "hooks": {
    "SessionStart": [
      { "hooks": [ { "type": "http", "url": "http://127.0.0.1:52789/hook",
        "headers": { "X-Dashboard-Token": "$CLAUDE_DASHBOARD_TOKEN" },
        "allowedEnvVars": ["CLAUDE_DASHBOARD_TOKEN"] } ] }
    ],
    "UserPromptSubmit": [
      { "hooks": [ { "type": "http", "url": "http://127.0.0.1:52789/hook",
        "headers": { "X-Dashboard-Token": "$CLAUDE_DASHBOARD_TOKEN" },
        "allowedEnvVars": ["CLAUDE_DASHBOARD_TOKEN"] } ] }
    ],
    "Notification": [
      { "hooks": [ { "type": "http", "url": "http://127.0.0.1:52789/hook",
        "headers": { "X-Dashboard-Token": "$CLAUDE_DASHBOARD_TOKEN" },
        "allowedEnvVars": ["CLAUDE_DASHBOARD_TOKEN"] } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "http", "url": "http://127.0.0.1:52789/hook",
        "headers": { "X-Dashboard-Token": "$CLAUDE_DASHBOARD_TOKEN" },
        "allowedEnvVars": ["CLAUDE_DASHBOARD_TOKEN"] } ] }
    ],
    "StopFailure": [
      { "hooks": [ { "type": "http", "url": "http://127.0.0.1:52789/hook",
        "headers": { "X-Dashboard-Token": "$CLAUDE_DASHBOARD_TOKEN" },
        "allowedEnvVars": ["CLAUDE_DASHBOARD_TOKEN"] } ] }
    ],
    "SessionEnd": [
      { "hooks": [ { "type": "http", "url": "http://127.0.0.1:52789/hook",
        "headers": { "X-Dashboard-Token": "$CLAUDE_DASHBOARD_TOKEN" },
        "allowedEnvVars": ["CLAUDE_DASHBOARD_TOKEN"] } ] }
    ]
  }
}
```

- The **`allowedHttpHookUrls`** allowlist is mandatory — Claude Code runs an HTTP hook only if its URL matches. The first-run setup writes it.
- The token is interpolated from **`CLAUDE_DASHBOARD_TOKEN`**, which must be listed in both `allowedEnvVars` (per-handler) and `httpHookAllowedEnvVars` (global), and set in the user environment. It never appears literally in the committed file.
- The dashboard verifies `X-Dashboard-Token` at `/hook` and drops mismatches (§3.4).

### 9.3 Merge, don't clobber

Hook entries merge across settings scopes, but *within* `~/.claude/settings.json` the first-run setup must **append** its handlers to the relevant arrays and add the two allowlists without overwriting the user's existing hooks. Parse, merge, write back — never replace the file wholesale.

---

## Part 10 — Startup, packaging, install

### 10.1 Startup model

Realizes the "logon tray app, not a service" decision (TS integration constraints):

- **Not a Windows Service** — Session 0 isolation blocks the tray icon, UIA, the foreground hook, and the virtual-desktop COM against the interactive session. A service would be blind to exactly the windows it exists to watch.
- **Auto-start at logon via Task Scheduler**, not the Run key or Startup folder — because the scheduled task can *also* restart the app on failure, which the other two cannot. Trigger: at logon (this user). Settings: "restart every 1 minute, up to 3 times, if the task fails." Run as the user, **normal integrity** (highest-privileges off), so UIA still sees non-elevated terminals.
- **In-process resilience:** .NET Generic Host + global handlers (`AppDomain.CurrentDomain.UnhandledException`, `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`) + Serilog. A UIA/VD fault downgrades a feature (TS §IV.7); it does not kill the process.
- **No watchdog** up front — add one only if real-world crashes prove Task Scheduler's restart is insufficient.

### 10.2 Packaging and first-run setup

- **Publish:** `dotnet publish -c Release -r win-x64 --self-contained` as a single-file exe. **Not MSIX** — its sandboxing fights writing the scheduled task and merging Claude Code's settings, both of which this tool must do.
- **First-run setup** (the step that realizes everything above):
  1. Register the logon scheduled task (via `schtasks`, or `Microsoft.Win32.TaskScheduler` for a typed API).
  2. Merge the hook config + `allowedHttpHookUrls` + `httpHookAllowedEnvVars` into `~/.claude/settings.json` (§9.3), pointing at the chosen port.
  3. Write `port.txt`; ensure `CLAUDE_DASHBOARD_TOKEN` exists (generate and set if absent).
- A later **Settings UI** (Phase 6) re-runs/repairs the task and hook config, and edits thresholds and sounds.

---

## Part 11 — Phase → implementation map

| Phase | Theme | Built this phase |
|---|---|---|
| **1** | See clearly | Core (Registry, state machine, attention engine, sound-policy engine, ports); App shell (Generic Host, Kestrel loopback ingress `/hook` + `/show`, Channel + EventConsumer, Dispatcher marshalling, WPF window with grouped/flat + bands + expanded rows, tray status light, ack tiers 1–2, NAudio notices+nudges, collapse rules); Serilog; SQLite event log; first-run setup (task + hook config). **No Win32/UIA.** |
| **2** | Go there | `ITerminalLocator` (FlaUI content-matching) + `ITerminalNavigator` (`wt.exe`/UIA); navigator wiring; per-terminal locate strategy. |
| **3** | It notices | `IFocusSource` (WinEvent + UIA selection); dwell → synthetic ack; on-screen notice suppression. |
| **4** | Task lens | `IVirtualDesktopService` desktop grouping + names; MScholtes adapter hardening. |
| **5** | Memory | SQLite history + search + wait-time stats; warm-restart snapshot. |
| **6** | Polish | Settings UI; sound editor; themes; task/hook repair. |
| **7** | Anywhere | `ClaudeDashboard.Remote` (ASP.NET Core + SignalR) as a second Core consumer; authenticated remote read/ack. |

---

## Appendix A — NuGet / dependencies (pin to .NET 10-compatible)

- **CommunityToolkit.Mvvm** — MVVM source generators.
- **H.NotifyIcon.Wpf** — tray icon + dynamic per-state glyph.
- **Microsoft.Extensions.Hosting** + ASP.NET Core (via the Web SDK / framework reference) — Generic Host + Kestrel minimal API.
- **FlaUI.UIA3** — UI Automation for content-matching, tab enumeration, selection events *(Phase 2+)*.
- **NAudio** — sound with programmatic gain/fade/mixing.
- **Microsoft.Data.Sqlite** — history/warm-restart store.
- **Serilog** + `Serilog.Sinks.File` — rolling logs.
- **System.Text.Json** — in-box; settings.
- **Microsoft.Win32.TaskScheduler** *(optional)* — typed scheduled-task registration.
- **MScholtes VirtualDesktop** — vendored as source behind `IVirtualDesktopService`, version-pinned *(Phase 4; pin-to-all now)*.

## Appendix B — Open items

- **ClaudeSessions relationship** — absorb it, or sibling? Its session-addressing scheme could slot into Phase 2 navigation as an alternative to UIA location. (Unresolved across all three docs.)
- **Subagents** — roll `SubagentStart`/`SubagentStop` into the parent row, or ignore? Default: ignore for Phase 1.
- **Queued prompts** — surface a "queued" hint on Working rows, and from which signal?
- **Retention default (proposed):** keep 30 days of events in `dashboard.db`; **Unread never auto-fades** — it persists until acked (finished-but-unseen is the thing the tool exists to surface).
- **Always-on-top default (proposed):** off, with a toggle.
- **Content-match disambiguation** — if two tabs ever show identical recent text, fall back to window level; a hidden per-session marker read only during troubleshooting is a maybe-later, not now.
