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
- **Architecture / RID:** `win-x64` (dev box is x64). Self-contained publish, as a **directory of files**, so "starts with Windows" doesn't depend on a machine-wide runtime install. *(This said single-file until PKG.2. The reason was that nothing should be able to copy "the exe" without its halves; Velopack ended the argument from the other side — it diffs releases at the file level, single-file is not a shape it packages, and the exe's neighbours now live inside Velopack's managed install directory, which no user browses. Packaging Design D2.)*
- **DPI:** Per-Monitor v2 (Part 5).

### 1.2 Projects

A hexagonal split: a portable domain core with **ports** (interfaces), and a Windows host supplying **adapters**. This is what makes the Phase 7 remote surface nearly free — it becomes a second consumer of the same Core — and what makes the domain unit-testable without a desktop.

| Project | TFM | Role | References |
|---|---|---|---|
| **ClaudeDashboard.Core** | `net10.0` | Domain: Registry, state machine, attention engine, sound-policy engine, and the port interfaces. **Zero** WPF, zero Win32, zero ASP.NET. | — |
| **ClaudeDashboard.App** | `net10.0-windows` | The Windows host: WPF UI, ingress (Kestrel), and every Win32/UIA/COM adapter. | Core |
| **ClaudeDashboard.Remote** *(Phase 7)* | `net10.0` | ASP.NET Core + SignalR read/ack surface for the phone. A second consumer of Core. | Core |
| **ClaudeDashboard.Tests** | `net10.0-windows` | xUnit. Exercises Core (pure) and adapter contracts against fakes. | Core, App |

> **Correction (2026-08-24, ratified at T1.0).** The Tests row originally read `net10.0`. That was unsatisfiable: a `net10.0` project cannot reference `ClaudeDashboard.App` (`net10.0-windows`) — NuGet rejects it with NU1201. The App reference is the load-bearing half of the row, so the TFM was widened to `net10.0-windows` and the single test project retained. Core's platform-neutrality is guaranteed by Core's own TFM and the forbidden-reference architecture tests, not by the test project's TFM. Alternatives weighed and rejected: splitting into `Core.Tests` + `App.Tests` (drags in a third `TestSupport` project once T1.6 adds shared fakes), and multi-targeting (doubles every test run).

**Dependency rule:** everything points *at* Core; nothing points at App. All OS-specific code lives in App behind interfaces declared in Core. Enforced by project references and, optionally, an architecture test.

### 1.3 The ports Core declares (host implements)

These interfaces are the seam. Core is written entirely against them; App provides the Windows implementations; Tests provide fakes; Remote ignores the desktop-only ones.

- `IClock` — `Now`; injectable so state-machine timing and nudge scheduling are testable.
- `ISoundPlayer` — `Play(SoundId, gain, fade)`; NAudio adapter in App.
- `ITerminalLocator` — `Task<TabRef?> FindTab(SessionId)` and `TabRef? IdentifyForegroundTab()`; the content-matching adapter (Phase 2/3).
- `IFocusSource` — raises `ForegroundChanged`/`TabFocusChanged`; the WinEvent+UIA adapter (Phase 3).
- `ITerminalNavigator` — `Task<bool> Activate(TabRef)`; the `wt.exe`/UIA adapter (Phase 2). Async because the adapter launches a process and may then drive UI Automation, neither of which may run on the WPF Dispatcher — the very thread the click arrives on. Returns `bool` because TS §IV.7 forbids throwing for a platform failure, and a `void` method that cannot throw can only fail silently, leaving the UI unable to fall back or tell the operator anything. *(Signature corrected 2026-08-24 at T1.6; the original sketch read `Activate(TabRef)`.)*
- `IVirtualDesktopService` — `GetDesktop(hwnd)`, `PinToAllDesktops(hwnd)`, later `Switch`/`Name` (Phase 4).
- `IEventSink` — how ingress hands normalized events to the pipeline.

---

## Part 2 — The Core library (portable domain)

Realizes TS Part IV. No behavior here is new; this is the C# shape of it.

### 2.1 Domain types

- `SessionId` — a wrapper over Claude Code's `session_id` string; Registry key.
- `Exchange` — `{ Prompt: string, Answer: string?, PromptId: string?, StartedAt, AnsweredAt? }`. The latest `Exchange` is the session's context line and the payload of an expanded row.
- `SessionState` — enum: `Working`, `NeedsPermission`, `NeedsQuestion`, `Error`, `Unread`, `Acked`, `Ended`.
- `Session` — `{ Id, State, Latest: Exchange, Cwd, Group: GroupKey, EnteredAt, LastActivity, ErrorKind? }` plus a small transition log.
- `Group` — derived container keyed by `Cwd` (Phase 1) or virtual-desktop id (Phase 4); exposes worst-member state and most-recent activity.

