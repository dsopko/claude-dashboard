# Claude Dashboard — Core and App, and what a second interface needs

**Describes the code at commit `0488527` · Written 2026-10-02**

This document says which logic is in `ClaudeDashboard.Core` and which is in `ClaudeDashboard.App`. It then tests that split with one question: *if we build a web interface (ClaudeDashWebApp) or a phone app that looks like the Windows dashboard, what must change in Core, and what would we write twice?*

It is for the person who builds that interface.

- **Sections 2 to 5 describe the code as it is.** The code is the authority for them.
- **Sections 6 and 7 are analysis and proposals.** Nothing in them is ruled. Section 9 lists the decisions that are the operator's.
- **Section 8 is the map:** where to find each piece of knowledge.

The [Technical Specification](claude-dashboard-spec.md) (TS) gives the reasons for the design. The [Implementation Specification](claude-dashboard-impl-spec.md) (Impl) gives the C# shape. The [event flow](claude-dashboard-event-flow.md) follows one event from Claude Code to the window. This document does not repeat them.

---

## 1. The short answer

- **Core decides.** It holds every rule about a session: its state, its band, its place in the list, its group, and the time a sound is due. It has no window, no thread, no timer, no file and no network.
- **App does three other jobs.** It *runs* Core: it receives the hooks, owns the one thread, supplies the clock and stores the history. It *adapts* Windows: sound device, tray, registry, clipboard. It *shows* the result: view models and XAML.
- **A second interface must be a second screen, not a second Core.** The Registry is in the memory of one process. A second Registry would play each sound twice and would not agree about acknowledgments.
- **Core's rules need almost no change. Its outward shape needs one.** Today no single message describes the board. Three listeners in App each build their own copy from `SessionChanged`. The read model behind `GET /state` is in App, and it uses a type from the WPF folder.
- **The duplication risk is in `App/Ui`, not in Core.** The view models hold display rules that have no WPF in them: which rows collapse, which instant a row's clock counts from, which rows show an Ack. A web app would have to write each of these again, unless they move.
- **`GET /state` is not enough to draw the window.** It has no prompt and no answer (by ruling), no rosters, no mute or pause state, and no notice. It also cannot push, and it accepts no commands.

---

## 2. The projects and the rule between them

| Project | Target | Holds | May reference |
|---|---|---|---|
| `ClaudeDashboard.Core` | `net10.0` | The domain, and the port interfaces | Nothing |
| `ClaudeDashboard.App` | `net10.0-windows` | The host, the Windows adapters, the WPF interface | Core only |
| `ClaudeDashboard.Remote` | `net10.0` | Nothing. An empty project for Phase 7 | Core only |
| `ClaudeDashboard.Tests` | `net10.0-windows` | xUnit tests | Core and App |

**The rule: all projects point at Core, and nothing points at App.** `tests/ClaudeDashboard.Tests/Architecture/DependencyRuleTests.cs` holds it. That file reads the project files and the compiled Core assembly. It fails if Core takes a reference to WPF, Win32, ASP.NET, the Generic Host, NAudio, the MVVM toolkit or Velopack.

Two of its tests matter for a second interface:

- `App_references_only_Core`. App cannot reference Remote.
- `Remote_references_only_Core`. Remote cannot reference App.

Thus, as the tests stand, App and Remote cannot be in one process unless a third project composes them, or a test changes. Section 6.6 comes back to this.

---

## 3. What Core holds

Core is 45 files and about 6,500 lines. Most of the lines are comments that give the reason for a rule.

### 3.1 The rules, by subject

| Subject | Types | The rule in one line | Authority |
|---|---|---|---|
| A session | `Session`, `Exchange`, `SessionId`, `TransitionLog` | An immutable record. An event makes a new record | TS §IV.1 |
| The states | `SessionState` (9 values) | Working, Waiting, NeedsPermission, NeedsQuestion, Error, Unread, Acked, Interrupted, Ended. The numbers are stored and must not change | TS §IV.1 |
| The state machine | `SessionRegistry.Apply` | One event in, one decision out. Three guards: stale, uncorrelated, duplicate | TS §IV.1, event flow §8 |
| Silence | `SessionRegistry.SweepSilent`, `SilenceWatch` | A Working session with no event for 10 minutes becomes Interrupted | TS §IV.1 |
| The title | `SessionRegistry` (the latch) | The last different, non-empty title wins. A title never moves a row | Issue #18 |
| Background work | `BackgroundTasks.cs` | A `Stop` that lists a running `shell` or `subagent` gives Waiting, not Unread | T1.41 |
| Quiet ticks | `QuietTicks` | A scheduled job that answers `WATCHDOG-QUIET` puts the row back | [Quiet scheduled jobs](quiet-scheduled-jobs.md) |
| Machine prompts | `UserPromptSubmit.IsMachinePrompt`, `ContinuesTheAsk` | A prompt that nobody typed is not an acknowledgment | T1.40, T1.41 |
| Acknowledgment | `Acknowledgment` | Applies to Unread, NeedsPermission, NeedsQuestion and Error. Builds the `Ack` event | Design §4 |
| Severity and bands | `AttentionOrder` | One rank table. `BandOf`, `Rank`, `WorstOf`. A roster group uses a different order | TS §IV.2, §IV.3 |
| The order on screen | `AttentionEngine` | `Order` for the flat view, `OrderGroups` for the grouped view | TS §IV.2 |
| Groups | `GroupKeys`, `GroupResolver`, `Group` | The key comes from the directory, or from a roster. `Effective` decides which | TS §IV.3 |
| Rosters | `RosterBook`, `Roster` | A name is in one roster at most. An empty roster does not exist | Issue #16 |
| The settle window | `RosterSettle`, `RosterGroupWatch` | A roster group reads finished only after 1.5 s of quiet | TS §IV.3 |
| Sound policy | `SoundPolicyEngine`, `SoundPolicyOptions` | Notices on state entry. Nudges at 2, 5, 10 minutes. Mute and pause | TS §IV.5 |
| The roll-up | `StatusSummary` | The worst state, and a count for each kind | Impl §5.2 |
| The one-writer check | `SingleWriterGuard` | Throws if two threads are in the Registry or the sound engine at one time | Impl §2.2 |

