# Claude Dashboard — Design Document

**v0.2 · 2026-10-02 · no technology named · agrees with the product at commit `0488527`**

This document describes behaviour and concepts: the *what* and the reasons. It names no technology. The [Technical Specification](claude-dashboard-spec.md) (TS) holds the mechanisms and is the authority for the event mapping, the bands and the order. The [Implementation Specification](claude-dashboard-impl-spec.md) (Impl) holds the exact values; its §5.6 says what each part of the window shows.

*Not built* marks an intent that the product does not have yet. TS Appendix C lists all such items.

---

## 1. What this is

Claude Dashboard is a Windows application for a developer who runs many Claude Code sessions at one time. At a glance it answers three questions:

1. **What needs me now?** A session waits for a permission or an answer.
2. **What finished that I did not look at yet?**
3. **What still works?**

It replaces a mental map and a search through terminals with one list in order of priority. It replaces "a beep occurred somewhere" with "this session, on this task, finished" or "needs you".

## 2. The problem

Fifteen terminals on several virtual desktops, each with an agent on a different job. A sound says *that* something occurred, never *what* or *where*. A terminal shows the answer and not the question, so to check a result the operator must find the window and scroll up. Work that is finished and not seen collects out of view, and the operator carries the full map in their head.

## 3. Product principles

**Attention is the product.** The list is in the order of what needs the operator. It is never in alphabetical order and never in pure time order. A session that is blocked for ten minutes must not sink below one that finished ten seconds ago.

**Mirror reality; ask for no bookkeeping.** Grouping comes from things that exist: the working folder, and the names that sessions already have. When a tool asks the operator to file sessions under tasks, it becomes a chore that goes stale. This is not a project-management tool.

**Quiet by default, loud only for an interrupt.** Motion and alarm are for sessions that need a person. A reminder is *softer*, not louder: the first sound informed, and the reminder only taps a shoulder.

**An absence of activity never makes a session louder.** A session that goes silent can become quieter on screen. It can never become red. This rule was learned twice (§4).

**Remove trips, not only shorten them.** Each row can show the question *and* the answer. Many checks end in the dashboard.

**Each phase ships something useful alone.** Phase 1 has no navigation and no focus tracking, and it is still better than fifteen terminals.

## 4. Domain model

**Session** — one Claude Code instance that runs. It has an identity, a working folder, a group, a state, a current exchange, and a short history of its state changes.

**Exchange** — one prompt and its answer. The latest exchange is the session's context line: the prompt says what the session does, and the answer is what the operator reads when it finishes. What *names* a session is its title, where it has one (§9).

**Session states**

| State | Meaning | Entered when | Colour and motion |
|---|---|---|---|
| **Working** | Claude works on a prompt | A prompt is submitted, or a blocked turn continues | Blue, breathes |
| **Waiting** | The turn ended, and background work of the session still runs | The turn ends with a background command or a background agent that still runs | Blue, still |
| **Needs You — Permission** | Claude wants approval for an action | A permission prompt shows | Red, blinks |
| **Needs You — Question** | Claude asked something and is blocked on the answer | Claude asks for input | Red, blinks |
| **Error** | The turn stopped (rate limit, authentication, server) | The turn fails | Amber, still |
| **Unread** | Claude finished; the result is not seen | The response is complete | Green, still |
| **Acked** | The result is seen. Also: a session that started and did nothing yet | See *Acknowledgment* | Grey |
| **Interrupted** | The session was working and went silent for ten minutes | No event arrived for the threshold | Grey, still |
| **Ended** | The session exited | The session ends | Dim grey |

The TS (§II.2, §IV.1) is the authority for which event gives which state. This table is a summary.

**Two lessons about silence.**

- *Idleness is not a question.* Claude Code sends an "idle" notice when a session sat untouched, and each finished session does that. When the dashboard read that notice as a question, each finished row became red and blinking about ninety seconds after it finished, with nothing to ask. Only "Claude asked something and is blocked" is a Question.
- *Silence can make a session quieter.* A Working session that says nothing for ten minutes stops its claim to be busy: grey, with the badge `INTERRUPTED`. The session was probably interrupted, or it is in one long tool call; the two look the same from outside. To make it quieter costs the operator one glance at a row that is in order. To make it louder would cost a false alarm. No sound plays, and nothing moves.