> **Correction (2026-08-24, found at T1.1).** `Session.Group` is a group **key**, not a
> `Group`. The two cannot be the same type: a container holding its members while each
> member holds the container is a cycle an immutable record graph cannot express. So a
> session carries the key, and a `Group` is the container derived over all sessions
> sharing it — which is also what §IV.3's "re-derived on directory-change events, not
> fixed at session start" requires. Key **assignment** and normalization belong to the
> group resolver (T1.4), not to `Session` and not to the Registry.
- `InboundEvent` — the normalized internal event (see 3.2), a discriminated shape (record hierarchy or a tagged struct) the pipeline applies.

### 2.2 SessionRegistry and the state machine

`SessionRegistry` holds `IReadOnlyDictionary<SessionId, Session>` and applies `InboundEvent`s. It realizes TS §IV.1 with two invariants baked in:

- **Idempotent:** re-applying the same event is a no-op.
- **Timestamp-guarded:** an event older than the session's last-applied stamp is dropped (delivery is at-least-once and can reorder — TS §I.2).

Mutation happens on exactly one thread (Part 4), so the Registry needs no internal locking. It raises change notifications (a plain `event` or `INotifyPropertyChanged`) that the UI layer subscribes to; Core does not know those subscribers are a UI.

### 2.3 Attention engine

A pure function `Order(sessions) → banded, ordered list`, realizing TS §IV.2 exactly — Needs-You **by kind first (Permission > Error > Question), then oldest-first within each kind**; Unread newest-first; then Working, Quiet, Ended. Pure and deterministic, so it is straightforward to unit-test the ordering asymmetry that is the heart of the model.

> **Correction (2026-08-24, found at T1.3).** This section previously read "Needs-You oldest-first", which was the pre-ruling rule and survived the TS amendments in `e645fd8`/`2860e14` because those touched TS §IV.2/§IV.3 and Impl §1.3/§2.1 but not this section. It therefore contradicted the very TS §IV.2 it claims to realize — and being the C# shape section for this task, it would have led anyone implementing or reviewing against it to the wrong ordering while correctly following the document. The severity order is defined once in code (`AttentionOrder`) and consumed by both the attention engine and `Group.WorstState`; see TS §IV.3 for the rationale.

### 2.4 Sound-policy engine

Realizes TS §IV.5 as a timer model over Registry state: each session in a nudge-eligible state carries a scheduled next-nudge time; entering `Acked` cancels it. The engine emits *intents* (`PlayNotice`, `PlayNudge(gain)`) against `ISoundPlayer` — it never touches audio APIs itself, so it runs in Tests with a fake player. Uses `IClock`.

---

## Part 3 — Ingress (event intake)

Realizes TS §II.1 and TS §II.7.

### 3.1 Host and binding

An in-process **ASP.NET Core minimal API on Kestrel**, started by the .NET **Generic Host** that also owns the WPF app, logging, and background services. Kestrel binds to **loopback only** — `http://127.0.0.1:<port>` — because nothing off-machine may post events (TS §II.7).

**The port is chosen per user, not fixed per machine.** A loopback bind is machine-wide while every other thing the dashboard owns is per-user, so a single fixed port lets the first user signed in take the only one and leaves every other user with a dashboard that can never hear anything.

The choice is made in three attempts, and **binding is the only question ever asked.** There is no registry of who owns which port and none is to be built: "may I have this port" is answered by trying to take it, which is instant and definitive (§5.3).

1. **The port recorded in `port.txt`**, if there is one. Continuity — the same user keeps the same port across restarts.
2. **A derived candidate**: the base port plus an offset from a stable hash of the user's SID, modulo a bounded range. **SHA-256, never `GetHashCode()`**, which .NET randomises per process — the same user would derive a different port every launch and every in-process test would still pass.
3. **A bounded walk upward.** Each step classifies the occupant with the `/health` identity of §3.2, so "another user's dashboard", "another instance of ours" and "a stranger" stay distinguishable rather than collapsing into "taken".

If all three fail, the dashboard **starts anyway**, logs at Error, and says so in the tray tooltip (§5.3). It never exits for want of a port.

Whatever is finally bound is written to `port.txt`, and announced in `listening.txt` for as long as it stays bound (§9.4). **No port appears anywhere in Claude Code's settings** — the hook names a script, and the script reads the announcement at the moment it runs. So a port that moves costs the operator nothing: they restart, and nothing in their hook configuration needs touching.

Two users therefore do not queue from a base port; they derive different candidates because their SIDs differ, and never contend. The walk exists only for a hash collision or a stranger.

> **Correction (2026-08-26, superseded 2026-08-30).** This section once read *"at a fixed default port… Fixed because the hook URL must be stable (Part 9)"*. That reason was true while first-run setup wrote the hook URL once. §9.3 then registered the handlers at every start and removed them at every quit, which made the URL follow the bound port instead — and **issue #29 has now removed the URL from the hook altogether**. The handler names a script, the script reads `listening.txt` for the port, and no port appears anywhere in Claude Code's settings. The port is free to move for the reason §3.1 gives — one per user — and for no other.
>
> **The allowlist residual is closed rather than accepted.** A moving port used to leave an entry in `allowedHttpHookUrls` that nothing removed, and the operator accepted the accumulation. A command hook is not on that allowlist, so nothing accumulates, and `--remove-hooks` clears the entries an earlier build left.

