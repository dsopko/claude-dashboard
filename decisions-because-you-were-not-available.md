# Decisions I had to make because you were not available

You went to bed on the night of 2026-08-25 and told me to run to the end of
Part 3 without waking you. Every choice below is one I would usually have put
to you first. Each entry says what I decided, why, and how to undo it.

Read the entries in order. The newest is at the bottom.

---

## What changed on your machine while you were asleep

Read this part first. Everything is reversible and the undo is beside it.

| What | Where it is | How to undo it |
|---|---|---|
| **The dashboard you were using — untouched.** | Your build at `%LOCALAPPDATA%\ClaudeDashboardApp\` was not stopped, not restarted and not replaced. It is still the one from 25 August, still on port 52789. | — |
| **Your `~/.claude/settings.json` — untouched.** | T1.18 will change it. It backs the file up first, to a plain copy you can put back by hand without the dashboard. | — |
| **A new optional setting, `CLAUDE_DASHBOARD_HOME`.** | Ships in the product. Absent means exactly today's behaviour, so it does nothing unless you set it. | Ignore it. |
| **Three new bug reports.** | GitHub issues #5, #6 and #7. None of them blocks you. | Close them. |
| **Two files hidden from `git status`, not deleted.** | `AGENTS.md` and `.agents/` are in `.gitignore`. Both files are untouched on disk. | Delete the last block of `.gitignore`. |
| **A document that something overwrote at midnight.** | `docs/claude-dashboard-execution-plan.md` was replaced by an older copy at exactly `00:00:00`. I restored it from git. | **See D2. Something on this machine writes into this folder on a schedule and I could not find out what. It could hit source code as easily as a document.** |

---

## D1 — Worktrees: dropped, not deferred

**2026-08-26, before T1.15**

**What you saw.** You said you did not know what worktrees were.

**The background.** All three sessions — director, coder, reviewer — share one
folder on disk. When two of them build at the same time, they write to the same
`bin` and `obj` folders and corrupt each other's results. This happened three
times. A git worktree gives each session its own copy of the folder, so the
collision cannot happen.

**The decision.** I dropped worktrees. We keep the rule we already use: only one
session touches the tree at a time.

**Why.** The rule has held since I wrote it. Worktrees cost a day of setup and
change how all three sessions work. A structural fix for a problem that the
procedure already stops is not worth that cost now.

**To undo.** Tell me to move the coder and the reviewer into worktrees. It is a
half-day of work and it does not touch product code.

---

## D2 — The execution plan was overwritten at midnight; I restored it

**2026-08-26 00:00**

**What happened.** `docs/claude-dashboard-execution-plan.md` was replaced with an
older copy at exactly `00:00:00.03`. The exact time says a scheduled task did it,
not a person and not one of the three sessions. The old copy removed 152 lines
of amendments I had already committed — the corrected Design Document
references, the "green is not evidence" standing rule, and the T1.12b and T1.13a
task entries.

**The decision.** I restored the file from the last commit. I kept the
overwritten copy for you at:

    C:\Users\daves\.claude\jobs\3314c264\tmp\execution-plan-overwritten-at-midnight.md

**Why.** The committed version is the correct one. Everything the old copy
removed was ratified by you.

**What I could not do.** I do not know what wrote the file. Nothing in the repo
schedules a write. **Look for a scheduled task or a sync tool that runs at
midnight and writes into `C:\Projects\Claude`.** If one exists it can do this
again, to source code as easily as to a document.

---

## D3 — `AGENTS.md` and `.agents/` added to `.gitignore`

**2026-08-26, before T1.15**

**The decision.** You told me to ignore them. I added both to `.gitignore`.

**Why.** "Ignore" in git has a precise meaning and it is the one we want. Our
main safety check between sessions is that `git status` is empty before anyone
starts work. Two permanent untracked entries make that check useless, because
they train everyone to skim past a dirty status. Ignoring them restores the
check. The files themselves are untouched and still on disk.

**To undo.** Delete the last block of `.gitignore`.

---

## D4 — One test fix folded into T1.15

**2026-08-26, in the T1.15 prompt**

**The decision.** I told the coder to fix `A_malformed_settings_file_still_starts_and_logs_the_reason` inside T1.15, instead of raising it as its own task.

**Why.** That test is the only one that starts a real host, and it binds the
fixed port 52789. Your dashboard is running on that port right now, so the test
fails while you are using the application. T1.15 is the task about who is allowed
to bind that port, so the fix belongs to the same subject. Without it the coder
cannot get a clean test run tonight at all.

**The rule I bent.** My own standing rule is "never merge two tasks into one
prompt". This is a one-line test change, not a second feature.

---
## D5 — CLAUDE_DASHBOARD_HOME ships as a real feature
Coder measured that redirecting LOCALAPPDATA does not move the data folder —
Environment.GetFolderPath resolves the known folder through the shell. So two test instances
cannot share a data folder that is not the operator's without a new surface.
Approved as an environment variable, not a command-line switch, and approved to SHIP rather
than be internal-only. Reasons: without it T1.15's four main criteria get no live run at all,
and "green is not evidence — launch it" is a standing rule; it matches CLAUDE_DASHBOARD_TOKEN,
an idiom the product already has; and it has real operator value for a portable install or a
roaming profile. Conditions: an unusable value degrades to the default and logs once; the
effective root is logged at startup; the specification is amended before the code lands.
To undo: delete the override; nothing else depends on it.

## D6 — The single-instance mutex is keyed to the data folder
Because the data folder can now move, "one resident process" needs a scope. The mutex name and
the bound port must observe the same thing, or a second instance can find the mutex held by an
instance that never bound the port the second one is looking at. Keyed both to the same root.
Director's addition: the hash must be SHA-256, not string.GetHashCode(), which .NET randomises
per process — that would have made the feature silently not work while every in-process test
passed.

## D7 — One test split into two
`A_malformed_settings_file_still_starts_and_logs_the_reason` could not simply be given a free
port: a malformed file always falls back to the default port, so "malformed" and "names a free
port" cannot both be true of one settings object. Split into a parse-failure test that starts
nothing, and a new test that starts a host on a free port from a valid file. The new test is
stronger than what it replaces — the old one bound the default port and never asserted the
settings port reached Kestrel at all. What is lost: nothing now starts a host from a malformed
file. Required in exchange: the malformed test asserts the settings equal the defaults, the
start test uses defaults-plus-a-port, and the gap is written down in the test file.

## D8 — Two rejections stop a task
If the reviewer rejects the same task twice tonight, I stop the task and leave it for the
operator rather than talk it through a third rejection with nobody awake to referee.

## D9 — A stray line in .gitignore, and a correction to the reviewer
The reviewer found a bare markdown code fence at .gitignore:63 and attributed it to my commit
3fde041. git blame puts it in 284ce2a, the original scaffolding of 2026-08-23. Harmless — it is
a pattern matching a file named ``` and no such file exists. I audited every tracked
non-markdown file for the same defect: that line is the only one. To be deleted.

