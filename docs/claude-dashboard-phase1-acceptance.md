# Phase 1 — end-to-end acceptance run (T1.20)

**Run date:** 2026-08-26 · **Artefact under test:** the T1.19 staged build at
`%LOCALAPPDATA%\ClaudeDashboardApp.staging\ClaudeDashboard.App.exe` (`752e934` + `c8a1d34`)
· **Harness:** `tools/replay-hooks.ps1` and `PhaseOneAcceptanceTests`

**Phase 1 is gated, not finished** — see §5a.

This records what was **observed**. Where a criterion was not observed, it says so and says why,
rather than reporting the expectation.

---

## 0 · How the load was produced, and what that costs the claim

The exit criteria ask for "real Claude Code sessions across ~15 terminals". Fifteen live sessions
would spend the operator's usage allowance on a test, and they asked for it to be conserved. So
the traffic is **replayed**: real hook payload shapes, posted at ingress at realistic concurrency
and spacing.

**What that evidences.** Everything from the wire inward — Kestrel, the token check, the mapper,
the bounded channel, the single-writer consumer, the Registry and its guards, the projection, the
view models, the tray roll-up, and the hook lifecycle around them.

**What it does not.** It says nothing about whether *Claude Code* delivers correctly from fifteen
terminals. That hop is evidenced only at **one or two concurrent sessions**, from the operator's
own dogfooding on 24–25 August. A defect living in Claude Code's dispatch under fifteen-way
concurrency would not appear here, and nothing in this document should be read as covering it.

**Isolation.** The staged build ran with `CLAUDE_DASHBOARD_HOME` and `CLAUDE_CONFIG_DIR` pointed
at scratch directories and a non-default port (58050). The operator's dashboard stayed up on 52789
throughout and their `~/.claude/settings.json` was never written. That was by construction, not by
care: the run could not have reached either.

---

## 1 · Ingress under load — observed

| | |
|---|---|
| Posts, first wave | **69**, of which a burst of **15 concurrent** |
| Posts, after relaunch | **42** |
| Non-200 responses | **0** |
| Non-empty response bodies | **0** |
| Slowest single post | **41 ms** |

Impl §3.3 requires `200` with an empty body on every path, including malformed JSON, a missing
`session_id`, an event ingress refuses, and an unknown event name. All four were posted. All four
answered `200` with nothing in the body.

**"Load" here means concurrency and mixed shapes, not queue pressure.** The channel holds 1024 and
the largest wave was 69 posts, so **the eviction path was never reached** and issue #3 — a dropped
`Notification(permission_prompt)` that nothing re-raises — is unexercised by this run.

---

## 2 · States, bands and the tray light — observed

**Observed in-process**, not against the staged executable, and that split is itself a finding:
the dashboard exposes **no state surface**. `/health` answers liveness and identity; nothing else
is readable from outside. So from outside the process **a correct dashboard and a dashboard showing
entirely wrong states are indistinguishable**. Everything in this section therefore comes from
`PhaseOneAcceptanceTests`, which drives the same composition over a real loopback socket and reads
the view models the operator would be looking at.

Scenario: 15 sessions across 5 working directories, each started and prompted, then one session
driven into each state.

| Observed | Value |
|---|---|
| Sessions in the Registry | 15 — none lost, none invented |
| Needs-permission | 1 |
| Error | 1 |
| Unread | 1 |
| Question | **0** — resumed by a tool batch, correctly |
| Working | 12 |
| Bands sum to sessions | 15 = 15 |
| Groups | 5, one per working directory |
| Tray colour | **Red** — the worst state present |
| Tray tooltip | contains `1 permission` and `1 error`, which the glyph merges onto amber |

Both live regressions are covered by the same run:

- **Issue #1** — a finished session going idle stays unread. It does not become a question.
- **Issue #2** — a tool batch resumes a blocked turn, and leaves an unread one alone.

### Acknowledgment, both tiers — observed

Phase 1 ships tiers 1 and 2 (Design Document §4). **The first version of this document neither
observed them nor listed them as unevidenced — it was silent, and silence in a gate reads as
coverage.** Two of the six things Part 3 asks for concern ack. They are now observed.