### 3.2 Endpoints

- `POST /hook` — the single ingest endpoint. Body is the hook's JSON. The handler reads `hook_event_name`, deserializes the event-specific fields (Part 9), maps to an `InboundEvent`, writes it to the Channel (Part 4), and returns **`200` with an empty body immediately**. It does no Registry work on the request thread.
- `POST /show` — single-instance signal (Part 5.3): tells the running instance to surface its window. Returns `200`.
- `GET /health` — liveness for diagnostics, **and instance identity**. Unauthenticated, and answers JSON: `{ "status": "ok", "instance": "Local\ClaudeDashboard.SingleInstance.<hash>" }` — the same gate name the single-instance mutex uses (Part 5.3). The identity is there because the two interlocks have different scopes: **a loopback port bind is machine-wide, while the gate is per logon session and per data folder**. Without it, a health answer cannot tell "another copy of me" from "another user's dashboard, which holds the port and which I must not signal" — and under fast user switching the second user's dashboard would conclude it was a duplicate, raise the *first* user's window on the *first* user's desktop, and exit having said nothing. JSON rather than a bare name, so the contract stays self-describing and a later field does not break this one. The name is a hash of a local path returned on loopback: no secret, and nothing reversible.
- `GET /state` — what the Registry believes now (T1.46): one entry per session, the band counts and the tray roll-up, as JSON. **The token is required**: a missing or wrong `X-Dashboard-Token` gets `401`, as on `/hook` and `/show`. Since T1.48 there is no run without a token (§3.4), so the `404` it once gave when none was configured is gone. Read-only; the request thread reads a report the consumer thread published and never touches the Registry. Titles and task descriptions are included; prompt and answer text never are.

### 3.3 Pure-observer property

Every hook the dashboard registers is **observational**: the endpoint returns `200`/empty and never returns a decision field. Per the hooks reference, a `2xx` empty body is "success, no decision," so the dashboard **cannot block, delay, or alter** any Claude Code turn. This is a hard design constraint — the monitor must never become able to interfere with the thing it monitors — and it also means a crashed or stopped dashboard degrades Claude Code to "no hooks fire," never to "Claude is stuck."

### 3.4 Boundary security