**Waiting is work, and it is calm.** Before this state, a turn that started a long build and ended played "finished" while the agent still waited for the build. A Waiting row stays in the Working band and is blue. It does not move, it makes no sound, and it is never nudged.

**Acknowledgment** — the change from Unread (or Needs You, or Error) to Acked. Three tiers:

1. *Automatic:* the operator submits a new prompt in that session. That is proof that the answer was seen. **Only a prompt that a person typed counts.** Claude Code also submits prompts by itself: a notice that background work is complete, a message from a different session, a scheduled job. These start the work again, and they are not proof that anybody saw anything.
2. *Manual:* an Ack on the row. In a roster group the Ack is **one, at the group's heading**: the orchestration is the unit, one click clears each member that waits, and a member's row has no Ack (it keeps its badge and its light). The accepted cost: a blocked member can be cleared by hand only at the group. A group by working folder is a filing convenience, and its members keep their own Acks. **Ack all** in the toolbar clears each session that waits.
3. *Inferred (Phase 3, not built):* the session's terminal held the focus for some seconds.

An acknowledgment never restarts a row's clock. A row that finished four hours ago still says so after the Ack.

**Group** — a container of sessions that is derived, not assigned.

- By default the key is the **working folder**.
- A **roster** is a named set of session names that the operator made by a selection of rows (§9). A session whose title is in a roster is in that roster's group, wherever it runs. The dashboard still invents no membership: a roster matches names that the sessions report.
- Later (Phase 4, not built): the virtual desktop as the key.
- A group's state is the *worst* state of its members: permission > error > question > unread > working > waiting > quiet.
- **In a roster group, working outranks unread.** The members are one piece of work that passes between them. One member that finishes while a second works is a hand-off, not a result. The group reads finished only after all members are quiet for a moment (1.5 seconds).

**Event feed** — the application consumes session lifecycle events: a session started or ended, a prompt was submitted (with its text), a response finished (with its text), attention was asked for, a turn failed, the folder changed, a batch of tool calls ended. The TS (§II.2) has the contract.

**Notifier** — the sound policy: a first sound for each state, and the reminder policy of §8.

## 5. The attention model

The list has priority bands, top to bottom:

| Band | Contains | Order in the band |
|---|---|---|
| **Needs You** | Permissions, errors, questions | **By kind first: permission, then error, then question. Then oldest first in each kind** |
| **Unread** | Finished, not seen | **Newest first.** After a sound, the newest green is the one that the operator looks for |
| **Working** | Working, and waiting on background work | Most recent change first |
| **Quiet** | Acknowledged, idle, interrupted | Most recent change first; sinks to the bottom |
| **Ended** | Sessions that exited | Dim. Removal after some minutes is *not built* |

**The asymmetry is deliberate:** reds sort by how long they starve, greens by the "I just heard a beep" workflow.

**Why kind comes before age.** A permission is usually seconds of operator time between an agent and an unlimited wait, so to clear it gives back the most blocked capacity for each second of attention. An error often recovers on a retry. A question can need real thought, and that thought unblocks nothing else. Thus a question that is blocked for twenty minutes is *below* a permission that is three minutes old. The operator chose this with both orders shown.

*Alternative considered:* pure "last status change" order. Rejected, because a fresh green would bury a red that starves.

In the grouped view, groups sort by their most urgent member (then by latest activity), and the same bands apply in each group. In the flat view the bands are global and have headings. Active groups float to the top with no pin.

## 6. Space, staleness and overflow

The window is a narrow side panel. Rows are the scarce resource. The rules, in sequence:

1. **A stale group costs one row.** When each member of a group is quiet for 15 minutes, the group becomes one line: name, member count, "idle 38 min". A click opens it.
2. **Quiet rows collapse in their group.** A group with live work shows a line "+ 3 quiet" in place of the grey rows.
3. **An Unread row always gets a full row.** Work that is finished and not seen is what gets lost today. It is never summarised.

Rule 3 replaces the idea "show only the first green of each group when space is short". That rule would hide the thing that the tool exists to show. To collapse only what is *dealt with* is simpler and safe.

In the flat view, the Quiet band and the Ended band are each one line that can be opened.

