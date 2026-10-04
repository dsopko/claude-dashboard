# Claude Dashboard — Implementation Specification

**C# / .NET / WPF · v0.2 · 2026-10-02 · agrees with the code at commit `0488527`**

## Part 0 — Scope, and the relation to the Technical Specification

This document gives the **Technical Specification** (`claude-dashboard-spec.md`, "TS") a concrete form: C#, .NET, WPF.

- The **TS** names no technology and owns the *reasons*: why the bands sort as they do, why the world comes from events. A port to a different platform starts from it.
- This **Implementation Specification** owns the *form*: which project holds what, which type carries which field, which value a threshold has. For a reason, it points to the TS by section number.

Section numbers here are independent. A reference to the TS is written "TS §IV.2".

**Marks.** *Not built* means that the text is the intent and the code does not do it. TS Appendix C lists all such items. Appendix C of this document gives the dated history of the changes.

**Other documents.** The [event flow](claude-dashboard-event-flow.md) follows one event through the code, with the file for each step. [Core and App](claude-dashboard-core-and-app.md) says which project holds which rule, and what a second interface needs.

---

## Part 1 — Platform and solution structure

### 1.1 Targets

- **Runtime:** .NET 10 (LTS).
- **Language:** C# 14.
- **UI:** WPF (`net10.0-windows`, `<UseWPF>true</UseWPF>`).
- **Architecture:** `win-x64`. A self-contained publish, as a **directory of files**. Self-contained, so that the start with Windows does not depend on a runtime install. A directory and not one file, because Velopack makes update packages from the differences between files (Packaging Design D2).
- **DPI:** Per-Monitor v2 (§5.4).

### 1.2 Projects

A portable domain core with **ports** (interfaces), and a Windows host that supplies **adapters**. The domain can thus be tested with no desktop, and a second consumer of Core (Phase 7) is possible.

| Project | Target | Role | References |
|---|---|---|---|
| **ClaudeDashboard.Core** | `net10.0` | The domain: Registry, state machine, attention order, groups and rosters, sound policy, and the port interfaces. **No** WPF, Win32 or ASP.NET | — |
| **ClaudeDashboard.App** | `net10.0-windows` | The Windows host: WPF interface, ingress (Kestrel), the event loop, storage, and each Windows adapter | Core |
| **ClaudeDashboard.Remote** *(Phase 7; an empty project)* | `net10.0` | A surface for a phone. A second consumer of Core | Core |
| **ClaudeDashboard.Tests** | `net10.0-windows` | xUnit | Core, App |

**The dependency rule:** all projects point *at* Core, and nothing points at App. All code for one operating system is in App. `tests/ClaudeDashboard.Tests/Architecture/DependencyRuleTests.cs` enforces the rule from the project files and from the compiled Core assembly.

The test project targets `net10.0-windows` because a `net10.0` project cannot reference App. Core's neutrality comes from Core's own target and from the dependency tests, not from the test project's target.

### 1.3 The ports that Core declares

Core is written against these interfaces. App supplies the Windows implementations, and Tests supply fakes.

| Port | Member | Adapter in App |
|---|---|---|
| `IClock` | `Now` | `SystemClock` |
| `ISoundPlayer` | `SoundOutcome Play(SoundId, gain, fade)`: `Queued`, `NoOutput` or `Failed` (T1.55) | `NAudioSoundPlayer` |
| `IEventSink` | `bool TryPublish(InboundEvent)`. Never blocks and never throws | The sink of `EventPipeline` |
| `IDecisionSink` | `SoundPlayed(…)`, `SoundDropped(…)`, `SoundSuppressed(…)`. The sound engine says what it decided, and what the player did with it | `DecisionRecorder` |
| `ISoundModeReader` | `IsMonitoringPaused`, `AllMutedUntil`. Read-only, safe on any thread | `SoundPolicyEngine` itself |
| `IVirtualDesktopService` | `GetDesktop(hwnd)`, `PinToAllDesktops(hwnd)`; later `Switch`, `Name` | `VirtualDesktopService` (the pin is used today) |
| `ITerminalLocator` *(Phase 2/3)* | `Task<TabRef?> FindTab(SessionId)`, `TabRef? IdentifyForegroundTab()` | None yet |
| `IFocusSource` *(Phase 3)* | Raises `ForegroundChanged`, `TabFocusChanged` | None yet |
| `ITerminalNavigator` *(Phase 2)* | `Task<bool> Activate(TabRef)`. Asynchronous, because the adapter starts a process. It returns `bool`, because a platform failure must not throw (TS §IV.7) | None yet |

---

## Part 2 — The Core library

Core is the C# form of TS Part IV. No behaviour here is new.

### 2.1 Domain types

- **`SessionId`** — a wrapper for Claude Code's `session_id`. The Registry's key.
- **`GroupKey`** — a wrapper for a group's identity. Not a display string (§2.5).
- **`Exchange`** — `{ Prompt, Answer?, PromptId?, StartedAt, AnsweredAt? }`. `IsAnswered` is true when `AnsweredAt` has a value.
- **`SessionState`** — an enum with nine values. **The numbers are stored on disk and must not change:** `Working = 1`, `NeedsPermission = 2`, `NeedsQuestion = 3`, `Error = 4`, `Unread = 5`, `Acked = 6`, `Ended = 7`, `Interrupted = 8`, `Waiting = 9`.
- **`Session`** — an immutable record. An event makes a new record.

| Field of `Session` | Meaning |
|---|---|
| `Id` | The session id |
| `State` | The state |
| `Latest` | The latest `Exchange` |
| `Cwd` | The working directory. It can be empty and is never null |
| `WorkspaceGroup` | The group key that the directory gives. A roster can override it at read time (§2.5) |
| `EnteredAt` | When the session entered its state. The sort key of the Needs You and Unread bands, the start of the nudge schedule, and the input of the settle window |
| `LastActivity` | When a transition last changed something. The sort key of the Working, Quiet and Ended bands. An event that changes nothing does not move it |
| `LastHeardAt` | When an event last *arrived*, also one that was declined. The silence sweep reads it. It is not an ordering key |
| `ClockAnchor` | The instant that the row's clock counts from, for the display only (§5.6.5) |
| `ErrorKind` | The raw kind of a `StopFailure`, in the Error state only |
| `Title` | The last title that arrived, or null |
| `Transitions` | A `TransitionLog`: the last 32 state changes, each with its cause |
| `WaitingOn` | The background tasks that the last `Stop` left running, if no prompt arrived since |
| `ListedTasks` | The tasks that the last `Stop` listed, with the time each was first listed. A prompt does not clear it, so a task keeps its age |
| `ScheduledPrompts` | The prompts of the session's scheduled jobs, from the last `Stop`. Held to be compared and never shown. The type has no string property |
| `PreTick` | A `TickSnapshot`: what the row showed before a scheduled job's tick began, while the tick runs |

- **`Group`** — the sessions that share a key. It gives `WorstState`, `Order` (which severity order applies) and `LastActivity`. A session carries the *key*; the `Group` is derived from all sessions.
- **`InboundEvent`** — the internal event. A closed hierarchy of records: the eight hook events (`SessionStart`, `UserPromptSubmit`, `Notification`, `Stop`, `StopFailure`, `SessionEnd`, `CwdChanged`, `PostToolBatch`) and three internal events (`Ack`, `SoundCommand`, `RostersChanged`). Each has `SessionId`, `Timestamp`, `Cwd`, `PromptId`, `TranscriptPath`, `SessionTitle` and `Payload`.
- **`PayloadJson`** — the raw hook body. It has no public string property, its `ToString()` gives a size only, and `Reveal()` is the one way to the text. Only the archive's insert calls `Reveal()`.

### 2.2 SessionRegistry and the state machine

`SessionRegistry` holds the sessions and applies events. It is the C# form of TS §IV.1.

| Member | Behaviour |
|---|---|
| `Apply(InboundEvent)` | Applies one event. Returns `ApplyOutcome`: `Applied`, `Ignored`, `Stale`, `Duplicate` or `Uncorrelated`. Only `Applied` changed the Registry. `Uncorrelated` is the one outcome that must not be frequent, and it is logged as a warning |
| `SweepSilent(now, threshold)` | Moves each `Working` session that is silent for longer than `threshold` to `Interrupted`. Returns the sessions it moved |
| `SessionChanged` | Raised one time for each change, with `Added` or `Updated` and the new `Session`. There is no `Removed`: nothing removes a session |
| `Sessions` | A live view. Read it on the writer's thread only |

Rules of the type:

- **One writer, no locks.** One thread calls `Apply` and `SweepSilent` (Part 4). `SingleWriterGuard` throws if two threads are inside the Registry or the sound engine at one time. The Registry and the sound engine share one guard.
- **No clock.** Each time that the Registry writes comes from the event or from the caller. A replay thus gives the same result.
- **No log.** The Registry returns what occurred, and the host writes it.

### 2.3 Attention order

`AttentionOrder` holds the one severity table (TS §IV.3). `AttentionEngine` puts sessions in order (TS §IV.2). Both are pure functions.

| State | `Rank` | `Rank` in a roster group | `BandOf` |
|---|---|---|---|
| `NeedsPermission` | 8 | 8 | NeedsYou |
| `Error` | 7 | 7 | NeedsYou |
| `NeedsQuestion` | 6 | 6 | NeedsYou |
| `Unread` | 5 | 3 | Unread |
| `Working` | 4 | 5 | Working |
| `Waiting` | 3 | 4 | Working |
| `Interrupted` | 2 | 2 | Quiet |
| `Acked` | 1 | 1 | Quiet |
| `Ended` | 0 | 0 | Ended |

- The roster order is written as an exchange of three ranks, not as a second table. Thus it cannot drift.
- `WorstOf(states, order)` is the one roll-up. A group and the tray light both use it. No states gives `Ended`.
- `AttentionEngine.Order(sessions)` gives the flat view: the bands that are not empty, most urgent first.
- `AttentionEngine.OrderGroups(groups, stateOf)` gives the grouped view. **The caller must say which state orders a group.** The window gives `RosterSettle.StateOf(group, now)`, the same value that the heading shows. A group that sorts by a state that the screen does not show would move by itself.
- Each order ends with the session id or the group key, so it is total.

### 2.4 Sound-policy engine

`SoundPolicyEngine` is the C# form of TS §IV.5. It calls `ISoundPlayer` and `IDecisionSink` and nothing else.

| Member | Behaviour |
|---|---|
| `OnSessionChanged(session, effectiveGroup)` | Plays a notice if the session entered a state that has one. Starts, continues or clears the nudge schedule. The group must be the *effective* group (§2.5) |
| `OnRosterGroupSettled(group, settledAt, quietSince)` | Plays the group's one finished notice |
| `OnRosterGroupUnsettled(group)` | Stops the group's nudge. Silent |
| `Evaluate(now)` | Plays each nudge that is due. One nudge for each session in one call. The next one is scheduled from `now` |
| `SetAllMuted(muted, until)` | Mute all. `until` null means no end |
| `SetMonitoringPaused(paused)` | Pause |
| `SetSessionMuted`, `SetGroupMuted` | Mute for one session or one group. *No caller: not built in the host* |
| `NextNudgeAt(session)` | The due time of the next nudge, or null |
| `NudgeScheduleAdvanced` | An event, raised when `Evaluate` moved a due time |

Values, all in `SoundPolicyOptions`:

| Option | Default | In `settings.json` |
|---|---|---|
| `NudgeLadder` | 2, 5, 10 minutes. The last repeats | No |
| `UnreadNudgeAfter` | 5 minutes, one nudge | No |
| `NudgeOnError` | `true` | `sound.nudgeOnError` |
| `NoticeGain` | 1.0 | `sound.noticeGain` |
| `NudgeGain` | 0.6. Constant on all rungs | `sound.nudgeGain` |
| `NudgeFadeIn` | 150 ms | No |
| `MasterVolume` | 1.0. A multiplier on each gain | `sound.masterVolume` |

- A nudge that is louder than a notice is not valid. `Validate()` throws; the settings layer repairs the pair first (§8.2).
- The engine knows a roster group only by the kind of its key. It holds no roster book.
- **An entry that comes back unchanged is not announced again.** After a quiet tick, the session has the same state and the same `EnteredAt` as before. The engine recognises that pair, restores the nudge schedule, and records a suppression with the reason `AlreadyAnnounced`.
- Mute and pause are checked at the point where a sound would play, in this sequence: pause, mute all, session mute, group mute. The schedule goes on.

### 2.5 Groups, rosters and the settle window

| Type | Role |
|---|---|
| `GroupKeys` | The one place that makes a key. `ForWorkspace(cwd)` gives `workspace:` and the directory with separators unified, the last separator removed, and upper case. `ForUngrouped(id)` gives `session:` and the id. `ForRoster(name)` gives `roster:` and the name |
| `GroupKeys.Effective(session, rosters)` | **The group that a session is in:** its roster's key if its title is in a roster, or else `WorkspaceGroup`. The Registry does not store this, because a roster changes at run time and there is no event for that |
| `GroupKeys.KindOf(key)` | `Workspace`, `Session`, `Roster` or `Unknown` |
| `GroupResolver.Resolve(sessions, rosters)` | Makes the groups again from the sessions at each call. No cache |
| `RosterBook` | All rosters. Immutable. `From`, `With`, `Without` and `RosterFor(title)` |
| `RosterSettle` | `DefaultWindow` = 1.5 s. `DefaultMisMarkWindow` = 5 s. `StateOf(group, now)` gives `Working` while a roster group whose raw state is `Unread` is inside the window. `QuietSince(group)` is the latest `EnteredAt` of its members |
| `RosterGroupWatch` | The only history in the feature. `Observe(groups, now)` reports `Settled`, `Unsettled` and `MisMarked`. `NextDeadline` gives the instant that the host must wake |

Rules of `RosterBook`, held by each operation:

- A name and a member are trimmed. An empty one is dropped.
- A member is in the first roster that claims it. In an edit, the edited roster claims first.
- A roster with no members does not exist.
- The match of a title is exact and ordinal, after a trim of both sides.
- A session with no title matches no roster.

A roster is logged by its name and never by its members: a member is a session title, and a title can be a summary of the operator's prompt.

### 2.6 Rules of the state machine, with the type that holds each

The transition table is in TS §IV.1. This table says where each rule that was added after the first version is in Core.

| Rule | Where | Notes |
|---|---|---|
| The title latch | `SessionRegistry.Latched` | Runs for each event, also a declined one. Never moves `LastActivity` or `EnteredAt` |
| Heard from | `SessionRegistry.Heard` | Sets `LastHeardAt` for each event that is not stale and is not an `Ack` |
| The silence sweep | `SessionRegistry.SweepSilent`, `SilenceWatch` | Threshold 10 minutes (`SilenceWatch.DefaultThreshold`). `Working` only. Sets `ClockAnchor` to `LastHeardAt`. The cause in the log is `silence` |
| Waiting | `BackgroundTaskKind`, `WaitingTasks`, `SessionRegistry.ApplyStop` | Allowed kinds: `Shell`, `Subagent`. `WaitingTasks.Following` keeps each task's first-seen time |
| Back to Waiting | `SessionRegistry.ApplyPostToolBatch` | If `WaitingOn` is not empty, a batch gives `Waiting`, not `Working` |
| Machine prompts | `UserPromptSubmit.IsMachinePrompt` | Prefixes, matched at the start, ordinal: `<task-notification>`, `<cross-session-message`, `[Cross-session idle notice]`, `<agent-message` |
| A prompt that continues the work | `UserPromptSubmit.ContinuesTheAsk` | The prefix `<task-notification>`. Keeps `Prompt` and `StartedAt`; takes the new `PromptId`; clears the answer |
| The tick of a scheduled job | `QuietTicks.IsTick` | The prompt is in `Session.ScheduledPrompts` |
| The quiet tick | `QuietTicks.IsQuiet`, `SessionRegistry.Reverted` | The reply, trimmed, is exactly `WATCHDOG-QUIET` (`QuietTicks.Sentinel`), and a `PreTick` exists |
| The opt-in line | `QuietTicks.OptInLine` | A test holds the guide and the plan to this text, word for word |
| The clock anchor | `SessionRegistry.AnchorEntering` | Entering `Acked` or `Ended` keeps the anchor of the state that is left, but from `Working` or `Waiting` it is the instant itself. Entering `Unread` takes `AnsweredAt` |
| The cause of a change | `StateTransition.Cause` | The hook name, with a note for an auto-ack, a machine prompt, a scheduled prompt, and a quiet tick |

### 2.7 The roll-up and acknowledgment

- **`StatusSummary.Of(sessions)`** gives the worst state and five counts: permissions, errors, questions, unread, working. `Waiting` counts as working. `IsAllQuiet` is true when all five are zero.
- **`Acknowledgment.Applies(state)`** is true for `Unread`, `NeedsPermission`, `NeedsQuestion` and `Error`. All three ways to acknowledge read this one rule, and so does each Ack button.
- **`Acknowledgment.For(session, now, source)`** builds the `Ack` event. It copies the session's `Cwd`, so that an Ack cannot move a session to a different group. `AckSource` is `Manual` or `InferredFocus`.

---

## Part 3 — Ingress

The C# form of TS §II.1 and §II.5.

### 3.1 Host and binding

An **ASP.NET Core minimal API on Kestrel**, in the dashboard's process. The .NET **Generic Host** also owns the WPF application, the log and the background services. Kestrel listens on **loopback only**: `http://127.0.0.1:<port>`.

**The port is chosen for each user, not for each machine.** A loopback port serves the whole machine, and all else that the dashboard owns belongs to one user. One fixed port would let the first user take it, and leave each other user with a dashboard that receives nothing.

The dashboard tries to bind each candidate. **To bind is the only question that it asks.** There is no register of who owns which port.

1. **The port that the operator pinned** as `port` in `settings.json`. If it is in use, the dashboard tries no other: a pinned port is usually a contract with something outside the dashboard.
2. **The port in `port.txt`:** the port that this user last bound.
3. **A derived port:** `52789` (`DashboardSettings.IngressPortBase`) plus an offset from 0 to 999. The offset comes from **SHA-256** of the user's SID. Never `GetHashCode()`: .NET makes it different in each process, and all in-process tests would still pass.
4. **The next ports above the derived port**, 32 at most. For each occupied port, the `/health` answer of §3.2 says who holds it.