- Loopback bind (above).
- **Shared secret, always on (T1.48, issue #57):** a token in a hook header, checked at ingress on every endpoint but `/health`, to stop other local processes spoofing events. The dashboard makes a new one at every start — 32 random bytes, base64url, 43 characters — holds it in memory, and writes it as the second line of `listening.txt`, below the port, in one temp-then-rename step. `post-status.cmd` reads it from there at every event, so no Claude Code session ever holds a copy and a restart of the dashboard never cuts a session off. It is never in the environment, never in a committed file, and never logged. `CLAUDE_DASHBOARD_TOKEN`, which carried it until T1.48, is retired and ignored.
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

**The archive and the decisions record (T1.37, issue #48).** After the consumer has applied an event and the sound engine has decided, it hands the event and every decision it caused to the archive as one record, and the archive's own thread writes both in one transaction: an `events` row, and `decisions` rows whose `event_id` points at it (NULL for a tick). To answer "why did that sound play?", take the sessions that played something around the instant, and read their state moves, sweeps and sounds before it, with the event that caused each:

```sql
SELECT d.ts, d.session_id, d.kind, d.from_state, d.to_state, d.reason, d.detail, e.event_type
FROM decisions d LEFT JOIN events e ON e.id = d.event_id
WHERE d.session_id IN (SELECT session_id FROM decisions
                       WHERE kind IN ('NoticePlayed', 'NudgePlayed') AND ts BETWEEN $from AND $to)
  AND d.kind IN ('NoticePlayed', 'NudgePlayed', 'StateMoved', 'SilenceSwept')
  AND d.ts BETWEEN $since AND $to
ORDER BY d.session_id, d.id;
```

`ts` is ISO-8601 text with the local offset, so the range compares correctly as text within one offset. Rows of one event share `ts` and `event_id`, and the sound is listed before the move that caused it: the engine decides inside the Registry's change notification, before the consumer records the move, so `id` order within an event is not causal order. A roster group's notice has no `session_id`; its `detail` names the group and the member ids.

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
| **Red** | ≥1 session in `NeedsPermission` |
| **Amber** | ≥1 session in `Error` **or** `NeedsQuestion`; nothing above |
| **Green** | ≥1 Unread finish waiting; nothing above |
| **Blue** | ≥1 Working; nothing above |
| **Grey** | all quiet |

> **Correction (2026-08-24, ratified by the operator).** This table previously read **Red** = "question or permission" and **Amber** = "Error", while the section simultaneously claimed the tray uses the "same *worst wins* as groups (TS §IV.3)". After the Needs-You ratification (`e645fd8`, `2860e14`) those two statements contradict each other: §IV.3 ranks **Permission > Error > Question**, so a dashboard holding one `Error` and one `NeedsQuestion` rolls up as `Error` — but the old table showed **Red**, because Question shared Red and Red outranked Amber.
>
> **Ruled: the tray mirrors §IV.3.** `Error` + `NeedsQuestion` now shows **Amber**, as does `Error` alone. Achieved by moving `NeedsQuestion` down to share Amber with `Error`, rather than by inventing a sixth colour: that keeps the precedence linear, needs no palette the mockups lack, and preserves the original intent that Red means *"it is asking you for something only you can give"*.
>
> The tray is still a **coarsening** — five colours for eight states — so `Error` and `NeedsQuestion` are indistinguishable in the glyph. That is what the tooltip counts are for; the distinction stays available where there is room to render it.

> **The tray palette is deliberately *not* the row LED palette (noted 2026-08-24, drafting T1.13).** `RowVisuals.AccentOf` already maps a state to a colour, and it puts `NeedsQuestion` on **Red** — right for a row, where the LED says what *that one session* is, and where Red is also what earns the blink (`MotionPolicy`: "red blinks; an error is amber and does not blink"). That mapping is **not monotone in `AttentionOrder.Rank`** — Permission 6 → Red, Error 5 → Amber, Question 4 → Red — so it cannot also serve a roll-up; reusing it for the tray reintroduces the contradiction above in the one-session-asking-a-question case.
>
> The two palettes are separate on purpose, and the property that tells a legitimate coarsening from a second severity opinion is **monotonicity in `Rank`**: if `Rank(a) > Rank(b)`, the tray colour of `a` must be at least as severe as the tray colour of `b`. The tray mapping must satisfy that; `AccentOf` demonstrably does not. Derive the tray colour from `Rank`, and do not "unify" the two.
>
> Consequence the operator sees: a lone session asking a question shows **amber** in the tray and **red, blinking** in its row. That is intended — the tray triages (*how urgently should I look?*), the row diagnoses (*what is it doing?*).

- **Counts live in the tooltip**, not the glyph — a 16px icon can't render legible digits. Because the glyph merges `Error` and `NeedsQuestion`, the tooltip **breaks the Needs-You kinds out** rather than reusing the header's summary line: `2 permissions · 1 error · 1 question · 2 unread · 3 working`, omitting any zero count, and reading `all quiet` when every count is zero. H.NotifyIcon can generate the icon bitmap per state; if a digit is drawn, cap at `9+`.
- **No animation.** The tray is static per state (TS reserves motion for needs-you rows inside the window, not the tray).
- **Left-click:** toggle the dashboard window. **Right-click:** context menu — Open · Mute all (and "for 30 min") · Pause monitoring · Settings · **Quit**.

#### Mute all versus Pause monitoring (ratified by the operator, 2026-08-24)

Neither spec previously said what **Pause monitoring** does — it appeared only in this menu list and in the plan's T1.13 deliverables — which left it behaviourally identical to Mute all. Ruled:

| | Sound | Glyph | Ends |
|---|---|---|---|
| **Mute all** | silenced | **unchanged — still the true colour** | on expiry (30 min) or on unmute |
| **Pause monitoring** | silenced | **grey, visibly "off duty"** | only when the operator resumes |

**Mute all is the volume knob; Pause monitoring is going off duty.** Muted, the operator can still glance at a burning red icon and know; paused, nothing pulls at them at all.

- This is the **one deliberate exception** to "the tray tells the truth" (Design §9). It is not a leak — the operator turned it off *on purpose, from that menu*, this second — and the exception is the entire value of the item.
- **The paused glyph must be visually distinct from all-quiet grey** (dim, hollow, or outlined — whatever reads as *off* rather than as *calm*). Same bitmap for both would make "nothing is happening" and "I switched it off" indistinguishable, which is the failure this ruling is one click away from. Still static, still no digits.
- The tooltip **leads** with the mode: `paused · click to resume` when paused, `muted 24 min · …counts…` when muted. Paused, the counts may follow but the first words say why the icon is grey.
- The menu item **toggles** — it reads *Resume monitoring* while paused. Not a second item.
- **Pause does not survive a restart.** A dashboard that comes back silently paused tomorrow morning is the same trap as an append-only group mute, with the whole app behind it.
- Neither mute nor pause stops **ingestion**. Events keep arriving, the Registry stays correct, and the window shows the truth whether or not the glyph does.

### 5.3 Single instance

A named `Mutex` created at startup; if already held, this process is the second instance. The **loopback port bind** is the belt-and-suspenders interlock — a second instance can't bind the same port. The second instance signals the first by `POST /show` to the loopback endpoint (reusing ingress; no separate IPC), then exits.

The mutex is **session-local** (per logon session, not machine-wide) and its name is **keyed to the data folder root** — the full path, lowercased, trailing separator stripped, reduced to a process-stable hash. Both interlocks must observe the same thing: the port the second instance signals comes from the settings file under that root, so if the mutex were one fixed name while `CLAUDE_DASHBOARD_HOME` could move the root, a second instance could find the mutex held by an instance that never bound the port its own settings name — and would report the first one unreachable while a dashboard was plainly running. With one data folder, the shipping case, this is indistinguishable from a fixed name.

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

- **Documented-tier** calls are `IVirtualDesktopManager` (`shobjidl_core.h`) and nothing else: `GetWindowDesktopId`, `IsWindowOnCurrentVirtualDesktop`, `MoveWindowToDesktop`. Which desktop a window is on drives Phase 4 grouping.
- **Undocumented-tier** calls are enumerate, switch, name, **and pin**. All are isolated here; the interface GUIDs shift between Windows builds, so this adapter is the one expected to need occasional maintenance. Version-pin: record in the source which Windows build the GUIDs were taken from. Degrades per TS §IV.7 — if switching breaks on an update, window activation still jumps to the session and grouping still works; if pinning breaks, the dashboard is confined to one virtual desktop, which is a lost convenience and not a broken product.

> **Correction (2026-08-26, found while drafting T1.16).** This section previously filed
> "pin a window to all desktops" under the **documented** tier. It is not there. Verified
> against Microsoft's own reference: `IVirtualDesktopManager` has exactly the three methods
> listed above and none of them pins. Pinning lives on `IVirtualDesktopPinnedApps`, which
> Microsoft does not document.
>
> Left uncorrected, a coder building T1.16 would have looked for a pin method on the
> documented interface, not found one, and then either abandoned the §5.4 pin-to-all-desktops
> behaviour or reached for the undocumented interface **without** the isolation, the
> version-pinning and the degrade-to-`false` discipline that the undocumented tier requires —
> which is exactly the maintenance burden this section exists to contain.

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

> **Clarification (2026-08-24, drafting T1.14).** The gain line above is a **rationale for requiring NAudio**, not a division of responsibility, and read as the latter it puts mute in the wrong place. **The adapter does not implement mute and knows nothing of sessions or groups** — `ISoundPlayer.Play(SoundId, gain, fade)` is the whole surface, and its own contract says master volume and per-session/per-group mute *"are folded in by the caller, since they are policy, not playback"*. The adapter literally cannot do per-session mute: it has no session.
>
> This matters because **T1.13 already implemented mute in `SoundPolicyEngine`**, and proved it by asserting the recording player sees **no `Play` call at all**. A second mute inside the adapter would be the same rule in two code paths — the failure mode this spec has been amended three times to avoid. Master volume is the one genuinely new quantity and belongs beside the other gains, in Core's `SoundPolicyOptions`, folded into the gain the engine passes. One place computes a final gain; the adapter plays it.

---

## Part 8 — Storage and configuration

Location: **`%LOCALAPPDATA%\ClaudeDashboard\`**.

**`CLAUDE_DASHBOARD_HOME`** (optional). Overrides the root of the dashboard's own data folder — `settings.json`, `port.txt`, the log directory and `dashboard.db`. Absent or blank means the default under `LocalApplicationData`. It exists because `Environment.GetFolderPath` resolves the known folder through the shell and ignores the `LOCALAPPDATA` environment variable, so there is otherwise no way to relocate the data folder — which a portable install, a roaming profile that should not keep data under `Local`, and a second instance under test all need. An unusable value falls back to the default and logs the reason; it never stops startup. It does not move Claude Code's own `~/.claude/settings.json`, which is not ours.

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
| `Notification` (`agent_needs_input`) | → **NeedsQuestion** | notification type |
| `Notification` (`idle_prompt`) | **no state change** — observed and inert (see the correction after this table) | notification type |
| `Notification` (`agent_completed`) | corroborating "finished" signal (optional) | notification type |
| `Stop` | → **Unread**; store the answer | **`last_assistant_message`** (final answer text, inline — preferred over the transcript) |
| `StopFailure` (`rate_limit`,`overloaded`,`authentication_failed`,…) | → **Error**; record kind | error type (from matcher) |
| `SessionEnd` (`clear`,`resume`,`logout`,`prompt_input_exit`,`other`) | → **Ended**; schedule removal | end reason (from matcher) |
| `PostToolBatch` | **`NeedsPermission`/`NeedsQuestion`/`Error` → Working** — the turn resumed. Never from `Unread`. See TS §IV.1's 2026-08-25 addition | *(common fields only — `tool_calls`/`batch_id` are not read)* |
| `CwdChanged` *(optional)* | re-derive the session's **Group** | `cwd` |
| `SubagentStart` / `SubagentStop` *(optional)* | subagent roll-up, if surfaced | `agent_id`, `agent_type` |

> **Correction (2026-08-24, found by dogfooding — [issue #1](https://github.com/dsopko/claude-dashboard/issues/1)).** The `Notification` row previously read **`idle_prompt`, `agent_needs_input` → NeedsQuestion**. `idle_prompt` is not a question and must change no state; see the reasoning in TS §II.2's correction, which is the authority. In short: `idle_prompt` fires because a session has been sitting there, every finished session eventually sits there, and so **every Unread was being promoted to red-and-blinking Needs You about ninety seconds after it finished.**
>
> Confirmed against live Claude Code, not inferred: one day's hook log carried **207 `Notification`s against 13 `PermissionRequest`s**, and the flip was traced to a `Notification` at `22:48:00` with no permission request before it.
>
> Two things this measurement settled in passing, both previously open since 2026-08-22 and both blocking T1.18:
> - **`notification_type` is real and carries the spellings this table expects.** `idle_prompt` parsed, which is the only way `NeedsQuestion` was reachable.
> - **`permission_prompt` arrives as a `Notification`, so `NeedsPermission` is reachable and the tray's Red is live.** Every `PermissionRequest` in the log is followed ~6s later by a `Notification`, and the dashboard was observed entering `NeedsPermission` correctly. **`PermissionRequest` is therefore corroboration, not the primary path, and ingress is right not to consume it.**

Notes:
- `Notification`, `StopFailure`, `SessionEnd`, `CwdChanged` have **no decision control** — pure observation, which is exactly what the dashboard wants (§3.3).
- `transcript_path` is **fallback only**: it is written asynchronously and may lag the live turn, which is precisely why `prompt` and `last_assistant_message` are read inline instead.
- `UserPromptSubmit`, `Stop`, and `CwdChanged` take **no matcher** (they always fire); `SessionStart`, `Notification`, `StopFailure`, `SessionEnd` filter by the matcher values above.

### 9.2 The hook handler (command hook)

This is what the plugin's `hooks\hooks.json` holds (§9.4). The dashboard writes it in its own data folder, and nowhere else.

```json
{
  "hooks": {
    "SessionStart": [
      { "hooks": [ { "type": "command",
        "command": "C:\\Windows\\System32\\cmd.exe",
        "args": ["/c", "C:\\Users\\<user>\\AppData\\Local\\ClaudeDashboard\\post-status.cmd"],
        "async": true } ] }
    ]
  }
}
```

One entry of that shape per accepted event, taken from `HookEventNames.Accepted`: `SessionStart`, `UserPromptSubmit`, `Notification`, `Stop`, `StopFailure`, `SessionEnd`, `CwdChanged`, `PostToolBatch`.

- **The exec form — `command` plus `args` — so no shell runs.** On Windows the `shell` field defaults to `bash`, or to `powershell` when Git Bash is not installed, so it varies by machine and cannot be chosen by us; the two disagree about backslash paths and quoting. Both paths are absolute and resolved at install time, because nothing expands `%SystemRoot%` or `%LOCALAPPDATA%` in this form.
- **`async: true`**, so the hook never delays a turn. **No `asyncRewake`**: it acts on an exit code, and the script exits 0 on every path by design.
- **No allowlists and no `headers`.** `post-status.cmd` reads the token from `listening.txt` itself and always sends `X-Dashboard-Token` (§3.4). `allowedHttpHookUrls`, `allowedEnvVars` and `httpHookAllowedEnvVars` are all unnecessary.
- The dashboard verifies `X-Dashboard-Token` at `/hook` and drops mismatches (§3.4).

### 9.3 The dashboard never writes Claude Code's settings

`~/.claude/settings.json` belongs to Claude Code, which writes it too. **The dashboard reads that file and never writes it** (the operator's ruling of 2026-10-01). The hook reaches Claude Code as a plugin, which Claude Code registers itself (§9.4).

*Until that ruling this section was "Merge, don't clobber". The dashboard merged its handler into that file: at every start and quit until issue #29, once at install after it, at any start that found the handler missing from issue #39 (T1.32), and from issue #30 only as a fallback behind the plugin. Each step bounded the damage a second writer can do, and none removed it. A second writer rewrites the whole file. It can lose a change Claude Code made in the same moment, and it reformats what it did not mean to touch: `JsonNode` carries neither comments nor spacing. Its mistakes break Claude Code, not the dashboard. The ruling removes the writer, and `SettingsFileWriter` and the merge with it.*

What the dashboard reads there, at every start:

- **`enabledPlugins` and `extraKnownMarketplaces`**, to learn whether its plugin is enabled, turned off, or held by another data folder. "Ours" is decided by the folder the settings give for the marketplace, never by the name alone.
- **`hooks`**, for a handler that a build before the plugin left there. It is identified by the script path in `args`, compared after `Path.GetFullPath`, ordinal-ignore-case, and by nothing else. *Accepted limit: an 8.3 short path does not match.*

The read is defensive, because the file is hand-editable and the read is on the startup path:

- Comments and trailing commas are tolerated.
- A file that will not parse — a duplicate key included, which `JsonNode` would otherwise raise late and as a different exception — is "cannot be read". Nothing is then claimed about it, nothing is asked of Claude Code, and the operator is shown a notice.
- A value of the wrong type is "not ours", never an exception.
- **Nothing out of the file is logged or shown.** The settings are the operator's, and one of their hooks may carry their prompt text.

A source guard holds the ruling: only the type that reads the file may name it, and that type holds no call that writes, moves, copies, creates or deletes anything.

### 9.4 The plugin — the only route (issue #30)

A plugin is the door Claude Code provides for another program's hooks. It is the only way the dashboard connects itself to Claude Code. **Where the plugin cannot be registered, the dashboard works round nothing: it tells the operator, on screen, what is wrong and what to do.**

- **The plugin lives in the data folder**, at `%LocalAppData%\ClaudeDashboard\plugin`: `.claude-plugin\marketplace.json`, `.claude-plugin\plugin.json` and `hooks\hooks.json`. Never in the install folder. Claude Code loads a plugin added from a folder **in place**, and the install folder is replaced at every update.
- **The plugin is a pointer.** `hooks.json` carries the handler of §9.2 on every accepted event and names `post-status.cmd` by absolute path. The script does not move, so it still finds `listening.txt` beside itself. `${CLAUDE_PLUGIN_ROOT}` is not used, so nothing depends on where Claude Code runs the plugin from. The files are rewritten at every start when they differ, which is how a build that changes the event set reaches a machine that is already registered.
- **Claude Code registers it.** The dashboard runs `claude plugin marketplace add <folder>` and `claude plugin install claude-dashboard@claude-dashboard`, with input closed and a time limit. Claude Code records the result in its own settings. A `claude` that records the plugin and then reports a failure has still registered it; the settings are read again to see.
- **`--install-hooks` registers the plugin and `--remove-hooks` removes it.** Neither does anything else. `--remove-hooks` clears `installHooksAtStart` in the dashboard's own `settings.json`, default `true`, so that the next start does not put the plugin back; `--install-hooks` sets it again, and turns a turned-off plugin back on, saying so.

**What a start does**, in this order, because several findings can hold at once and the operator is shown one notice:

| A start finds | It does | The notice |
|---|---|---|
| No Claude Code configuration directory | Nothing. Nothing is created (T1.33). | No Claude Code install was detected. |
| Claude Code's settings will not read | Nothing. | The file could not be read; nothing was changed. |
| An old hook in Claude Code's settings | **Does not register the plugin**, because both together would post every event twice. Does not touch the file. | Remove the old hook, then restart the dashboard: ask Claude, or use `/hooks`. Says "every event arrives twice" when the plugin is enabled as well. |
| The plugin enabled | Nothing. | None. |
| The plugin turned off | Nothing: `claude plugin install` would turn it back on. | The command that turns it on. |
| A plugin of the same name from another data folder | Nothing. | Names the other folder. |
| The dashboard's own settings will not read | Nothing: the opt-out is unknown, and unknown is not consent. | Fix or delete that file, or run `--install-hooks`. |
| `installHooksAtStart` is `false` | Nothing. | The plugin was removed; `--install-hooks` puts it back. |
| None of the above | Registers the plugin. | Registered: restart the open sessions. |
| …and `claude.exe` is not found | Nothing more. | The two commands to run by hand. |
| …and Claude Code refuses | Nothing more. | What it said, and the two commands. |

- **The notice is a row in the window and the first line of the tray tooltip.** A dashboard that receives nothing must not look like a quiet day.
- **A notice that says nothing is reporting clears when a session reports.** An arriving event is proof of the opposite. **The old-hook notice does not**: events arrive normally in that state, through the old hook itself. It stays until a start finds the hook gone.
- **`claude.exe` is looked for on `PATH`, then in `%USERPROFILE%\.local\bin`.** A `claude.cmd` shim is reported as not found: running it needs `cmd.exe` quoting around a path that may hold spaces, and that branch was not measured. Such a machine gets the notice with the two commands.

Measured on 2026-09-30 and 2026-10-01 against Claude Code 2.1.286: both install commands ran with no person present and exited 0, and exited 0 again when repeated; the two removal commands exit 1 for a thing that is not there; `claude plugin enable` exits 0, and `claude plugin install` turns a disabled plugin back on; a hook fired in a new session; a hook whose folder had been moved away did not fire, with no error; a session open before an install or an enable never ran the hook, while a new session did. Not measured: `claude plugin update`, and the reload command inside an open session.

The rules that follow from a hook that names a script and not a port:

- **The two files are not one file.** `port.txt` records the port last bound and is an **input** — §3.1's first attempt, and how a second launch finds the running instance for `POST /show` (§5.3). `listening.txt` says a dashboard is bound **now**: written after a successful bind, overwritten at every start, deleted on a clean exit, and written temp-then-rename so the script cannot read half a number. Merging them breaks §3.1 and §5.3 in silence.
- **Nothing is announced unless ingress is bound.** A port held by a stranger means no `listening.txt`, because hook payloads carry the operator's prompts.
- **Residual, stated plainly.** A hard kill leaves `listening.txt` naming the last bound port. Until the next start the script posts there, and if something else has taken the port it receives the operator's prompts. Overwriting at every start is what bounds it. `TerminateProcess`, a CLR fast-fail and power loss reach none of the four withdrawal points; every exit the application initiates or observes reaches one.

---

## Part 10 — Startup, packaging, install

### 10.1 Startup model

Realizes the "logon tray app, not a service" decision (TS integration constraints):

- **Not a Windows Service** — Session 0 isolation blocks the tray icon, UIA, the foreground hook, and the virtual-desktop COM against the interactive session. A service would be blind to exactly the windows it exists to watch.
- **Auto-start at sign-in through the `Run` key** (issue #36, T1.50; the operator's ruling of 2026-10-01). The value is `Claude Dashboard` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, and its data is the quoted path of the installed copy's `current\ClaudeDashboard.App.exe` — never the root stub, which Velopack renames after an update. `startWithWindows` in the settings file, default on, is the truth: each start makes the value match it, and only the Settings window's checkbox changes it. Only a copy Setup installed registers; a portable copy or a repository build never writes the value. Windows' own off switch — the `StartupApproved\Run` mark that Windows Settings and Task Manager write — is respected: a start leaves it alone, and only ticking the checkbox clears it. Velopack's uninstall hook removes the value. It runs as the user at **normal integrity**, so UIA still sees non-elevated terminals.
  - **Why the `Run` key, and not Task Scheduler as this section said until T1.50.** The `Run` key is where users look for startup programs: Windows Settings › Apps › Startup and Task Manager's Startup tab both list it, and both can switch it off. A scheduled task appears in neither. The price is the one thing the task did that the key cannot: **restart after a crash is given up.** The task's "restart every 1 minute, up to 3 times" never ran in practice — the task was never registered on the operator's machine (Phase 1 acceptance) — and `LogonTask` is removed.
- **In-process resilience:** .NET Generic Host + global handlers (`AppDomain.CurrentDomain.UnhandledException`, `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`) + Serilog. A UIA/VD fault downgrades a feature (TS §IV.7); it does not kill the process.
- **No watchdog** up front, and since T1.50 no restart after a crash either — add one only if real-world crashes prove it is needed.

### 10.2 Packaging and first-run setup

- **Publish:** `build\package.ps1 -Version <semver>`, which runs `dotnet publish -c Release -r win-x64 --self-contained` to a **directory of files** under `artifacts\publish` (PKG.2). *(This said "a single-file exe" until PKG.2; Packaging Design D2 records why the shape changed — Velopack's delta updates diff at the file level, and a single-file bundle collapses every release into one opaque blob.)* **Not MSIX.** *(The reason given here was that its sandboxing fights writing the scheduled task and merging Claude Code's settings. The dashboard does neither now — T1.50 and T1.51 — so that reason has gone, and the choice of packager stands on the Packaging Design.)*
- **First-run setup** (the step that realizes everything above):
  1. *(Replaced by T1.50, issue #36.)* This step registered a logon scheduled task. Starting with Windows is now the app's own concern (§10.1): the first start of an installed copy writes the `Run` value, because `startWithWindows` defaults to on, and every later start keeps it matching the setting. So no installer step is needed, and Velopack's uninstall hook removes the value.
  2. *(Removed by T1.48, issue #57.)* This step set `CLAUDE_DASHBOARD_TOKEN` at User scope. The dashboard now makes its own token at every start and hands it to the hook in `listening.txt` (§3.4), so there is nothing to set, and a session started before a token existed can no longer be cut off by one.
  3. *(Replaced by T1.49 and T1.51, issue #30.)* This step merged the hook configuration into `~/.claude/settings.json`. The dashboard never writes that file now (§9.3). The app registers its plugin with Claude Code at its first start, and at any later start that finds the plugin missing (§9.4; issue #39, T1.32), so no installer step is needed. `port.txt` is written by the dashboard at every bind and is not a setup step.
- The **Settings window** opened in T1.50 with one setting, start with Windows (§10.1). Phase 6 grows it: it repairs the plugin registration, and edits thresholds and sounds.

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
- **Serilog** + `Serilog.Sinks.File` + `Serilog.Extensions.Hosting` — rolling logs, and the bridge from `Microsoft.Extensions.Logging` to Serilog. *(The bridge was added 2026-08-24 at T1.7. Without it the host's own framework diagnostics — DI resolution failures, hosted-service start/stop errors, and Kestrel binding errors — never reach the rolling file, which Part 8 calls "the only console a resident app has". A port-binding failure at startup is precisely the fault the operator needs the log to explain, and it would otherwise be invisible.)*
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