## 7. View modes

**Grouped** (the default at each start) and **Flat**, with a toggle in the toolbar. The band logic is the same in both. The flat view adds a small folder tag to each row and gives the bands headings. A "needs me only" filter is a candidate for later.

## 8. Sound design

A **notice** is the first sound for an event. A **nudge** is the reminder.

- **Notices:** finished, permission, question and error each have their own sound. Working, Waiting and Interrupted have none.
- **Nudges** play when a permission, an error or a question waits: the *same melody, softer*, after 2 minutes, then 5 minutes later, then each 10 minutes. Never louder, never faster.
- **Unread** gets one soft nudge after 5 minutes.
- **A roster group makes one sound.** No chime for each hand-off, and none for each member. One notice plays when the last member finishes.
- **A scheduled job that finds nothing can be silent.** See [Quiet scheduled jobs](quiet-scheduled-jobs.md).
- **Mute all** stops all sound, for 30 minutes or until the operator ends it. The tray light stays true.
- **Pause monitoring** stops all sound and makes the tray light grey and visibly "off", until the operator resumes.
- **No sound device is said, not hidden.** When Windows has no output device, the window and the tray say "no sound device". The tray keeps its colour and gets no mark. A sound that could not play is recorded as dropped, and it is not played later when a device returns: a stack of old sounds is noise.
- **Mute for one session or one group.** *Not built.*
- **Settings for the intervals.** *Not built:* the values above are fixed.
- Later (Phase 3, not built): no notice for the session that is on screen.

## 9. Main window anatomy

This section is the authority for the anatomy of a row and for the motion rule. Impl §5.6 gives each rule with its exact value.

- **Caption:** the application name and the **counts strip** ("11 sessions · 3 need you · 2 unread · 1 working"). When the window is narrow, the strip drops words before numbers.
- **Toolbar:** the Grouped/Flat toggle · Select · Mute all · Ack all.
- **Notice:** amber lines under the toolbar, one for each thing the operator must see and would not otherwise: the dashboard is not connected to Claude Code, history is not being recorded, or there is no sound device. Each says what is wrong and what to do, and each clears by its own rule. Two can be true at one time, so the row is a list in a fixed order, the connection first. A dashboard that receives nothing, records nothing or plays nothing must not look like a quiet day.
- **Body:** groups (or bands) of session rows.
- **Session row:** status light · the session's title where it has one, then the start of the prompt (monospace: it *is* terminal text) · a badge with the state · the age · an Ack on a row that waits. The title is what Claude Code calls the session: a name that the operator set with `--name` or `/rename`, or one that Claude Code made. It is cut to a fixed length and does not take space from the prompt. A session with no title shows the prompt only.
- **The age on a row** says whose time it is. "Waiting 4 min": the agent is stopped and the time is the operator's. "2 min ago": the work is done and the time measures how long it is unseen. "6 min": the agent is busy. A working row counts from the operator's question, and a stop for a permission does not restart it.
- **Selection:** the operator makes a roster in *selection mode*, which starts from the toolbar. The mode shows itself in the toolbar: a mode that can be on and not seen is a mode that will be wrong. In the mode, a click on a row selects it and does not open it. **A selected row shows that by its own state, never by focus:** a check takes the place of the status light, and the row has a selection shade that is different from the focus shade. **A session with no title cannot be selected**, and its row says so: a roster stores names, and that session has none.
- **Lit action buttons:** the toolbar has two kinds of control. A *segmented toggle* (Grouped / Flat) always has one raised segment, which means "this is the current state". An *action button* is **lit when it is the main action of the current state**: *Ack all* when something waits, *Group these* when two or more rows are chosen. *Select*, *Cancel* and *Mute all* are never lit. A button that is not lit looks plain, not dim: a quiet board is an ordinary state, not a broken control.
- **Group heading:** the group's name (the roster's own name for a roster; the folder's short name for a folder group), one dot for each member in its colour, and, for a stale group, the member count and the idle time. A roster group that has a member that waits shows the group's one Ack. A right click on a member of a roster group removes its name from the roster permanently; the session goes back to its folder group.
- **Roster prompt:** after the operator makes a group, one row above the list asks if the dashboard must remember the group as a roster. It is not modal. **A prompt with no answer is a "no"**: the group exists and is not saved, so no answer and "no" leave the same state.
- **Open row:** the full latest exchange ("You asked …" with the time, "Claude answered …"), the background work that a Waiting session waits on, the Ack, and the session id (the first eight characters; a click copies the full id). An "Open terminal" action belongs here in Phase 2; it is *not built* and is hidden.
- **Tray light:** always there. Red for a permission, amber for an error or a question, green for unread, blue for working, grey for quiet. It has no digits; the counts are in its tooltip. The window can be closed and the tray still tells the truth.