## D10 — A taken port is not proof another copy of the dashboard is running
Found by the reviewer, ruled into T1.15 while the coder was building it.
The port is fixed at 52789. After a hard kill it is free, and any other process can take it.
So "the bind failed" does not mean "a second copy of us is running" — and a dashboard that
believed it did would silently never start, with no message the operator could act on.
Ruling: the mutex decides; the port corroborates. Same shape as the earlier ruling that a
Notification is the primary signal for a permission prompt and PermissionRequest only
corroborates. Where the mutex is free but the port is taken, the app probes GET /health —
which already exists and is unauthenticated — and only treats the holder as one of ours if it
answers `ok`. A stranger means: start anyway, log at Error, and say so in the tray tooltip,
because "no sessions" and "I cannot hear anything" look identical otherwise. No new tray
colour; that is a design change and not one to make overnight.

## D11 — The issue #4 residual is conditional, and the condition matters
"A dead dashboard makes a loud error and delays nothing" is true only while the port is
closed. After a hard kill the port is free; a stranger that accepts the connection and never
answers turns that loud error into a silent timeout the operator never sees. That is the case
they cannot detect, so it is the case that gets written down.

## D12 — I got ruling D10 wrong, and the reviewer caught it
My ruling said: mutex free, port taken, probe /health, and `ok` means another copy of us. The
reviewer showed that `ok` is not exclusive. Two Windows users signed in at once: the mutex is
free for the second user twice over (Local\ is per logon session, and the gate hashes a
per-user folder), the port is taken because a loopback bind is machine-wide, and /health
answers `ok` because it IS a real dashboard — the other user's. Under my ruling the second
user's dashboard would decide it was a duplicate, raise the FIRST user's window on the FIRST
user's desktop, and exit. No window, no tray, no tooltip, no explanation. The silent failure
we spent the night trying not to build.

