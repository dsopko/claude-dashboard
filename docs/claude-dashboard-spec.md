# Claude Dashboard — Technical Specification

**v0.3 · 2026-10-02 · agrees with the code at commit `0488527`**

This specification says *how the system works and which mechanisms make it possible*. It names each component by the **role** it plays, not by a programming language, a UI framework or a storage engine. Where it names a concrete API, that API belongs to the operating system or to Claude Code: a fixed surface that the system must work with, not a choice.

The companion documents:

- [Design](claude-dashboard-design.md): the product and the reasons for it.
- [Implementation Specification](claude-dashboard-impl-spec.md): the C# and WPF form of this document, with exact values.
- [Event flow](claude-dashboard-event-flow.md): one event, step by step, with the file for each step.
- [Core and App](claude-dashboard-core-and-app.md): which project holds which rule, and what a second interface needs.

**How to read the marks.** *Not built* means that the text is the intent and the code does not do it yet. Appendix C lists all such items. Appendix D gives the dated history of the rulings that changed this document.

Reading order: Part I is the architecture. Part II is the Claude Code side. Part III is the Windows side. Part IV is the logic that joins them. Part V maps each mechanism to a phase.

---

## Part I — System architecture

### I.1 Logical components

The system is a set of roles. One process holds all of them today. A later phase can put one of them behind a network. The roles stay the same.

| Role | Responsibility | Depends on |
|---|---|---|
| **Ingress** | Receive lifecycle signals from Claude Code sessions and give them to Intake | A local endpoint |
| **Intake** | Change a raw signal into an internal *event*; stamp the arrival time | Ingress |
| **Session Registry** | The world model: the known sessions and, for each, its state, its latest exchange, its directory and its group | Intake |
| **Attention Engine** | Put the sessions into bands and into order | Session Registry |
| **Group Resolver** | Find the group of each session from facts that can be observed | Session Registry, the rosters |
| **Notifier** | The sound policy: notices and nudges, mute and pause | Session Registry |
| **Presenter** | Show the list, the bands, the exchanges, the counts and the tray light; accept Ack and the view mode | Attention Engine, Group Resolver |
| **Reporter** | Answer "what does the Registry believe now?" to a local caller (§IV.9) | Session Registry, Notifier |
| **Recorder** | Keep each event, and each decision that it caused, on disk (§IV.6) | Intake, Session Registry, Notifier |
| **Navigator** *(Phase 2, not built)* | Bring a session's terminal window and tab to the front | Windows layer |
| **Focus Observer** *(Phase 3, not built)* | Find which terminal the operator looks at, to infer an acknowledgment | Windows layer |

### I.2 The core principle: the world comes from events

**No API lists "all Claude Code sessions that run on this machine".** Claude Code does not show its sessions to an observer. Thus the dashboard builds its world from a stream of lifecycle events. A session is in the Registry because the system saw an event from it. It has a state because the last event gave it one.

Three results follow:

1. **A session that started before the dashboard is invisible until its next event.** Almost any event makes the session appear, not only a session start (§IV.1 gives the exceptions). A sweep that looks for Claude Code processes (§III.6) could show such a session earlier. *Not built.*
2. **A restart loses the world.** The Registry is in memory and starts empty. Each session appears again at its next event. A snapshot on disk that restores the world at start is the intent. *Not built.* The event log that it would read does exist (§IV.6).
3. **An event can arrive twice, or late.** Each state change must be *idempotent*, and must compare times (§IV.1). The same "finished" signal applied twice changes nothing.

### I.3 Data flow

```
 Claude Code session ──hook──▶ Ingress ──▶ Intake ──▶ Session Registry ──▶ Recorder
                                                          │
                        ┌─────────────────────────────────┼───────────────┬─────────────┐
                        ▼                                  ▼               ▼             ▼
                  Attention Engine                     Notifier      Group Resolver   Reporter
                        │                                  │               │
                        └──────────────▶ Presenter ◀───────┴───────────────┘
                                             │
                                    operator ▲│▼ (Ack, mute, group, view mode)
                                             │
                          (later) Navigator ─┘   Focus Observer ──▶ Intake (as ack events)
```

**Each change to the world goes through Intake, on one path.** An Ack from a click is an event. A mute is an event. In Phase 3, an acknowledgment that the Focus Observer infers is an event too. Nothing changes the Registry from the side. This is the property that lets the Registry have one writer and no locks, and it is the property that a second interface can use: it sends the same events. Intake is bounded, and when it falls behind it sheds only events that repeat information: a batch of tool calls, or a notification that changes no state. An event that can change what the operator sees is never shed for want of room (§IV.7).

### I.4 Deployment shape

One resident process that:

- always runs, so that it misses no event;
- has a receiving endpoint on the loopback interface only;
- has a tray light that stays when the window is closed;
- can show and hide its window with no loss of state.

The window is one *consumer* of the Registry. This separation is what lets a remote surface (Phase 7) be a second consumer.

---

## Part II — The Claude Code event layer

The system learns what each agent does through **Claude Code hooks**: handlers that Claude Code runs at fixed points in a session's life. A hook fires in each place where Claude Code runs: terminal, IDE extension, desktop app. Each hook gets a JSON payload that describes the event.

### II.1 Transport: how a hook reaches Ingress

**The transport is a command handler.** For each event, Claude Code runs a small script in the background and writes the payload to its standard input. The script sends the payload, unchanged, to the loopback endpoint.

Three rules make the script safe:

- **It finds the dashboard when it runs.** The dashboard writes a small file that says "a dashboard listens on this port now, and accepts this token". The script reads that file at each event. If the file is absent, the script stops and opens no connection. Thus the hook is correct when the dashboard is closed, when the port moves, and when the dashboard restarts.
- **It prints nothing.** For a prompt submission and a session start, Claude Code adds a hook's output to the model's context. One line of output would change each prompt in each session.
- **It always reports success.** One failure code shows an error to the operator. A different one stops the turn.

**The registration is a Claude Code plugin.** A plugin is the door that Claude Code gives to a different program's hooks. The dashboard keeps the plugin's files in its own data folder and asks Claude Code to register it. **The dashboard never writes Claude Code's settings.** That file belongs to Claude Code, which writes it too. A second writer can lose a change and can reformat the file, and its errors break Claude Code, not the dashboard. Where the plugin cannot be registered, the dashboard says so on screen and tells the operator what to do.