| Tier | What was done | Observed |
|---|---|---|
| **1 — automatic** | a second `UserPromptSubmit` posted into an unread session | state `Unread` → `Working`; the unread result is gone with the new turn |
| **2 — manual** | the row's `AcknowledgeCommand` raised on the UI thread, as a click would | Registry state `Unread` → `Acked`; **the row leaves the visible list**, because `Acked` is quiet and the quiet band is hidden by default |

Both are asserted with a *before* as well as an after — an unread row was confirmed to offer the
action first, because "not unread at the end" is also true of a session that never became unread.

The manual tier is the architecturally interesting one: the click publishes a synthetic `Ack` down
the **same channel** as hook events (Impl §4, TS §I.3), so the Registry keeps one writer. What was
checked is not that the command exists but that its effect travels the pipeline and comes back to
the row.

---

## 3 · Shapes the dashboard does not claim to handle — observed

A replay built only from traffic we classify is shaped to the thing it is testing. These were
included to see what actually happens.

| Shape | Observed |
|---|---|
| Unknown event name (`SomeEventFromTheFuture`) | `200`; logged at Information as "ignored, ingress does not consume" |
| `PermissionRequest` (registered by the operator, refused by us) | same |
| Malformed JSON body | `200`; logged at Warning with the parse position |
| `Stop` with no `session_id` | `200`; logged at Warning, distinct from the malformed case |
| **Eight unclassified notification matchers** | `200` — and **nothing whatever in the log** |
| Session ending without finishing | accepted; no error |
| `Stop` stamped before the prompt it answers | accepted; the timestamp guard declines it |
| Two events sharing an instant | both accepted |

### Finding: unclassified notifications are silent

The eight unrecognised `notification_type` values were mapped to `NotificationKind.Unknown`,
changed no state, and **produced no log line at any level that reaches the file**. The four other
unhandled shapes each leave a trace; this one does not.

That matters because it is the shape most likely to occur: Claude Code adding a notification type
is a routine upstream change, and the first one that *should* drive a state would be discarded in
silence. Compare the deliberate care taken to distinguish "malformed JSON" from "unknown event" in
the log — the same reasoning applies here and the line is missing.

**Not fixed under this task.** Raised for the Director; it is a change to ingress logging, not to
the acceptance run.

---

## 4 · Crash and relaunch — observed

| Step | Observed |
|---|---|
| Forced kill (`Stop-Process -Force`) | process gone; port 58050 released |
| Hooks in Claude Code's settings after the kill | **still registered, 8 events** — the documented residual |
| Relaunch (**by hand**) | started; `/health` answered; same gate identity as before the kill |
| Ingest after relaunch | 42 further posts, all `200` |
| `/show` to the relaunched process | window surfaced |
| Clean quit | exit code 0; **hooks removed, 0 events**; consumer reported `24 applied, 20 declined` |

The residual is exactly as T1.18 documented it: a hard kill leaves the handlers registered with
nothing listening, because no managed code runs. Nothing in this run closed it and nothing claims
to.

**The relaunch was manual.** The automatic one — restart-on-failure via the scheduled task — is
in §5.2, not here.

---

## 5 · Criteria this run could NOT evidence

**Four.** The list was derived from Part 3 and Part 2 rather than from what the run happened to
produce, because a gate that only lists the gaps it noticed is the same failure as one that lists
none.

Everything here is code that **exists** and this run could not reach. Criteria that no run could
evidence, because the feature was never built, are separate — see §5a. Keeping them apart matters:
"we could not observe it" and "there is nothing to observe" are different claims and only one of
them is closed by running the test again.

1. **Survives a logon restart.** Needs a logon. The scheduled task exists as verified code
   (T1.19: registered under a scratch name, read back from Windows, deleted) but **no real logon
   task is registered**, deliberately — one pointing at staging would start the wrong executable
   at the operator's next logon.

2. **"Relaunches via the task" after a crash.** §4 kills the process and relaunches it **by hand**.
   The automatic relaunch is `RestartOnFailure` — `PT1M`, three times — on a task that, per the
   point above, **is not registered**, so it could not have happened and did not. This is a
   different event from the logon trigger: a mid-session crash is precisely what restart-on-failure
   exists for, and it is the half of the criterion §4 does not reach. As first written, §4 read as
   satisfying the whole of it.

