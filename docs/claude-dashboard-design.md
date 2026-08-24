# Claude Dashboard — Design Document

**Draft v0.1 · 2026-08-22 · technology-agnostic**

Working title: *Claude Dashboard*. Everything in this document describes behavior and concepts, not implementation. No stack decisions are made or implied here.

---

## 1. What this is

Claude Dashboard is a Windows application for a developer running many concurrent Claude Code sessions. At any moment it answers three questions in one glance:

1. **What needs me right now?** (a session is waiting on permission or an answer)
2. **What finished that I haven't looked at yet?**
3. **What's still working?**

It replaces mental tracking and terminal-hunting with a single prioritized list, and it replaces "a beep happened somewhere" with "this session, working on this task, finished / needs you."

## 2. The problem

Fifteen terminals across multiple virtual desktops, each running an agent on a different job. Audio notifications announce *that* something happened, never *what* or *where*. Terminals show the answer but not the question, so checking a result means finding the right window, then scrolling up to reconstruct context. Finished-but-unseen work piles up invisibly, and the operator carries the whole map in his head.

## 3. Product principles

**Attention is the product.** The list is sorted by what needs the operator, never by alphabet and never by pure chronology. A session that's been blocked for ten minutes must not sink below one that finished ten seconds ago.

**Mirror reality; never require bookkeeping.** Grouping is derived from things that already exist (working folder now, virtual desktop later). The moment the tool asks the operator to file sessions under tasks, it becomes a chore that goes stale. This is explicitly not a project-management tool.

**Quiet by default, loud only for interrupts.** Motion and alarm are reserved for sessions that need a human. Reminders get *softer*, not louder — the first sound informed; reminders only nudge.

**Reduce trips, don't just guide them.** Each row can show the question *and* the answer, so many checks end in the dashboard without a context switch at all.

**Every phase ships something useful on its own.** Phase 1 with no navigation and no focus-tracking still beats trolling fifteen terminals.

## 4. Domain model