**Why not an HTTP handler.** Claude Code can post a payload to a URL directly, and the first builds used that. It was removed because:

- the URL holds the port, and the port must be free to move (one loopback port serves one user only);
- Claude Code needs the URL in an allow-list, in the settings that the dashboard must not write;
- with the dashboard closed, each event showed a connection error in the session.

**A second transport, for the plan's usage only: a mod** (Impl §9.5). No hook event carries the plan's usage figures. Claude Code gives them to a mod: a function in a plugin that Claude Code calls inside its own process, at the end of each turn. The dashboard's plugin carries one, which posts the figures to the loopback endpoint. It does not bring back the three faults of the HTTP handler:

- **The port is free to move.** The mod reads the port, with the token, from the same file as the script, at each call.
- **No allow-list.** The mod posts through Claude Code's own call for the network, and no setting was needed for a post to the loopback address.
- **With the dashboard closed, nothing shows.** With no file the mod opens nothing; with a port that nothing holds, its failure ends in its own `catch`.

It holds the script's three rules too: it reads the file when it runs, it prints nothing, and it never fails a turn, because it hands the event on as it came. It adds one rule of its own: **the post starts from a timer, outside the event,** because a post started inside it holds the end of a `claude -p` run until the dashboard answers. The endpoint keeps the newest reading of each limit, outside the world model of sessions: a limit belongs to the account. The state report shows them (§IV.9), and so does the window's caption (Impl §5.6.1).

### II.2 The events consumed, and what each means

The dashboard registers eight events.

| Claude Code event | Fires when | Meaning for the dashboard | Fields read |
|---|---|---|---|
| **SessionStart** | A session starts or resumes | Make the session known. No state change for a known session, but an Ended one lives again | `source` |
| **UserPromptSubmit** | A prompt is submitted | State → **Working**. Keep the prompt as the session's context line. A prompt that the operator typed is also an acknowledgment of what waited (§IV.1) | `prompt` |
| **Notification**, type `permission_prompt` | A permission dialog is shown | State → **Needs You — Permission** | `notification_type` |
| **Notification**, type `agent_needs_input` | Claude is blocked on an answer | State → **Needs You — Question** | `notification_type` |
| **Notification**, all other types | For example `idle_prompt`: nothing occurred for some time | **No state change** | `notification_type` |
| **Stop** | Claude finishes its response | State → **Unread**, or **Waiting** if background work still runs. Keep the answer | `last_assistant_message`, `background_tasks`, `session_crons` |
| **StopFailure** | The turn stops on an API error | State → **Error**. Keep the kind of error | `error`, or `error_type` if `error` is absent or not a string |
| **SessionEnd** | The session terminates | State → **Ended** | `reason` |
| **CwdChanged** | The working directory changes | Find the group again | None |
| **PostToolBatch** | A batch of tool calls is complete, before the next model call | The turn runs: a session that was blocked, in error or silent goes back to **Working** | None |

All events also give `session_id`, `prompt_id`, `cwd`, `transcript_path` and `session_title`.

**The dashboard reads only these fields.** It keeps the full payload in the event log, but it does not interpret the remainder. It never reads the `command` of a background task: that text can hold a prompt or a secret.

Two payload properties make the product possible:

- **UserPromptSubmit carries the prompt text.** The session's context line comes from the source. No scrollback is read.
- **Stop carries the last assistant message.** An expanded row can show the answer next to the question. Many checks end in the dashboard, and the operator does not open the terminal.

Two rules about what an event must *not* do, each learned from use:

- **`idle_prompt` is not a question.** `agent_needs_input` is a request. `idle_prompt` is the absence of one: Claude Code sends it because a session sat untouched, and each finished session does that. When the dashboard read it as a question, each Unread row became red and blinking about ninety seconds after it finished. Idleness already has its place: a result that nobody read is Unread, and one that was read is Quiet. The general rule is in the Design (§4): *an absence of activity must never make a session louder.*
- **An unknown type changes nothing.** Claude Code has twelve notification types and ten error kinds, and it can add more. A type that the dashboard does not know is kept in the log and changes no state.

**The kind of error comes from `error`, the field the wire sends.** Claude Code's documentation names the field `error_type`, but all 18 archived `StopFailure` events carry `error`, and none carries `error_type` (issue #67). Where the two disagree, the wire is the authority. The dashboard reads `error` first, and `error_type` only when `error` is absent or not a string, in case a later Claude Code follows its documentation. The row shows the kind as it arrives, so a kind the dashboard does not know still reaches the operator. It never reads `error_message`: that is prose about the operator's turn.

A second `StopFailure` with a different kind, on a session already in Error, changes the kind on the row. It is a real change, not a duplicate. The same kind twice is a duplicate.

### II.3 Correlation and identity

- **Primary key: `session_id`.** It is on each event. It is the Registry's key.
- **Grouping input: `cwd`.** It can change in a session. The group is found again when it changes, and is not fixed at the start. On a `CwdChanged` event, `cwd` is the directory of the session, which is not always the new directory: a change of directory for one command does not move the session.
- **Turn correlation: `prompt_id`.** It connects a `Stop` to the prompt that caused it. Measured on the operator's archive: 2,318 of 2,335 `Stop` events have the id of the session's last prompt.
- **The name: `session_title`.** It is the name that the operator gave the session, or one that Claude Code made. It arrives on few events and never on `Stop`. Thus the Registry keeps the last title that it saw (§IV.1).
- **Fallback: `transcript_path`.** The full conversation on disk. It is written late and can be behind the live turn, so the dashboard does not read it.

### II.4 What is not available

- **No list of sessions.** See §I.2.
- **No "which window and tab is this session in".** The event gives the session and its directory, not its place on screen. To find the window is the Windows layer's task (Part III).
- **No sequence guarantee, and no exactly-once delivery.** See §IV.1.
- **No event for an interrupt.** When the operator stops a turn, Claude Code sends nothing. The dashboard can measure only silence (§IV.1).
- **No event for an approval.** When the operator approves a permission, Claude Code sends nothing. The dashboard infers that the turn continues from the next `PostToolBatch`.

### II.5 Security at the boundary

The endpoint is a local attack surface, and a payload is content that the dashboard cannot trust.