### 3.2 What Core takes in

Core takes in one kind of message: an `InboundEvent`. The host gives each event to `SessionRegistry.Apply`, on one thread.

| Event | Source | Goes to |
|---|---|---|
| `SessionStart`, `UserPromptSubmit`, `Notification`, `Stop`, `StopFailure`, `SessionEnd`, `CwdChanged`, `PostToolBatch` | A Claude Code hook | The Registry |
| `Ack` | A click in the interface | The Registry |
| `SoundCommand` (mute, unmute, pause, resume) | The tray menu and the header | The sound engine. The Registry does not see it |
| `RostersChanged` | An edit of a roster | Nothing. It wakes the consumer, which reads the rosters again |

The last three are commands from an interface. They are already messages, and they already go through the same channel as the hooks. **This is the part of the design that a second interface can use with no change to Core.**

Core also takes in three calls that are not events. The host makes them on the same thread:

- `SessionRegistry.SweepSilent(now, threshold)`
- `SoundPolicyEngine.Evaluate(now)`
- `RosterGroupWatch.Observe(groups, now)`

### 3.3 What Core gives out

This is the full list. Core emits nothing else.

| Output | Form | Carries | Listener today |
|---|---|---|---|
| A session changed | The event `SessionRegistry.SessionChanged` | `Added` or `Updated`, and the new immutable `Session` | Three, in App (section 5) |
| A nudge moved the schedule | The event `SoundPolicyEngine.NudgeScheduleAdvanced` | Nothing | `StateBoard` |
| Play this sound | The port `ISoundPlayer.Play(sound, gain, fade)` | An intent | `NAudioSoundPlayer` |
| A sound decision | The port `IDecisionSink` | Played, or suppressed and the cause | `DecisionRecorder` |
| The result of an event | The return value `ApplyOutcome` | Applied, Ignored, Stale, Duplicate, Uncorrelated | `EventConsumer` |
| The sessions that went silent | The return value of `SweepSilent` | The session and its silence | `EventConsumer` |
| A roster group settled | The return value of `RosterGroupWatch.Observe` | Settled, Unsettled, MisMarked | `EventConsumer` |
| The mute and pause modes | The read-only port `ISoundModeReader` | Two values, safe to read on any thread | `TrayViewModel` |

Three facts about these outputs matter for a second interface:

- **There is no "removed" message.** Nothing removes a session from the Registry. An Ended session stays until the dashboard starts again.
- **A change of mute or pause raises no event.** The interface learns of it because the consumer echoes a tick to the UI after a `SoundCommand`.
- **Time raises no event.** An age, a stale group and a settled roster group all change because time passed. The host must ask.

### 3.4 What Core does not do

- **It reads no clock in the Registry.** Each time that the Registry writes comes from the event or from the caller. Thus a replay gives the same result.
- **It starts no thread and no timer.** The sound engine records the time that a nudge is due. It plays the nudge only when the host calls `Evaluate`.
- **It writes no log and no file.** It returns what occurred, and the host writes it.
- **It does not know that a listener is an interface.**

The ports for later phases are declared and have no behaviour yet: `ITerminalLocator`, `ITerminalNavigator`, `IFocusSource`. `IVirtualDesktopService` has an adapter, which the window uses to pin itself to all desktops.

---

## 4. What App holds

App is about 100 source files and 21,000 lines. It has four layers. **The layer decides what a second interface must do about the code in it.**

### 4.1 The engine room: it runs Core

No part of this layer is about Windows or about WPF. It is in App because App is the only host.

| Part | Files | Job |
|---|---|---|
| Ingress | `Ingress/IngressEndpoints.cs`, `IngressToken.cs` | Kestrel on loopback. `/hook`, `/show`, `/health`, `/state` |
| Wire to domain | `Ingress/HookPayload.cs`, `HookEventMapper.cs`, `HookEventNames.cs`, `BackgroundTaskReader.cs`, `SessionCronReader.cs` | The only place where a wire value becomes a domain value. Stamps the arrival time |
| The channel | `Pipeline/EventPipeline.cs` | 1,024 events, drop-oldest. Implements `IEventSink` |
| The one thread | `Pipeline/EventConsumer.cs` | Reads the channel, calls `Apply`, and runs the 15-second tick |
| The decisions record | `Pipeline/DecisionRecorder.cs`, `Storage/` | Each event and its decisions go to `dashboard.db` |
| The read model | `Ingress/StateBoard.cs`, `StateReport.cs`, `OperatorText.cs` | An immutable report for `/state`, built on the consumer thread |
| Settings and rosters | `Configuration/` | `settings.json`, `RosterStore`, the port files |
| Start and stop | `Program.cs`, `Hosting/` | The port choice, the single-instance gate, the composition |