**Session** — one running Claude Code instance. It has an identity, a workspace (the folder it's working in), a derived group, a state, a current exchange, and a timeline of state changes.

**Exchange** — one prompt-and-answer turn. The latest exchange is the session's context line: the prompt snippet is what identifies the session in the list, and the answer is what the operator reads when it finishes.

**Session states**

| State | Meaning | Entered when | Color language |
|---|---|---|---|
| **Working** | Claude is processing a prompt | operator submits a prompt | blue, breathing |
| **Needs You — Question** | Claude asked something / is idle waiting for input | Claude requests input | red, blinking |
| **Needs You — Permission** | Claude wants approval for an action | permission request raised | red, blinking |
| **Error** | the turn died (rate limit, auth, server) | turn fails | amber, steady |
| **Unread** | Claude finished; result not yet seen | response completes | green, steady |
| **Acked** | result seen and acknowledged | see *Acknowledgment* | grey |
| **Ended** | session exited | session ends | dim grey, then removed |

**Acknowledgment** — the transition from Unread (or Needs You) to Acked. Three tiers:

1. *Automatic:* the operator submits a new prompt in that session — proof the answer was seen. Zero extra plumbing; covers most cases.
2. *Manual:* an Ack action on the row.
3. *Inferred (later phase):* the session's terminal window/tab held focus for a few seconds.

Phase 1 ships tiers 1 and 2.

**Group** — a derived container of sessions. Phase 1 grouping key: workspace folder. Later: virtual desktop (which is how tasks are already organized), with desktop names as group names. A group's state is the *worst* state of its members (needs-you > error > unread > working > quiet), and its recency is its most recent member event.

**Event feed** — the app consumes session lifecycle notifications: session started/ended, prompt submitted (with prompt text), response finished (with answer text), attention requested (question/permission), turn failed (with reason). Already verified feasible against Claude Code's lifecycle hooks; the precise contract is a Phase 1 implementation detail. The feed is deliberately generic so other agent tools could feed it someday — a design convenience, not a goal.

**Notifier** — the sound policy engine. First-notice sounds per state, plus the reminder ("nudge") policy in §8. This is where claude-beeps eventually lives.

## 5. The attention model

The list is organized into priority bands, top to bottom:

| Band | Contains | Order within band |
|---|---|---|
| **Needs You** | permissions, errors, questions | **by kind first — Permission > Error > Question — then oldest first within each kind** (see the correction below) |
| **Unread** | finished, unseen | **newest first** — when a beep just fired, the newest green is the one being hunted |
| **Working** | processing | most recent activity first |
| **Quiet** | acked, idle | sinks to the bottom |
| **Ended** | exited sessions | dim single line for a few minutes, then gone (history is a later phase) |

The ordering asymmetry is deliberate: reds are sorted by starvation, greens by the beep-chasing workflow. Within the Needs-You band that asymmetry now operates *inside* each kind — see the correction.

> **Correction (2026-08-24).** The Needs-You row above previously read "oldest first — the longest-blocked agent is the most wasted capacity". That was superseded by the operator's ratification recorded in **TS §IV.2 and §IV.3** (commits `e645fd8`, then `2860e14`), which is the authority for ordering. The ratified rule sorts the band **by kind first — `Permission` > `Error` > `Question` — then oldest-first within each kind**, so a Question blocked twenty minutes appears *below* a Permission raised three minutes ago.
>
> The rationale changed with it, from age to **throughput**: a permission is usually seconds of operator time standing between an agent and an indefinite wait, so clearing it returns the most blocked capacity per second of attention; an error is often self-recoverable on retry; a question may need real thought, and thinking about it unblocks nothing else meanwhile.
>
> This section went stale because the amendment landed in the TS while this document was not yet in the authoritative set — it was added to the Execution Plan's companion list at `9de8ab3`/`41d0f57`. **TS §IV.2/§IV.3 remain the authority for banding and ordering; this section is a summary of them.** The single implementation lives in `AttentionOrder` and is consumed by both the attention engine and `Group.WorstState`.

*Alternative considered:* pure "last status change" ordering (the original sketch). Rejected because a fresh green would bury a starving red; recency is preserved *within* bands, so the feel survives.

In grouped view, groups sort by their most urgent member (tie-break: latest activity), and the same bands apply inside each group. In flat view the bands are global and visibly labeled. Active groups float to the top automatically — no manual pinning needed.

## 6. Space, staleness, and overflow

The window is a narrow side panel; rows are the scarce resource. Rules, in order:

1. **A stale group costs one row.** When every member of a group is quiet for N minutes (default 15), the group collapses to a single line — name, member count, "quiet 38 min." Still findable, click to expand, never pushes active work down.
2. **Acked rows collapse inside their group.** An expanded group shows a footer like "+ 3 quiet" instead of individual grey rows.
3. **Unread rows always get a full row.** Finished-but-unseen work is exactly what gets lost today; it is never summarized away.

Rule 3 replaces the "show only the first green per group when space is tight" idea: that rule would hide precisely the thing the tool exists to surface, and it adds a special overflow mode. Collapsing only what's already been *dealt with* is simpler and safe.

Budget check: a row is about two text lines. Fifteen sessions with zero collapsing fit a half-height column; with collapsing, the typical visible count is far lower.

## 7. View modes

**Grouped** (default) and **Flat**, switched by a toggle in the header. Same band logic in both; flat view adds a small group tag to each row and labels the bands. A "needs me only" filter is a candidate for later — the band sort may make it unnecessary.

## 8. Sound design

Vocabulary: a **notice** is the first sound for an event; a **nudge** is the reminder.

- **Notices** keep the existing claude-beeps language: finished = "bee-boop"; permission, question, and error each get their own distinct sound.
- **Nudges** fire when a Needs You session sits unacknowledged past T₁ (default 2 min): the *same melody, softer* — "beee-booop," lower volume, gentler timbre — repeating at widening intervals (2 → 5 → 10 min). Never louder, never faster. The first sound informed; the nudge only taps a shoulder.
- **Unread** gets at most one soft nudge (default: after 5 min) or none — configurable per state.
- Per-group and per-session mute are cheap and worth having early.
- Later, once focus-awareness exists (Phase 3): suppress the notice for the session currently on screen — you're watching it finish anyway.

## 9. Main window anatomy

- **Header:** app name · counts strip ("3 need you · 2 unread · 1 working") · Grouped/Flat toggle · mute.
- **Body:** groups (or bands) of session rows.
- **Session row:** status LED · prompt snippet (the session's name, in monospace — it *is* terminal text) · state + age line · Ack action on unread rows.
- **Expanded row:** the full latest exchange — "You asked …" / "Claude answered …" — with Ack, and a disabled "Open terminal" slot reserved for Phase 2.
- **Tray icon:** always-ambient summary — grey all-quiet, blue working, red with a needs-you count badge. The dashboard can be closed and the tray still tells the truth.

Motion discipline: red blinks; working breathes; nothing else moves.

## 10. Phase plan (strawman — reorder freely)

| Phase | Theme | Contents |
|---|---|---|
| 1 | **See clearly** | event intake · session list with states and bands · grouped/flat toggle · ack tiers 1+2 · notices + nudges · collapse rules · tray icon |
| 2 | **Go there** | click-to-navigate (window + Windows Terminal tab) · tab titling from prompts |
| 3 | **It notices** | focus-based ack (tier 3) · on-screen notice suppression |
| 4 | **Task lens** | virtual-desktop grouping · desktop names as group names |
| 5 | **Memory** | session history · searchable past exchanges · simple stats (e.g., how long agents wait on you) |
| 6 | **Polish** | settings UI · sound editor · themes |
| 7 | **Anywhere** | phone/remote view — read states and ack from anywhere |

Each phase is independently shippable. Phase 7 is the reason the domain model stays cleanly separated from the Windows-specific parts — a remote read surface later shouldn't touch the core.

## 11. Non-goals (for now)

- Project or task management: no assigning, no kanban, no manual task lists.
- Typing prompts or replying from the dashboard (open question — it pulls the tool toward being a terminal frontend; revisit after Phase 3).
- Multi-machine aggregation (until Phase 7 forces the question).
- Managing non-Claude agents (the event feed stays generic, but it's not a goal).

## 12. Open questions

- Relationship to ClaudeSessions: does this absorb it (the `claudesessions://` scheme would slot straight into Phase 2 navigation), or are they siblings?
- Unread rows that are never acked and never revisited — auto-fade after some hours, or sit forever?
- Session naming: always derived from the latest prompt, or allow a manual rename that sticks?
- Subagents: roll up into the parent session, or hide entirely?
- Queued prompts (Claude Code lets you queue messages) — show a "1 queued" hint on working rows?
- Retention: is "today only" enough until Phase 5?