- **Loopback only.** Nothing outside the machine can reach the endpoint. A remote surface (Phase 7) is a *separate, authenticated* surface on top of the Registry. It is never this endpoint opened to the network.
- **A shared secret, always.** The dashboard makes a new token at each start and holds it in memory. It gives the token to the hook script through the same small file that holds the port. Each request but the liveness check must carry it. The token stops a process of a different user on the same machine. A process of the same user can read the file, and can read the event log too.
- **Event text is data, never an instruction.** The dashboard stores and shows prompt text, answer text, titles and task descriptions. It never interprets them as commands, and it never writes them to a log.

---

## Part III — The Windows integration layer

This part says how the system can observe the desktop (to infer that the operator looked at a terminal) and act on it (to go to a terminal). **Almost all of it is for later phases and is not built.** It is specified now because it constrains the earlier design: the Registry must keep sufficient text of each exchange to recognise a tab by its content.

What is built from this part: the tray light and the sound (§III.10), the single instance, the start with Windows and the rule of no elevation (§III.11), and the pin of the window to all virtual desktops (§III.9).

### III.1 The two hard problems

1. **Observation:** "Does the operator look at session S's terminal now?" (Focus Observer, Phase 3.)
2. **Action:** "Bring session S's terminal window and tab to the front." (Navigator, Phase 2.)

Both are one mapping problem: **session ⇄ window and tab on screen.** The Registry holds the session side. Windows holds the screen side. The approach is **content-matching** (§III.2).

### III.2 The content-matching join

The system recognises a session's tab by **what the tab contains**. The Registry holds each session's latest exchange. The Windows layer reads a tab's visible text through UI Automation (§III.4) and compares it with the Registry.

- **What to compare:** the most recent prompt line is the strongest signal. The answer and the directory in the prompt support it.
- **Ambiguity:** if two tabs show the same recent text, there is no match. The system then works at the level of the window (§III.7) and does not guess.
- **Cost:** to read a tab's text costs more than to read a title. It occurs only at a click or at a change of focus, not continuously.

Content-matching needs **no cooperation from the terminal title, no identifier written anywhere, and nothing from Claude Code** but the text that the Registry already has.

### III.3 Identifiers that the join does not use

- **The terminal title.** Claude Code writes the full title again at each render and gives no way to add to it. A hook cannot set it either. The system leaves the title alone and writes nothing to it.
- **The pane session GUID.** Windows Terminal gives each pane a GUID in its environment. Windows gives no public way to ask "which tab has this GUID". The GUID can *correlate* and cannot *locate*.
- **The process tree.** For a classic console, one process is one window. For Windows Terminal, one process holds many windows and tabs, and there is no path from a process to a tab.

### III.4 Reading the desktop: UI Automation

Windows Terminal draws its own tab strip. A tab is not an OS window. To see tabs, the system uses **UI Automation (UIA)**, the accessibility API:

- The tab strip shows which tab is **selected**. Each tab's content can be read as text.
- To find a tab: go through the terminal window's UIA tree, read each tab, and compare with the Registry.

UIA is the fragile part: it depends on the terminal's accessibility tree, which can change between terminal versions. All UIA is behind an adapter, with one hard rule: **a UIA failure gives window-level behaviour, never a crash** (§III.8).

### III.5 Observing focus

To know when the operator goes to a window, the system installs a system-wide hook for the foreground-changed event. The thread that registers it must run a message loop.

**A change of tab in one terminal window does not change the foreground window.** Thus the Focus Observer has two tiers:

- **Window focus** (the foreground event): cheap and reliable. It says that the operator is in *some* terminal window.
- **Tab focus** (a UIA selection change): fragile. It says *which tab*, and through content-matching, which session.

Inference: the foreground stays on a terminal window → read the focused tab → match it to a session → if the focus stays for a short time, send an acknowledgment event into Intake (§I.3). The wait prevents an acknowledgment of each tab that the operator only passes.

### III.6 Reconciliation sweep *(optional; not built)*

To show sessions that are older than the dashboard, the system can look at the running processes, find the Claude Code instances, and make placeholder entries ("known, waiting for the first event"). This adds to the event stream and does not replace it.

### III.7 Locate strategy for each terminal type

For a **terminal with no tabs**, the process tree is a valid and cheaper path: one process, one window. For a **terminal with tabs**, content-matching is necessary. Window-level activation is the fallback for both.

### III.8 Acting on the desktop

Two levers, in this sequence:

1. **Ask the terminal.** Windows Terminal accepts a command that focuses a tab of an existing window, by window id and tab index.
2. **Direct activation.** If that is not possible, bring the window to the front with the OS call and select the tab through UIA.

**Foreground lock.** Windows limits which process can take the foreground. The dashboard is exempt by construction: the operator clicks a row in the dashboard's own window, so the dashboard has just received input and may set the foreground.

### III.9 Virtual desktops

The operator uses one virtual desktop for each task. Thus the virtual desktop is the truest grouping key (Phase 4, not built). Windows has two tiers:

- **Documented tier:** which desktop a window is on, whether a window is on the current desktop, and a move of a window to a desktop. This is sufficient to **group sessions by desktop**.
- **Undocumented tier:** to list desktops, to switch to one, to read their names, and to **pin a window to all desktops**. These need an internal interface whose identifier changes between Windows builds. The rule: put this tier behind an adapter, pin the version, and **degrade without a crash**.

Built today: the dashboard pins its own window to all desktops through the undocumented tier. If the pin fails, the window stays on one desktop and the log says so.

### III.10 Tray presence and audio

