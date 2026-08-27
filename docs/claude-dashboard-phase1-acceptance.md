# Phase 1 — end-to-end acceptance run (T1.20)

**Run date:** 2026-08-26 · **Artefact under test:** the T1.19 staged build at
`%LOCALAPPDATA%\ClaudeDashboardApp.staging\ClaudeDashboard.App.exe` (`752e934` + `c8a1d34`)
· **Harness:** `tools/replay-hooks.ps1` and `PhaseOneAcceptanceTests`

**Phase 1 is gated, not finished** — see §5a and its supplement §5b. The suite has an open defect of its own: §5c.

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

## 5b · Supplement: the two tasks landed (T1.16 `5d69767`, T1.17 `0e75bf1`)

**§5a above is left standing on purpose.** It was true when it was written, and a line that
quietly disappears once the feature exists leaves no record that the gate ever predated it. This
section says, criterion by criterion, what is now evidenced and what still is not.

**Derived from the Execution Plan's acceptance criteria for each task, not from what the runs
happened to produce.** That distinction is the reason §5a exists at all: a list built from
observations records what was looked at, and a list built from criteria records what was not.

### T1.16 — DPI, pin-to-all-desktops, placement

| Criterion (Execution Plan Part 3) | State |
|---|---|
| Window stays crisp when dragged between differently-scaled monitors | **Still unevidenced, and not by this run either.** Per-Monitor v2 is declared, guarded by a test, and verified present in the published executable — but all three monitors on this machine are at 100%, so no run here can show the visual result. A scale factor is the operator's environment, not a fixture. |
| Appears on every virtual desktop | **Performed but unverified**, in those words. The staged build logs a successful pin and every step of the undocumented path returns `S_OK`. It is not verified because the check needs a real desktop switch, and Windows ignores an injected Win-key hotkey from a background process — `keybd_event` and `SendInput` both accepted every event and changed nothing. `tools/verify-pin.ps1` finishes it with about fifteen seconds of a human's keyboard, and its result is to land as a one-line follow-up commit naming `5d69767`. **Until that runs, this row is not evidence of pinning.** |
| Position restores; a vanished monitor falls back to the focused one | **Evidenced by unit tests, not by a live run.** `WindowPlacementTests` covers restore, the vanished monitor, a window straddling two, sizes that are not sizes, and no monitors at all. Nobody has undocked this machine to watch it happen. |
| With pinning forced to fail, the app starts, logs once, and behaves normally on one desktop | **Evidenced.** The adapter takes a shell factory so the degrade path is reachable on a machine where pinning works, and the reviewer confirmed the path is load-bearing by stubbing the pin to return `true` and watching seven tests die. |
| Republish and repoint the logon task | **Republish done. Repointing deferred** — see §7's open items; the logon task still points at the operator's live build, deliberately. |

### T1.17 — SQLite event log

| Criterion (Execution Plan Part 3) | State |
|---|---|
| Events persist across runs | **Evidenced, by a SQLite that is not ours.** Two stores over one path, the first closed before the second opens, then read back through `winsqlite3.dll` — Windows' own copy in System32, a different vendor's binary from the `e_sqlite3.dll` the product writes with. Our own reader agreeing with our own writer would not have been evidence of anything. |
| Write path is off the UI thread | **Evidenced, and stronger than the criterion asks.** It is off the *consumer* thread too, which matters more: the consumer is the single writer of the Registry and the sound engine, and a disk wait there would stop the dashboard seeing events. The hand-over is a non-blocking `TryWrite` onto a bounded channel, timed with nothing draining and the channel driven past capacity. |
| No pruning yet | **Evidenced by absence, and the absence is checkable**: there is no `DELETE`, no retention window and no row count anywhere in `src/`. What that costs is now stated rather than left implicit — about 288 KiB on a typical day, 2.6 MiB on the busiest day in 95, and about 103 MiB for a year unpruned, measured through the real store at payload sizes taken from 4,439 real prompts and 11,757 real assistant messages. `GrowthMeasurement` re-measures on every build and fails if the published constant drifts either way. |
| Write-only in Phase 1, no read-back required | **Evidenced by absence.** `IEventStore` has one method. The foreign reader is a test instrument and is not reachable from the product. |
| Do not block the consumer on disk | Same evidence as the second row. |