`EventConsumer` says of itself "routing only". That is true of decisions about one session. But it holds the **sequence** that makes Core correct, and a second host would have to copy that sequence:

- The tick is 15 seconds.
- In a tick: the silence sweep first, then the nudges, then the roster groups.
- After each batch of events: the roster groups again.
- A roster group that is due to settle wakes the loop before the next tick.
- After a `SoundCommand`: an echo to the interface at once.

### 4.2 The Windows adapters

| Adapter | Files | Port |
|---|---|---|
| Sound | `Adapters/NAudioSoundPlayer.cs`, `SoundCatalog.cs`, `AudioEndpoints.cs` | `ISoundPlayer` |
| Clock | `Adapters/SystemClock.cs` | `IClock` |
| Virtual desktops | `Adapters/VirtualDesktopService.cs` | `IVirtualDesktopService` |
| Tray icon | `Ui/TrayIcon.cs`, `TrayIcons.cs` | None |
| Clipboard, dispatcher | `Ui/IClipboard.cs`, `IUiDispatcher.cs` | App's own interfaces |
| The Claude Code plugin | `Setup/` | None |
| Start with Windows, the installer | `Setup/StartWithWindows.cs`, `InstalledCopy.cs` | None |

A second interface needs none of these. The dashboard process keeps them.

### 4.3 The display rules: no WPF in them, but they are in the WPF project

**This is the layer that a web app would write twice.** Each rule below is in a view model or a helper under `App/Ui`. Each one asks Core a question and then decides something about the screen. None needs a WPF type to make the decision. The Implementation Specification §5.6 is the full reference for these rules, with each value.

| Rule | Where it is | What it decides |
|---|---|---|
| Which rows exist | `MainViewModel.GroupedRows`, `FlatRows`, `IsQuiet` | A quiet group that is idle for 15 minutes is one line. Quiet rows in a live group are a "+ 3 quiet" line. The Quiet and Ended bands are one line in the flat view. An Unread row is never hidden |
| Which state orders a group | `MainViewModel.GroupedRows` | It gives `RosterSettle.StateOf(group, now)` to `AttentionEngine.OrderGroups`, and the same value to the heading |
| The row's clock | `SessionViewModel.AnchorOf` | Working and Waiting count from the ask (`Latest.StartedAt`). All other states count from `ClockAnchor`, or from `EnteredAt` if there is none |
| The words of an age | `RowVisuals.Age`, `Duration` | "waiting 4 min", "2 min ago", "6 min". "48s" below a minute, "2h 05m" from 90 minutes |
| The badge | `RowVisuals.BadgeOf` | PERMISSION, QUESTION, ERROR, FINISHED, WORKING, WAITING, QUIET, INTERRUPTED, ENDED |
| The row colour | `RowVisuals.AccentOf` | Red for permission and question. Amber for error. Green, blue, grey |
| The motion | `MotionPolicy.Wanted` | Red blinks. Working breathes. Nothing else moves. Waiting does not move |
| The title on a row | `SessionViewModel.TitleText` | One line, 40 grapheme clusters, 160 characters at most, then "…" |
| The prompt on a row | `SessionViewModel.PromptSnippet` | 140 characters, then "…" |
| The session id | `SessionViewModel.ShortId` | 8 characters on the row. The copy is the full id |
| The answer's label | `SessionViewModel.AnswerLabel` | "CLAUDE SAID SO FAR" while Waiting. "CLAUDE ANSWERED" otherwise |
| The "Waiting on" lines | `SessionViewModel.WaitingOnLines`, `WaitingSummary` | One line for each task: description, "background command" or "subagent", age |
| Which row shows an Ack | `SessionViewModel.ShowsOwnAck` | `Acknowledgment.Applies`, and the row is not a member of a roster group |
| The group's Ack | `GroupViewModel.CanAcknowledge`, `Acknowledge` | Roster groups only. It reads the members, not the settled state |
| Ack all | `MainViewModel.AckAll`, `AnythingToAcknowledge` | One `Ack` for each session that `Acknowledgment.Applies` to |
| The group heading | `GroupViewModel.Label`, `RowVisuals.WorkspaceLabel` | The roster's name, or the folder name, or the session id. Never the key |
| The band heading | `BandHeaderViewModel` | NEEDS YOU, UNREAD, WORKING, QUIET, ENDED, and the colour of each |
| The counts strip | `MainViewModel.RecountBands`, `CountsText` | "11 sessions · 3 need you · 5 unread · 8 working". A zero band is left out |
| Selection | `SessionViewModel.CanSelect`, `MainViewModel.GroupSelected` | A session with no title cannot be selected. A group needs 2 members. The default name is "Group", "Group 2" |
| The roster prompt | `RosterPromptViewModel`, `MainViewModel.RememberRoster` | A prompt with no answer is a "no" |
| The tray colour | `TrayVisuals.ColourOf` | Thresholds on `AttentionOrder.Rank`. Not the row palette: a lone question is amber in the tray and red on its row |
| The tray sentence | `TrayTooltip` | "2 permissions · 1 error · 1 question · 2 unread · 3 working". A fault leads, then pause, then mute |
| Mute and pause labels | `TrayViewModel` | The item toggles. A timed mute is 30 minutes. A mute that is past is not a mute |
| The notice | `Setup/HookNotice.cs` | The text for each way the dashboard is not connected, and which event clears it |

