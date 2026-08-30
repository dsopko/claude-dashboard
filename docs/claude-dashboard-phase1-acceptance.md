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
   **Widened by T1.22** (§5e): the adapter now follows the default output device, and the five-row
   card that would evidence it needs the operator's hands and, for one row, their consent. None of
   the five has been run. The log now names the bound endpoint, so that card can be judged by
   reading rather than only by listening.

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

### The stale assembly does not only fake a pass. It can serve another experiment's answer.

Found during T1.23's review, and it is the worst form of this trap seen so far.

A planted mutation failed to build — a XAML error, `MC3089`. The next command was
`dotnet test --no-build`, which duly ran the **previous** assembly and reported
`Failed: 2, Total: 1160`. Those two failures were real, reproducible, and belonged to a
**different plant that had already been reverted**.

Every earlier instance of this trap in this repository produced a **green** that looked like a
survival — a mutation that appeared not to matter. This one produced a **plausible red belonging to
another experiment**, which is worse in two ways: a red is what a planter is hoping for, so it
invites no suspicion; and the failing test names are consistent with a story, so the result reads
as a finding rather than as noise.

**The consequence for method.** Checking the build result before believing a test result was
already the rule. What this adds is that the rule cannot be relaxed when the result looks like the
one you wanted — a red is not self-validating, and "the plant killed a test" needs the same
`0 Error(s)` in front of it as "the plant survived".

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
| The registered URL and `port.txt` carry the **bound** port | `HookLifecycleTests`, using a port that is neither the default nor inside the derivation range. **T1.28 deleted that class** — the surviving half is `IngressAnnouncementTests`, which makes the same claim about `listening.txt` and `port.txt` (§5j) |

**What no run here evidences.** Two *actual* signed-in Windows users have not been observed. Two
identities and two data roots have, which is the part that can be exercised without a second
account; the hop from "a different SID derives a different port" to "a second signed-in user gets a
working dashboard" rests on the derivation being the only per-user input, and that is an argument
rather than a measurement.

**The accepted residual, ruled by the operator and not designed around — and now closed (§5j).**
Entries in `allowedHttpHookUrls` accumulate — one per distinct URL ever registered — and nothing
removes them. Three users mean three entries; a user who never comes back leaves one behind; an
entry pointing at no hook is inert. Pruning was declined rather than forgotten.