3. **Sound: notices and nudges fire.** Needs an ear. Open since T1.14. Everything up to the sound
   card is evidenced; what came out of the speakers is not, and cannot be from here.

4. **Nudges coalesce — and this one is newly named.** The nudge ladder begins minutes after a
   session starts waiting. The staged run lasted about forty seconds and the consumer reported
   **1 nudge evaluation**: the schedule never engaged at all. Worse, nudge *firing* is
   unobservable from outside the process — there is no log line and no state surface — so a longer
   run would not have fixed it either. The engine's behaviour is covered by unit tests;
   **the assembled system's nudge behaviour is not covered by anything in this document.**

---

## 5a · Criteria **no** run could evidence, because the feature does not exist

§5 lists things the code can do that this run could not reach. This is a different category and it
was missing: **two Phase 1 tasks were never started**, so no run of anything could evidence them.
Both sit inside this task's own declared dependency range, `T1.11–T1.19`.

| Task | State in the tree, verified | What is therefore unevidenced |
|---|---|---|
| **T1.16 — DPI, pin-to-all-desktops, placement** | `IVirtualDesktopService` exists as a **port in Core with no adapter and no caller**. Per-Monitor v2 *is* done and verified against the published executable (T1.19). | Pinning the window to all virtual desktops; restoring the last window position; the always-on-top toggle. Nothing in `src/` mentions any of the three. |
| **T1.17 — SQLite event log** | **Nothing.** No `Microsoft.Data.Sqlite` reference, no `dashboard.db`, no `events` table, no write path. | Durable event recording; persistence across runs. |

**This was a deliberate reorder, not an oversight.** The Director moved T1.18 and T1.19 ahead of
T1.16 and T1.17 so that the operator's requested feature — hooks that install and uninstall with
the dashboard, closing issue #4 — and a shippable package landed first, ahead of display scaling
and a log nothing in Phase 1 reads back. The reasoning is sound and this document does not dispute
it. What this section exists to prevent is the reorder being visible only in the correspondence
that produced it.

**So: Phase 1 is gated, not finished.** Everything this document reports was observed and holds.
It is not a statement that Phase 1 is complete, and the exit criteria in Part 2 are not fully met
while these two remain. **When T1.16 and T1.17 land, this document needs a supplement** — their
criteria have never been run, and a gate that predates a feature says nothing about it.

---

## 6 · Was the harness capable of producing the other outcome?

A green run proves nothing unless a red one was reachable. Five defects were planted, each taken
from the code's own history rather than shaped to fit an assertion:

| Planted defect | Result |
|---|---|
| `idle_prompt` read as a question (the real issue #1 defect) | **fails** — Unread 1 → 0 |
| `PostToolBatch` no longer resumes a question (the real issue #2 defect) | **fails** — Questions 0 → 1 |
| Tray red threshold raised so a permission prompt is amber | **fails** — Red → Amber |
| Manual ack no longer applies to an unread session (tier 2 dead) | **fails** — the unread row never offers the action |
| The publisher drops the ack instead of publishing it | **fails** — the acknowledgment never reaches the Registry |

The replay script and the acceptance test both carry the same scenario, so the two halves describe
one run rather than two.

**Two traps found while doing this, both of which produce the same green as a working harness.** A
plant that turns out to be a **no-op** — one attempt at the issue #1 defect fell through to a
catch-all with identical behaviour — is indistinguishable from a test that fails to notice a real
defect, and only reading the code you changed tells them apart. And a plant that **does not
compile** leaves the previous assembly in place under `--no-build`, so the run reports on the old
code, sometimes still carrying the previous plant's failure message. Check the build result, not
only the test result.

---

## 7 · Standing consequences

- **Two artefacts exist from T1.19 onward.** Every task after it must republish and repoint, or
  the operator's logon starts a build that has drifted from source.
- **The staged build has not been swapped in.** Live install, the user-scope token, and the real
  logon task are all deferred to the operator by earlier rulings.
- **This gate needs a supplement when T1.16 and T1.17 land** (§5a). A gate that predates a feature
  says nothing about it, and this one predates two.
- **Phase 1 is gated, not finished.** Every observation here holds; the phase's exit criteria in
  Part 2 do not, while §5 and §5a stand.