The pattern is the same in each row of the table: **the question goes to Core, and the answer about the screen is made in App.** The comments in these files say so ("Asked, not restated"). The project found out twice what a second copy of a rule costs: `AttentionOrder` exists because two severity tables disagreed about Error, and `Acknowledgment` exists because the Registry held two copies of one list.

### 4.4 The WPF view

`Ui/*.xaml`, `Converters.cs`, `FittingStrip.cs`, `CaptionChrome.cs`, `WindowPlacement.cs`, `WindowPresence.cs`, `MonitorLayout.cs`. Templates, brushes, the two storyboards, the window frame, and where the window sits.

A web app writes this layer again in HTML and CSS. That is expected, and it is not a risk: [the mockups](claude-dashboard-mockups.html) are already HTML, and they hold the same colours and the same two animations.

---

## 5. How a change reaches each consumer today

```
 EventConsumer (the one thread)
        │  SessionRegistry.Apply / SweepSilent
        ▼
 SessionRegistry ── SessionChanged(kind, Session) ──┬─▶ (1) SoundPolicyEngine.OnSessionChanged
                                                    │        plays, or schedules a nudge
                                                    ├─▶ (2) SessionProjection
                                                    │        posts the Session to the UI thread
                                                    │        ▶ MainViewModel, TrayViewModel
                                                    └─▶ (3) StateBoard
                                                             builds a StateReport, Volatile.Write
                                                             ▶ GET /state reads it on a request thread
```

- **Each listener keeps its own copy of the sessions.** `SessionProjection` has an `ObservableCollection<Session>` on the UI thread. `StateBoard` has a dictionary on the consumer thread. No listener reads `SessionRegistry.Sessions`, because it is a live view and is not safe off the consumer thread.
- **The sequence of the listeners matters.** `StateBoard` reads the nudge time that the sound engine has just set. `AppHost` subscribes it after the sound engine, and `Hosting/StateHostTests.cs` holds that.
- **The `Session` record is what crosses the thread.** It is immutable, so no copy is necessary.

The screen changes for six causes. Only the first is a Core event.

| Cause | How the interface learns of it |
|---|---|
| A session changed | `SessionChanged` → `SessionProjection` → the UI thread |
| Time passed (ages, stale groups, a lapsed mute) | The consumer's tick → `UiTick` → the UI thread, each 15 seconds |
| A roster group settled | The consumer wakes on the deadline and echoes a tick |
| Mute or pause changed | The consumer echoes a tick after the `SoundCommand` |
| A roster changed | The view model changed it itself, on the UI thread, and refreshes |
| The notice changed | `HookNotice` raises a property change |

A second interface needs all six. Today each one arrives by a path that ends in WPF.

---

## 6. The thought experiment: ClaudeDashWebApp

The aim: a page in a local browser, or an app on a phone, that shows what the Windows dashboard shows.

### 6.1 The first decision: one Core, not two

A web app could, in principle, hold its own `SessionRegistry` and receive the same hooks. **It must not.**

- **Sound.** Two sound engines play each notice twice, or the second must be made silent, and then its nudge schedule means nothing.
- **Acknowledgment.** An Ack in one Registry does not reach the other. The two screens then disagree about what is unread.
- **Hooks.** The script posts to one port. A second receiver needs a second route, and each event then costs two posts.
- **The sequence.** The second host must copy the loop of section 4.1 exactly.
- **Start.** The Registry is in memory and starts empty. Two processes that start at different times see different sessions.

Thus the dashboard process stays the only holder of Core. ClaudeDashWebApp is a second **screen** on that process. Impl §1.2 calls Remote "a second consumer of Core"; read that as a second consumer of what Core *emits*, not a second instance of Core.

### 6.2 What a second screen needs from the dashboard

| Need | Today |
|---|---|
| **A read model:** all that the window shows, as data | `GET /state` gives a part of it (section 6.3) |
| **Push:** to be told when the board changes | None. `/state` answers a request and no more |
| **Commands:** Ack, Ack all, mute, pause, form a group, remove from a group | The events exist in Core. No endpoint sends them |
| **Authority:** proof that the caller may read and command | A token in `listening.txt`. A browser page cannot read that file |

### 6.3 What `/state` gives, and what the window needs