**Superseded by T1.28 (§5j, issue #29).** A command hook is not on that allowlist, so nothing
accumulates any more, and `--remove-hooks` clears the entries earlier builds left. The paragraph
above stands as the record of a ruling that was right for the design it was made about.

---
## 5e · Sound follows the default output device (T1.22, issue #13), and what is still open

The sound adapter opened the output device once, in its constructor, and held it. `Play` never
touched the device again — it added a provider to the mixer and returned — so once the device was
gone there was nothing left in `Play` that could fail. Unplugging a headset left the dashboard
silent for the rest of the session, with an empty log, while `PlayedCount` kept rising for sounds
nobody heard.

T1.22 makes the adapter follow the default endpoint, and stop reporting success while it has no
working one. The port contract is unchanged: `ISoundPlayer` did not grow a member, and "never
throws" still holds. This was an adapter fault and it is fixed inside the adapter.

### What the suite now proves, and what it cannot

Nine new tests. All nine were confirmed load-bearing by planting six mutations shaped like the
defect rather than like the checker, each one killing at least one test: never subscribing (the
original defect, 7 tests), publishing a device without re-checking the disposal (1), a strike limit
that never trips (2), a deliberate release not marked as deliberate (5), a bound endpoint echoing
the request instead of the player's own report (1), and no endpoint comparison before re-opening
(1). Two further mutations did not compile, so no test result was read from them.

**Four things none of these tests can prove.** They are the shape of what shipping this leaves
open, and none of them is closed by running the suite again.

1. That Windows raises anything at all on a real unplug, which of the four notifications arrive,
   in what order, and how many times. The fake raises them because a test told it to. This premise
   is shared between the test and the code, so no test on this seam can fail on it.
2. That opening a real endpoint produces an audible sound. Every test would pass against a build
   that opened the right device and played into a void.
3. That the endpoint Windows calls the default is the one the operator hears.
4. **The widest, and the limit on the second half of the fix.** The suite proves the adapter stops
   claiming success when Windows reports *no default endpoint*. It cannot prove the adapter stops
   claiming success for a device that is listed, reports itself active, accepts a stream, and is
   inaudible anyway. **The fix narrows the false "played" reading from "any dead device" to "no
   default endpoint at all". It does not close it.**
5. **That host start-up survives an audio stack which will not build.** Added after review, and it
   is the half of the start-up fault that stays unmeasured.
   `Host_startup_builds_the_audio_stack_rather_than_deferring_it` runs on a machine whose audio
   works. It shows start-up goes *through* the guarded constructor — `AppHost.Build` resolves
   `SoundPolicyEngine` before it returns, and the test asserts the engine takes the player
   directly, so building a host necessarily builds the stack — but it cannot show
   start-up *surviving* a broken stack. That half needs the operator's audio service stopped.
   Proved to be a real limit rather than assumed: the plant that removes the guard kills
   `An_audio_stack_that_will_not_build_degrades_to_silence` and leaves this test passing. The two
   tests together cover the path; neither covers it alone, and the second must not be read as
   evidence about a machine with no audio.

### Residual: a hung endpoint that heals itself

Attempts against one endpoint are bounded — three failures and the adapter stops trying it — and
the strikes reset when the default endpoint changes. Without the bound, an endpoint that opens and
immediately dies would re-bind, stop and re-bind for as long as the process ran.

**The cost, stated plainly because it is the original defect in miniature: an endpoint that hangs
and then heals with no default device change stays silent until the default changes or the app
restarts.** "Silent until restart" is what issue #13 was filed about, and it survives here in a
narrow form. It is narrower than the defect it replaces — that one covered every device change,
this one covers only a device that fails three times and then recovers without Windows noticing —
and unlike the defect it is not silent in the log: entering that state writes one line naming the
endpoint and the number of attempts.

Nobody should read the closing of #13 as closing this.

### Residual: the silence says which kind it is, but only three lines up

An earlier draft of this section claimed the log could not distinguish one cause of silence from
another. That was too strong, and the correction belongs here rather than being quietly dropped.

Every failed attempt already writes its own distinguishing Warning before the silence is
announced: a rejected format logs the endpoint and `capability.Reason`; a hung endpoint logs
"stopped immediately after opening"; a stack that would not build logs "The Windows audio stack
could not be created". What collapses to one sentence is only the *terminal* summary and the
`SilentReason` readout — "it gave up on … after 3 failed attempts" — which says that the adapter
stopped trying but not what went wrong three times.

So this is an observability wrinkle, not a gap: the distinguishing evidence is always in the log,
three lines above the summary. A proper fault taxonomy carried through to the summary would be new
behaviour on an already large fix, and it is not worth adding here. Recorded so that anyone reading
`SilentReason` alone knows to look up.

### The hardware acceptance card — NOT RUN

Five rows. Rows 2, 3 and 4 need the operator's hands on real devices, and row 5 changes their
Windows sound settings and needs their consent. **None of the five has been run.** They are listed
so the state is recorded rather than assumed.

| # | Do this | Expect | Result |
|---|---|---|---|
| 1 | Headphones default. Raise a notice. | Heard in headphones. | **not run** |
| 2 | Unplug the receiver or power off Bluetooth. Raise one. | Heard on speakers. | **not run** |
| 3 | Reconnect, make default. Raise one. | Heard in headphones. | **not run** |
| 4 | Both connected. Change the default in Sound settings. | Heard on the new default. | **not run** |
| 5 | Disable every output, start, raise one; then enable one and raise another. | First silent and logged, second heard. | **not run** |

**The card must not be judged by ear alone.** Every successful bind writes one line at
Information naming the endpoint the player itself reports:

```
Sound output bound to Headphones (Arctis 7) ({0.0.0.00000000}.{...}).
```

Losing the output writes one line, once, naming which of the two silences it is — "Windows reports
no default output endpoint", or "it gave up on … after 3 failed attempts". Recovering writes one
line carrying what the silence cost: `N sound(s) were dropped while there was none`. So a pass and
a near-miss are distinguishable without trusting anyone's hearing.

**Row 4 may already pass on the unfixed build, and if it does, a fix that makes row 4 pass has
demonstrated nothing.** NAudio documents `WaveOut.DeviceNumber = -1` as "stick to default device
even default device is changed", which is the behaviour issue #13 reports missing. The two
reconcile if WinMM over WASAPI reroutes the mapper on a soft default change but does not survive
the current endpoint being *removed*. That predicts row 4 passing today and rows 2 and 3 failing.
The prediction is recorded here before the baseline was collected, so the baseline tests it rather
than being fitted to it. It is documented, not measured: measuring it needs the default device
changed, which is the operator's settings and was not touched.

### Considered and rejected: letting Windows do it

NAudio 3.0.1 offers `WasapiPlayerBuilder.WithDefaultDeviceStreamRouting()`, which asks Windows to
follow the default device with no application code at all. It was built, initialised with this
mixer's format, played and disposed — it works, and it is less code than what shipped.

It is rejected for one decisive reason: under routing there is no fixed endpoint, so
`WasapiPlayer.DeviceId` and `DeviceFriendlyName` are both null. The only readout left would be
what the dashboard separately believes the default to be, which is a different fact from what the
player opened, can disagree with it, and is exactly the kind of assertion satisfiable another way.
The whole acceptance above rests on the operator seeing which endpoint is bound. A supporting
reason: routing cannot start with no device at all, so the notification subscription would have
been needed regardless.

Recorded so nobody re-proposes it later as an obvious simplification.

### Facts established by compiling and measuring, not by reading

- `MMDeviceEnumerator` and the rest of `NAudio.CoreAudioApi` resolve from the NAudio 3.0.1
  metapackage. No new `PackageReference`.
- **`IMMNotificationClient` is `internal` in NAudio 3.0.1**, so the mechanism issue #13 names —
  a class implementing it, handed to `RegisterEndpointNotificationCallback` — cannot be written.
  The supported replacement is `MMDeviceEnumerator.CreateNotificationClient`.
- `WasapiOut` is `[Obsolete]` in 3.0.1, superseded by `WasapiPlayerBuilder`.
- The device on this machine mixes at 48kHz. The fixed 44.1kHz stereo float mixer is accepted
  unchanged in shared mode, because the Windows audio engine converts the sample rate itself. This
  is checked at every bind rather than trusted. It would stop being true under
  `WithLowLatency`, which takes the `IAudioClient3` path and does not resample.
- **A `WasapiPlayer` raises `PlaybackStopped` when it is disposed**, with a null exception. So the
  adapter's own teardown raises the same signal a dying device does, and a player being released
  on purpose has to be marked or every deliberate swap books a strike against a healthy endpoint.

### Two faults found in review, after the first commit

**A degradation was lost, and the app could no longer start without an audio stack.**
`MMDeviceEnumerator` is a COM activation and it fails on a machine whose audio service is stopped —
an RDP session, a headless build agent. It was created in a *field initialiser*, which runs before
its own class's constructor body, so no `try` inside that class could ever have caught it. The
exception left the player's constructor, and the container resolves the player eagerly during
host start-up. **A beep would have stopped the whole dashboard.** T1.14 had handled exactly this
and said "the dashboard will run silently"; T1.22 restructured the `try` around a different call
and lost it. Nothing in the diff removed the guard, which is why reading the diff would never have
found it.

It is fixed by making the seam *create* rather than *receive*: the thing that can fail is the
construction, and a parameter taking a finished object cannot express that. The failure now
degrades to a stand-in stack that reports no endpoint, so the ordinary no-device path handles it
with no special case, and the silence names its own cause rather than blaming a cable.

**The proof needs two tests and neither is sufficient alone**, which is worth stating because it is
the honest shape of the coverage. `An_audio_stack_that_will_not_build_degrades_to_silence` injects
the failure and shows the constructor never throws. `Host_startup_builds_the_audio_stack_rather_
than_deferring_it` shows the construction really is on the start-up path — it asserts the engine
takes the player directly, so a host build necessarily builds the stack. The second runs on a
machine whose audio works and **cannot** show start-up surviving a broken stack; that half is only
reachable with the operator's audio service stopped. Confirmed by planting: narrowing the guard so
it no longer covers the failure kills the first test and leaves the second passing.

**A flaky test shipped in the first commit — the fourth flake on this project.** It waited on a
state flag set inside the gate, then asserted a log line written after the gate was released, so
the assertion could read a log that was correct a moment later: 4 failures in 40 runs. Fixed in
the test, not the product — logging under a lock on the audio path would be worse. Both waits now
wait on the log line itself.

The fix was proved with the instrument that exposed the defect rather than by re-running and
hoping: with a 50ms sleep planted between the publish and the line, the old waits fail 5 of 5 and
the new waits pass 10 of 10. The control matters as much as the result — without showing the probe
still bites, ten green runs would only have shown the probe did nothing.

**The rule this leaves behind: wait on the thing you are about to assert, not on a neighbour of
it.** A second assertion in the same test had the same defect and had simply never been seen to
fail.

### Declined: reading the registration table instead of resolving the player

`The_sound_player_is_the_real_adapter` resolves `ISoundPlayer` from a built host. The proposal was
to read `ImplementationType` and `Lifetime` off the `ServiceDescriptor` instead — identical
protection for those two facts, and it would hand back the native surface the suite gained on this
task.

**Measured, and the second half does not hold, so it is declined.** Reaching the descriptors needs
`IReadOnlyList<ServiceDescriptor>` resolved from a built host, and `AppHost.Build` resolves
`SoundPolicyEngine` eagerly — which takes `ISoundPlayer` — so building a host constructs the real
audio stack *before it returns*. A descriptor read therefore constructs exactly as much as the
resolving test does. It hands back nothing.

Nor is this test the surface. `AppHostTests` alone builds 28 hosts, and five other test files build
more; every one of them constructs the audio stack. Converting this test would change one of at
least twenty-eight, for no reduction and a slightly weaker assertion.

### One new thing the suite itself now does, named for issue #12

`The_sound_player_is_the_real_adapter` resolves the adapter from the container, so one test in the
suite builds the *real* Windows audio stack. It already opened a real device before T1.22; it now
also registers a Core Audio endpoint notification callback and starts a background thread, and
releases both when the host is disposed.

This is named, not blamed. Issue #12 is an unexplained test-host crash at roughly 1 in 24 Release
runs (§5c), and a COM callback registered by managed code is the kind of thing that produces
exactly that signature if it ever outlives its object. Nothing here shows that it does — the
subscription is disposed before anything else in `Dispose`, and a test asserts it. It is written
down because the suite's list of "things that could crash a host" grew by one on this task, and
that list is the only place anyone will look.

### Role: eConsole

`GetDefaultAudioEndpoint` takes a role, and Microsoft defines them: `eConsole` for games, system
notification sounds and voice commands; `eMultimedia` for music, movies and narration;
`eCommunications` for talking to another person. What this application plays is a system
notification sound. `eCommunications` is rejected deliberately — a notice must not follow the audio
path of a call the operator is in.

## 5f · T1.23 — what the session id does not prove

The expanded row shows the first eight characters of the session id, carries the whole value in a
tooltip, and copies the whole value on a click (issue #15, commit `b97596d`). Design §9 was amended
in the same commit, so the authority on row anatomy names the element rather than going stale
around it.

Eleven tests cover it and five planted mutations confirmed they are load-bearing — most importantly
that copying the eight-character preview instead of the whole value kills a test, which is the
defect this task was most likely to ship. What follows is what none of that reaches.

### The three things the suite cannot reach

1. **That the real Windows clipboard received anything.** The fake records a string and answers
   yes. A wrong data format, an apartment problem, or a hold that outlasts the single attempt are
   all untouched by every test here.
2. **That the operator can paste it.** Nothing in-process observes that, and nothing could.
3. **`WindowsClipboard` has no test at all, and that is deliberate rather than an oversight.**
   Exercising it would write to the operator's real clipboard and destroy whatever they had
   copied, to prove a point about a row; save-and-restore is racy and still destructive. Every
   caller of the port is tested through a fake, and the adapter itself is not. If it is ever to be
   covered it is a **hardware-card row needing the operator's consent**, not a suite test.

Item 3 is the one a later reader is most likely to mistake for a gap somebody forgot to fill. It
was a decision, and it is recorded in the class as well as here.

### Residual: the failure marker is hidden while the row is collapsed

A copy that fails puts a static "copy failed" marker beside the id. It appears at the moment of the
click and stays until a copy works.

**It is hidden while the row is collapsed, not lost.** `CopyFailed` is instance state on the row
view model, `MainViewModel` caches rows in `_sessionRows` and does not rebuild one on collapse, and
the marker binds its visibility to the flag — so re-expanding the row brings the marker back.
Measured over the real visual tree with a failing clipboard: visible after the failure, absent
while collapsed, visible again after re-expanding, with the flag still set throughout.

**The true boundary is elsewhere:** the row is discarded, marker included, when the session leaves
the projection — `MainViewModel.Forget` at :383. That is correct behaviour rather than a defect; a
session that is gone should not keep reporting a stale copy failure.

An earlier draft of this section said the operator "would not see it again". That was wrong, and
pessimistically wrong, which is no better: §5f is the operator-facing artefact and a limit
overstated here is as misleading as one understated.

**Which of these is measured and which is true by construction**, because the two are different
kinds of claim and neither is asserted by a test:

- **True by construction:** the marker is per row. `CopyFailed` is an instance field on
  `SessionViewModel`, there is no static state behind it, and every row is its own view model — so
  one session's failed copy cannot mark another's row. No test asserts this; it follows from the
  shape of the type, and it would stop following if the flag ever moved to shared state.
- **Measured:** the re-expand behaviour above, over the real visual tree.

### Why success is silent, and why that was measured rather than assumed

The obvious design puts "Copied." in the tooltip. It cannot work, and the numbers say so:

- `ToolTipService.InitialShowDelay` is **1000 ms** — a full second of hover before a tooltip
  appears at all.
- `PopupControlService` — `OnPostProcessInput`, `ProcessMouseUp`, `DismissCurrentToolTip` — is what
  closes a tooltip on input, and it does not re-show while the pointer stays where it is.

So a tooltip can say nothing at the moment of a click, which is the only moment that matters. The
design removes the dependence on that answer instead of guessing at it: **failure gets a surface,
success gets none, and the absence of the marker is the success signal.**

**What was not measured, stated as such.** The end-to-end dismissal was not observed. An
automation-driven click does not produce the mouse input that dismisses a tooltip, so a headless
"the tooltip is still open" would have been an artefact of the harness rather than a fact about the
product. What was measured is the framework's own machinery and its delay defaults — which is
enough to settle the design, and is not the same claim.

### One correction carried forward

An earlier note in this task's reasoning said `Clipboard.SetDataObject` retries on a busy clipboard.
**It does not.** The four-argument retrying overload belongs to *WinForms*' `Clipboard`; WPF's
exposes only `SetDataObject(object)` and `SetDataObject(object, bool)`. The adapter makes one
attempt, because a hand-rolled retry loop would sleep on the dispatcher thread and freeze the
window — trading a visible failure the operator can act on for an invisible stall they cannot.

## 5g · T1.24 — the session title on the row, and what it does not prove

The row's context line now reads `Director — run the tests`: the session's title where it has one,
cut to 40 grapheme clusters, then the prompt exactly as before (issue #18). Design §9 and §3, the
markup comment, the hooks reference's Discrepancy 3 and one sentence of the TS were amended in the
same commit, because five statements stopped being true at once.

Fifty-seven tests cover it. Six planted mutations confirmed they are load-bearing — the table is at
the end of this section. What follows is what none of that reaches.

### The finding that changed the design, and was not in the brief

**The latch cannot live in the transition table, and a suite can be entirely green while the
feature never appears.** The events that carry a title are, in the main, the events whose
transition *declines*: a `PostToolBatch` on an already-Working session is `Ignored`, and that is
799 of the archive's 1,210 payloads; an `idle_prompt` `Notification` is `Ignored` too; `Moved`
returns null whenever nothing else differs. A latch inside the transition table drops every one of
those on the floor — and a test that hands the title to a state-*changing* event never walks the
path that loses it.

This is the same shape as the defect the task was commissioned to fix, one layer further down.
`SessionStart` had the field and never fired; the transition table would have had the latch and
mostly declined. Both fail silently, and both fail *with the tests passing*.

So the latch is an unconditional step in `Apply`, able to produce an `Applied` outcome and a
`SessionChanged` on its own. That widening was checked rather than assumed: `ApplyOutcome` reaches
only `EventConsumer.Report`, which uses it for counters and a log level, and nothing keys on
`Applied` for behaviour.

### The four things the suite cannot reach

1. **That a title ever arrives from Claude Code on the events this build reads it from.** The
   archive says 72 titles across 1,210 payloads and names which events carried them. Nothing here
   observes a live one: every test supplies its own. The wire contract for `session_title` is
   undocumented (Discrepancy 3), so there is no specification to test against either.
2. **That a rename is seen promptly.** A title lands on the next event that happens to carry one,
   and only 6% of payloads do. A session that is renamed and then sits idle keeps its old name on
   the row for as long as it stays idle. Nothing measures that delay, and nothing could without a
   live session to rename.
3. **That the tooltip appears.** The same limit as the id tooltip in §5f: an automation-driven
   hover does not produce the input WPF's tooltip machinery reacts to, so a headless assertion
   about a popup would be an artefact of the harness. What is asserted is that `TitleTooltip` is
   the whole title when the title was cut and **null** when it was not — null being what WPF reads
   as "no tooltip", so an empty popup cannot open.
4. **That a screen reader reads the row aloud correctly.** `AutomationProperties.Name` is asserted
   to equal exactly what the row draws. Whether a real screen reader announces it is untested here
   and would be a hardware-card row.

### Residual: an old title arriving late wins, and no guard is possible

A session is renamed; an event already in flight arrives afterwards carrying the previous title.
It wins, and the row shows the old name until the next event carrying the new one.

**This is recorded rather than fixed, because it cannot be fixed with what is on the wire.** Ingress
stamps events at *arrival*, not occurrence — hook payloads carry no timestamp of their own — so the
late event is stamped later and beats any comparison the domain could make. There is no title
version and no sequence number. A stamp comparison would in any case be a restatement rather than a
guard: `Apply` has one writer behind a FIFO channel, so arrival order is total and stamps are
monotonic *because of* that order.

And underneath the mechanics is the part no field would solve. **A stale title arriving late and a
genuine rename back to a previous name are the same observation, byte for byte.** Any rule that
rejected the first would reject the second, and Claude Code documents the second as real — a name
collision at startup renames a session with no operator action at all.

The trigger window is the gap between two loopback posts from one session, measured in
milliseconds. The failure is cosmetic, self-healing, and never wrong about state. It is asserted as
behaviour in `SessionTitleLatchTests`, in the place a reader would go looking for the guard.

### Two smaller residuals, stated so they are not mistaken for oversights

- **The tooltip is not length-capped, and the only bound is Kestrel's.** Issue #18 rules that a
  truncated title is shown in full on hover, and a cap would make the tooltip a second truncation
  with no way to read past it. Folding still applies, so a title full of line breaks cannot grow
  the popup vertically without limit — but nothing between the wire and the popup caps the string's
  length. The bound is therefore **Kestrel's default `MaxRequestBodySize`, 30,000,000 bytes**;
  `AppHost.ConfigureKestrel` sets the listener and the server header and never touches `Limits`, so
  the default is what stands. The title is one field inside that body, so it is at most that and in
  practice less. Accepted, with the number named so the residual is actionable.
- **A single grapheme cluster larger than the character ceiling shows as an ellipsis alone.** The
  ceiling cuts on a cluster boundary, and if the first boundary is already past the ceiling there
  is no boundary to cut at. Bounded and harmless, and it costs a row that could not have been
  rendered legibly anyway.

### What was measured rather than read

All on .NET 10, in a scratch console, before any of it was written into the code:

- **Cutting a title at 40 UTF-16 code units produces a lone surrogate** whenever the 41st position
  is an astral character — `U+D83D` for 👍 and for a ZWJ family, `U+D83C` for a regional-indicator
  flag. The result does not round-trip through UTF-8 and enumerates as `U+FFFD`. `é` cut that way
  loses its accent and stays legible, which is the quieter version of the same defect.
- **`StringInfo` cuts at 40 clusters keep all four whole.**
- **A cluster budget is not a length bound.** Forty clusters of a letter plus two hundred combining
  marks each is **8,040 characters** and passes a forty-cluster cut completely untouched. This is
  the whole reason there are two numbers in `SessionViewModel` rather than one, and it was not
  something the brief or the issue anticipated.
- **`TextBlock.Text` reads back only what was set through `Text`.** Content authored as inlines
  reads back empty, *whatever the count — one `Run` included*. Measured: `Text = "plain"` gives
  `Inlines.Count` 1 and reads back `plain`; one explicit `Run` gives `Inlines.Count` 1 and reads
  back empty; two and four `Run`s likewise read back empty. The UI tests read `TextBlock.Text`, so
  the new context line came back blank — and so, already, did the group header's four-inline
  "· 1 sessions · idle 0s", which those assertions had been blind to before this task touched
  anything. The helper now reads a `TextRange` over the block's content, which returns the text in
  every shape. **A blind spot here fails in the direction of a passing test**: "the row does not
  show X" passes when the reader cannot see X at all.
- **`Run.Text` binds two-way by default**, and a two-way binding onto a read-only view-model
  property throws while the template loads — taking the whole window with it, not one row. Hence
  `Mode=OneWay` on both runs, which is load-bearing rather than decorative.

**The first version of that first bullet said "two or more inlines", and the error cost a test.**
Fix cycle 1. `MainWindowTests.A_row_shows_its_prompt_in_the_mono_face` located its subject by
`candidate.Text == "draft a migration plan"`. The new context line's `Text` went empty, the
predicate stopped matching it, and it matched the *expanded* row's prompt block instead — which is
invisible while the row is collapsed. **The subject moved from a visible element to a hidden one
and the test kept passing**: the row's context line could lose its monospace face entirely, in
contradiction of §9, and nothing said so. Demonstrated by changing `MonoFont` to `UiFont` on the
row's context line and running the whole suite: 1217 passed, 0 failed.

The count was not merely the wrong threshold — it cannot discriminate at all, because the readable
case reports `Inlines.Count` of 1 too. So an audit asking "which blocks have two or more inlines?"
clears a single-`Run` block that is equally blind, which is exactly what happened: the sweep that
fixed three call sites cleared the fourth. **A measurement written down slightly wrong is worse
than one not written down, because the next reader trusts it.** The rule above is now stated by
cause rather than by count, with the table that shows why the count says nothing.

The repaired test selects on three things instead of one — inline-aware text, `IsVisible`, and
`Single` so an ambiguous match fails loudly — and asserts the face on the `Run` that carries the
prompt rather than on the block around it. Verified the way the defect was found: with `UiFont`
planted on the context line it now fails, alone, 1216 passed 1 failed.

### The premise sweep (five statements, not two)

The brief named two. The sweep found five:

| Where | Said | Now |
|---|---|---|
| `RowTemplates.xaml` | "The prompt is the session's name" | Rewritten: the title names it, the prompt says what it is doing |
| Design §9, session row | "prompt snippet (the session's name…)" | Replaced with the authorised text |
| Design §3, Exchange | "the prompt snippet is what identifies the session in the list" | Replaced; points at §9 for what names a session |
| TS §II.2 | "the session's **identifying** line" | "the session's **context** line"; the rest of the sentence stands, since its load is about the payload arriving inline |
| Design §12, Open questions | "Session naming: always derived from the latest prompt, or allow a manual rename that sticks?" | Deleted — it is answered: the name comes from Claude Code and the dashboard never sets one |

The hooks reference's Discrepancy 3 was also updated: its heading claimed we read `session_title`
on `SessionStart`, which stopped being true here.

### Never logged, proven two ways

`SessionTitleLoggingTests` drives a real ingest with a marker title on every event, deliberately
walking the two paths that *do* write lines — the Debug decline and the uncorrelated-completion
Warning — and asserts no emitted line contains the marker, with a control asserting those lines
were actually written. Planting the title into the decline template kills it.

The second test asserts the opposite and is equally necessary: the title *does* come back through
`{Event}` on a record and `{@Row}` on the view model. That is the measurement behind its
classification in `UnprotectedTextInventory`, which is meant to be measured rather than reasoned
into.

**The classification is `CarriesOperatorText`, and #15's `ShortId` precedent does not carry.** One
slot holds two kinds of value with nothing to tell them apart: a name the operator set, and — for
a session nobody named — a title a background model call wrote by summarising their first prompt.
A classification has to hold for every value the slot can carry, not for the common one. The
session id passes the test the title fails: Claude Code mints it and nothing the operator typed
reaches it.


### Fix cycle 1: the settled group that spun the loop

**A settled roster group left the consumer loop waking about a hundred times a second, indefinitely.**
Measured with the production 1.5-second window, not a zero-length one: 39 wakes in 600 ms against
an ungrouped control of 3, each one re-resolving every group from the Registry and posting to the
dispatcher thread.

The mechanism was one missing question. `DeadlineOf` returned a deadline whenever a roster group's
raw roll-up was `Unread`, and never asked whether that deadline had already passed — and a settled
group stays `Unread` until the operator acknowledges it. So the deadline stayed "pending" for ever,
the floor turned the negative wait into 10 ms, and the loop re-armed on the same past instant.

**The trigger is the feature's success path.** An orchestration finishes, the group settles, the
operator has not looked yet — which is exactly the state this product exists to leave sitting on
screen. A tray app whose first principle is that it never polls spent it burning a core.

The method is now `PendingDeadlineOf(group, now, window)` and returns null once the window has
elapsed. The name says what it answers, and taking `now` is what makes it able to answer.

**Four sentences here and in the code asserted the opposite of what the code did**, and that is the
part worth keeping. `WaitFor`'s remark said a past deadline "costs one short sleep rather than
spinning" — it cost an unbounded run of them; **the floor is a rate limiter, not a guard.** A test
called `A_deadline_in_the_past_is_floored_rather_than_spun_on` asserted one call of a pure function
while its name claimed the loop. This section said the wake happens once per settle. And the "what
the suite cannot reach" list correctly identified that the loop was untested and then **named the
harmless half of the gap** — real-time accuracy, which is `Task.Delay`'s — while the half that
mattered was what the loop did after the deadline passed, which was this code's.

That last one is the lesson. The instinct to write down the limit was right; the limit chosen was
the one that could not hurt anybody. A residual that names the comfortable half of an untested area
is worse than none, because it reads as though the area was considered.

`SettleSpinTests` closes the gap the rest of the suite left: `SettleWakeTests` only ever exercised a
pure function, and `RosterLoggingTests` stopped watching once the group settled. It runs the real
consumer, settles a group, counts wake-ups over a window, and compares against an ungrouped control
— so the number means something on any machine. It was shown failing on the unfixed code before the
fix went in.
### Was the harness capable of producing the other outcome?

Six plants, each verified present by md5 **before** any result was read, and each run to completion
with the count taken from the run's summary line rather than from a display:

| Planted defect | Result |
|---|---|
| The title read on the `SessionStart` arm only — the original issue #18 defect | **fails** — `Every_accepted_event_carries_the_session_title`, 7 of 8 cases (`SessionStart` still passes, correctly) |
| The title spent out of the prompt snippet's 140 characters | **fails** — `The_prompt_keeps_its_whole_budget_when_a_title_is_present` |
| The latch made conditional on the transition succeeding | **fails** — 6 tests, including `A_title_lands_on_an_event_whose_transition_is_declined` and `A_title_arriving_on_a_declined_event_reaches_the_screen` |
| The title added to the Registry's decline log line | **fails** — `No_log_line_anywhere_contains_the_session_title` |
| The cut done by character instead of by grapheme cluster | **fails** — all four cases of `A_cluster_straddling_the_cut_survives_whole` |
| The character ceiling removed, leaving the cluster budget alone | **fails** — `A_title_of_forty_enormous_clusters_is_still_bounded` |

**A third plant trap, found the expensive way and added to the two in §6.** Plant 1 was pulled with
`git checkout -- <file>`, which restored the file to **HEAD** and silently discarded the task's own
uncommitted change to it — leaving a tree that still compiled, still passed most tests, and no
longer contained the feature. It was caught only because the md5 taken after the revert did not
match the one taken before the plant. **Revert a plant from a copy of the file, never from git,
while the file carries uncommitted work**, and keep taking the md5 on the way back as well as on
the way in.

**And §5c's stale-assembly finding turned up a third time, in the same hour.** Counting the new
tests afterwards with `--no-build` read the assembly still carrying the sixth plant, so one test
was reported as absent rather than as failing, and the total came out one short. It was caught by
arithmetic — the per-file counts did not add up to the run's own total — and not by anything in
the tooling. The counts in this section are from a rebuilt tree.

### One observation that is not this task's to fix

Toggling `IsGrouped` on a realized window raises WPF binding errors from the group headers being
torn down — `IsStale` and `SessionCount` on `GroupViewModel`, neither of which this task touches.
The two view tests each realize their own window with the mode set beforehand, so they assert the
flat view rather than the toggle. Reported to the director rather than fixed here.

## 5h · T1.25 — roster grouping, and what it does not prove

A roster is a named set of session names; a session whose current title is in one is grouped by it,
wherever it is running (issue #16, part 1 of 2). The operator UI is T1.26 — **after this commit
there is no way to create a roster from the running application**, by design, and the tests build
them directly.

Sixty-two tests cover it and four planted mutations confirmed they are load-bearing. TS §IV.3 was
amended in the same commit. What follows is what none of that reaches.

### The two findings that changed the design

**1 · The tick is fifteen seconds and the settle window is one and a half.**
`EventConsumer.DefaultTickInterval` is `TimeSpan.FromSeconds(15)`. A settle evaluated only on that
tick would have delivered the group's finished state — and its done chime — **up to fifteen seconds
after the work finished**, while every test passed, because tests drive the clock directly and
never wait on the loop. The operator chose 1.5 s deliberately; a tick interval is an implementation
detail and must not overrule it.

The loop now waits until the earlier of the next tick and the next settle deadline. **It is a wake
rather than a poll only because a deadline that has passed stops being reported as pending** — that
was got wrong first time and is recorded below under the fix cycle. With that in place the deadline
is known the instant a group goes quiet, so one extra wake-up per settle is enough. A fast repeating
timer would have caught the same window by firing thousands of times an hour on an idle machine,
which is polling with a smaller number.

**2 · The settle needs no history at all.**
`Session.EnteredAt` is when a session entered its current state, and the Registry advances it *only*
on a real state change. So for a group whose members have all stopped, the latest `EnteredAt` among
them is exactly the moment the last one stopped — and the displayed state becomes a pure function of
the group and the instant it is asked about. `Group` stays "the shape only", as its own remark says.

`LastActivity` would have been wrong, and the difference is what makes this work: it advances on
events that change no state — a tool batch on a session already working, a title latching — so
measuring quietness with it would restart the window on things that are not the session going
quiet.

The only history left is the mis-mark monitor, which by definition compares two instants.

### Where the roster is applied, and what `Session.Group` became

Not the Registry, and the disqualifier is stronger than "config does not belong in the domain":
**there is no event for "the operator edited a roster"**, so a stamped key could only be corrected
by walking the dictionary and rewriting records — a mutation outside the event stream, in a store
whose whole design is that every value it writes comes from the event being applied, so a replay
rebuilds the same world.

So the roster is an overlay computed on read, and `Session.Group` was renamed to
**`Session.WorkspaceGroup`** in its own commit (`93f3e80`). `Group` promised "the group" while
meaning "the group observable reality implies", with a truer notion sitting above it — the defect
class that has cost this project three fix cycles in a week, caught here before any code could be
written against the wider reading. Every changed line outside `Session.cs` in that commit is the
identifier alone, checked by pairing the diff.

### What the suite cannot reach

1. **That the operator can make a roster.** There is no UI until T1.26. Everything here builds a
   `RosterBook` directly, so nothing observes the path a real roster will actually arrive by.
2. **That the settle window is the right length.** 1.5 s is a guess and is treated as one. Nothing
   here measures a real hand-off; the mis-mark warning is the instrument that will, and it has
   never run against live traffic.
3. **What the loop does while a settle is pending, in real time.** `WaitFor` is asserted directly in
   both directions, and `SettleSpinTests` now watches the running loop before and after a deadline
   passes. What remains unproven is the real-time accuracy of the wake itself, which is
   `Task.Delay`'s. **The earlier version of this list named that limit and only that limit, which
   was the harmless half of the gap** — the half that mattered was what the loop did once the
   deadline had passed, and that half was this code's. See the fix-cycle note below.
4. **That two sessions sharing a rostered name behave sensibly beyond joining.** #16 accepts the
   collision; the row-level consequences are T1.26's.

### Residual: a member renamed out mid-settle never sounds

A member whose finished notice was suppressed, and which then leaves the roster before the group
settles, never sounds at all. Accepted: the alternative is a done chime triggered by a rename,
which is worse.

### Residual: group mute does not follow a session across a roster edit

**Per-session mute follows the session, always** — that is the one the operator set deliberately,
and it is untouched by any roster edit. **Per-group mute does not follow.** A session muted through
its workspace group becomes audible when it joins a roster, because the roster key is not in the
muted set, and the reverse. Defensible — group mute means mute-this-group, and the session left
that group — but **silent**, which is why it is written down here rather than left to be
discovered.

### Residual: a malformed rosters section loses the whole settings file

Ruled to be consistent rather than special: a tolerant converter on the one property being added,
while `"port": "abc"` still loses the file, would leave two behaviours for one class of fault and
the newer one would look like the rule. The application still starts on defaults — "degrade, never
crash" holds — but the operator's other settings are lost for that run. That is pre-existing
behaviour of every field in the file and is filed separately.

Load does **not** rewrite the file. A read that triggers a write is a new write path, and the one it
would use is the non-atomic one (issue #7). The corrected shape reaches the file the next time the
operator edits a roster.

### The inventory cannot see a roster's members, and a test closes the gap instead

`UnprotectedTextInventory` scans **public instance `string` properties**, so a *collection* of
strings is invisible to it. A roster's members are session titles — operator text by T1.24's
ruling — and would have been fully exposed to `{@Roster}` while the guard reported nothing. The
guard has been claiming more than it delivers, and that is filed as its own issue rather than
widened inside a feature commit.

What closes the real exposure meanwhile is `RosterLoggingTests`: a real ingest, a marker member
name, the settle and mis-mark paths both walked, and a control asserting those lines were actually
written. `Roster.Name` is classified as an identifier — operator-authored, never derived from a
prompt or an answer, and deliberately logged so that the mis-mark warning can name something.

### Was the harness capable of producing the other outcome?

Four plants, each verified present by md5 **and verified back**, each run to completion with the
count taken from the run's summary line:

| Planted defect | Result |
|---|---|
| The roster roll-up reverted to the single-session order | **fails** — 3 tests, incl. `Working_outranks_finished_in_a_roster_group_only` |
| The settle window set to zero | **fails** — 5 tests, incl. `A_quiet_roster_group_reads_finished_only_after_the_settle_window` |
| The sound suppression removed | **fails** — 4 tests, incl. `Members_finishing_one_at_a_time_produce_one_done_sound` |
| Rule 4 disabled in the store | **fails** — 6 tests, incl. `A_name_added_to_a_second_roster_leaves_the_first` |

**The third plant found a test of mine that was passing for the wrong reason**, which is what plants
are for. `RosterLoggingTests.Only_the_group_sounds_when_a_member_finishes` claimed to prove the
suppression end to end, and did not break when the suppression was deleted — because nothing in the
fixture subscribed the sound engine to the Registry, so no member was ever announced to it and the
only done sound in the run was always the group's. The fixture now wires it exactly as `AppHost`
does, and with that line in place the plant kills the test. **A test that cannot fail is worse than
a missing one, because it is counted.**

## 5i · T1.26 — forming and editing a roster, and what it does not prove

T1.25 built the behaviour and none of it was reachable. T1.26 is the half the operator can see: tick
rows to form a group, an inline row asking whether to remember it, right-click removal, and the
roster's name on the group heading (issue #16, part 2 of 2). Design §9 gained three bullets in the
same commit.

Twenty-four tests cover it and three planted mutations confirmed they are load-bearing.

### The finding, and it is the same question as last time

**A roster edit happens on the dispatcher; the roster book is read on the consumer thread.** The
consumer re-resolves groups only after a drain or a tick, and the tick is fifteen seconds. So an
edit that did not wake it would leave a dissolved group still able to nudge and a new group unable
to settle, for up to fifteen seconds, **with the screen already right** — the two halves of the
product disagreeing, and no test able to see it because tests drive the clock.

That is T1.25's spin in a different costume: there, a deadline nobody re-examined; here, a
membership change nobody re-examines. It was found by asking the same thing — not "does it work"
but "what happens after, and how long does the wrong answer last".

The fix is a synthetic event of the kind `SoundCommand` already is: it names no session, carries
nothing, never reaches the Registry, and its whole job is to have woken the loop so the settle pass
that already runs after every drain re-reads membership. **It is published by `RosterStore.Replace`
rather than by the caller** — the same principle that put issue #16's rules 4 and 6 in
`RosterBook`. A caller that has to remember to announce its change is a caller that will forget,
and T1.26 is not the last one.

`RosterEditWakeTests` runs the consumer with a **fifteen-minute** tick and asserts `TickCount` is
still zero when the edit has been observed, so nothing in it can be explained by a tick having
happened.

### The constraint the design rests on

**A member name is never typed. It is only ever copied from a row.** T1.25 matches exactly —
ordinal, case-sensitive — and that is sound only because a stored name is a copy of a title the
session itself reported. A typed name would make "exact" something else, and the failure is silent:
a name that looks right, matches nothing, and reports no error anywhere.

So the operator ticks rows, and the only text input in the whole window is the **roster's** own
label, which is compared against nothing.

**Two nets, and the second is the stronger one.** `RosterUiGuardTests` asserts the exact set of text
bindings in the markup rather than merely omitting one, because a convenience added later would
otherwise be a one-line change with no test to argue with — and it now *enumerates* the application's
markup files rather than naming them, so a panel in a new `.xaml` cannot arrive unscanned. But
`MainWindowTests.Selection_mode_and_the_prompt_render_without_binding_errors` counts the `TextBox`
controls in the realized visual tree, **in the state with the most inputs there is** — selection mode
on, prompt open — and finds exactly one. That is a runtime count over the whole window, so it would
catch a box added in code-behind or in a file no scan opened.

What neither reaches is a panel that opens from a menu: the scan would see it only if it were markup
under the app, and the runtime count never realizes that state. The measured position today is three
markup files, one `TextBox`, no editable `ComboBox`, no `RichTextBox` or `PasswordBox`, and no
code-behind text binding.

### Selection is a mode, and the motion rule decided that

Three shapes were considered and one is disqualified rather than merely weaker. **Hover-reveal is
ruled out by the motion rule**: either the row reflows when the tick appears, which is the row
moving, or the width is reserved permanently and it is the always-visible checkbox in a disguise.
"Nothing else moves" is not only about storyboards.

A permanent tick costs width on every row for something done rarely, and competes with the one
element §9 protects — the prompt snippet's budget. So: a mode, and the answer to "a state can be
wrong" is that **the state is never invisible** — the header carries `Selecting · n chosen` and both
ways out of it.

It also resolves a conflict the others do not. A row is a `ToggleButton` whose click expands it; a
tick inside it would depend on the inner control marking the click handled, and would leave one
gesture with two meanings for ever. In the mode a click selects and does not expand.

**Two, not one.** A group of one would gain the settle window and the done suppression, so a single
session's finished chime would be delayed for no benefit — and that chime is what this product
exists to deliver. Rules 4 and 6 can still *reduce* a roster to one member, and such a group renders
normally; that is asserted separately.

### The prompt is a row, not a dialog

A modal would need a port whose adapter is then deliberately untested — the shape this project
already carries once for the clipboard, and a second is a real cost. As a row it is driven by the
same harness that realizes a window and invokes a command, so both paths are proved by the thing the
operator actually uses.

**An unanswered prompt is a declined one, and that is a correctness argument rather than a
convenience.** The window can be used and dismissed with it showing. That is safe only because the
group is already formed and already unpersisted, so no answer and "no" leave the same state — which
is what makes an ignorable prompt an acceptable one. Asserted by comparing the two states rather
than by describing them.

### The four things the suite cannot reach

1. **That the operator can find the mode.** Discoverability is not testable here and the affordance
   is one button.
2. **That the inline prompt is noticed.** It can be ignored in a way a modal cannot. The design
   makes ignoring it safe; nothing here shows that anybody reads it.
3. **That right-click actually opens the menu.** The command and its `CanExecute` are asserted, and
   the menu is markup; an automation-driven right-click does not produce the input WPF's context
   menu reacts to, so a headless "the menu opened" would be an artefact of the harness. Same limit
   as T1.23's tooltip.
4. **That a remembered roster survives a real restart.** The write is asserted through a recording
   port, and the file's round trip was proved in T1.25. Nothing here starts the application twice.

### Residual: the shutdown save is what makes declining work, and only a tripwire says so

A declined group lives in `RosterStore` and nowhere else. Nothing writes it, so it is gone when
those sessions end. **That holds because `Program.cs` saves the window position as
`Load().Settings with { Window = … }` — it re-reads the file and overrides one section rather than
serialising what is in memory.** A reasonable-looking refactor to "serialise the settings we already
have" would silently persist every declined one-off, and the operator would find groups they said no
to back again after a restart, with nothing in the log.

`RosterUiGuardTests` pins it by reading the source, which is a weak assertion used because the exit
path is not reachable from a test. It is a tripwire, not a proof, and it is labelled as one.

### Two T1.25 residuals become operator-reachable, which is a different thing to accept

Both were recorded in §5h as reachable only by a rename. Removal makes them reachable by a
deliberate action, so they are re-stated here rather than pointed at:

- **A member removed after it finished never sounds.** Its own Finished notice was suppressed while
  it was in the roster, and the notice fires on state entry, which has passed. If the group had not
  yet settled, no done sound is produced for that session at all.
- **Its per-group mute does not follow it.** A session muted through its roster group becomes
  audible in its workspace group, and the reverse.

### Removal's blast radius, stated because it looks like a bug

**Removal is by name, so one right-click can move two rows.** Two live sessions can share a rostered
name and both join (#16 accepts this); removing the name removes both. It is deliberately not
special-cased away — the second row moving is what the store did, and hiding it would be the UI
lying. Asserted.

**A removed session is not removed from the dashboard.** It returns to its workspace group, which is
why §9 and the menu item both say "Remove from group".

### One observation about the row list, not fixed here

Changing what a row *is* at a given index — a prompt appearing above a group whose key also changed
— makes WPF evaluate the old template's bindings once against the new item before swapping the
template, which `BindingErrorWatch` reports. It is transient and invisible on screen, and it is the
same class as issue #23.

**An attempt to fix it by teaching `Reconcile` to insert rather than replace was written and then
reverted**, because it did not fix this case: the group's key changes at the same moment, so the
replacement is unavoidable there. Keeping a change whose stated reason had turned out to be false
would have been the defect this project has paid for repeatedly. The two window tests instead
realize a window whose state is already final, which is what they are about anyway.

### Was the harness capable of producing the other outcome?

Three plants, each verified present by md5 **and verified back**:

| Planted defect | Result |
|---|---|
| Removal never reaches the store | **fails** — 4 tests, incl. `Removal_takes_the_name_out_of_the_roster` |
| Accepting the prompt persists nothing | **fails** — `Remembering_writes_the_roster`, `The_roster_can_be_renamed_in_the_prompt` |
| The heading falls back to a directory label | **fails** — 5 tests, incl. `Ticking_two_rows_forms_a_group_at_once` |

**The first version of this table said 6, and the number was the problem rather than the guard.**
The count was honest about the run; the plant was not honest about what it tested. Disabling the
roster branch by rewriting its condition to `Kind == GroupKeyKind.Session` also *redirected the
session-kind case into it*, so a sixth test —
`MainViewModelTests.A_group_for_a_session_with_no_workspace_shows_no_path` — died of a second,
unintended mutation that happened to travel with the first. Re-planted as
`Kind == GroupKeyKind.Unknown`, which disables the roster branch and touches nothing else, it kills
**5** — the same five, every time.

**A plant that produces the wrong count is more often the wrong plant than a wrong guard.** This is
the `head -8` family arriving from the other direction: not a truncated instrument, but one
measuring two things at once and reporting both under one name.

A fourth attempt — disabling the heading branch with `if (false)` — **did not compile**, because
unreachable code is an error here. That is T1.22's trap and the rule held: no result was read from a
failed build, and the plant was re-shaped into one that compiles.

## 5j · T1.28 — hooks that survive a closed dashboard, and what it does not prove

T1.27 got no section here, and that was right: an icon proves nothing about behaviour. This is the
opposite case. **T1.28 changes the path every event arrives on** (issue #29), and it carries an
acceptance criterion nobody has observed.

Eight HTTP handlers, added at every start and removed at every quit, become one command handler
running `post-status.cmd` in the data folder. The script reads `listening.txt`; finding none, it
exits having opened nothing. So the hook is correct whether a dashboard is running or not, and it is
installed once and left alone.

**Two things in §5d are superseded here**, and both are pointed at from there as well as from here.
Its evidence row cites `HookLifecycleTests`, which this task deleted — `IngressAnnouncementTests`
makes the same claim about `listening.txt` and `port.txt`. And its accepted residual, the
`allowedHttpHookUrls` entries that accumulate because nothing removes them, is closed: a command
hook is not on that allowlist, and `--remove-hooks` clears what earlier builds left. **The ruling
that accepted the accumulation was right about the design it was made about**, which is why it
stands there rather than being edited away.

### The criterion nobody has observed, stated first

**§6.10 — "with the dashboard closed, no hook error appears in a Claude Code session, and a prompt
is not noticeably slower" — is NOT OBSERVED.** It is the whole point of the task and nothing here
evidences it.

It cannot be evidenced from this work. It needs the handler in the operator's own
`~/.claude/settings.json`, on their machine, in their sessions — and nothing has been installed
there. Their dashboard is untouched and their settings file has never held this handler.

What *is* measured is the mechanism underneath it: with no `listening.txt` the script opens no
socket, prints nothing on either stream, and exits 0 in 65 ms. That is a good reason to expect
§6.10 to hold. It is not the same claim, and the difference is the whole of why this paragraph is
first.

### What the isolated instance could not stand in for

The live verification ran a second dashboard with `CLAUDE_DASHBOARD_HOME` and `CLAUDE_CONFIG_DIR`
pointed at scratch folders, against a settings file holding only our handler, driven by a single
`claude -p` run. **It evidences the mechanism and nothing about the operator's world**:

- not their real settings file, with their own hooks beside ours and whatever a year of edits left;
- not eight events under load — one short session fired three;
- not a long-lived session, where `Notification`, `PostToolBatch` and `StopFailure` live;
- not a restart, a logoff or a real clean quit (see the withdrawal note below).

### What was verified live, and against which version

**Claude Code 2.1.251**, 2026-08-30. `SessionStart`, `UserPromptSubmit` and `Stop` arrived through
`cmd.exe /c post-status.cmd` and reached the archive, all carrying one session id.

**That check exists because `args` is documented and not observed.** A Claude Code that ignored it
would run `command` alone, the hook would do nothing, and the symptom would be a dashboard receiving
no events — indistinguishable from a quiet day. This repository has met documented fields that
behave differently in practice; the Discrepancies section of the hook reference is what that costs.

**`SessionEnd` did not arrive** in that run and was not investigated. Recorded so nobody reads three
events as a complete list.

### The measured numbers, with the conditions that make them true

Per invocation of `post-status.cmd`, ten runs each, on this machine on 2026-08-30:

| Condition | Cost |
|---|---|
| dashboard listening, payload delivered | **97 ms** |
| no `listening.txt` — the dashboard is closed | **65 ms** |
| `listening.txt` naming a port nothing answers | **~1.06 s** (reviewer 1062 ms; author 1.09 s) |

**The dead-port case is a timeout and not a refusal, and that is measured here rather than claimed
generally.** A loopback connect to a free port normally fails instantly; on this machine it spends
the whole `--connect-timeout`, which dropping the timeout to 0.25 s confirms — the cost falls to
0.34 s, proportionally. It is probably a firewall dropping the SYN. **On a machine that refuses fast
the cost is near zero**, and nothing here establishes which kind of machine any other operator has.

One second was kept rather than shortened. The cost falls only between a hard kill and the next
start, the hook is `async` so no turn waits for it, and a shorter timeout would risk dropping a real
event to buy something nobody can see.

### The residual, and which exits reach it

`listening.txt` is withdrawn at four exits: the process exception handlers, `SessionEnding`, the
ordinary quit, and the `finally` in `Main`. **The fourth was a hole in the shipped code** — both
`catch` blocks return without reaching the ordinary quit, so a throw after the bind and before the
window ran left the old lifecycle's handlers registered on an exit that was otherwise orderly.

**A kill reaches none of the four**, and that was observed: the isolated instance was killed and
`listening.txt` survived with the last bound port. Until the next start the script posts there, and
if something else has taken the port it receives the operator's prompts — Impl §9.3's hard-kill
exposure, in a new place. The unconditional overwrite at every start is what bounds it.

**The clean-shutdown deletion was not observed live.** Quitting the isolated instance needs its tray
menu, and driving that means injecting input into the operator's desktop, which was declined. It is
proved by unit tests at all four sites plus a source tripwire counting the call sites in
`Program.cs` — the same shape as §5i's shutdown-save tripwire, and labelled as one.

### Two defects found by running things rather than reading them

Both were found while doing something else, and both are worse than the feature they interrupted.

**A duplicate key in Claude Code's settings would have stopped the dashboard starting.** Two `"Stop"`
keys in one object is legal JSON and is what a hand merge of two blocks produces. `JsonNode` builds
its dictionary lazily, so the file parses and the throw is an `ArgumentException` from the first
indexer that touches it, arbitrarily far away. Every caller catches `JsonException`; none catches
`ArgumentException`. Before T1.28 that cost a failed switch. T1.28 puts a settings read on the
startup path, **so one duplicate key would have stopped the dashboard starting** — and a tray
application that will not start does not present as a configuration error. It presents as the
product being gone.

**A `"type"` that is not a string did the same thing**, through `GetValue<string>()` on a numeric
node, at two sites on the same startup path. Found by review, and the hole is older than this task —
the previous `IsOurs` had the same read. What was new was the claim, written in this repository's
strongest register, that the class was closed.

**`--remove-hooks` would have stripped the operator's comments from a file it had nothing in.** The
settings writer decides "did anything change" by comparing read text with rendered text, and
rendering preserves neither comments nor formatting — so a removal that removed nothing still
counted as a change on any hand-formatted file.

### The lesson the review left, which is not about hooks

Both must-fixes were **a remark more confident than the code under it**. The file-name confinement
and the "every failure is a `JsonException`" claim were each true as intent and false as fact.

**A comment that overstates is worse than no comment, because it stops the next reader checking.**
The same applies to a guard: `The_startup_path_checks_the_hooks_and_does_not_write_them` scanned the
whole file while promising something about one method, and was renamed rather than narrowed —
the wide scan is the better guard, and it was the name that was wrong.

### Was the harness capable of producing the other outcome?

Four plants, each verified present by md5 **and verified back**:

| Planted defect | Result |
|---|---|
| the `nul` redirect removed from the `call` | **fails 1** — `With_no_curl_it_says_nothing_and_still_exits_zero` |
| `listening.txt` deletion removed from `Withdraw` | **fails 1** — `Withdrawing_takes_the_announcement_away` |
| the missing-file path changed to `exit /b 1` | **PASSES — no test fails** |
| the identity rule reverted to URL matching | **fails 23** across three classes |

**Plant 3 not biting is a finding, not a gap.** An inner `exit /b 1` cannot reach the process: `call
:post` returns and the unconditional `exit /b 0` on the next line overrides it. **The guarantee
lives in that one outer line and not in per-branch discipline** — so the plant was re-aimed at the
outer `exit /b 0`, and **23 tests fail**. The harness sees an escaping exit code perfectly well.
That finding is now in the script's own header, because the person it protects is the next one to
edit the script.

**Plant 1 deserves the same second look.** Only one test fails, and it is the missing-`curl` case,
because `curl`'s own `-s -o nul` already silences every path that runs normally. **The redirect is
load-bearing for exactly one measured branch: the one that only runs when something has already gone
wrong.** Measured without it, `cmd` writes `The system cannot find the path specified.` to stderr and
sets errorlevel 3. That is the trap the whole script is shaped around — on `UserPromptSubmit` and
`SessionStart`, Claude Code feeds a hook's stdout to the model as prompt context, and nothing in the
transcript would show it.

Two further plants covered the fix cycle, both reverted byte-identical:

| Planted defect | Result |
|---|---|
| the pre-fix `ForeignScriptPaths` filter | **fails 1** — the operator exec-form test, and only that |
| the pre-fix `GetValue<string>()` type reads | **fails 6** — 4 of 5 theory cases, plus both installer theories |

**Four of five is not a weak theory.** The fifth case is `"type": null`, where the JSON null makes
the indexer return a null node, so `?.` short-circuits before the unsafe call and the plant cannot
reach it. Recording that is the difference between a plant table and one anybody can trust.

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
- **Sound follows the default device, and the claim is narrow** (§5e, T1.22, issue #13). The false
  "played" reading is narrowed from "any dead device" to "no default endpoint at all"; it is not
  closed. One residual keeps the original defect's shape: an endpoint that hangs and then heals
  with no default change stays silent until the default changes or the app restarts. The five-row
  hardware card has not been run, in whole or in part.
- **Every event now arrives through a script, and the criterion that matters is unobserved** (§5j,
  T1.28, issue #29). The hook is installed once and left alone, so a closed dashboard no longer
  makes Claude Code print an error — but **§6.10 has not been seen**: nothing is installed in the
  operator's own `~/.claude/settings.json`, and only they can observe it there. The mechanism is
  measured (65 ms, silent, no socket opened) and the mechanism is not the criterion. A hard kill
  still leaves `listening.txt` naming the last bound port until the next start.
- **Phase 1 is gated, not finished.** Every observation here holds; the phase's exit criteria in
  Part 2 do not, while §5 stands and while §5b's open row stands.