**What this run does *not* evidence about T1.17.** The growth figures are upper bounds built from
transcript entries, which over-count the hooks that actually arrive, and they are one operator's
traffic on one machine. No run here shows the dashboard recording fifteen concurrent real Claude
Code sessions, for the same reason §4 gives: that hop is evidenced only at one or two sessions,
from dogfooding. And nothing has read the database back for a purpose — Phase 5 will be the first
thing that does, and the first thing that can discover the stored shape is wrong for it.

**The gate.** With these two landed, the two rows of §5a are answered. Every other limit this
document records — §4's, §5's, §6's — still stands unchanged.

---

## 5c · A defect in the suite itself: issue #12, an unexplained host crash

**This does not block Phase 1, and it is here because a gate that does not mention it claims a
cleanliness the suite does not have.**

During T1.15b's soak, one full Release run in fifteen ended with the **test host process dying of
an access violation, `0xC0000005`**. The run aborted partway through; 110 tests never ran.

| | |
|---|---|
| Observed rate | roughly **1 in 24** Release runs, counting one earlier unexplained failure that may be the same event |
| Cause | **unknown** |
| Reproduced under `--blame-crash` | **no**, in 8 attempts |
| First suspect | `ForeignSqliteReader`, the raw `winsqlite3.dll` P/Invoke added at T1.17, called from four test classes xUnit runs in parallel |
| Status of that suspect | **named by its author, unproven** |

**The suspicion is deliberately weak, and the timing establishes nothing.** The P/Invoke is the
newest unmanaged code in the suite and that is the whole of the case against it. Nothing shows the
crash postdates T1.17: the suite also drives real WPF windows, a shell tray icon and NAudio, any of
which can die natively during teardown, and a 1-in-24 event is entirely capable of having been
present for weeks unobserved. **Bisecting the P/Invoke out of the suite is the correct first move**
and it belongs to issue #12, not to this document.

**Why it is filed rather than fixed.** Phase 1 is not held open waiting out an unreproducible
native crash at 1 in 24. But the number in §6 — and every "the suite is green" statement anywhere
in this repository — is a statement about runs that could, at that rate, have been truncated.
Section 6a says what was done about that.

---


## 5d · The port is no longer fixed (T1.21), and what that costs §1 and §4

**§1 and §4 were measured against a single fixed port**, because that is what the dashboard had
when they were written. Issue #5 changed it: the loopback port is now chosen per user — the port
in `port.txt`, then a SHA-256-of-SID derivation, then a bounded walk (Impl §3.1, amended
2026-08-26).

**What that does not change, which is most of it.** Both sections drove one dashboard on one port
and observed what came back. That the port was 58050 rather than derived does not alter what was
observed about ingress, states, bands, the tray light, crash and relaunch: **a port is a port once
something is listening on it.** The figures stand as measurements of the run they describe.

**What it does change, and it is one thing.** Neither section evidences the part of the port choice
that only exists now: *which* port gets bound, and what happens when the first candidate is taken.
That was not a limitation of those runs — the behaviour did not exist to be observed.

| What the port choice needs evidenced | Where it is |
|---|---|
| A fresh profile derives and binds; a recorded port is preferred; a taken one falls through; a stranger causes a walk; the walk is bounded and giving up is not a crash | `PortSelectionTests` — decided against a table of probe answers |
| **Two users' ports differ and both bind at the same moment**, against real sockets | `TwoUsersBindTests` |
| A real listener on the derived port is walked past, and classified rather than counted | `TwoUsersBindTests` |
| The registered URL and `port.txt` carry the **bound** port | `HookLifecycleTests`, using a port that is neither the default nor inside the derivation range |

**What no run here evidences.** Two *actual* signed-in Windows users have not been observed. Two
identities and two data roots have, which is the part that can be exercised without a second
account; the hop from "a different SID derives a different port" to "a second signed-in user gets a
working dashboard" rests on the derivation being the only per-user input, and that is an argument
rather than a measurement.

**The accepted residual, ruled by the operator and not designed around.** Entries in
`allowedHttpHookUrls` accumulate — one per distinct URL ever registered — and nothing removes them.
Three users mean three entries; a user who never comes back leaves one behind; an entry pointing at
no hook is inert. Pruning was declined rather than forgotten.

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