If no port is free, the dashboard **starts**, and says what to do in three places (T1.57, issue #14): an Error line in the log, the first line of the tray tooltip, and the first line of the window's notice row (§5.2, §5.6.1). With no pin, the tray reads `port <n> taken · free a port and restart`, and the window names the first and the last port tried and the full path of `settings.json`. A pin that is taken reads `pinned port <n> taken · unpin it or free it, then restart`. **Nothing retries:** the dashboard asks once, by binding, because no event says that a port became free.

The port that is bound goes into `port.txt`, and into `listening.txt` for as long as it stays bound (§9.4). **No port is in Claude Code's settings.**

### 3.2 Endpoints

| Endpoint | Purpose | Token | Answers |
|---|---|---|---|
| `POST /hook` | Receives one hook event | Necessary | `200` with an empty body, always. `401` for a bad token |
| `POST /show` | A second start asks the first to show its window | Necessary | `200`, or `401` |
| `GET /health` | Liveness, and the identity of the instance | **Not checked** | `{"status":"ok","instance":"<gate name>"}` |
| `GET /state` | What the Registry believes now (§3.5) | Necessary | `200` with JSON, or `401` |

- **`/hook`** reads the body as text, parses it into `HookPayload` (all fields optional), maps it to an `InboundEvent` (§9.1), writes the event to the channel (Part 4), and answers. It does no Registry work.
- **The self-test's message is taken out before the mapper** (T1.61, issue #74). A body whose `hook_event_name` is `ClaudeDashboardSelfTest` is noted as arrived, by its one-time value in `self_test`, and answered `200` empty. It reaches no mapper, no channel, no Registry and no archive, so it makes no session, no row, no sound and no decision. `HookEventNames.Accepted` does not have it (§9.4).
- **Every other post with a good token moves "last heard"** (`HookHealth.Heard`, on the request thread), the tooltip's last item (§5.2). The self-test and a refused post do not.
- **A refused post** (`401`) is counted (`HookHealth.RefusedCount`, which #76 reads) and is recorded in `HookRefused` decision rows (§8.3), with no event and no session: at most one row a second, with the count (the operator's ruling of 2026-10-04; a flood wrote about 100 MB a minute). The tick writes what a flood left over, so no refusal goes unrecorded while the dashboard runs; at a stop, the refusals counted since the last row (at most one tick, 15 seconds) are not written (T1.62). It is not trusted, so nothing from its body or headers is kept. The Warning for each refused post stays.
- **`/health` has no token, and single-instance detection depends on that.** A start asks this endpoint if the dashboard on a port is a copy of itself. The dashboard of a different user has a different token, which the caller cannot hold. The instance value is the name of the single-instance gate (§5.3): a hash of a local path, not a secret.

### 3.3 The pure-observer property

Each hook that the dashboard registers only observes. `/hook` answers `200` with an empty body and **never a decision field**. To Claude Code, that means "success, no decision". The dashboard thus **cannot block, delay or change** a Claude Code turn.

`/hook` answers `200` for a good event, an unknown event, a body that is not JSON, an absent session id, a full channel, and any exception after the token check. A dashboard under load must never push back on the thing that it watches.

### 3.4 Boundary security

- **Loopback only.**
- **A token, always** (T1.48). At each start the dashboard makes 32 random bytes and writes them as 43 characters of base64url. It holds the token in memory and writes it as line 2 of `listening.txt`, below the port, in one write-then-rename step. `post-status.cmd` reads it at each event and sends it as the header `X-Dashboard-Token`. Thus no Claude Code session holds a copy, and a restart of the dashboard cuts no session off. The token is never in the environment, in a committed file or in a log. The variable `CLAUDE_DASHBOARD_TOKEN`, which held it before, is ignored; the dashboard says so one time in its log.
- **All event text is data.** WPF shows a string as text. No path evaluates it.
- **Text that the operator wrote, or that a model wrote for the operator, is never logged:** a title, a prompt, an answer, a payload, a task description, a task command. `PayloadJson` and `OperatorText` make this hold by construction for the body and for `/state`. `tests/.../Domain/UnprotectedTextInventory.cs` lists each property where the same words are still a plain string.

### 3.5 The `/state` contract

`GET /state` answers one JSON object. Names are camelCase. An enum is its name as a string. An instant is ISO 8601 with an offset.

**The report**

| Field | Type | Meaning |
|---|---|---|
| `publishedAt` | instant | When the report was built. Not when the request arrived |
| `sessionCount` | number | All sessions in the Registry, Ended included |
| `bands` | object | The count for each band. All five keys are present, also at zero: `needsYou`, `unread`, `working`, `quiet`, `ended` |
| `tray` | object | `worst`: the state that sets the tray light. `light`: `Red`, `Amber`, `Green`, `Blue` or `Grey` |
| `sessions` | array | One entry for each session, in the order of the flat view |
| `health` | object | The path from Claude Code (T1.61, issue #74). `lastHeardAt`: when the last real message was accepted, in UTC ending in `Z`, or null since start. `selfTest`: the last self-test, `passed`, `roundTripMs` (or null) and `at` (UTC), or null before the first has finished. Read when the request is served, not when the report is built. #76 adds to this object |

**One session**

| Field | Type | Meaning |
|---|---|---|
| `id` | string | Claude Code's session id |
| `state` | string | `Working`, `Waiting`, `NeedsPermission`, `NeedsQuestion`, `Error`, `Unread`, `Acked`, `Interrupted` or `Ended` |
| `band` | string | `NeedsYou`, `Unread`, `Working`, `Quiet` or `Ended` |
| `group` | string | **The directory group key** (`workspace:…` or `session:…`). It is an identity with upper case, not a label. It is not the roster group: rosters are not in the report |
| `cwd` | string | The working directory. Can be empty |
| `title` | string or null | The session's title. Null if none arrived |
| `enteredAt` | instant | When the session entered `state` |
| `lastActivity` | instant | When a transition last changed something |
| `lastHeardAt` | instant | When an event last arrived |
| `errorKind` | string or null | In `Error` only. The kind as Claude Code sent it, for example `rate_limit`. Empty if the event carried none (§9.1) |
| `nextNudgeAt` | instant or null | The due time of the next nudge. See below |
| `waitingOn` | array | The background tasks that the session waits on. Empty if none |

**One task in `waitingOn`**

| Field | Type | Meaning |
|---|---|---|
| `id` | string | Claude Code's task id |
| `kind` | string | `Shell` or `Subagent` |
| `description` | string | What the agent said the task is |
| `firstSeenAt` | instant | When a `Stop` first listed it |

**What a caller must know**

- **`nextNudgeAt` is the schedule, not what is audible.** A muted session still shows a time.
- **A null `nextNudgeAt` has two meanings** (issue #58): no nudge is due, or the session is a finished member of a roster group and the group owns the notice. The report cannot tell them apart.
- **`waitingOn` is usually empty unless `state` is `Waiting`.** It is also not empty for a session that waits on a background agent while that agent's permission prompt, question or error shows on the parent.
- **The report never carries a prompt, an answer or a task's command.**
- **`title` and `description` are operator text.** A caller must not log them.
- **Before the first event,** the report has `sessionCount` 0, all bands 0, `tray.worst` `Ended`, `tray.light` `Grey` and no sessions.
- **The report does not change with time alone.** It is built when a session changes and when a nudge fires. It holds instants, not ages. `health` is the exception: it is read at each request, because it is written on request threads.
- **Not in the report:** the mute and pause modes, the rosters, the settle window's state of a group, the notice, and the row's clock anchor.

How it crosses threads: `StateBoard` listens to `SessionChanged` and `NudgeScheduleAdvanced` on the consumer thread, builds a new immutable `StateReport`, and stores it with one `Volatile.Write`. A request does one `Volatile.Read`. `StateBoard` must subscribe **after** the sound engine, because it reads the nudge time that the engine has just set; `AppHost` resolves it in that sequence and `Hosting/StateHostTests.cs` holds it.

---

## Part 4 — Threading and data flow

```
 Kestrel request threads (many)              UI thread (clicks)
        │  IEventSink.TryPublish                   │  IEventSink.TryPublish
        ▼                                           ▼
 Channel<InboundEvent>   1,024: noise shed after; 16,384: drop-oldest; one reader
        │
        ▼
 EventConsumer (one thread: the only writer)
        │  SessionRegistry.Apply          ── for each event
        │  SweepSilent · Evaluate · roster groups   ── on the tick, each 15 s
        │
        ├─ SessionChanged ─▶ SoundPolicyEngine.OnSessionChanged ─▶ ISoundPlayer
        ├─ SessionChanged ─▶ SessionProjection ─▶ dispatcher ─▶ view models ─▶ WPF
        ├─ SessionChanged ─▶ StateBoard ─▶ the report for /state
        ├─ the tick ───────▶ UiTick ─▶ dispatcher ─▶ view models (ages, stale groups, mute)
        ▼
 Channel<ArchiveRecord>  1,024 records, drop-oldest, one reader
        │
        ▼
 EventArchiveWriter (one thread) ─▶ dashboard.db
```

- **The channel sheds only noise** (T1.58, the operator's ruling of 2026-10-03 on issue #3). Until T1.58 it dropped its oldest event when full, and the oldest could be the permission prompt the operator most needed. A write never blocks.
  - **Below 1,024 queued** (`EventPipeline.DefaultCapacity`), every event is written.
  - **At or above 1,024**, a noise event is refused at the door: the newest is shed, not the oldest. Every other event is still written. Nothing already queued is removed, so order is kept.
  - **Noise is decided by kind, at the door, without reading the Registry** (`PipelineNoise`): a `PostToolBatch`, and a `Notification` whose kind moves no state. The kinds come from `SessionRegistry.TargetOf(NotificationKind)`: today `idle_prompt`, `agent_completed` and an unrecognised type. An event the window or the operator publishes (`Ack`, `SoundCommand`, `RostersChanged`) is never noise.
  - **The safe direction.** A shed `PostToolBatch` that would have resumed a blocked session leaves the row red until the session's next event. A row that is too loud for a moment is the safe failure; a row that is silent while Claude waits is the one this removes. A shed event's other effects wait for the session's next written event too: a title it carried, the `LastHeardAt` refresh, and the first row of a session that no written event has started yet.
  - **A hard limit of 16,384** (16 times the capacity) is for a flood of state-changing events, a fault never seen: there the oldest event is dropped, as before, so memory stays bounded.
  - **The record:** each shed event records `EventDropped` with reason `noise` and its kind in the detail; each drop at the hard limit, reason `pipeline` (§8.3). **The log:** one Warning when shedding starts, and one Information line when the queue is below 1,024 again, with the counts. No line for each event: that would bury the log in the one situation where it matters. `/hook` still answers `200` with an empty body.
  - **The notices** (§5.6.1): `fell behind` while noise was shed in the last 5 minutes, cleared on the tick; `events lost` after a drop at the hard limit, until the next start.
  - **The bound is asserted.** `QueueThroughputTests` drives the real consumer through 1,024 state-changing events (each applied, raising `SessionChanged`, running the sound policy and reaching the projection) and fails above 5 seconds. Measured on the development box: 31 to 45 ms. Blocking work on the consumer loop is how a full queue becomes reachable, and this test catches it.
- **The archive channel is not changed:** it still drops its oldest record when full. A drop there loses history, not state: the Registry, the window and the sounds already have the event. It writes a decision row with reason `archive`.
- **`EventConsumer`** is the one hosted service that reads the channel. It is also the only thread that changes the Registry and the sound engine. It runs the tick in the same loop. A second loop or a second timer would be a second writer.
- **The tick** runs each 15 seconds, in this sequence: the silence sweep, then `Evaluate`, then the roster groups. Then it tells the UI the time.
- **After each batch of events** the consumer looks at the roster groups again.
- **A roster group that is due to settle** wakes the loop at its deadline, before the next tick. One wake for one settle, not a fast timer.
- **A `SoundCommand`** goes to the sound engine, and the consumer then tells the UI at once, so that the label changes at the click.
- **A `RostersChanged`** only wakes the loop, so that the roster groups are read again.
- **The UI hop.** `SessionProjection` takes the immutable `Session` from the event arguments and posts it to the dispatcher. On the UI thread it replaces that one session in an `ObservableCollection<Session>`. No UI code reads the Registry.
- **A click** never changes the domain directly. An Ack, a mute and a pause are events in the channel. The row changes when the Registry's answer comes back.

**The archive and the decision record.** After the consumer applied an event and the sound engine decided, the consumer gives the event and its decisions to the archive as one record. The archive's own thread writes them in one transaction (§8.3). The consumer never waits for the disk. The store may be closed while it writes, by a host disposed without a stop: the close waits for the current write to commit, and a write after the close is dropped without a sound (no log line, no change to the history notice), because the store is going away.

To answer "why did that sound play?":

```sql
SELECT d.ts, d.session_id, d.kind, d.from_state, d.to_state, d.reason, d.detail, e.event_type
FROM decisions d LEFT JOIN events e ON e.id = d.event_id
WHERE d.session_id IN (SELECT session_id FROM decisions
                       WHERE kind IN ('NoticePlayed', 'NudgePlayed') AND ts BETWEEN $from AND $to)
  AND d.kind IN ('NoticePlayed', 'NudgePlayed', 'StateMoved', 'SilenceSwept')
  AND d.ts BETWEEN $since AND $to
ORDER BY d.session_id, d.id;
```

The rows of one event share `ts` and `event_id`. The sound row comes before the row of the move that caused it, because the engine decides inside the Registry's change notification.

**`$from`, `$to` and `$since` are UTC text** in the one form of §8.3, for example `2026-10-02T12:03:11.1230000Z` (T1.62). Text order is then time order, also across a clock change. To read a time as local, select `datetime(d.ts, 'localtime')`.

---

## Part 5 — WPF host and tray

### 5.1 Application lifecycle

- `ShutdownMode.OnExplicitShutdown`: the application does not stop when the window closes.
- The close button hides the window ("Close to the tray"). The window is shown and hidden, never made again, so it keeps its state.
- The process stops only through **Quit** in the tray menu.

### 5.2 The tray icon is the status light

The colour is the worst state of all sessions (`StatusSummary.Of`, then `TrayVisuals.ColourOf`). The library is **H.NotifyIcon.Wpf**.

| Colour | Worst state |
|---|---|
| **Red** | `NeedsPermission` |
| **Amber** | `Error` or `NeedsQuestion` |
| **Green** | `Unread` |
| **Blue** | `Working` or `Waiting` |
| **Grey** | `Interrupted`, `Acked`, `Ended`, or no session |

- **The tray palette is not the row palette, on purpose.** On a row, a question is red and blinks: the row says what that one session is. In the roll-up, a question is amber, because the severity order puts it below an error. The mapping is written as thresholds on `AttentionOrder.Rank`, with named states and not numbers, so that it cannot disagree with the rank.
- **The counts are in the tooltip.** A 16-pixel icon cannot show digits. The tooltip gives each Needs You kind: `2 permissions · 1 error · 1 question · 2 unread · 3 working`. A zero count is left out. With all counts zero it reads `all quiet`.
- **No animation.**
- **Left click:** show or hide the window.
- **Right click:** `Open` · `Mute all` (or `Unmute all`) · `Mute all for 30 min` · `Pause monitoring` (or `Resume monitoring`) · `Settings…` · `Quit`.

**Mute all and Pause monitoring**

| | Sound | Glyph | Ends |
|---|---|---|---|
| **Mute all** | None | **The true colour** | After 30 minutes for the timed item, or when the operator unmutes |
| **Pause monitoring** | None | **Grey, and visibly "off"** | Only when the operator resumes |

- Mute all is the volume control. Pause is "off duty". Pause is the one deliberate exception to "the tray tells the truth".
- The glyph for pause is different from the grey of "all quiet".
- **The tooltip leads with what the operator cannot see.** First the faults, joined by ` · `: the tray text of each notice in the notice row's order (the port, not connected to Claude Code, then `messages cannot arrive`, then `messages refused`, then `history not recorded`, then `no sound device`, then `settings not read · using defaults`, then `fell behind`, then `events lost`; §5.6.1). The port fault is a notice on the board like the others, so it shows once, first (T1.57). Then `paused · click to resume`, then `muted 24 min`, then the counts. The minutes of a mute are rounded up.
- **The last item, always, says when the dashboard last heard from Claude Code** (T1.61, the operator's ruling of 2026-10-03): `last heard from Claude Code just now` under a minute, then `… 2 min ago`, `… 3 h ago` or `… 2 d ago`; before the first message, `not heard from Claude Code since start`. **It is information, never an alarm** (Design §3): no colour, no sound and no notice at any gap. It is read on the 15-second tick; nothing polls.
- **Windows shows at most 127 characters** (`TrayTooltip.MaxLength`). A longer text leaves out "last heard" first, whole, then whole items from the end, so no word is cut. Until T1.61 nothing measured the text: it went whole to the icon, and Windows cut it wherever the limit fell.
- A mute ends by a test of the time, not by a timer. Thus the tooltip is computed again on each tick.
- **Pause does not survive a restart.**
- Mute and pause do not stop the events. The Registry stays correct, and the window shows the truth.

### 5.3 Single instance

- A named `Mutex` (`SingleInstanceGate`) is taken at start. It is local to the logon session, and its name holds a hash of the data folder path. If it is held, this process is the second instance.
- The second instance reads the port from `port.txt` and the token from `listening.txt`, sends `POST /show`, and exits.
- The port corroborates only. After a hard stop, any program can hold the port. `GET /health` says if the holder is a copy of this dashboard, a different user's dashboard, or a stranger. **Only a copy of this dashboard changes the start**: it is signalled. Any other holder of the port in `port.txt` is a candidate that the port choice skips (§3.1), and the start goes on. Until the T1.57 review such a holder left the dashboard without a port (`StartWithoutIngress`, from T1.15's single fixed port) although its choice had found a free one; that action is removed.
- **A dashboard with no port still starts.** The tray tooltip and the window's notice row give the cause and what to do (§3.1).

### 5.4 DPI and window placement

- **Per-Monitor v2** is declared in `app.manifest`.
- The window **pins itself to all virtual desktops** through `IVirtualDesktopService.PinToAllDesktops` (§6.3). If the pin fails, the window is on one desktop and the log says so.
- The window opens where it was. If that place is on no monitor, it opens on the monitor that has the focus.
- **Always on top:** the key `window.alwaysOnTop` in `settings.json`, default off. There is no control for it in the interface. *Not built.*

### 5.5 MVVM

**CommunityToolkit.Mvvm**. The view models read `SessionProjection`'s collection on the UI thread. Grouped and Flat are two views of the same collection. The view mode is not saved: each start opens Grouped.

### 5.6 What the window shows

This section is the reference for the display rules. They are in the view models under `src/ClaudeDashboard.App/Ui`. Each asks Core a question and decides something about the screen. [Core and App](claude-dashboard-core-and-app.md) §4.3 says why that matters for a second interface.

#### 5.6.1 The parts of the window

Top to bottom:

1. **The caption:** the icon, "Claude Dashboard", the **counts strip**, a help slot that does nothing yet, and the buttons Minimize, Maximize and "Close to the tray".
2. **The counts row:** shown only when the caption is too narrow for the counts.
3. **The toolbar:** `Grouped | Flat` · `Select` · `Mute all` · `Ack all`.
4. **The notice row:** a short list, one line for each notice that is shown, in a fixed order: the port (§3.1), then the connection to Claude Code (§9.4), then the self-test notice, `Messages from Claude Code cannot reach the dashboard: a test message did not arrive.` with its cause (§9.4), then `Messages from Claude Code are being refused: their token does not match this dashboard's.` (§9.4), then `History is not being recorded: the database could not be written. The dashboard tries again each minute.` (§8.3), then `No sound device. Notices and nudges are silent until Windows has an output device.` (Part 7), then the settings notice (§8.2), then the two queue notices (Part 4): "The dashboard fell behind and skipped repeated tool events. Rows may lag until each session's next event." and "The dashboard fell far behind and lost events. A row may be wrong until its session's next event; restart the dashboard to be sure." Hidden when none is shown. Two can be true at one time, so it is a list (T1.54, issue #71). Each notice has its own window text, its own tray text and its own rule for when it clears. `NoticeBoard` orders them; a new notice is one more `INotice` source, and the board does not change. The tray colour does not change for a notice.
5. **The body:** the rows.

**The counts strip** reads `11 sessions · 3 need you · 5 unread · 8 working`. The total always shows. A band with zero is left out. Quiet and Ended have no count. The counts are of sessions, not of rows, so a collapsed group still counts. When the space is short, the strip drops words before numbers; its tooltip always has the full sentence.

**The toolbar:**

| Control | Behaviour |
|---|---|
| `Grouped` / `Flat` | A toggle with two segments. One is always raised |
| `Select` | Starts selection mode (§5.6.8). In the mode it becomes `Selecting · 2 chosen`, `Group these` and `Cancel` |
| `Mute all` / `Unmute all` | The same command as the tray menu |
| `Ack all` | Sends one Ack for each session that `Acknowledgment.Applies` to. Lit when one or more sessions wait |

A control that is the main action of the current state is *lit*: `Ack all` when something waits, `Group these` when two or more rows are chosen. `Select`, `Cancel` and `Mute all` are never lit.

#### 5.6.2 Which rows exist

**Grouped view.** For each group, in the order of `AttentionEngine.OrderGroups`:

1. The group heading.
2. If the operator opened the group: each member, quiet ones included.
3. Or, if the group is **stale**: nothing more. The heading is the one line.
4. Or: each member that is not quiet, then one line `+ 3 quiet` if any member is quiet.

- **Quiet** means the Quiet band or the Ended band (`AttentionOrder.BandOf`). Thus an `Unread` row is never hidden.
- **Stale** means that all members are quiet and the group's last change is 15 minutes old or more (`MainViewModel.DefaultStaleAfter`).
- A click on the heading, or on the `+ 3 quiet` line, opens and closes the group.

**Flat view.** For each band that has sessions, in the order of `AttentionEngine.Order`:

1. The band heading: `NEEDS YOU`, `UNREAD`, `WORKING`, `QUIET` or `ENDED`.
2. For Quiet and Ended, if not opened: one line `4 quiet sessions`.
3. Or: each session of the band.

**The roster prompt** (§5.6.8), when there is one, is the first row in each view.

Rows are used again and not made again. A refresh changes only what moved, so the selection and the scroll position stay.

#### 5.6.3 A session row

| Part | Content | Source |
|---|---|---|
| LED | The colour of the state (§5.6.6). In selection mode, a check mark takes its place on a chosen row | `RowVisuals.AccentOf` |
| Title | The session's title, folded to one line and cut to 40 grapheme clusters (160 characters at most), then ` — `. Nothing if there is no title. The tooltip has the full title, only if it was cut | `SessionViewModel.TitleText` |
| Prompt | The first 140 characters of the prompt, then `…`. Monospace | `PromptSnippet` |
| Badge | The word for the state (§5.6.6) | `RowVisuals.BadgeOf` |
| Detail | The kind of error, in `Error` | `Session.ErrorKind` |
| Age | The row's clock, in words (§5.6.5) | `RowVisuals.Age` |
| Waiting summary | In `Waiting`: ` · ` and the description of the first task | `WaitingSummary` |
| Group tag | In the flat view: the folder name | `RowVisuals.WorkspaceLabel` |
| `✓ Ack` | Shown where §5.6.7 says | `ShowsOwnAck` |

A click opens the row. In selection mode a click selects it.

#### 5.6.4 An open row

| Part | Content |
|---|---|
| `YOU ASKED · 14:32 · 23 min ago` | The time and the age of the ask, `Latest.StartedAt`, in each state |
| The prompt | In full |
| `CLAUDE ANSWERED` and the answer | Shown when there is an answer. While the session is `Waiting`, the label is `CLAUDE SAID SO FAR` |
| `WAITING ON` | In `Waiting`: one line for each task: the description, `background command` or `subagent`, and the age from when it was first listed |
| `✓ Acknowledge` | The same command as the row's Ack, shown where §5.6.7 says |
| `Open terminal` | Hidden. The markup stays in the template for Phase 2. *Not built* (issues #60, #63) |
| The session id | The first 8 characters. A click copies the **full** id. `copy failed` shows if the clipboard refused |

#### 5.6.5 The row's clock

The age on a row counts from one of two instants (`SessionViewModel.AnchorOf`):

| State | Counts from | The words |
|---|---|---|
| `Working`, `Waiting` | The ask: `Latest.StartedAt`. A stop for a permission, and a notice of complete background work, do not restart it | `6 min` |
| `NeedsPermission`, `NeedsQuestion` | When it became blocked | `waiting 4 min` |
| `Error` | When the turn stopped | `4 min` |
| `Unread` | The finish: `AnsweredAt` | `2 min ago` |
| `Interrupted` | The last event that arrived, not the sweep that came 10 minutes later | `20 min ago` |
| `Acked`, `Ended` | The instant that mattered in the state before: the finish, the block, or the silence. From `Working` or `Waiting`, the Ack or the close itself | `2 min ago` |

All states but the first row read `Session.ClockAnchor`, which the Registry sets (§2.6). If it is null, the row reads `EnteredAt`.

**Only the display reads the anchor.** The sort order, the nudge schedule and the settle window read `EnteredAt`. `Architecture/DisplayOnlyAnchorTests.cs` holds that line.

**A duration** (`RowVisuals.Duration`): below one minute `48s`; below 90 minutes `9 min`; from 90 minutes `2h 05m`.

**The words say whose time it is.** "Waiting" means that the agent is stopped and the time is the operator's. "Ago" means that the work is done and the time measures how long it is unseen. A bare duration means that the agent is busy.

The ages change on the consumer's tick, each 15 seconds. No view model starts a timer.

#### 5.6.6 Words, colour and motion

| State | Badge | Row colour | Motion |
|---|---|---|---|
| `NeedsPermission` | `PERMISSION` | Red | Blinks |
| `NeedsQuestion` | `QUESTION` | Red | Blinks |
| `Error` | `ERROR` | Amber | None |
| `Unread` | `FINISHED` | Green | None |
| `Working` | `WORKING` | Blue | Breathes |
| `Waiting` | `WAITING` | Blue | None |
| `Acked` | `QUIET` | Grey | None |
| `Interrupted` | `INTERRUPTED` | Grey | None |
| `Ended` | `ENDED` | Grey | None |

- **Red blinks, working breathes, nothing else moves** (Design §9). An error is amber and still: "a turn stopped" must read differently from "it asks you".
- **No motion at all** when Windows has animations off (`SystemParameters.ClientAreaAnimation`, the setting that a browser shows as `prefers-reduced-motion`). `MotionPolicy` follows a change of the setting with no restart.
- The colours: red `#FF6B5E`, amber `#FFB454`, green `#55C96A`, blue `#5AA9FF`, grey `#6B7480`. The blink goes to 15% opacity and back in 1.1 s. The breath goes to 45% and back in 2.6 s. `Ui/RowTemplates.xaml` is the authority.

#### 5.6.7 Where an Ack appears

| Place | Shown when | Sends |
|---|---|---|
| A session row | `Acknowledgment.Applies(state)`, and the row is **not** a member of a roster group in the grouped view | One Ack |
| A roster group's heading | One or more members can be acknowledged. Read from the members, not from the settled state, so the button is there inside the 1.5-second settle window | One Ack for each such member |
| A directory group's heading | Never | — |
| `Ack all` | One or more sessions can be acknowledged | One Ack for each such session |

- A roster group is one piece of work, so it is acknowledged one time, at its heading. A member keeps its badge and its LED.
- In the flat view there are no roster groups, so each row has its own Ack.
- **An Ack sends an event and changes nothing itself.** Each command checks the state again at the click, and sends only for a session that can be acknowledged: an Ack that the Registry will decline still takes a place in the channel.

#### 5.6.8 Headings, selection and rosters

**A group heading** shows:

- **The label:** the roster's name for a roster group; the folder name for a directory group; the session id for a session with no directory. Never the key.
- The full directory path, for a directory group.
- One dot for each member, in the member's colour.
- If the group is stale: `· 2 sessions · idle 38 min`.
- The heading's colour is the group's state, with the settle window applied.

**Selection mode** is how the operator makes a roster.

1. `Select` starts the mode. The toolbar says so.
2. A click on a row selects it. **A session with no title cannot be selected:** a roster stores names, and that session has none. Such a row is dim and says `no name to remember`.
3. `Group these` is active at 2 or more chosen rows. It makes a roster named `Group` (or `Group 2`, …) and ends the mode. The group exists at once.
4. **The roster prompt** appears as the first row: `Remember this group as a roster?`, a name field, `Remember` and `Just this once`. `Remember` writes the rosters to `settings.json`. No answer is the same as `Just this once`: the group stays until the dashboard restarts.
5. A right click on a member of a roster group gives `Remove from group`. It removes the **name** from the roster, so it moves each live session with that name.

The mode ends when the operator groups, cancels, or hides the window.

#### 5.6.9 What makes the window change

| Cause | Path |
|---|---|
| A session changed | `SessionChanged` → `SessionProjection` → `MainViewModel.Refresh` |
| Time passed | The consumer's tick → `UiTick` → `MainViewModel.Tick` and `TrayViewModel.Tick` |
| A roster group settled | The consumer wakes at the deadline and sends a tick |
| Mute or pause changed | The consumer sends a tick after the `SoundCommand` |
| The operator opened or closed a heading, changed the view, or edited a roster | The view model refreshes itself |
| A notice changed | A source raises a property change, and `NoticeBoard` rebuilds the list. `HookNotice` changes at a start or an event. `HistoryNotice` looks at the store, and `SoundDeviceNotice` at the player, on `TrayViewModel.Tick`, which passes the tick to the board |

`EventConsumer` is the only caller of `UiTick`. A test holds that, because the view models do not check that time goes forward.

---

## Part 6 — Windows integration adapters (Phases 2 to 4)

All are behind Core ports (§1.3). **Only the pin of §6.3 is built.**

### 6.1 Content-matching locator — `ITerminalLocator` *(Phase 2/3, not built)*

The C# form of TS §III.2, with **FlaUI (UIA3)**:

- List the `WindowsTerminal.exe` top-level windows. Go through each window's UIA tree to its tabs and its content control.
- Read a pane's visible text with the UIA **Text pattern**. Compare it with the Registry's `Exchange` text: the latest prompt first, then the answer and the directory.
- `FindTab(session)` gives the tab's `TabRef`. `IdentifyForegroundTab()` reads the selected tab of the foreground terminal window.
- The same recent text in two tabs gives "no match". The caller falls back to the window.
- **A UIA failure gives window-level behaviour and never throws.**

### 6.2 Focus observer — `IFocusSource` *(Phase 3, not built)*

The C# form of TS §III.5:

- `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, …, WINEVENT_OUTOFCONTEXT)` through P/Invoke. The thread that registers it runs a message loop.
- Window focus first. Tab focus later, through UIA selection events.
- When the foreground stays on a terminal window: `IdentifyForegroundTab()`, and after a short time, an `Ack` with the source `InferredFocus` into the channel.

### 6.3 Virtual desktop — `IVirtualDesktopService`

The C# form of TS §III.9. The **MScholtes VirtualDesktop** wrapper, as source in the repository, behind this adapter.

- **Documented tier:** `IVirtualDesktopManager` only: `GetWindowDesktopId`, `IsWindowOnCurrentVirtualDesktop`, `MoveWindowToDesktop`. *Used in Phase 4, not built.*
- **Undocumented tier:** list, switch, name, **and pin**. The pin is on `IVirtualDesktopPinnedApps`, which Microsoft does not document. The interface identifiers change between Windows builds, so this adapter is the one that needs maintenance. **The pin is built** and is used for the dashboard's own window. A failure returns `false`.

### 6.4 Navigation — `ITerminalNavigator` *(Phase 2, not built)*

The C# form of TS §III.8:

1. **Ask Windows Terminal:** `wt.exe -w <window> focus-tab -t <index>`.
2. **Direct activation:** `SetForegroundWindow`, then select the tab through UIA.

### 6.5 Integrity

The process runs at the user's **normal level and never elevated**. A test reads the manifest and fails if it asks for more than `asInvoker`.

---

## Part 7 — Sound (NAudio)

`ISoundPlayer` over **NAudio**.

- **One file for each sound:** `finished.wav`, `permission.wav`, `question.wav`, `error.wav`. A notice and a nudge are the same file at different gains. A nudge also gets a short fade-in.
- **WAV only.** It needs no codec, so a sound that plays on one machine plays on each.
- **The files ship beside the executable**, in `sounds\`. A file of the same name in the data folder's `sounds\` folder replaces the shipped one (§8.1).
- **One output device, one mixer.** Fifteen sessions that finish together are one stream, not fifteen.
- **The device follows Windows.** When the default output changes or a stream stops, the adapter opens the new device. A device that fails three times in a short time is left alone.
- **The samples are decoded one time and kept.**
- **The adapter never throws.** A file that is absent, a device that will not open and a file that will not decode each give silence and a log line.
- **The adapter decides nothing.** Mute, pause and master volume are in `SoundPolicyEngine`, which gives the adapter a final gain. The adapter knows no session and no group.
- **The adapter says what it did** (T1.55, issue #72). `Play` returns a `SoundOutcome`: `Queued` when the sound reached the mixer, `NoOutput` when there was no working device, `Failed` for a missing or broken file or an unexpected exception. The outcome comes from the same paths that count `QueuedCount` and `DegradedCount`. Queued is not heard: see the limit below.
- **The record tells the truth** (the operator's ruling of 2026-10-03). A queued sound records `NoticePlayed`, `NudgePlayed` or `GroupNoticePlayed`, as before. A dropped one records `SoundDropped`, with the reason and the same identifiers (§8.3).
- **A dropped sound changes no rule.** It still counts as announced, and the nudge ladder advances as it would have, so the schedule is the same with a device and without. **Nothing is replayed when a device returns:** a stack of old sounds at that moment is noise, each about a state the operator may already have seen.
- **No sound device is a notice** (§5.6.1). Window: `No sound device. Notices and nudges are silent until Windows has an output device.` Tray: `no sound device`. No mark on the tray icon, and the colour does not change. `SoundDeviceNotice` reads `ISoundOutput.HasOutput`, an App interface that the player implements, on the tray's 15-second tick. It clears at the tick after a device returns. The read takes the player's gate, which is never held while a device opens.
- **The notice does not flash at start.** The player binds its first device in its constructor, on the constructing thread, before its worker starts. The constructor runs while the host is built, before the window and the tray exist. So the first tick reads the real answer.
- **The limit that stays:** a device that is listed, active and silent (the volume at zero, a monitor with no speakers) counts as an output. Nothing the process can ask tells it apart from a device that works.

---

## Part 8 — Storage and configuration

### 8.1 The data folder

Location: **`%LOCALAPPDATA%\ClaudeDashboard\`**. The variable `CLAUDE_DASHBOARD_HOME` moves it (§8.5). The folder's permissions come from the user's profile: the user, Administrators and SYSTEM.

| File or folder | Written by | When | Content |
|---|---|---|---|
| `settings.json` | The dashboard, and the operator by hand | At quit (the window's place); when the operator remembers a roster; at the Settings window; at `--install-hooks` and `--remove-hooks` | §8.2 |
| `settings.error-<yyyyMMdd-HHmmss>.json` | The dashboard, by a rename | At a start that finds `settings.json` does not parse (§8.2) | The operator's file, byte for byte. The dashboard never writes or deletes it |
| `dashboard.db` | The archive writer | For each event; at each start and clean stop (`runs`) | §8.3. **It holds prompts and answers** |
| `logs\dashboard-<date>.log` | Serilog | Always | §8.4 |
| `port.txt` | The dashboard | After a bind. Never deleted | The port that this user last bound. An *input* to the next start and to a second instance |
| `listening.txt` | The dashboard | After a bind and after the script is written. Deleted at exit | Line 1: the port. Line 2: the token. Its presence means "a dashboard listens now" |
| `post-status.cmd` | The dashboard | At each start, if it is different from the text in the build | The hook script (§9.2). An edit by hand is undone at the next start |
| `plugin\` | The dashboard | At each start, if different | The Claude Code plugin: `.claude-plugin\marketplace.json`, `.claude-plugin\plugin.json`, `hooks\hooks.json` (§9.4) |
| `sounds\` | The operator | — | Optional. A `.wav` here with the name of a shipped sound replaces it. The dashboard does not create the folder |

`port.txt` and `listening.txt` hold the same number and are two different facts. One file cannot carry both.

The install is in a different folder: `%LocalAppData%\dsopko.ClaudeDashboard\` (§10.2). An uninstall removes that folder and leaves the data folder.

### 8.2 `settings.json`

The dashboard's own settings. A person can edit it: comments and a comma at the end of a list are accepted. **A file that cannot be read never stops the start, and is never overwritten** (T1.56, the operator's ruling of 2026-10-03 on issue #73). Until T1.56 the dashboard ran on the defaults and "left the file as it is", which held only until the next save wrote the defaults over it (issue #26).

- **A file that does not parse** (bad JSON, a wrong type, a bare `null`) is renamed to `settings.error-<yyyyMMdd-HHmmss>.json` in the same folder, local time, with `-2`, `-3` when the name is taken. It is a move: the bytes do not change. A fresh `settings.json` with the defaults takes its place, so later saves go to it. The fresh file is written first under a temporary name, so a failure at any step leaves the bad file where it was (`SettingsStore.PrepareForStart`).
- **Only the first instance that will show the window does it**, after the single-instance decision and before anything else reads the file: `Program` hands this start's first load to `AppHost.Build`, which reads the file no more. A second instance that stands down and the one-shot switches (`--install-hooks`, `--remove-hooks`, `--replay`) leave the file byte for byte.
- **This start registers no plugin, and leaves start with Windows as it found it** (§9.4, §10.1). The first load is the authority for the whole start (`SettingsAtStart.Original`), though the fresh file says `installHooksAtStart: true` and `startWithWindows: true`. The `Run` value and Windows' `StartupApproved` mark are not touched, for the reason no plugin is registered: the operator's choice was in the file this start could not read (the director's ruling of 2026-10-03, extending the operator's ruling on #73). The same holds when the file cannot be opened or kept aside. The next start reads the fresh file, and turns both on unless the operator copied `"installHooksAtStart": false` and `"startWithWindows": false` back; the notice says so.
- **A file that cannot be opened at all** (no permission, or another program holds it), **or a keep-aside that fails**, is left alone. The dashboard runs on the defaults, and `SettingsStore.Save` refuses every save for the rest of the run, with one Warning line for each refused save. Every save site goes through it: the window's place at quit, the rosters, the Settings window and the `installHooksAtStart` record. The Settings window then says "This choice is not remembered: the settings file could not be opened." A file that opened but could not be kept aside has its own notice text, which says "could not be read or kept aside", not "could not be opened".
- **One Error line** names the backup's full path, or why the file was left alone, with the parse problem. No setting value is logged. The window notice and the tray (`settings not read · using defaults`) show it until the next start; neither shows the parse problem.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `port` | number | None | Pins the ingress port (§3.1). A value that is not from 1 to 65535 is "not set", and the log says so |
| `installHooksAtStart` | boolean | `true` | If a start registers the Claude Code plugin when it is absent. `--remove-hooks` sets it to `false`; `--install-hooks` sets it to `true` (§9.4) |
| `startWithWindows` | boolean | `true` | If an installed copy starts when the operator signs in (§10.1) |
| `logging.minimumLevel` | text | `Information` | The lowest level that is logged, in the log file too. `Debug` adds the decision record (§8.4) |
| `logging.retainedFileCount` | number | `14` | How many log files are kept |
| `logging.fileSizeLimitBytes` | number | `16777216` | The size at which a log file rolls in one day |
| `sound.masterVolume` | number, 0 to 1 | `1.0` | A multiplier on each sound |
| `sound.noticeGain` | number, 0 to 1 | `1.0` | The gain of a notice |
| `sound.nudgeGain` | number, 0 to 1 | `0.6` | The gain of a nudge. A value above `noticeGain` is brought down to it |
| `sound.nudgeOnError` | boolean | `true` | If a session in Error is nudged |
| `window.left`, `window.top`, `window.width`, `window.height` | number | None | Where the window was |
| `window.alwaysOnTop` | boolean | `false` | If the window stays above other windows |
| `rosters` | object | `{}` | Each key is a roster's name. Each value is the list of session names in it |

- A `sound` value that is absent or out of range takes Core's default. The file never holds a second copy of a default.
- The `rosters` section is made valid when it is read (§2.5). Each correction is logged with the roster's name and never a member.
- **Not built:** keys for the nudge intervals, the Unread nudge, the stale time, the choice of sounds, mutes and the default view. Those values are fixed in the code.
- **Known defects:** a save truncates the file before it writes (issue #7). A save after a failed read no longer replaces a malformed file with the defaults: the file is kept aside first (T1.56; issue #26 described the loss).

### 8.3 `dashboard.db`

SQLite, through `Microsoft.Data.Sqlite`. One writer thread. Append-only, but for one update: a clean stop sets `stopped_at` on its own `runs` row. **Never pruned** (retention is *not built*). A typical day adds about 300 KB.

If the file cannot be opened or written, the store writes one Warning when it fails (not for a failed retry), and the window and the tray say `history not recorded` (§5.6.1). **It tries again each minute** (the operator's ruling in issue #71; before T1.54 it stopped until the next start):

- The next attempt is the first write at least 60 seconds after the failure (`SqliteEventStore.RetryAfter`, from the injected `IClock`). A failed retry starts the minute again.
- The records that arrive inside the minute are lost, not queued: a queue would hold the operator's words in memory for as long as the disk stays full. `LostCount` counts them, with each record whose write failed.
- No timer and no thread retry. The attempt rides on the next record, on the writer thread, so it costs one normal write at most and never touches the consumer thread.
- The first Warning of the process carries the exception and its stack. A later failure writes the exception's type and message and no stack, so a disk that flaps (a backup program that locks the file) costs one short line a minute (T1.55).
- A failed retry writes no log line. The first write that succeeds writes one Information line with the count of records lost. A record is one event with its decisions, or the decisions of one tick. The open announcement is not written again.
- The notice reads `SqliteEventStore.Available` (false while the last write failed) on the tray's 15-second tick. The writer thread publishes it with `Volatile`. So the notice shows within one tick of the failure, and clears within one tick of the write that succeeds.

**Every time in the file is UTC, in one form** (T1.62, issue #80): `UtcDateTime.ToString("o", CultureInfo.InvariantCulture)`, seven fractional digits and `Z`. One helper (`SqliteEventStore.Utc`) writes `ts`, `started_at` and `stopped_at`. Text order is time order only when every row has the same form, so a range that crosses a clock change compares correctly as text. The window and the log still show local time.

- **Existing rows are converted once,** in the schema step, on the archive writer's thread, when `PRAGMA user_version` is 0: every `ts` in `events` and `decisions` is read, parsed and written back in the one form, and `user_version` becomes 1, in one transaction. The start never waits; records that arrive meanwhile wait in the archive channel. It is the first upgrade step the file has had.
- **A time that will not parse is left as it is,** and counted. One bad row does not stop the rest.
- **A conversion that fails** (a full disk, a locked file) rolls back: `user_version` stays 0, and T1.54's rule applies, the history notice and another attempt a minute later.
- **One Information line** says how many rows were converted, how many were already in UTC, how many were left, and the time it took. No time from a row, and no payload. A new file has no rows and writes no line.
- Rows, ids and payloads never change: only the text of the time. No `VACUUM` (the operator's ruling on #81).
- Measured on a copy of the operator's database (2026-10-04): 32,169 rows converted, 0 left as they were, in 865 ms; the file went from 97,669,120 to 101,163,008 bytes.

**The table `events`:** one row for each event that reached the consumer.

| Column | Type | Content |
|---|---|---|
| `id` | INTEGER PRIMARY KEY | The row id |
| `session_id` | TEXT | The session |
| `ts` | TEXT | The arrival time, in UTC: ISO 8601 with seven fractional digits and `Z`, for example `2026-10-02T12:03:11.1230000Z` (T1.62) |
| `event_type` | TEXT | The hook name, or `Ack` |
| `payload_json` | TEXT | **The hook body exactly as it arrived.** Empty for an `Ack` |
| `cwd` | TEXT | The directory |

An event that the Registry declined is in the table too. A `SoundCommand` and a `RostersChanged` have no row here.

**The table `decisions`:** one row for each judgement.

| Column | Type | Content |
|---|---|---|
| `id` | INTEGER PRIMARY KEY | The row id |
| `event_id` | INTEGER or NULL | The `events` row that caused it. NULL for a tick, and for a decision from a different thread |
| `ts` | TEXT | When it was decided, in UTC, in the same form |
| `session_id` | TEXT or NULL | The session. NULL for a decision that is about no one session |
| `kind` | TEXT | The name of a `DecisionKind` |
| `from_state`, `to_state` | TEXT or NULL | For a kind that moves something |
| `reason` | TEXT or NULL | The name of an enum value, or an identifier |
| `detail` | TEXT or NULL | Pairs of `key=value` identifiers |

**`reason` and `detail` hold identifiers only.** Never a title, a prompt, an answer or the text of an exception.

**The kinds:**

| `kind` | When | `reason` · `detail` |
|---|---|---|
| `SessionAdded` | The first event of a session | — |
| `SessionRefreshed` | A `SessionStart` for a known session | `startup`, `resume`, `fork`, `clear`, `compact` or `other` |
| `StateMoved` | A change of state | `AutoAcknowledgment`, `MachinePrompt`, `ScheduledPrompt` or `QuietTick`, where one applies |
| `SessionEnded` | A change to `Ended` | — |
| `EventDeclined` | The Registry declined an event | `Ignored`, `Stale`, `Duplicate` or `Uncorrelated` |
| `GroupRederived` | The directory group changed | `from=… to=…` (the keys) |
| `SilenceSwept` | The silence sweep moved a session | `silence` · `silentMinutes=…` |
| `AckApplied` | An Ack was applied | `Manual` or `InferredFocus` |
| `AckDeclined` | An Ack was declined | The outcome |
| `TaskTypeUnrecognised` | A `Stop` listed a kind of background work that the dashboard does not know | `UnrecognisedType` · `count=…` |
| `NoticePlayed` | A notice that the player queued | The sound |
| `NudgePlayed` | A nudge that the player queued | The sound · `rung=… waitedMinutes=…` |
| `NoticeSuppressed` | A sound that was due did not play | `MonitoringPaused`, `AllMuted`, `SessionMuted`, `GroupMuted`, `GroupDone` or `AlreadyAnnounced` · `kind=… sound=…` |
| `GroupNoticePlayed` | A roster group's sound | `notice` or `nudge` · `group=… members=…` (session ids) |
| `SoundDropped` | A sound that the player dropped, in place of the played row (T1.55) | `NoOutput` or `Failed` · `kind=… sound=…`, then `rung=… waitedMinutes=…` for a nudge, or `group=… members=…` for a group sound |
| `MuteApplied` | A `SoundCommand` | `MuteAll`, `UnmuteAll`, `PauseMonitoring` or `ResumeMonitoring` · `until=…` |
| `MuteExpired` | A timed mute ended, seen on the tick | `until=…` |
| `EventDropped` | The event channel shed noise at its capacity, or a full channel dropped its oldest (Part 4) | `noise` · `kind=… type=…` (the shed event's kind), or `pipeline` (the event channel's hard limit) or `archive` |
| `ApplyFailed` | `Apply` threw | The **type** of the exception |
| `HookRefused` | A `/hook` post was refused: its token did not match (T1.61). At most one row a second, with the count | — · `refused=…` (the refusals since the last row). No event, no session: nothing from the post |
| `TrayLightChanged` | The tray colour changed | The worst state; the colours are in `from_state` and `to_state` |
| `WindowSurfaced` | A `/show` | — |
| `RosterEdited` | The operator edited a roster | — |

**The table `runs`:** one row for each start of the dashboard (T1.60, issue #78). **A table of its own, not two decision kinds** (the operator's ruling of 2026-10-03): a run has two times, and the stop may never come.

| Column | Type | Content |
|---|---|---|
| `id` | INTEGER PRIMARY KEY | The row id |
| `started_at` | TEXT | When the host had started (ingress bound or failed). UTC, ISO 8601, ending in `Z` |
| `stopped_at` | TEXT or NULL | The clean stop, in UTC. NULL after a kill, a crash or a host disposed without a stop: an empty stop is the record of the crash |
| `version` | TEXT | The informational version, as the log's first line has it (`StartupVersion`) |
| `port` | INTEGER or NULL | The port that ingress bound. NULL when it could not bind |
| `data_root` | TEXT | The data folder |

- **The start row** is written once per process, by the archive writer's loop, never on the consumer or the UI thread. The time is taken when the host has started, before `listening.txt` names the run, so no hook of the run is older than its start. A run so short that the loop never ran writes the row at the stop, before the drain. A second instance that stands down writes no row, and a start whose bind throws ends before it.
- **The stop** is set after the archive's drain and before the store closes, so every record that the run queued is written first.
- A row that the disk refuses is lost and counted like any record (`LostCount`), and not retried: a late row would say the wrong time.
- **Times are UTC from the first row,** in the one form above. `events` and `decisions` have the same form since T1.62.
- **No operator text.** The data folder is the one path.

**`--replay <path>`** builds the `decisions` table for a database that has events and no decisions. It runs the stored events through the real Registry and sound engine. It writes only `decisions` rows, and refuses a database whose `decisions` table is not empty. Run it on a copy. **It opens the file through the store,** so a file that was never converted has its times converted to UTC first (T1.62): the instants, the rows and the payloads are unchanged, and only the form of `ts` changes. Apart from that, replay never modifies `events`.

**Replay forgets every session at each run's start, as a live start does (T1.60).** When the next tick or event is at or after a `runs` row's `started_at`, replay first starts a new Registry and sound engine, empty. It does the same at a clean `stopped_at`, because a dashboard that is off sends no nudges; a run with no stop (a crash or a kill) keeps its sessions until the next start. It compares the times as instants, never as text. Since T1.62 both tables hold UTC in one form, so text would agree; a time that would not parse stays in its old form. History before the first `runs` row replays as one uninterrupted run, as before T1.60, because older restarts were never recorded. That is where most nudge rows came from: over the operator's database, all one such run, one session that never sent another event made 4,076 of 5,168 nudge rows, a question nudge every ten minutes for four weeks. The summary line says how many runs replay saw, and how many events came before the first. Replay never writes `runs`.

**To read the database:** copy `dashboard.db` and its `-wal` file, and query the copy.

### 8.4 The log

Serilog, to `logs\dashboard-<date>.log`. One file for each day, 14 files kept, 16 MB for each file. The file is flushed each 2 seconds. A second instance and the one-shot switches write to the same file.

- The first line of a start is the version.
- The log never holds a title, a prompt, an answer, a payload or a task description (§3.4).
- **The file keeps the level that `logging.minimumLevel` sets**, Information by default. At `Debug` the file also keeps the decision record, one line for each decision, and one line for each event the Registry declined. Every `Debug` line holds identifiers only. The file sink had a fixed Information floor of its own until T1.52 (issue #68), so `Debug` did not reach the file. The `decisions` table is still the record to query.

### 8.5 Environment variables

| Variable | Effect |
|---|---|
| `CLAUDE_DASHBOARD_HOME` | Moves the data folder. It must be a full path that can be created. A value that cannot be used gives the default folder and a warning. Two users must not point it at one folder |
| `CLAUDE_CONFIG_DIR` | Claude Code's own variable. The dashboard obeys it to find Claude Code's settings |
| `CLAUDE_DASHBOARD_TOKEN` | Retired. Ignored, with one Information line if it is set |

---

## Part 9 — Claude Code hook configuration

This is the wire between Claude Code and the dashboard. The handler is a **command hook**. It reaches Claude Code as a **plugin** (§9.4). The dashboard never writes `~/.claude/settings.json` (§9.3).

The [hooks reference](claude-code-hooks-reference.md) gives all of Claude Code's hook events, and where the documentation and the wire disagree.

### 9.1 Events consumed, and the fields read

The dashboard accepts eight event names: `HookEventNames.Accepted`. The plugin and ingress both read that one list, so the dashboard cannot register an event that ingress refuses. `HookEventMapper` is a switch on those eight names with a default that refuses. It cannot make an `Ack`, a `SoundCommand` or a `RostersChanged`: thus a post cannot forge an acknowledgment.

Fields read from each event: `hook_event_name`, `session_id`, `prompt_id`, `transcript_path`, `cwd`, `session_title`.

| Event | → State or action | More fields read |
|---|---|---|
| `SessionStart` | Makes or refreshes the session. An Ended session becomes `Acked` | `source` |
| `UserPromptSubmit` | → `Working`. Keeps `prompt`. An acknowledgment if the operator typed it (§2.6) | `prompt` |
| `Notification`, `permission_prompt` | → `NeedsPermission` | `notification_type` |
| `Notification`, `agent_needs_input` | → `NeedsQuestion` | `notification_type` |
| `Notification`, any other type | No change | `notification_type` |
| `Stop` | → `Unread`, or `Waiting`, or back to the state before a quiet tick. Keeps the answer | `last_assistant_message`; `background_tasks` (`id`, `type`, `status`, `description` of each); `session_crons` (`prompt` of each) |
| `StopFailure` | → `Error`. A second `StopFailure` of another kind changes the kind; the same kind again is a duplicate | `error`, then `error_type` |
| `SessionEnd` | → `Ended` | `reason` |
| `CwdChanged` | The group is found again | — |
| `PostToolBatch` | `NeedsPermission`, `NeedsQuestion`, `Error`, `Interrupted` → `Working`, or `Waiting` if the session waits. **Never from `Unread`** | — |

- For `SessionStart`, `Notification`, `StopFailure` and `SessionEnd`, the mapper reads the field `matcher` if the named field is absent.
- `StopFailure` gives its kind in `error`, not in the `error_type` that Claude Code's documentation names. All 18 archived events carry `error`, and none carries `error_type` (hooks reference, discrepancy 4). The mapper reads `error`, then `error_type`, then `matcher`. `HookPayload.Error` is held raw, as a `JsonElement`, and only a JSON string is read: an `error` of any other shape is passed over for the next name, and never costs the event. `error_message` is not bound.
- A hook payload has no time. The mapper stamps the arrival time from `IClock`.
- A `background_tasks` list or a `session_crons` list that is malformed reads as empty.
- A task's `command` is never read.
- `PermissionRequest` is not consumed. A `Notification` with `permission_prompt` follows it, and that is the path.

### 9.2 The hook handler

This is what the plugin's `hooks\hooks.json` holds.

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

One entry of that form for each of the eight events.

- **`command` with `args`**, so that no shell runs. On Windows the default shell differs from machine to machine. Both paths are absolute, because nothing expands a variable in this form.
- **`async: true`**, so that the hook never delays a turn.
- **No allow-list and no `headers`.** The script reads the token from `listening.txt`.

**What `post-status.cmd` does:**

1. If `listening.txt` is not in the script's own folder: stop. No connection is opened.
2. Read line 1 as the port and line 2 as the token.
3. Stop if the port is not a number from 1 to 65535.
4. Stop if the token is not exactly 43 characters from `A–Z a–z 0–9 - _`.
5. Send standard input, unchanged, with `%SystemRoot%\System32\curl.exe`: a `POST` to `http://127.0.0.1:<port>/hook`, with `X-Dashboard-Token`. One second to connect, two seconds in total.
6. Exit 0.

- **The script prints nothing.** One redirect covers the full script.
- **The script always exits 0.** It ignores the HTTP status.
- The first three lines never change between builds. `cmd` reads a batch file from the disk as it runs, so a script that is replaced while it runs must find the same exit at the same place.
- The measured cost for each event: 97 ms with a dashboard that listens, 65 ms with none. Two processes: `cmd.exe` and `curl.exe`.

### 9.3 The dashboard never writes Claude Code's settings

`~/.claude/settings.json` belongs to Claude Code, which writes it too. **The dashboard reads that file and never writes it** (the operator's ruling of 2026-10-01).

Why: a second writer writes the full file again. It can lose a change that Claude Code made in the same moment. It changes text that it did not mean to touch: comments, spacing, line ends. And its errors break Claude Code, not the dashboard.

What the dashboard reads there, at each start:

- **`enabledPlugins` and `extraKnownMarketplaces`**, to learn if its plugin is enabled, turned off, or held by a different data folder. "Ours" is decided by the folder that the settings give, never by the name alone.
- **`hooks`**, for a handler that a build before the plugin left there. It is recognised by the script path in `args`, and by nothing else. *Accepted limit: an 8.3 short path does not match.*

The read is defensive:

- Comments and a comma at the end of a list are accepted.
- A file that will not parse is "cannot be read". Nothing is then claimed about it, nothing is asked of Claude Code, and the operator sees a notice.
- A value of the wrong type is "not ours", never an exception.
- **Nothing from the file is logged or shown.**

`Architecture/ClaudeSettingsReadOnlyGuardTests.cs` holds the ruling: only the type that reads the file may name it, and that type has no call that writes, moves, copies, creates or deletes. A rule about names can be walked round, so the guard also pins the writers: it lists the exact set of product files that hold any call that writes, and a new file that writes fails it until a person checks what it writes and where. `ClaudeCodePaths.cs`, which holds the file's path, holds no call that writes.

### 9.4 The plugin — the only route

**Where the plugin cannot be registered, the dashboard works round nothing. It tells the operator, on screen, what is wrong and what to do.**

- **The plugin is in the data folder**, at `plugin\`. Never in the install folder: Claude Code loads a plugin from a folder **in place**, and the install folder is replaced at each update.
- **The plugin is a pointer.** `hooks.json` names `post-status.cmd` by its absolute path. `${CLAUDE_PLUGIN_ROOT}` is not used.
- **Claude Code registers it.** The dashboard runs `claude plugin marketplace add <folder>` and `claude plugin install claude-dashboard@claude-dashboard`, with the input closed and a time limit. It then reads the settings again to see the result.
- **`--install-hooks` registers the plugin and `--remove-hooks` removes it.** Neither does anything else. Each runs before the single-instance gate, so each works while the dashboard runs.

**What a start does**, in this sequence. The operator sees one notice.

| A start finds | It does | The notice |
|---|---|---|
| No Claude Code configuration directory | Nothing. Nothing is created | No Claude Code install was detected |
| Claude Code's settings will not read | Nothing | The file could not be read; nothing was changed |
| An old hook in Claude Code's settings | **Does not register the plugin**: both together would post each event twice. Does not touch the file | Remove the old hook, then restart the dashboard: ask Claude, or use `/hooks` |
| The plugin enabled | Nothing | None |
| The plugin turned off | Nothing: an install would turn it on again | The command that turns it on |
| A plugin of the same name from a different data folder | Nothing | Names the other folder |
| The dashboard's own settings will not read | Nothing: the opt-out was in that file. This start's first load decides, not the fresh file (§8.2) | Kept aside: the settings notice only, which says to copy the settings back, and `installHooksAtStart: false` if the plugin had been removed. Could not be opened: repair or delete that file, or run `--install-hooks` |
| `installHooksAtStart` is `false` | Nothing | The plugin was removed; `--install-hooks` puts it back |
| None of the above | Registers the plugin | Registered: restart the open sessions |
| …and `claude.exe` is not found | Nothing more | The two commands to run by hand |
| …and Claude Code refuses | Nothing more | What it said, and the two commands |

- **The notice is the first line of the notice row, and the first notice in the tray tooltip** (§5.6.1).
- **A notice that says "nothing reports" clears when a session reports.** An event that arrives is proof of the opposite.
- **Three notices do not clear on an event alone.** The old-hook notice stays until a start finds the hook gone: events arrive through that hook. The notices for a plugin that is turned off or removed clear only when a session reports **and** Claude Code's settings show the plugin enabled: a session that was open before keeps the plugin until it restarts. For those, the dashboard reads the settings again at an event, 30 seconds apart at most.
- **`claude.exe` is looked for on `PATH`, then in `%USERPROFILE%\.local\bin`.** A `claude.cmd` from npm counts as not found.

Measured on Claude Code 2.1.286 (2026-09-30 and 2026-10-01): both install commands ran with no person and exited 0, also when repeated. The two removal commands exit 1 for a thing that is not there. `claude plugin install` turns a disabled plugin on. A hook whose folder was moved did not fire, with no error. A session that was open before an install never ran the hook; a new session did. Not measured: `claude plugin update`, and the reload command in an open session.

**The self-test** (T1.61, issue #74)

- **After `listening.txt` is written, the dashboard runs `post-status.cmd` as Claude Code does** (`cmd.exe /c`, the JSON on standard input), on a pool thread. The start never waits for it. The **Test connection** button in the Settings window runs the same test, and shows the result beside the button; while a test runs, a second request joins it. The script is not edited.
- **It proves** that the script runs, `curl.exe` is there, the token is accepted and the port answers. **It does not prove** that Claude Code fires the hook: only a real message does, which is why the tooltip says when the last one arrived (§5.2).
- **Arrived within 3 seconds:** one Information line with the round trip in milliseconds, the real cost of one message on this machine. **Not arrived:** the notice `Messages from Claude Code cannot reach the dashboard: a test message did not arrive.` followed by the cause where it can be known: the script is missing, `curl.exe` is not in `System32`, the script could not be started, or the script ran and nothing arrived. Tray: `messages cannot arrive`. It clears when a later test passes, or when a real message is accepted after the failed test. No colour and no sound.
- **Refused messages:** while 3 or more posts got `401` on `/hook` within 10 minutes, the notice `Messages from Claude Code are being refused: their token does not match this dashboard's.` Tray: `messages refused`. A refusal while it shows keeps it, and it clears on the tick 10 minutes after the last refusal. **One refusal, as at a restart, never shows it** (`HookHealth.RefusalsToShow`, with the reason beside it). Only the last three refusal times are held, so a flood of refused posts costs no memory.
- **Plain words on screen** (the operator's ruling of 2026-10-03): "messages from Claude Code", never "hook". "Idle" is not used for this: Idle is a session's state.

**The two port files, and the announcement**

- `listening.txt` is written after a bind, **after** the script is written, and by write-then-rename. Thus the script cannot read half a file, and an old script never meets a dashboard that needs a token.
- `listening.txt` is deleted at four points: the usual quit, a Windows logoff, a fault that stops the process, and the `finally` block of `Main`.
- **Nothing is announced unless ingress is bound.** Hook payloads carry the operator's prompts.
- **Residual:** a hard stop leaves `listening.txt` with the last port. Until the next start, the script posts there. If a different program took the port, it receives the prompts. The next start writes the file again, with a new token.

---

## Part 10 — Startup, packaging, install

### 10.1 Startup model

- **Not a Windows Service.** A service runs in session 0, where there is no tray, no UIA and no desktop.
- **The start sequence** (`Program.Main`):
  1. Velopack's lifecycle arguments. First, always.
  2. The one-shot switches: `--install-hooks`, `--remove-hooks`, `--replay <path>`.
  3. The single-instance gate. A second instance sends `/show` and exits. A first instance then keeps aside a settings file that does not parse (§8.2).
  4. The port choice (§3.1).
  5. The host is built and started.
  6. `post-status.cmd` is written, then `listening.txt`.
  7. The plugin check (§9.4).
  8. The start-with-Windows value is made to match the setting, **unless this start's settings were unreadable**: kept aside, could not be opened, or could not be kept aside. That start leaves the `Run` value and Windows' `StartupApproved` mark as it found them, because the operator's choice was in the file it could not read: the same reason it registers no plugin (§8.2, §9.4).
  9. The window and the tray are made, on the UI thread.
- **Start with Windows is the `Run` key** (T1.50). The value `Claude Dashboard` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` holds the quoted path of `current\ClaudeDashboard.App.exe`. `startWithWindows` in `settings.json` is the truth: each start makes the value match it. Only a copy that Setup installed registers; a portable copy never does. Windows' own off switch (in Settings › Apps › Startup, and in Task Manager) is respected. Velopack's uninstall removes the value.
- **Why the `Run` key and not a scheduled task:** it is where a user looks for startup programs, and where a user can turn one off. The price: **no restart after a crash.**
- **Resilience in the process:** handlers for `AppDomain.UnhandledException`, `DispatcherUnhandledException` and `TaskScheduler.UnobservedTaskException`, and the log. A failure of an adapter makes a feature less; it does not stop the process.

### 10.2 Packaging

- **Publish and pack:** `build\package.ps1 -Version <semver>`. It runs `dotnet publish -c Release -r win-x64 --self-contained` and then `vpk pack` (Velopack). The result is a `Setup.exe`, a portable zip and the update packages.
- **The install** is for one user, with no elevation, under `%LocalAppData%\dsopko.ClaudeDashboard\`. `current\` holds the binaries and is replaced at each update. Nothing of ours may live there.
- **No installer step configures anything.** The application registers its plugin (§9.4) and its `Run` value (§10.1) at its first start.
- **The Settings window** has one setting: start with Windows. *The remainder is Phase 6.*
- The [Packaging Design](claude-dashboard-packaging-design.md) gives the decisions.

---

## Part 11 — Phase → implementation map

| Phase | Theme | Built in this phase |
|---|---|---|
| **1** | See clearly | Core (all of Part 2); ingress and the token; the channel, the consumer and the tick; the WPF window (grouped and flat, collapse, open rows, selection and rosters); the tray light; Ack; notices and nudges with NAudio; the event log and the decision record; `/state`; the plugin; start with Windows; packaging. **No UIA** |
| **2** | Go there | `ITerminalLocator` (FlaUI) and `ITerminalNavigator` (`wt.exe`, UIA). *Not built* |
| **3** | It notices | `IFocusSource`; an Ack from focus; no notice for a session on screen. *Not built* |
| **4** | Task lens | Grouping by desktop; desktop names. *Not built* |
| **5** | Memory | Search of the history; statistics; a restart from the log; retention. *Not built* |
| **6** | Polish | The full settings interface; a sound editor; themes. *Not built* |
| **7** | Anywhere | `ClaudeDashboard.Remote`; an authenticated remote read and Ack. *Not built* |

---

## Appendix A — Dependencies

| Package | Version | Use |
|---|---|---|
| ASP.NET Core | Framework reference | Kestrel and the minimal API |
| CommunityToolkit.Mvvm | 8.4.2 | View models |
| H.NotifyIcon.Wpf | 2.4.1 | The tray icon |
| Microsoft.Data.Sqlite | 10.0.0 | `dashboard.db` |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.13 | A pin that clears a security advisory in the version that Sqlite brings. Do not remove it |
| NAudio | 3.0.1 | Sound |
| Serilog, Serilog.Sinks.File, Serilog.Extensions.Hosting | 4.4.0, 7.0.0, 10.0.0 | The log, and the bridge that puts the host's own diagnostics in it |
| Velopack | 1.2.161 | Install and update. The `vpk` tool is pinned to the same version |
| MScholtes VirtualDesktop | Source in the repository | The pin to all desktops |
| FlaUI.UIA3 | — | *Phase 2. Not referenced yet* |

Core references no package.

## Appendix B — Open items

- **The relation to ClaudeSessions.** Absorb it, or keep it apart?
- **Subagents.** Show a background agent as its own row, or not? Today its events arrive under the parent's id.
- **Queued prompts.** A "queued" hint on a Working row, and from which signal?
- **Retention.** Proposed: keep 30 days of events. Unread never fades. *Not built.*
- **Content-match disambiguation** (Phase 2).
- **The silence threshold.** Ten minutes is a guess (issue #49).

## Appendix C — Change history

The text above says what is true now. This list says when each part changed.

| Date | Change | Source |
|---|---|---|
| 2026-08-24 | The Tests project targets `net10.0-windows` | T1.0 |
| 2026-08-24 | `Session.Group` is a key, not a `Group` | T1.1 |
| 2026-08-24 | The attention order is "by kind, then oldest", in one table | T1.3 |
| 2026-08-24 | `ITerminalNavigator.Activate` is asynchronous and returns `bool` | T1.6 |
| 2026-08-24 | The tray: a question is amber. Mute all and Pause are two things | Operator's rulings |
| 2026-08-24 | `idle_prompt` changes no state | Issue #1 |
| 2026-08-24 | Mute is in the sound engine, not in the audio adapter | T1.14 |
| 2026-08-26 | The port is chosen for each user. The pin is in the undocumented tier | T1.16, T1.21; issue #5 |
| 2026-08-30 | The command hook replaces the HTTP hook. `listening.txt` | T1.28; issue #29 |
| 2026-08-30 | Rosters and the settle window | T1.25, T1.26; issue #16 |
| 2026-08-31 | `Interrupted`, `LastHeardAt`, the silence sweep | T1.30; issue #28 |
| 2026-09-02 | Publish is a directory, with Velopack | PKG.1 to PKG.3 |
| 2026-09 | A start registers the hook if it is absent; no Claude Code means no change | T1.32, T1.33; issues #39, #42 |
| 2026-09 | Ack all. The group's Ack | T1.34, T1.36; issues #43, #47 |
| 2026-09 | The decision record and `--replay` | T1.37; issue #48 |
| 2026-09 | The working clock counts from the ask | T1.40; issue #51 |
| 2026-09 | `Waiting`, machine prompts | T1.41; issue #52 |
| 2026-09 | The quiet tick | T1.44; issue #56 |
| 2026-09-29 | `GET /state`. The clock anchor. Mute all in the toolbar. The "Open terminal" button hidden | T1.46, T1.47; issues #10, #59, #60 |
| 2026-09-30 | The token is made at each start and is in `listening.txt` | T1.48; issue #57 |
| 2026-10-01 | The plugin is the only route; no write of Claude Code's settings. Start with Windows through the `Run` key; the scheduled task is removed | T1.49, T1.50, T1.51; issues #30, #36, #65 |
| 2026-10-02 | v0.2. Written again to agree with the code. Added: §2.5 to §2.7, §3.5, §5.6, §8.1 to §8.5 | — |
| 2026-10-02 | The log file follows `logging.minimumLevel` (§8.2, §8.4). The no-write guard pins the files that may write (§9.3) | T1.52; issues #68, #65 |
| 2026-10-03 | `StopFailure` gives its kind in `error`, read before `error_type` (§3.5, §9.1). A second error of another kind changes the kind | T1.53; issue #67 |
| 2026-10-03 | The store tries again each minute (§8.3). The notice row is a list, and history not recorded is a notice (§5.6.1); the tooltip leads with each notice (§5.2) | T1.54; issue #71 |
| 2026-10-03 | The player reports what it did, and a dropped sound is `SoundDropped` (Part 7, §8.3). No sound device is a notice (§5.2, §5.6.1). The store writes the stack on its first Warning only | T1.55; issue #72 |
| 2026-10-03 | A settings file that does not parse is kept aside and a fresh one written; one that cannot be opened refuses saves for the run (§8.1, §8.2, §9.4). The settings notice (§5.2, §5.6.1) | T1.56; issue #73 |
| 2026-10-03 | A port that is taken says what to do, in the log, the tray and the window; the port fault is the first notice on the board (§3.1, §5.2, §5.3, §5.6.1). A program on the port in `port.txt` no longer leaves the dashboard deaf: the choice walks on (§5.3). A start whose settings were unreadable leaves start with Windows as it found it (§8.2, §10.1) | T1.57; issue #14 |
| 2026-10-03 | The event channel sheds only noise when full, and drops the oldest only at a hard limit of 16,384; the bound is asserted (Part 4, §8.3). Two queue notices (§5.2, §5.6.1) | T1.58; issue #3 |
| 2026-10-03 | The history store may be closed while it writes: the close waits for the current write, and a write after it is dropped without a sound (Part 4) | T1.59; issue #84 |
| 2026-10-03 | The history database records each start and stop in a table of its own, `runs`, in UTC; `--replay` forgets every session at each start and each clean stop (§8.1, §8.3) | T1.60; issue #78 |
| 2026-10-03 | The dashboard tests the path from Claude Code at each start and from a Settings button; notices for messages that cannot arrive or are refused; `HookRefused` rows, at most one a second; "last heard" is the tooltip's last item; the tooltip keeps to 127 characters; `/state` has `health` (§3.2, §3.5, §5.2, §5.6.1, §8.3, §9.4) | T1.61; issue #74 |
| 2026-10-04 | Every time in `dashboard.db` is UTC text in one form; existing rows are converted once, and the file has `user_version` 1. The documented query takes UTC. "No refusal goes unrecorded" holds while the dashboard runs (Part 4, §3.2, §8.3) | T1.62; issue #80 |