**Motion: red blinks; working breathes; nothing else moves.** With animations off in Windows, nothing moves at all.

**The tray palette and the row palette differ on purpose.** A lone question is amber in the tray and red on its row. The tray triages (*how urgently must I look?*). The row diagnoses (*what does it do?*).

## 10. Phase plan

| Phase | Theme | Contents | State |
|---|---|---|---|
| 1 | **See clearly** | Event intake · the session list with states and bands · grouped and flat · rosters · Ack tiers 1 and 2 · notices and nudges · collapse rules · the tray light · the event log | Built |
| 2 | **Go there** | Click a row to go to its terminal window and tab | Not built |
| 3 | **It notices** | Ack from focus (tier 3) · no notice for the session on screen | Not built |
| 4 | **Task lens** | Grouping by virtual desktop · desktop names as group names | Not built |
| 5 | **Memory** | Session history · search of past exchanges · simple statistics | Not built |
| 6 | **Polish** | A settings interface · a sound editor · themes | One setting is built: start with Windows |
| 7 | **Anywhere** | A phone or remote view: read the states and acknowledge from any place | Not built |

Each phase can ship alone. Phase 7 is the reason that the domain model stays apart from the Windows parts. [Core and App](claude-dashboard-core-and-app.md) says what a second interface needs.

## 11. Non-goals (for now)

- Project or task management: no assignment, no kanban, no task lists.
- To type a prompt or a reply from the dashboard. It pulls the tool toward a terminal front end. Look again after Phase 3.
- Sessions from more than one machine in one list (until Phase 7 forces the question).
- To manage agents that are not Claude.
- To write anything into Claude Code's own settings.

## 12. Open questions

- The relation to ClaudeSessions: does this absorb it, or are they siblings?
- An Unread row that is never acknowledged: fade it after some hours, or leave it? Today it stays.
- Subagents: show each as a row, or keep them under the parent? Today a background agent's events arrive under the parent session, and a parent that waits on one is Waiting.
- Queued prompts: show a "1 queued" hint on a working row?
- Retention: how long must the event log be kept?
- More than one observer: two people, or two devices with separate acknowledgments. Today "seen" is a state of the session, so there is one observer.

## 13. Change history

| Date | Change | Source |
|---|---|---|
| 2026-08-24 | Only "Claude asked something and is blocked" is a Question. An idle notice changes nothing | Issue #1 |
| 2026-08-24 | The Needs You band sorts by kind first (permission, error, question), then oldest first | Operator's ruling |
| 2026-08-30 | Rosters: selection mode, the roster prompt, the roster group's one sound | Issue #16 |
| 2026-08-31 | The Interrupted state: silence can make a session quieter | Issue #28 |
| 2026-09 | Ack all. A roster group is acknowledged at its heading | Issues #43, #47 |
| 2026-09 | The Waiting state. A prompt that nobody typed is not an acknowledgment | Issue #52 |
| 2026-09 | A quiet scheduled job makes no sound | Issue #56 |
| 2026-09-29 | An acknowledgment or a close never restarts a row's clock. The "Open terminal" button is hidden | Issues #59, #60 |
| 2026-10-01 | The notice when the dashboard is not connected. Nothing is written into Claude Code's settings | Issue #65 |
| 2026-10-02 | v0.2. Written again to agree with the product. "Tab titling from prompts" is removed from Phase 2: the terminal title is left untouched (TS §III.3) | — |
| 2026-10-03 | The notice row is a list. "History is not being recorded" is a notice, and the history tries again each minute | Issue #71 |
| 2026-10-03 | "No sound device" is a notice, with no mark on the tray icon. A sound that could not play is not played later | Issue #72 |