The structural lesson, which is worth more than the fix: I wrote "the mutex is the authority,
the port only corroborates", and then in the one case where the mutex says nothing I left the
port to decide alone. Stating a principle is not the same as making it true.

Fix, small: /health answers with the gate name — {"status":"ok","instance":"<gate name>"} —
so the authority is present in the answer instead of assumed by the caller. Case 4 then has
four outcomes, and three of them must have separate tests, because a build that lumps every
non-`ok` together would otherwise pass.

## D13 — Two issues filed rather than fixed tonight
GitHub #5: multi-user is genuinely broken, not merely undetected. One machine-wide port and one
dashboard per user means the second user's dashboard starts, tells the truth, and can never hear
anything. Fixing it means the port stops being fixed, which Impl §3.1 does on purpose so the
registered hook URL stays stable. A design decision, not an overnight fix.
GitHub #6: a resource lost is never re-acquired — the sound device at T1.14 and the ingress port
at T1.15 are one pattern, not two bugs. Filed as one issue so it gets one answer instead of two
half-answers.

## D14 — Task order changed: T1.18 comes straight after T1.15
Appendix A's order for the rest of Phase 1 is T1.16, T1.17, T1.18, T1.19, T1.20. Nothing in the
dependency graph requires that sequence — T1.16, T1.17 and T1.18 all hang off tasks that are
already done, and only T1.19 depends on T1.18. So the order is a choice, and I am changing it to:

    T1.15 → T1.18 → T1.19 → T1.16 → T1.17 → T1.20

Why. T1.15 alone consumed most of the night, and I cannot promise all five remaining tasks. If I
have to stop short, what you wake up to should be the thing you actually asked for. T1.18 carries
issue #4 — hooks registered on start and removed on quit — which you asked for by name, and T1.19
makes the result a package that starts itself at logon. T1.16 (crisp rendering across monitors,
pinning to every virtual desktop) and T1.17 (a write-only event log that nothing reads until
Phase 5) are the two with the least value to you this week, and T1.16 is also the most likely to
fight me, because the pinning call is undocumented COM.

What this costs. T1.20 is the Phase 1 gate and it needs T1.11 through T1.19, so if I stop before
T1.16 and T1.17 the gate cannot formally close. It could not close tonight in any case: it calls
for a documented run across about fifteen real terminals.

## D15 — An observation, not yet a rule
T1.15's one-line description is "one resident process". Working it through removed four separate
silent failures: a data folder that could move, a stranger holding the port, another Windows
user's dashboard being mistaken for ours, and a rejected /show that exited without a trace.
Three of the four came out of the reviewer reading the code rather than out of the plan.
I am not turning that into a rule at two in the morning. I record it because it bears on how the
remaining tasks should be sized, and because "the task was small" was my reason for expecting
T1.15 to be quick.

## D16 — Two rules adopted tonight, both from the reviewer
1. **"Ask what actually stops the bad thing, not what explains it."** I claimed a comment was the
   only guard on /health staying unauthenticated. Wrong: an existing test sends a GET with no
   token and expects 200, so a token check turns it red. The protection was already mechanical.
   The comment does a different job — it stops the test being deleted as an apparent oversight.
   Test guards the behaviour; comment guards the test. Believing the comment is the guard is how
   you come to accept a weak comment somewhere else and think you are covered.

2. **The four silent failures in T1.15 were one thing, not four.** Every one was two sources of
   truth disagreeing and us choosing one without asking why they differed. Mutex against port.
   Data folder against mutex name. Port against who owns it. Gate name against token. "One
   resident process" describes the world where they agree, which is why the task looked small.
   Adopted as a lens for the rest of Phase 1 — T1.18 is our writes against everybody else's
   writes to settings.json, and underneath that, the file on disk against what a running Claude
   Code already believes. T1.16 is two monitors against one DPI, and a saved window position
   against the monitors that exist now.

## D17 — Anything landing after T1.19 must republish
Because packaging now runs before T1.16 and T1.17, the published exe your logon task points at
will lack whatever lands afterwards. Nobody's acceptance criteria said so. Standing requirement
added: every task after T1.19 carries "republish and repoint the logon task" in its acceptance,
and T1.19's own summary states it. The symptom otherwise is an executable that quietly drifts
from the source, which nobody would connect to a change in task ordering.