- **The tray light** is a status icon that always shows. Its colour is the worst state of all sessions, with a roster group counted once, by the state its heading shows (Impl §2.7; T1.83, issue #130), in five colours (§IV.3 gives the order): red for a permission, amber for an error or a question, green for unread, blue for working, grey for quiet. It has no digits and it does not move. The counts are in its tooltip. A click shows the window.
- **Audio** plays notices and nudges. The schedule does not depend on the platform (§IV.5). Only the playback does.

### III.11 Housekeeping

- **One instance.** A second start asks the first to show its window, and exits.
- **Start with Windows.** The dashboard starts when the operator signs in. The operator can turn this off.
- **No elevation.** Terminals and shells run at the operator's normal level. An elevated dashboard could not inspect them.

---

## Part IV — Cross-cutting logic

Rules that do not depend on the platform.

### IV.1 The session state machine

Nine states:

| State | Meaning |
|---|---|
| `Working` | Claude works on the turn |
| `Waiting` | The turn ended, and background work of the session still runs. Claude Code will wake the session when that work reports |
| `NeedsYou.Permission` | Blocked on an approval |
| `NeedsYou.Question` | Blocked on an answer |
| `Error` | The turn stopped on an error |
| `Unread` | Finished, and not seen |
| `Acked` | Seen. Also: started, and nothing typed yet |
| `Interrupted` | Working, and silent for the threshold |
| `Ended` | The session terminated |

**Transitions for a known session:**

```
 (any)   ──UserPromptSubmit────────────▶ Working
 (live)  ──Notification(permission)────▶ NeedsYou.Permission
 (live)  ──Notification(needs input)───▶ NeedsYou.Question
 (live)  ──Notification(any other)─────▶ no change
 (live)  ──Stop, no background work────▶ Unread
 (live)  ──Stop, background work runs──▶ Waiting
 (live)  ──StopFailure─────────────────▶ Error
 NeedsYou.* / Error / Interrupted ──PostToolBatch──▶ Working   (Waiting, if the session waits)
 Unread / NeedsYou.* / Error ──Ack─────▶ Acked
 Working ──no event for 10 minutes─────▶ Interrupted
 (any)   ──SessionEnd──────────────────▶ Ended
 Ended   ──SessionStart────────────────▶ Acked
 (live)  ──SessionStart / CwdChanged───▶ no change of state; the group can change

 (live) = any state but Ended.  An Ended session reacts only to SessionStart and UserPromptSubmit.
```

**The first event of an unknown session** makes the session: `SessionStart` or `CwdChanged` gives Acked; the others give the state in the table. `PostToolBatch`, an Ack, and a notification that changes no state do not make a session.

**Three guards.** Delivery is at-least-once and the sequence can change. Each guard covers a case that the others cannot:

1. **The time guard.** An event that is older than the session's last activity is dropped.
2. **The correlation guard.** A `Stop` whose `prompt_id` is different from the id of the session's current exchange is refused. A late copy of a `Stop` gets a new, later arrival time, so the time guard cannot catch it. Without this guard, such a copy would pull a session that works back to Unread, with a false "finished" sound.
3. **No effect, no trace.** An event that would leave the session as it is changes nothing: no state change, no sound, and no change of the session's place in the list.

**Why `Stop`, `Notification` and `StopFailure` apply from any live state.** The usual permission flow is: Working → permission → the operator approves in the terminal → Claude finishes → `Stop`. An approval is not a prompt. If `Stop` applied only from Working, the session would stay in the loudest band permanently.

**Why `PostToolBatch` resumes.** No hook says "the operator answered". The proof that a turn continues is that the session does work again, and `PostToolBatch` is that proof. It covers a permission, a question and an error that recovers. It fires one time for each batch, not for each tool call. **It must never resume `Unread`:** to un-read a finished session is the quiet, worse mirror of the `idle_prompt` defect. *Accepted residual:* between the approval and the end of the tool call, the row stays red.

**`Waiting`.** A turn that ends while a background command or a background agent still runs is not finished: Claude Code will wake the session when the work reports. Before this state existed, such a turn played "finished" while the agent still waited.

- Only work of an allowed kind counts: a background command and a background agent. A long-lived watcher does not count, because it may never report. A kind that the dashboard does not know does not count, and the decision record says that it was seen.
- Any prompt ends the wait.
- While a session waits on a background agent, that agent's own permission prompt, question or error arrives under the parent's id and outranks Waiting. The next `PostToolBatch` then puts the session back to Waiting, not to Working.
- Waiting is work: it is in the Working band and is blue. It is calm: it does not move, it makes no sound, and it is never nudged. The silence rule does not apply to it.

**`Interrupted`.** A Working session that sent no event for ten minutes stops its claim to be busy.

- **It is silence that is observed, not an interrupt.** A tool call that is longer than the threshold looks the same. The name is the operator's word for the usual cause.
- **It only makes a session quieter.** It applies to Working only. A session that asks for the operator, and goes silent, still asks.
- **Any event leaves it.** A session that was marked wrongly corrects itself when it speaks.
- **The threshold is ten minutes, and it is a guess.** Each such change is logged with the silence that caused it, so that the value can be changed from evidence. There is no setting.

**Which prompt is an acknowledgment.** "The operator cannot type a new prompt and not see the last result" is true only of a prompt that somebody typed. Claude Code also submits prompts: a notice that background work is complete, a message from a different session, an idle notice, a message from an agent, and a scheduled job of the session itself. Each of these moves the session to Working, because the work did start again. None is recorded as an acknowledgment. They are recognised by a fixed prefix, or, for a scheduled job, by the structure in the next paragraph. The text is never interpreted.

**A prompt that continues the work.** The notice that background work is complete does not start a new piece of work. The session keeps the text and the start time of the exchange, so "You asked" still shows the operator's question and the working clock does not start again.

**The quiet tick.** A session can schedule a job for itself, for example a check each 30 minutes. Each run is a turn, so each run would play "finished".

- A prompt is a **tick** if it is exactly equal to the prompt of one of the scheduled jobs that the session listed at the end of its last turn. This is structure, not keywords.
- A tick is **quiet** if the full reply is the one agreed word. The job's prompt asks for this, so the feature is opt-in for each job.
- After a quiet tick the row goes back to what it showed before the tick: the same state, the same answer, the same entry time, and thus the same place in its nudge schedule. No sound plays.
- Any other reply is an ordinary reply. Thus an error costs one more sound, and can never make an escalation silent.

The guide for the operator is [Quiet scheduled jobs](quiet-scheduled-jobs.md).

**The title.** The Registry keeps the last title that is not empty and that is different from the one it holds. This runs for each event, also for an event that changes no state, because the events that carry a title are mostly such events. A title never changes a session's place in the list or its clock. A title cannot be removed: nothing on the wire says "no title".

**What each session carries:** the latest exchange (prompt, answer, the times of each), the time it entered its state, the time of its last change, the time an event last arrived, its directory, its group key, its title, the kind of its error, and the background work it waits on.

**The row's clock** is a display rule and is in the Implementation Specification (§5.6). One point is a rule of the domain: **an acknowledgment or a close never starts the clock again.** A row that finished four hours ago still says so after an Ack.

### IV.2 Attention banding and ordering

Bands, top to bottom:

| Band | Members | Order in the band | Reason |
|---|---|---|---|
| Needs You | `NeedsYou.Permission`, `Error`, `NeedsYou.Question` | **By kind first: Permission, then Error, then Question. Then oldest first in each kind** | The blocker that is cheapest to clear comes first |
| Unread | `Unread` | **Newest first** | After a sound, the newest finish is the one that the operator looks for |
| Working | `Working`, `Waiting` | Most recent change first | |
| Quiet | `Acked`, `Interrupted` | Most recent change first | Sinks; can collapse |
| Ended | `Ended` | Most recent change first | Dim. Removal after a short time is *not built*: an Ended session stays until the dashboard starts again |

The asymmetry is deliberate, and it is the centre of the attention model: **reds sort by rising age, greens by falling age.** A blocked session earns attention the longer it is blocked. A finished session is looked for immediately after its sound.

**The kind order makes sub-bands, not tie-breaks.** A Question that is blocked for twenty minutes is *below* a Permission that is three minutes old. The operator chose this over the alternative (age first, kind for equal ages) with both shown.

**No separate idle state.** A session that started and did nothing is `Acked`, the same as one that was seen. Both sort by recency, so the order does not change. A separate state would cost a change to the transition table, the rank table, the sound mapping and a stored value, for one edge case. This is settled.

Each order is total: the last tie-break is the session id.

In the **grouped** view this order runs *in* each group. Groups are in the order of their state (§IV.3), then their most recent change. In the **flat** view the bands are global and have headings.

### IV.3 Grouping and derivation

**One severity order** serves the bands, the groups and the tray light:

`NeedsYou.Permission` > `Error` > `NeedsYou.Question` > `Unread` > `Working` > `Waiting` > `Interrupted` > `Acked` > `Ended`

The reason is throughput, not age. A permission is usually seconds of operator time that hold an agent for an unlimited time. An error is next: it stays stopped until somebody looks. A question is the softest: it can need thought, and that thought unblocks nothing else.

- **Group state** = the worst state of its members.
- **Group recency** = the most recent change of a member.
- **The key** is the working directory. Case, the direction of separators and a separator at the end do not make two groups. A session with no directory is a group of one. *Phase 4, not built:* the virtual desktop as the key.

**Grouping mirrors what can be observed. The dashboard never invents membership.**

**Rosters.** The operator can define a roster: a named set of session names. A session whose title is in a roster is grouped by that roster, wherever it runs. A roster group outranks the directory group, because to gather sessions that directories scatter is its purpose.

- The operator's hand reaches the *rule* and never the membership: a roster matches names that the sessions report. A rename moves a session in or out with no restart.
- The match is exact. A session with no title can be in no roster.
- A name is in one roster at most. A roster with no members does not exist.
- A group that the operator forms exists immediately. It survives a restart only if the operator asks the dashboard to remember it.
- To remove a member removes its name from the roster permanently. The session goes back to its directory group.

**A roster group is one piece of work that passes between its members.** Thus:

- **In a roster group, `Working` and `Waiting` outrank `Unread`.** One member that finishes while a second works is a hand-off, not a result.
- **The settle window.** In a hand-off there is a moment when no member works. A roster group reads finished only after each member is quiet for **1.5 seconds**. Until then it reads Working. The value is a start value, not a measured one.
- **The check on that value.** A group that reads finished and goes back to work in **5 seconds** wrote a false "finished". The log says so, and that line decides if 1.5 seconds holds.
- **One sound for the group** (§IV.5), and **one Ack for the group** (Design §4).

### IV.4 Space, staleness, collapse

Rows are the scarce resource. The rules, in priority:

1. **A group that is fully quiet becomes one line** after 15 minutes with no change: name, count of members, idle time. It can be opened.
2. **Quiet rows in a live group become one "+ k quiet" line.**
3. **An Unread row always keeps a full row.** Work that is finished and not seen is what gets lost today. It is never summarised.

In the flat view, the Quiet band and the Ended band are each one line that can be opened.

"Quiet" here means the Quiet band and the Ended band, and nothing else.

### IV.5 Sound policy engine

A **notice** is the first sound for an event. A **nudge** is a reminder.

- **Notices** play when a session enters a state. Four states have one, each with its own sound: finished (Unread), permission, question, error. Working, Waiting, Acked, Interrupted and Ended have none.
- **Nudges** play for a session that stays in **`NeedsYou.Permission`, `Error` or `NeedsYou.Question`**: the *same melody, softer*, after 2 minutes, then 5, then 10. The last interval repeats. Never louder, never faster. If the dashboard was blocked and a nudge is late, one nudge plays, not all that were missed.
- **Unread** gets one soft nudge after 5 minutes, and no more.
- **A roster group** makes one finished sound when it settles (§IV.3), and one soft nudge. The finished sound of a member is not played. A member's permission, question and error sounds are not changed: they are about that member.
- **A roster group plays only when it has something new to announce** (the operator's ruling of 2026-10-04, issue #107). A member's finish is announced when its own finished sound was made (played, held back by a mute or a pause, or dropped for want of a device) or when a group's settle announced it. When every finished member is already announced, the settle makes no sound and no nudge, and the decision record says "announced before". So a roster made, renamed or joined after its members finished and sounded adds no sound, and each member keeps its own nudge. The one sound a roster owes is for a member that was still working when it joined. Removing a roster's working member, when its other members finished inside it, plays once: the roster held their sound.
- **A quiet tick** (§IV.1) makes no sound and leaves the nudge schedule where it was.
- **Mute all** makes all sound stop, for 30 minutes or until the operator ends it. The tray light stays true.
- **Pause** makes all sound stop until the operator resumes, and makes the tray light grey and visibly "off". This is the one deliberate exception to "the tray tells the truth". Pause does not survive a restart.
- **Mute for one session or one group.** The engine has it. No control reaches it. *Not built.*
- **Suppression (Phase 3, not built):** no notice for a session that the operator looks at.

Mute is a filter on the output, not a stop of the schedule. A muted session's schedule goes on silently, so that an unmute does not release a backlog.

The engine keeps a due time for each session and plays what is due when it is asked. It starts no timer. An acknowledgment clears the due time. Thus there is nothing to cancel, and no race between a nudge and an Ack.

### IV.6 Persistence

- **In memory only:** the Registry, the bands, the nudge schedule, the mute and pause modes, a roster that the operator did not ask to remember.
- **On disk:** the operator's settings; the rosters that the operator asked to remember; the place of the window; and an **event log**.
- **The event log** is append-only. It holds each event with its full payload, and a **decision record**: each judgement that the dashboard made (a state change, a refusal, a sound played, a sound not played and the cause), next to the event that caused it. It answers "why did that sound play?". The Activity window shows what the dashboard decided since it started, in plain words, with the session's name on each line (T1.70, issue #97). It is a log in memory, fed by the same decisions the record receives; it never reads the event log.
- The decision record holds identifiers and names, and the session's name and full path in columns of their own (T1.69, issue #98): a row says which session it is about without a search for the session's history. It never holds a prompt or an answer, and the session's name is in no other column. The event log holds the session's name beside each event too.
- **The dashboard reports on itself while it runs:** what it counted (events applied and declined, events and records dropped, posts refused, records not written, what the loop did) can be read at any moment, and once an hour a summary goes into the event log. A gap between summaries shows when the dashboard was not running.
- **The event log records each run of the dashboard:** when it started and stopped, its version and its port. A crash or a kill shows as a start with no stop. A replay of the log forgets every session at each start and at each clean stop, as the live dashboard does.
- **A restart from the log** is the intent. *Not built.* So is search of the history (Phase 5).
- **The log keeps as many days as Claude Code keeps its own sessions:** Claude Code's `cleanupPeriodDays`, read from Claude Code's settings at each prune, and 30 days when the key is not there (T1.68, issue #102). Why: the log holds a copy of what Claude Code sent, so it must not keep text that Claude Code has deleted. Before, the log had a setting of its own (T1.64), and the two numbers could disagree. When Claude Code's settings cannot be read, or the value is one that Claude Code would not use, nothing is deleted: Claude Code pauses its own cleanup in the same cases. It is pruned at each start and once a day. The space of the deleted rows is used again, so the file stops growing and does not shrink. A key in the settings file that a version does not know is kept when that version saves.

### IV.7 Degradation ladder

Each capability fails soft. The product continues with less.

| If this fails… | …the system does this | The product still |
|---|---|---|
| The dashboard does not run | The hook script finds no announcement and stops | Leaves each Claude Code session untouched. The events of that time are lost |
| No free port | A port held by another program is skipped, and the next one is tried. With no free port, or the pinned port held, the dashboard starts and announces nothing. The log, the tray and the window say what to do: free a port, or change the pin, then restart. Nothing retries | Shows its window; receives nothing |
| Claude Code is not connected | The dashboard shows a notice with what to do | Runs; receives nothing until it is connected |
| Messages from Claude Code cannot arrive, or are refused | A self-test at each start runs the hook script and checks that its message arrives, and refusals that go on are counted. The window and the tray say so, with the cause | Runs. The tooltip says when it last heard from Claude Code, as information and never as an alarm |
| The event log cannot be written | The dashboard writes one warning when it fails (not for a failed retry), says so in the window and the tray, and tries again each minute, each time on a new connection to the file: a connection that failed is never used again. A file that another program holds for a moment when the dashboard opens it costs nothing: the dashboard opens it again a few times within a second first | Shows and sounds as usual. The events of each minute that cannot be written are lost |
| The sound device fails | Silence, a log line, and a notice in the window and the tray: no sound device. The record says each sound was dropped, not played | Shows as usual. A device that is listed, active and silent (the volume at zero, a monitor with no speakers) cannot be told apart from one that works |
| The settings file cannot be read | A file that does not parse is renamed to `settings.error-<time>.json`, a fresh file with the defaults is written, and the window and the tray say so. A file that cannot be opened is left alone, and nothing is saved until a restart | Runs on the defaults. This start registers no plugin and leaves start with Windows as it found it |
| Intake falls behind | Only events that repeat information are shed, and the window says it fell behind. If even state-changing events flood, the oldest are dropped and the window says events were lost | Shows every permission, question, error and finish that arrives. A row may lag until its session's next event |
| The pin to all desktops fails | The window is on one desktop | Runs |
| The reconciliation sweep *(not built)* | The event stream only | Shows each session from its next event on |
| Tab-level UIA *(later)* | Window-level focus and activation | Acknowledges and goes to the window |
| Content match is ambiguous *(later)* | Window-level activation | Goes to the window |
| Desktop switching *(later)* | Window activation, and grouping from the documented tier | Goes to the session; groups correctly |
| Focus inference *(later)* | Manual Ack and the Ack from a new prompt | Phase 1 acknowledgment |

Phase 1 is at the bottom of each ladder and works alone.

**A lost feature must still show on screen.** The event log once wrote one warning and stopped until the next start, and nothing on screen changed: the operator found out days later, looking for a record that was not there (issue #71). Now the window and the tray say "history not recorded", and the log tries again each minute, by the operator's ruling. A disk that is full for a minute costs a minute of history, not the rest of the day. The events of that minute are lost, not held in memory: a queue would keep the operator's words for as long as the disk stays full.

**A silent dashboard must say that it is silent.** Sound is how the dashboard gets the operator's attention, so a dashboard with no output device that looks healthy is the failure it exists to prevent (issue #72). The window and the tray say "no sound device", by the operator's ruling of 2026-10-03: a notice row and a tooltip line, and no mark on the tray icon, which keeps its five colours. The record tells the truth too: a sound the player dropped is recorded as dropped, not as played. The sound rules do not change: a dropped notice still counts as announced, and nothing is replayed when a device returns, because a stack of old sounds at that moment is noise.

**The operator's settings are never overwritten.** One wrong character in the dashboard's own `settings.json` used to put it on its defaults, with one log line as the only sign, and the next save wrote the defaults over the file (issues #73 and #26). By the operator's ruling of 2026-10-03, a file that does not parse is renamed, byte for byte, a fresh file with the defaults takes its place, and the window says where the old one is. The start that does this registers no plugin, because an opt-out may be in the file it could not read. A file that cannot be opened at all may be perfectly good, so it is left alone, and the dashboard saves nothing until it restarts.

### IV.8 Threat surface summary

- The endpoint listens on loopback only. A token is necessary, always.
- Event text is display data. It is never executed. The log file may hold it, as the event log does (the operator's ruling of 2026-10-05); the token is never logged.
- No elevation.
- The dashboard never writes Claude Code's settings.
- Remote access (Phase 7) is a separate authenticated surface on the Registry. It is never the raw endpoint opened to the network.

### IV.9 The state report

A local caller can ask what the Registry believes now. The answer is one entry for each session (state, band, group, directory, title, times, kind of error, the background work it waits on, the time of its next nudge), then a count for each band, and the tray light. Since MOD.5 (issue #133) it also gives the plan's usage, the newest reading of each limit that has not reset, as the usage mod last posted it, or nothing before the first post. Since MOD.7 the window shows the same figures in its caption, beside the counts (Impl §5.6.1).

- It is read-only. It changes no session and no setting.
- It needs the token.
- **It never carries a prompt or an answer.** It does carry titles and descriptions of background work, by the operator's ruling, so that a caller can tell sessions apart.
- **The usage figure can be behind the true one,** and it is of one account. A reading arrives only when a session ends a turn, and other apps use the same limits; each reading says when it arrived. The post names no account.
- It is for tests and diagnosis. It is not a complete read model for a second interface: see [Core and App](claude-dashboard-core-and-app.md) §6.3.

---

## Part V — Mechanism-to-phase map

| Phase | Theme | Claude side | Windows side | Cross-cutting |
|---|---|---|---|---|
| **1** | See clearly | Command hook, through a plugin, for the eight events of §II.2 | Tray light; audio; one instance; start with Windows; the pin to all desktops | The state machine; banding; grouping by directory and by roster; collapse rules; notices and nudges; the event log and the decision record; the state report |
| **2** | Go there | — | UIA tab enumeration and content-matching; the terminal's focus command; direct activation | Navigator; locate strategy for each terminal |
| **3** | It notices | Acknowledgment events from focus | Foreground hook; UIA selection events; the dwell time | One path for all acknowledgments; no notice for a session on screen |
| **4** | Task lens | — | Grouping by virtual desktop; desktop names | The group key becomes the desktop |
| **5** | Memory | — | — | Search of the history; statistics; a restart from the log |
| **6** | Polish | — | — | Settings interface; sound editor; themes |
| **7** | Anywhere | — | — | An authenticated remote surface as a second consumer of the Registry |

Phases 2 to 7 are not built.

---

## Appendix A — External surfaces, and how stable each is

| Surface | Kind | Stability | How it is isolated |
|---|---|---|---|
| Claude Code hook events and payloads | Documented product API | Changes, and is documented. The documentation and the wire disagree in places: see the [hooks reference](claude-code-hooks-reference.md) | A thin mapper that reads a fixed list of fields and tolerates the remainder |
| Claude Code's plugin commands | Documented product API | Measured on one version | One adapter; a notice on screen when it fails |
| Claude Code's mods API: the `session.measure` event, and the calls `$.clock`, `$.fs`, `$.http`, `$.session` | Documented product API, **early access**: "this surface may change between releases without notice" | Measured on 2.1.294 (Linux) and 2.1.293 (Windows); a plugin name that starts with `claude-` fails `claude plugin validate` since 2.1.287 (issue #134) | One small file with one event and four calls. `build.ps1` runs `claude plugin validate` and `claude plugin test` on it. A mod that does not load costs the usage reading only |
| Claude Code's settings file | A file that Claude Code owns | The dashboard reads three keys | Read only, and tolerant: a file that will not parse is "cannot be read" |
| UI Automation tree and text of the terminal *(later)* | OS API over an application's UI | Depends on the terminal version | An adapter; window-level fallback |
| Foreground-changed hook *(later)* | OS API | Very stable | — |
| The terminal's window and tab command *(later)* | Documented | Stable | Used where possible |
| Virtual desktop, documented interface *(later)* | OS API | Stable | The primary source for grouping |
| Virtual desktop, internal interface | Undocumented | Changes with the Windows build | A pinned wrapper behind an adapter; failure is a lost convenience |
| Tray and activation calls | OS API | Very stable | — |

## Appendix B — Open technical questions

- **Content-match disambiguation.** If two tabs show the same recent text, the join has no answer and falls back to the window. Is that acceptable, or is a light disambiguator necessary later? Not built now.
- **Answers from the dashboard.** To type a reply into a session from the panel needs a write path into the terminal. That pulls the tool toward a terminal front end. Deferred past Phase 3.
- **Subagents.** Partly answered. A background agent's events arrive under the parent session's id, and the Waiting state covers a parent that waits on one. The dashboard does not show a subagent as its own row. Open: should it?
- **Queued prompts.** Claude Code lets the operator queue messages. Show a "queued" hint on a Working row? From which signal?
- **Unread that is never acknowledged.** Today it stays Unread until it is acknowledged or the dashboard restarts. Fade it after some hours?
- **The relation to ClaudeSessions.** Does this project absorb that one? Its session addressing could serve Phase 2 navigation.
- **Several observers.** Today "seen" is a state of the session, so there is one observer. Two people, or two devices with separate acknowledgments, need "seen" to be a fact about an observer. See [Core and App](claude-dashboard-core-and-app.md).

## Appendix C — Specified and not built

One list for all the documents. Each item is marked *not built* where it appears.

| Item | Where it is specified | State of the code at `0488527` |
|---|---|---|
| Removal of an Ended session after a short time | §IV.2; Design §4, §5 | Nothing removes a session. It stays until the dashboard restarts |
| A restart that restores the world from disk | §I.2, §IV.6; Impl Part 8 | The Registry starts empty. The event log exists and nothing reads it at start |
| Mute for one session or one group | §IV.5; Design §8; Impl Part 7 | The sound engine has both. No event and no control reaches them |
| Settings for the nudge intervals, the Unread nudge, the stale time, the sound choice, the default view | Design §8; Impl Part 8 | Fixed values. The settings file has four sound values only (Impl §8.2) |
| A control for always-on-top | Impl §5.4 | A key in the settings file only |
| A count badge on the tray light | Design §9 | No digits. The counts are in the tooltip |
| The reconciliation sweep | §III.6 | Not built |
| A restart of the dashboard after a crash | Earlier text of Impl §10.1 | Given up by ruling when the start moved to the `Run` key (Impl §10.1) |
| "Open terminal" on an open row | Design §9 | The button is in the markup and is hidden until Phase 2 |
| Navigator, Focus Observer, grouping by desktop, history search, the settings interface (but one checkbox), the remote surface | Part III, Part V | Phases 2 to 7 |

## Appendix D — Change history

The text above says what is true now. This list says when each rule changed, for a reader who meets an older statement in a commit, an issue or a comment in the code.

| Date | Change | Source |
|---|---|---|
| 2026-08-22 | v0.2. The join between a session and its tab is content-matching. The terminal title is left untouched | — |
| 2026-08-24 | `idle_prompt` changes no state. Before, it gave Needs You — Question, and each finished session went red | Issue #1 |
| 2026-08-24 | `Stop`, `Notification` and `StopFailure` apply from any live state, not from Working only | T1.2 |
| 2026-08-24 | One severity order: Permission > Error > Question. §IV.2 and §IV.3 had disagreed about Error | Operator's ruling |
| 2026-08-24 | No separate idle state. `Acked` covers "started, nothing typed" | Operator's ruling |
| 2026-08-24 | Error nudges, like the two Needs You states | T1.5 |
| 2026-08-25 | `PostToolBatch` resumes a blocked or failed session | Issue #2 |
| 2026-08-30 | Rosters, the roster severity order and the settle window. Before, "the operator never assigns groups by hand" | T1.25, T1.26; issue #16 |
| 2026-08-30 | The command hook replaces the HTTP hook. The hook names a script, and no port is in Claude Code's settings | T1.28; issue #29 |
| 2026-08-31 | The `Interrupted` state and the silence threshold | T1.30; issue #28 |
| 2026-09 | The event log gains the decision record | T1.37; issue #48 |
| 2026-09 | A notice of complete background work continues the exchange | T1.40; issue #51 |
| 2026-09 | The `Waiting` state. A prompt that nobody typed is not an acknowledgment | T1.41; issue #52 |
| 2026-09 | The quiet tick | T1.44; issue #56 |
| 2026-09-29 | The state report. An Ack or a close never restarts the row's clock | T1.46, T1.47; issues #10, #59 |
| 2026-09-30 | The token is made at each start and travels in the announcement file. An environment variable is no longer used | T1.48; issue #57 |
| 2026-10-01 | The hook is registered as a Claude Code plugin. The dashboard never writes Claude Code's settings. Start with Windows | T1.49, T1.50, T1.51; issues #30, #36, #65 |
| 2026-10-02 | v0.3. This document is written again to agree with the code. The dated correction blocks became this table | — |
| 2026-10-03 | The kind of an Error row is read from `error`, the field the wire sends. A second error of another kind changes the row | T1.53; issue #67 |
| 2026-10-03 | The event log tries again each minute, and the window and the tray say when history is not recorded (§IV.7). Before, it stopped until the next start. §II.2: `error_type` is also read when `error` is not a string | T1.54; issue #71 |
| 2026-10-03 | No sound device is a notice in the window and the tray, and the record says a dropped sound was dropped (§IV.7). The event log writes one warning when it fails, not for a failed retry | T1.55; issue #72 |
| 2026-10-03 | A settings file that does not parse is kept aside and a fresh one written, and the window and the tray say so (§IV.7). Before, it was "left as it is" until the next save wrote the defaults over it | T1.56; issue #73 |
| 2026-10-03 | A port that is taken says what to do, in the tray and the window. A program on the port the dashboard last used no longer leaves it deaf: it tries the next port. A start whose settings were unreadable leaves start with Windows as it found it (§IV.7) | T1.57; issue #14 |
| 2026-10-03 | Intake sheds only events that repeat information when it falls behind, and says so (§I.3, §IV.7). Before, it dropped its oldest event, which could be a permission prompt | T1.58; issue #3 |
| 2026-10-03 | The event log records each start and stop of the dashboard, and a replay forgets every session at each start and each clean stop (§IV.6). Before, a replay ran the whole history as one run | T1.60; issue #78 |
| 2026-10-03 | The dashboard tests the path from Claude Code at each start and on request, and says when messages cannot arrive or are refused; the tooltip says when it last heard from Claude Code (§IV.7) | T1.61; issue #74 |
| 2026-10-04 | The event log keeps 30 days by default, a setting; it is pruned at each start and once a day, and does not shrink (§IV.6). Before, it was never pruned; the "retention not built" row of Appendix C goes | T1.64; issue #81 |
| 2026-10-04 | The dashboard reports its counts at any moment and writes a summary into the event log each hour (§IV.6) | T1.65; issue #76 |
| 2026-10-04 | The event log keeps as many days as Claude Code's `cleanupPeriodDays` (30 when the key is absent), read at each prune; a file that cannot be read, or a value Claude Code would not use, deletes nothing. The log's own setting is no longer used (§IV.6) | T1.68; issue #102 |
| 2026-10-04 | The event log and the decision record store the session's name, and decisions its full path, as they were when the row was written (§IV.6) | T1.69; issue #98 |
| 2026-10-04 | The Activity window shows the decisions since the start in plain words, from memory, never from the event log (§IV.6) | T1.70; issue #97 |
| 2026-10-05 | Event text may appear in the log file; it is still never executed, and the token is never logged (§IV). Before, event text was never logged | T1.76; issue #118 |
| 2026-10-04 | The event log's retry uses a new connection each time, and a file held for a moment at the open is opened again a few times within a second (§IV.7). Before, each retry could get the same connection back, which could not write, until a restart | T1.74; issue #109 |
| 2026-10-04 | A roster group plays only when it has something new to announce: a settle in which every finished member already announced is silent, with no nudge, and recorded as announced before (§IV.5) | T1.72; issue #107 |
| 2026-10-08 | The tray light counts a roster group once, by the state its heading shows, so it is blue while an orchestration works (§III.10) | T1.83; issue #130 |
| 2026-10-08 | A second transport, for the plan's usage only: the plugin carries a mod, which posts the figures to the loopback endpoint from a timer, and brings back none of the HTTP handler's faults (§II.1, Appendix A); its endpoint is not built (Appendix C) | MOD.2; issue #133 |
| 2026-10-08 | The endpoint for the plan's usage is built: it keeps the newest reading of each limit, outside the world model of sessions; `/state` does not show it yet (§II.1, Appendix C) | MOD.4; issue #133 |
| 2026-10-08 | The state report gives the plan's usage, read at the request, and says that the figure can be behind and is of one account (§II.1, §IV.9); the not-built row for it leaves Appendix C | MOD.5; issue #133 |
| 2026-10-09 | The window shows the plan's usage in its caption, as the state report gives it (§IV.9) | MOD.7; issue #133 |