| The window shows | The window reads | In `/state` today |
|---|---|---|
| The rows in order, the band of each | `AttentionEngine.Order` | Yes |
| The state of each session | `Session.State` | Yes |
| The title | `Session.Title` | Yes |
| The prompt, the answer, "You asked" | `Session.Latest` | **No, by the operator's ruling.** `/state` carries titles and task descriptions, and never a prompt or an answer |
| The row's clock | `Latest.StartedAt` or `ClockAnchor` | **No.** It has `EnteredAt`, `LastActivity` and `LastHeardAt` only |
| The error kind | `Session.ErrorKind` | Yes. It is always empty today: the wire sends `error` and the mapper reads `error_type` (issue #67) |
| The "Waiting on" lines | `Session.WaitingOn` | Yes |
| The group, where it is a roster | `GroupKeys.Effective` and the roster book | **No.** `group` is the workspace key. The rosters are not sent |
| The group's state, with the settle window | `RosterSettle.StateOf(group, now)` | **No** |
| The band counts | `AttentionOrder.BandOf` | Yes |
| The tray colour | `TrayVisuals.ColourOf` | Yes |
| The count for each kind (permissions, errors, questions) | `StatusSummary` | Not sent. A client can count the sessions |
| Muted, paused, and until when | `ISoundModeReader` | **No** |
| The notice, and an ingress fault | `HookNotice`, `IngressStatus` | **No** |
| The next nudge | Not shown in the window | Yes |

`/state` was built to answer "what does the dashboard believe now?" for a person with a terminal. It does that. It is a diagnostic read model, not an interface read model.

One limit is a ruling and not an omission: **`/state` never carries prompt or answer text.** A page that shows "You asked" and "Claude answered" needs that text. That is a new decision for the operator, and it must not arrive as a quiet change to `/state` (section 9, decision 2).

### 6.4 What would be written twice if nothing moves

Assume that the web app gets a wider read model and command endpoints, and that the code of section 4.3 stays where it is. The web app must then contain its own copy of each rule in that table. They fall into three classes.

| Class | Rules | What a wrong copy looks like |
|---|---|---|
| **Rules about meaning.** A wrong copy shows a false fact | Which rows collapse, and that Unread never does. The row's clock. Which rows and groups show an Ack. Which state orders a group. The tray colour. That a session with no title cannot join a roster | A finished session is hidden. A row says "0 s ago" after an Ack. An Ack is missing in the 1.5 seconds of the settle window. A question burns red in the roll-up |
| **Rules about words.** A wrong copy reads differently | Badges, band names, age phrases, the counts sentence, the tray sentence, the labels | "9 min" on one screen and "9m" on the other. Not a false fact, but the two screens stop looking like one product |
| **Rules about cutting text.** A wrong copy can break a character | The title at 40 clusters, the prompt at 140, the id at 8 | A split emoji. JavaScript and .NET count string length the same way (UTF-16), so a copy by length carries the same defect that `PromptSnippet` has |

The first class is the one to move. Each rule in it was the subject of a ruling or an issue, and each is held by tests that only run against the C# copy.

### 6.5 How Core would change (proposal)

Nothing here changes a rule. It changes where four things are, so that two screens read one source.

**P1. Move the read model into Core.**
`StateReport`, `SessionStateEntry`, `WaitingTaskEntry` and `TrayRollUp` are in `App/Ingress`. `StateReport` calls `TrayVisuals.ColourOf`, which is in `App/Ui`. Thus the read model already depends on the WPF folder, and Remote, which may reference Core only, cannot build it. Move these types, with `TrayColour` and `TrayVisuals`, to Core. `StateBoard` can move too: it needs the Registry, the sound engine, a clock, and a place to report a failure in place of the logger.

**P2. Move the rules about meaning into Core.**
Each becomes a function beside the question it already asks:

| Rule today | A home in Core |
|---|---|
| `SessionViewModel.AnchorOf` | Beside `Session.ClockAnchor`, which exists only for it. `DisplayOnlyAnchorTests` already guards the line |
| `MainViewModel.IsQuiet`, the stale-group rule | Beside `AttentionOrder.BandOf` and `Group` |
| `ShowsOwnAck`, the group's `CanAcknowledge` | Beside `Acknowledgment.Applies` |
| `RosterSettle.StateOf` as the state that orders a group | Already in Core. What moves is the choice to use it |
| `SessionViewModel.CanSelect`, `SmallestGroup` | Beside `RosterBook` |
| `RowVisuals.AccentOf`, `MotionPolicy.Wanted` | Beside `AttentionOrder`, as coarsenings of the state, like `BandOf` |

The WPF view models then ask Core, as they do now for bands. Their tests move with the rules.

**P3. Give Core one outward message: the board.**
This is the "Core emits messages" step. Today three listeners each assemble the world from `SessionChanged`. A fourth, for the web, would be one more copy of the assembly, and it would need the tick, the mute state, the rosters and the notice, which do not arrive on `SessionChanged` at all (section 5).

The proposal is one type in Core, a wider `StateBoard`, that listens to all six causes of section 5 and publishes one immutable snapshot each time the board changes. Any number of listeners take it: the WPF projection, `/state`, and a push connection to a browser. The pieces exist: `StateBoard` already builds a whole report for each change on the consumer thread, and publishes it with one `Volatile.Write`.

What the snapshot must hold, beyond today's `StateReport`:

- For each session: the ask instant, the clock anchor, the effective group, and (if ruled) the prompt and the answer.
- For each group: its kind, its label source, its settled state, its last activity, and whether all its members are quiet.
- The mute and pause modes, and the instant a mute ends.
- The notice and the ingress fault.
- The instant of publication, so that a client can correct for a clock that differs from the PC's.

Two cautions:

- **Send an explicit list of fields, never the `Session` record.** `Session` holds `ScheduledPrompts` and `PreTick`. These are the prompts of scheduled jobs, kept in memory to be compared and never shown. `SessionStateEntry` is the correct pattern: a copy of named fields.
- **Per-viewer state stays in each screen.** Which rows are expanded, grouped or flat, and selection mode belong to one viewer. The board sends facts ("this session is quiet", "this group is stale since 14:02"). Each screen applies the last, trivial step: hide or show.

**P4. Commands need endpoints, not new Core types.**

| Command | Core event today | What is missing |
|---|---|---|
| Ack one session | `Ack`, built by `Acknowledgment.For` | An endpoint that takes a session id. `For` needs the session's `cwd`; the host reads it from the board's copy, not from the Registry |
| Ack all, Ack a roster group | A loop of `Ack` | The loop is in two view models. With P2 it is one function |
| Mute, unmute, pause, resume | `SoundCommand` | An endpoint |
| Form a group, remove a member, remember a roster | `RosterBook.With`, `Without`; then `RostersChanged` | An endpoint. `RosterStore.Replace` and the save to `settings.json` are called from the WPF view model today |
| Mute one session or one group | **None.** `SoundPolicyEngine.SetSessionMuted` and `SetGroupMuted` exist, and nothing calls them | An event, and a control in both screens. Not needed to match the window |

`AckSource` has two values, `Manual` and `InferredFocus`. A third, for an Ack from a remote screen, would let the decisions record say which screen acknowledged.

**P5. Wording: two ways, and the choice depends on the client.**

- *The dashboard sends finished text.* "waiting 4 min", "FINISHED", "+ 3 quiet". The browser prints it. Nothing is written twice. The cost: the dashboard must push each time an age changes its text. The window itself refreshes ages only on the 15-second tick, so four pushes a minute match it.
- *The dashboard sends instants and codes.* The client formats them. The cost: the age rule (`RowVisuals.Duration` and `Age`, about 30 lines) and the badge table exist a second time, in JavaScript or Swift.

For a local browser the first is cheaper and cannot drift. For a phone, a push each 15 seconds costs battery and data, so the second is likely. If the second is chosen, keep the copy small and test it against a table of cases that both languages read.

### 6.6 Where the web host lives

| Option | What it is | For | Against |
|---|---|---|---|
| **A. In App** | New endpoints beside `/state`, in the Kestrel that App already runs. Static files for the page | No new process. No change to the dependency tests | App grows. A phone must not reach this listener (TS §II.5) |
| **B. In Remote, hosted by the dashboard process** | Remote holds the endpoints and the push hub. The process loads both | Matches Impl §1.2 and the plan's T7.1 | App cannot reference Remote today. Needs a third, composing project, or a change to `App_references_only_Core` |
| **C. Remote as its own process** | Remote connects to the dashboard over loopback and serves the outside | The network-facing code is out of the process that holds the prompts | A wire between two of our own processes. Two things to start and to update |

For a local browser, A or B gives the same result on screen. For a phone, the listener must accept connections from outside the PC, and the TS is firm: that is "a separate, authenticated surface layered on top of the Registry, never this raw ingress exposed to the network". Thus the phone needs a second listener in each option.

### 6.7 A local browser and a phone are different problems

| | Local browser | Phone |
|---|---|---|
| Network | Loopback. The listener that exists | Off the PC. A second listener, and a way to reach it |
| Authority | The run token. The page cannot read `listening.txt`, so the dashboard must hand the token over, for example when it opens the page | Real authentication. The run token is not it: it changes at each start |
| Encryption | Not necessary on loopback | Necessary |
| Clock | The same clock as the dashboard | A different clock. Use the publication instant to correct ages |
| Sound | The PC plays it. The page plays nothing | The PC plays it. A phone that must alert needs its own notification path, which is not in any document |
| When the dashboard is closed | The page shows that it is not connected | The same |
| Other pages on the PC | A page from any site can try to call `127.0.0.1`. The token header stops a simple request. The host must also refuse a request whose `Host` or `Origin` is not its own | Not applicable |

### 6.8 One observer or several

Two use cases, raised by the operator on 2026-10-02. They need different things from Core.

**Use case 1: one observer, several front ends.** The desktop window, a web page and a phone all show the same board. An Ack on one shows on all.

- This is sections 6.1 to 6.7. One Registry, one sound engine, several screens.
- An Ack already syncs: it is an event in the channel, and each screen shows what the Registry then says.
- Mute and pause are one for all screens, because the one sound engine holds them.

**Use case 2: several observers.** Two people, or two roles, watch the same sessions. Each has their own acknowledgments, their own mute and their own pause.

- **Today this is not possible, because "seen" is a state of the session.** `Unread` and `Acked` are values of `SessionState`. One observer's Ack makes the session Acked for all.
- The split that it needs: **what the session does** (working, waiting, blocked, in error, finished, silent, ended) comes from the hooks and is the same for all. **What an observer saw and silenced** (acknowledged or not, muted, paused, the nudge schedule) belongs to one observer.
- In Core that means: the finished state loses its two halves (`Unread`, `Acked`) to an observer's record; `AttentionOrder`, the bands and `StatusSummary` take an observer as an input; there is one `SoundPolicyEngine` for each observer, or one engine that is keyed by observer; an `Ack` and a `SoundCommand` name their observer; rosters are either shared or one set for each observer (a decision).
- `SessionState` is stored on disk by number, so this is a change to the stored form too.
- An Ack from a typed prompt (tier 1) is a fact about the session, not about an observer: the next turn started. It clears "finished" for all.

**The two fit one design, if the first is built with the second in mind.** Use case 1 is use case 2 with one observer. The cheap step that keeps the door open: **put an observer id in each command and in each board message from the first day**, with one fixed value. Then the web page and the phone need no change of wire format when a second observer arrives.

---

## 7. Rules that a second screen must keep

A proposal, like section 6: nothing here is ruled for a second screen yet. Each item restates a rule the dashboard keeps today, and the first column of section 8 says where that rule is written. What is new is only how it reads for another screen, such as `textContent` in a browser (item 5) or a screen that does not poll the dashboard (item 12).

1. **`/hook` stays a pure observer.** Always `200`, always empty. Commands from a screen go to their own endpoints and never to `/hook`. The mapper must never turn a hook payload into an `Ack`, a `SoundCommand` or a `RostersChanged`.
2. **A command is an event in the channel.** A screen never changes the Registry or the sound engine directly. The Registry has one writer.
3. **No request thread reads the Registry.** A request reads a published snapshot.
4. **Nothing is optimistic.** A row goes grey when the Registry says it is acknowledged, not at the click. The same for mute and pause.
5. **Text is data.** In a browser this has a sharp meaning: a prompt, an answer, a title and a task description go into the page as text nodes (`textContent`), never as markup (`innerHTML`). The text comes from a model and from files that the model read.
6. **No prompt, answer, title, task description or task command in a log.** This includes the web server's request log and the browser's console. A task's `command` is never read at all.
7. **An absence of activity never makes a row louder.** It may make it quieter.
8. **Unread is never summarised away.** No collapse rule may hide a finished, unseen session.
9. **Red blinks, working breathes, nothing else moves.** Honour the reduced-motion setting: `prefers-reduced-motion` is the same Windows switch that `MotionPolicy` reads.
10. **The roll-up palette is not the row palette.** A lone question is amber in the roll-up and red on its row.
11. **Never show a group key.** A key is an identity with folded case and a prefix. The label comes from the roster's name or from a member's directory.
12. **The dashboard never polls Claude Code, and a screen should not poll the dashboard.** Push is the model.

---

## 8. Where to find what

Code paths are under `src/ClaudeDashboard.`. Test paths are under `tests/ClaudeDashboard.Tests/`.

| To know | Read | Then the code | The tests |
|---|---|---|---|
| Why the product exists, the bands, the row anatomy | [Design](claude-dashboard-design.md) §3 to §9 | | |
| Why a rule is as it is | [TS](claude-dashboard-spec.md) Part IV; its Appendix D for when the rule changed | The comment on the type | |
| The states and what moves a session | TS §IV.1; Impl §2.2, §2.6; [event flow](claude-dashboard-event-flow.md) §8 | `Core/SessionRegistry.cs`, `SessionState.cs` | `Domain/SessionRegistryTests.cs`, `WaitingStateTests.cs`, `SilenceSweepTests.cs`, `QuietTickTests.cs`, `SessionTitleLatchTests.cs` |
| The order on screen | TS §IV.2 | `Core/AttentionOrder.cs`, `AttentionEngine.cs` | `Domain/AttentionOrderTests.cs`, `AttentionEngineTests.cs` |
| Groups and rosters | TS §IV.3; Impl §2.5; Design §9 | `Core/GroupKeys.cs`, `GroupResolver.cs`, `RosterBook.cs`, `RosterSettle.cs`, `RosterGroupWatch.cs` | `Domain/GroupKeysTests.cs`, `GroupResolverTests.cs`, `RosterBookTests.cs`, `RosterGroupingTests.cs`, `RosterGroupWatchTests.cs` |
| When a sound plays | TS §IV.5; Impl Part 7 | `Core/SoundPolicyEngine.cs`, `SoundPolicyOptions.cs` | `Domain/SoundPolicyEngineTests.cs`, `RosterSoundTests.cs` |
| Mute and pause | Impl §5.2 | `Core/Events/Variants.cs` (`SoundCommand`), `App/Ui/TrayViewModel.cs` | `Pipeline/SoundCommandPipelineTests.cs`, `InstantModeLabelTests.cs`, `Ui/TrayViewModelTests.cs` |
| Acknowledgment, from the click to the Registry | Design §4; TS §I.3 | `Core/Acknowledgment.cs`, `App/Ui/AckPublisher.cs` | `Domain/AcknowledgmentTests.cs`, `Pipeline/AckPipelineTests.cs`, `Ui/AckTests.cs`, `GroupAckTests.cs`, `Architecture/AckAllGuardTests.cs` |
| What Claude Code sends | [Hooks reference](claude-code-hooks-reference.md); event flow §13 for what was measured | `App/Ingress/HookPayload.cs`, `HookEventMapper.cs` | `Ingress/HookEventMapperTests.cs` |
| How an event travels | Event flow §1 to §9; Impl Part 4 | `App/Pipeline/EventConsumer.cs`, `EventPipeline.cs` | `Pipeline/EventConsumerTests.cs`, `EventPipelineTests.cs`, `SettleWakeTests.cs` |
| How a change reaches the UI thread | Impl Part 4 | `App/Ui/SessionProjection.cs`, `UiTick.cs` | `Pipeline/SessionProjectionTests.cs`, `UiTickTests.cs` |
| The endpoints and the token | Impl §3.2, §3.4; hooks reference, "`GET /state` is not a hook" | `App/Ingress/IngressEndpoints.cs`, `IngressToken.cs` | `Ingress/IngressEndpointTests.cs`, `IngressResilienceTests.cs`, `Hosting/TokenHandoverTests.cs` |
| The shape of `/state`, field by field | Impl §3.5 | `App/Ingress/StateReport.cs`, `StateBoard.cs`, `OperatorText.cs` | `Ingress/StateEndpointTests.cs`, `StateBoardTests.cs`, `Hosting/StateHostTests.cs` |
| Which rows exist, and the collapse rules | Design §6; TS §IV.4; Impl §5.6.2 | `App/Ui/MainViewModel.cs` | `Ui/MainViewModelTests.cs`, `CollapseTests.cs`, `RosterEditingTests.cs` |
| What one row says, its clock, its words, its colour | Design §9; Impl §5.6.3 to §5.6.6 | `App/Ui/SessionViewModel.cs`, `RowVisuals.cs`, `MotionPolicy.cs` | `Ui/AskAnchorTests.cs`, `FinishClockTests.cs`, `SessionTitleRowTests.cs`, `SessionIdRowTests.cs`, `MotionTests.cs`, `SilenceVisualsTests.cs`, `Architecture/DisplayOnlyAnchorTests.cs` |
| Headings, footers, where an Ack appears, selection and rosters | Design §4, §9; Impl §5.6.7, §5.6.8 | `App/Ui/HeaderViewModels.cs`, `MainViewModel.cs`, `RosterPromptViewModel.cs` | `Ui/GroupAckTests.cs`, `CollapseTests.cs`, `RosterEditingTests.cs` |
| The files in the data folder, the keys of `settings.json`, the tables of `dashboard.db`, the decision kinds | Impl Part 8 | `App/Configuration/`, `App/Storage/` | `Hosting/SettingsStoreTests.cs`, `Storage/` |
| What is specified and not built | TS Appendix C | | |
| The tray colour and sentence | Impl §5.2 | `App/Ui/TrayVisuals.cs`, `TrayTooltip.cs` | `Ui/TrayVisualsTests.cs`, `TrayTooltipTests.cs` |
| Colours, sizes, the two animations | [Mockups](claude-dashboard-mockups.html) (visuals only, never ordering) | `App/Ui/RowTemplates.xaml` | |
| The "not connected" notices | Impl §9.4 | `App/Setup/HookNotice.cs`, `StartupHookInstall.cs` | `Setup/HookNoticeTests.cs`, `StartupHookInstallTests.cs` |
| What must never be logged | Impl §3.4 | `Core/Events/PayloadJson.cs`, `App/Ingress/OperatorText.cs` | `Domain/UnprotectedTextInventory.cs`, `PayloadJsonTests.cs`, `OperatorTextTests.cs` |
| What each project may reference | Impl §1.2 | The `.csproj` files | `Architecture/DependencyRuleTests.cs` |
| What the host composes, and in which sequence | Impl §3.1, Part 4 | `App/Hosting/AppHost.cs`, `App/Program.cs` | `Hosting/AppHostTests.cs`, `ServiceCompositionTests.cs`, `StateHostTests.cs` |
| What Phase 7 was planned to be | Design §10; TS §I.4, §IV.8; Impl §1.2, Part 11; Execution Plan T7.1 to T7.3 | `src/ClaudeDashboard.Remote/` (empty) | |
| The history of a decision | `decisions-because-you-were-not-available.md` in the repository root; the issue named in the comment | | |

The colours, for a stylesheet: red `#FF6B5E`, green `#55C96A`, blue `#5AA9FF`, amber `#FFB454`, grey `#6B7480`. The blink goes to 15% opacity and back in 1.1 s. The breath goes to 45% and back in 2.6 s. `RowTemplates.xaml` is the authority; the mockups have the same timing, and their blink stops at 25%.

---

## 9. Decisions that are the operator's

1. **Which screen first: a local browser, or a phone?** Section 6.7 shows that they share the read model and the commands, and differ in all else.
2. **May a read model for a screen carry the prompt and the answer?** `/state` may not, by ruling. A screen that looks like the window needs them. If yes, it is a separate endpoint with its own rule, and `/state` stays as it is.
3. **Do the rules about meaning move into Core (P2)?** If not, the web app copies them, and the two screens are held together by review only.
4. **One board message from Core (P3), or a fourth listener in the host?**
5. **Finished text or instants (P5)?**
6. **Where the web host lives (section 6.6),** and thus whether `App_references_only_Core` changes.
7. **How a browser page gets the token.**
8. **May a second screen command, or only read?** Phase 7 plans read first (T7.2) and Ack after (T7.3).
9. **One observer or several (section 6.8)?** And if several: are rosters shared, or one set for each observer?

---

## 10. The other documents were corrected with this one

When this document was first written (2026-10-02), several other documents were behind the code. They were corrected on the same day:

- The Technical Specification, the Implementation Specification and the Design say what is true at `0488527`. Each has a change history at its end.
- The event flow and the hooks reference describe the plugin and the fields that the wire sends.
- **What is specified and not built is in one list:** the Technical Specification, Appendix C. A second interface must not count on an item in that list: for example, an Ended session is never removed, and a restart loses all sessions.