## 6a · Were the runs behind these figures complete? Re-verified, 2026-08-26

§6 asks whether a red outcome was reachable. This asks something the document had not: **whether
the runs that produced its greens actually finished.**

**The trap, and it is worse than it sounds.** When the test host crashes late, `dotnet test` prints:

```
The active test run was aborted. Reason: Test host process crashed : Process terminated.
Passed!  - Failed: 0, Passed: 1042, Skipped: 0, Total: 1042, Duration: 8 s
```

It says **`Passed!`**. `Failed: 0`. And **`Total` equals `Passed`** — because `Total` on that line
is the number of tests that *ran*, not the size of the suite. A truncated run is internally
self-consistent, so **any green figure in this document could have come from one and would look
exactly as it does now.**

**A check that could not fail was standing for an hour.** The first response to the crash was the
rule "a green run is `executed == total`, not `failed == 0` alone". That is unfalsifiable:
`executed == total` holds in both runs below, one complete and one aborted, measured from retained
result files.

| Run | `total` | `executed` | `passed` | `failed` | Actually |
|---|---|---|---|---|---|
| Complete | 1093 | 1093 | 1093 | 0 | green |
| Aborted | 983 | 983 | 983 | 0 | **110 tests never ran** |

**The corrected rule needs a third input the run cannot supply.** A run is green when `Failed: 0`,
**`Total` equals the suite size known in advance**, and **no "The active test run was aborted"
line** is present. The expected size has to come from outside the run, because everything inside it
agrees with itself.

**Re-verified under that rule, one run per configuration, expected total stated before the run:**

| Configuration | Expected | Ran | Abort line | Result |
|---|---|---|---|---|
| Debug, full suite | 1093 | 1093 | none | green |
| Release, full suite | 1093 | 1093 | none | green |
| `PhaseOneAcceptanceTests` — the source of every figure in §2 | 2 | 2 | none | green |

**No figure moved.** The checker was itself controlled before being trusted: given a deliberately
wrong expected size it reports `SHORT RUN`, so its greens are not the only outcome it can produce.

**What was and was not exposed.** The trap fabricates greens; it cannot fabricate a red. So §6's
five planted-defect results are unaffected by it — each records a run that *failed*, and an aborted
run cannot invent a failure. §2's figures are assertions inside a test that passed, so the risk was
never a wrong number but the test **not running at all**, which is what the expected-total check
now excludes. §1's and §4's figures come from a live staged process driven by
`tools/replay-hooks.ps1`, not from a test host, so this defect does not reach them — they remain
dated to the T1.19 artefact named in the header and are not re-run here.

---

## 7 · Standing consequences

- **Two artefacts exist from T1.19 onward.** Every task after it must republish and repoint, or
  the operator's logon starts a build that has drifted from source.
- **The staged build has not been swapped in.** Live install, the user-scope token, and the real
  logon task are all deferred to the operator by earlier rulings.
- **The supplement for T1.16 and T1.17 is written** (§5b). Both tasks have landed, and §5b answers
  their criteria one by one rather than deleting the rows that said they were unbuilt. One row is
  still open on purpose: pinning is recorded as **performed but unverified**, and it stays that way
  until somebody presses the keys.
- **The suite has an open defect of its own** (§5c, issue #12): an unexplained host crash at
  roughly 1 in 24 Release runs, cause unknown, first suspect named and unproven. It does not block
  Phase 1. It does mean every "green" statement about this suite is a statement about a run that
  could have been truncated, which is why §6a exists and why the check is now three inputs rather
  than one.
- **"Failed: 0" is not a green run, and neither is `Total == Passed`.** Both are true of an
  aborted run. A green run is `Failed: 0`, `Total` equal to the suite size known in advance, and no
  abort line (§6a).
- **The ingress port is per user and moves** (§5d, T1.21). Anything that assumes 52789 is wrong,
  including anything read from `port.txt` before a dashboard has run. Allowlist entries accumulate
  by ruling, and two users sharing one `CLAUDE_DASHBOARD_HOME` share one database, which is
  unsupported and documented as such.
- **Phase 1 is gated, not finished.** Every observation here holds; the phase's exit criteria in
  Part 2 do not, while §5 stands and while §5b's open row stands.