(The reviewer raised this as a claim that T1.19 would ship without the Per-Monitor v2 DPI
manifest. That part was wrong — the manifest has carried it since T1.0 on 2026-08-23, and
T1.16's plan text listing it as a deliverable is stale. The reviewer read the plan; I read the
file. Worth recording: the plan is a claim about the tree and it can be out of date.)

## D18 — The settings.json backup must be restorable without us
Accepted from the reviewer as an acceptance item on T1.18, not a note. A plain copy at a path
stated in the summary, which you can put back by hand from a shell with the dashboard
uninstalled, deleted, or refusing to start. Not a restore command we provide; not a filename
only our code knows how to compute. If our writer is wrong in a way tonight's tests did not
catch, that backup is the whole recovery path, and it must not depend on the thing that broke.

## D19 — T1.18 needs a second acceptance criterion
"Starting a Claude Code session causes real events to reach /hook" only tests sessions that
start AFTER the dashboard. The sessions already open are the case the whole feature turns on:
if Claude Code reads settings.json once at startup, then registering hooks when the dashboard
starts does nothing for them, and removing hooks when it quits does nothing either — they keep
firing at a dead port until each one ends. The coder must determine which it is rather than
assume it.

## D20 — A second small thing folded into T1.15, and it is the last
The manifest test asserts that the application asks for no elevation. It says nothing about the
Per-Monitor v2 DPI declaration sitting three lines below it in the same file under the same
comment. Delete the elevation line and a test goes red; delete the DPI line and every test stays
green. The only thing behind Per-Monitor v2 was a future acceptance criterion asking a human to
look at a window on two monitors once — a sighting, not a guard.
Three lines of test, added with T1.15 rather than with T1.16, because T1.16 now runs after
packaging and may not land tonight. A guard that closes a gap should not wait on a task I cannot
promise. Found by the reviewer while checking a correction I had made to it.

## D21 — T1.19 must check the published executable, not the source
A self-contained single-file publish builds its own apphost. Whether the source manifest reaches
it is a packaging behaviour neither the reviewer nor I have seen, so T1.19 verifies the published
exe. The failure would be silent and would look exactly like the source manifest being wrong,
which sends whoever debugs it to the wrong file.
After T1.19 there are two artefacts — the source and the exe — and they can disagree. Same lens
as everything else tonight, one layer out from the code.

## D22 — Two files are called settings.json, and the name almost cost us the whole feature
Found by the reviewer surveying the tree before T1.18 was dispatched; verified by me.
`DashboardPaths.SettingsFile` returns the DASHBOARD's settings file. Claude Code's is a
different file in a different folder. A coder writing the hook merge reaches for
`paths.SettingsFile` because it is the obvious move and the name confirms it — and writes the
hook configuration into the wrong file. Nothing throws, every test passes, and the dashboard
never receives another hook. The symptom is "no sessions appear", which looks exactly like a
quiet day.
Ruling: Claude Code's settings path may not be a member of `DashboardPaths` and may not be
called `SettingsFile`. That class is rooted at our folder and Claude's file is not under it, so
it is wrong by construction as well as by name.
Worth noting this is a different species from the rest of the night. Everything else was two
sources of truth disagreeing. This is two NAMES agreeing when the things behind them do not.

## D23 — GitHub #7 filed: our own settings writer is not atomic
`SettingsStore.Save` is a plain whole-file write. A crash mid-write truncates our settings file.
Bounded impact — the loader already tolerates it and leaves the bad file alone as evidence — so
not a blocker. Filed because T1.18 must build a careful writer for Claude Code's file, and the
careless one sitting beside it is what somebody will eventually copy.

## D24 — Acceptance sharpened: test the port with a non-default value
The hook URL registered in Claude Code's settings must carry the port actually bound, not the
compiled-in default. The operator can override the port, so those are two different values. A
test that uses the default cannot tell them apart, so the test must use a non-default port.

## D25 — A third lens adopted: does any name promise more than the thing behind it?
From the reviewer, after the settings.json find. Worth recording because it is invisible to
every other technique we use tonight. Mutation testing cannot find it — nothing is wrong with
the code. Reading the tests cannot find it — they all pass and they are all correct. It is only
visible by asking what a name promises against what it actually returns.
`SettingsFile` promises "the settings file" and delivers one of two. The failure needs no
mistake by anybody: the obvious action is the wrong one, and the name confirms it on the way
past. The question now asked of every remaining task.
