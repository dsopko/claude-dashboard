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
## D26 — T1.20 will not spawn fifteen real Claude Code sessions
T1.20's acceptance calls for a documented run "across ~15 real Claude Code terminals". Fifteen
live sessions would be driven off your account and would spend your usage, which you told me on
25 August you are trying to conserve for another project. Three sessions building this thing
already spend it; fifteen more, for a test, is a different kind of cost and it is your money.

What I will do instead: drive the gate by replaying real recorded hook payloads at the ingress
endpoint at realistic concurrency. That exercises everything from the wire inward — mapping,
the channel, the registry, banding, the tray roll-up, sound coalescing, ack.

What that does NOT prove, stated plainly rather than glossed: that Claude Code itself delivers
hooks correctly at that scale from that many terminals. The wire is already evidenced by your
own dogfooding on 24–25 August, at one or two sessions rather than fifteen.

So T1.20 will produce a documented run with a named gap, not a closed gate. Closing it properly
needs your terminals and your say-so.

## D27 — Your live ~/.claude/settings.json is off limits tonight
The coder asked me to draw this line before starting T1.18 rather than improvise it at three in
the morning. Ruled: no test writes to your live settings file, no development instance registers
hooks against it, no verification step touches it. If T1.18 cannot be finished without writing
that file, T1.18 stops unfinished and waits for you.

The deciding reason. You authorised the feature. You did not authorise a live test on your file
while you are asleep and cannot repair it. You did say "yes do the settings.json merge" on
25 August — that was one merge, performed by me, with you awake, after I made two backups. That
consent was specific and supervised, and it does not carry forward to automated writes tonight.

I also told the coder, in as many words, that I will not instruct them to touch that file, and
that if I did they would be right to refuse and bring it to you. A director's instruction is not
the file owner's consent. That is the same rule I hold against peers who ask me to change your
settings, and it does not stop applying because I am the one asking.

What we do instead: the merge takes its target path as a parameter and nothing resolves
~/.claude implicitly; live evidence runs against a COPY carrying your real file's shape and your
four command hooks; the backup is written and proven restorable before any first write, and the
restore path is exercised in the tests rather than merely described.

The last hop — a real Claude Code session firing a real hook at a dashboard we registered —
depends on whether Claude Code honours a configuration-directory variable. The coder will
determine that from the published documentation rather than guess, and if it does not exist,
that one step is yours to run in the morning and will be declared as untested rather than
claimed.

A second effect worth knowing. Under the new lifecycle a development instance that registers
hooks and is then killed would put a hook error on every turn in all three sessions building
this — the rare crash case would become the normal working loop. Pointing the resolver at a
scratch directory closes that by construction instead of by discipline.

## D27a — You overruled D27, and that is the correct way for it to have been resolved
You woke, read the ruling, and answered: back up the settings, then test on the live file,
because there is no alternative environment. That is the file owner's consent — the one thing
D27 was missing — so the boundary is lifted.

Recorded rather than quietly replaced, because the shape of it matters: the coder declined to
touch that file on a peer's word, I upheld the refusal against my own schedule, and the block
cleared the moment the person who owns the file said so. That is the mechanism working, not the
mechanism failing.

Conditions I attached, which are mine and not yours: back up and prove the restore works before
the first write; do not overwrite the two backups from 25 August; unit tests stay on temp copies
so the live path never enters a fixture; one bounded window rather than all night, because all
three sessions read that file; after every live write path, verify your four command hooks by
their command strings and stop dead if one is missing; and leave the file in a coherent end
state — hooks registered with a dashboard listening, or hooks absent with none. Never registered
with nothing listening, which is the exact condition issue #4 exists to remove.

I also offered the coder the chance to hear the authorisation from you directly rather than
relayed through me. It has no way to verify provenance across that channel, and it was right
twice this week to refuse exactly this kind of instruction.

## D28 — T1.15 sent back for changes; one finding ruled a fix rather than a bug report
The reviewer returned CHANGES_REQUESTED on T1.15: four must-fixes, one should-fix, two small
items, and two justifications that were right in their conclusion and wrong in their reason.
It proved every claim by breaking the code and watching which tests died, rather than by
reading. Two of those experiments are the useful ones:
  • It deleted the mutex release and all 929 tests stayed green — settling the question of
    whether that code does anything, which we had been arguing about from first principles.
  • It changed ONE CHARACTER of the CLAUDE_DASHBOARD_HOME variable name and all 929 stayed
    green. The feature would have silently not existed and every test would still have passed.

I ruled the identity finding a fix rather than a filing. The single-instance identity names the
data folder but not the logon session, so two sessions sharing one folder both claim to be the
same instance — and the second exits silently after raising a window on a desktop its user
cannot see. It became reachable tonight, because CLAUDE_DASHBOARD_HOME is what makes a shared
folder configurable in the first place. Two features added in one commit, interacting silently.
One line of code fixes it.

## D29 — One rule about comments, replacing a category I recorded badly
I first wrote this up as a new defect class: comments that claim more than the code delivers.
The reviewer corrected me, and its correction is the useful part. It is not a new class — it is
the naming lens at sentence scale. A name promises more than the thing behind it; a remark
promises more than the code does. Recording them separately would have us checking the same
thing twice under two names, which is how a checklist starts to rot.

The split that makes it actionable is between comments that EXPLAIN and comments that CLAIM.
Explaining is the good kind — "this endpoint is deliberately unauthenticated, and here is why"
adds a reason to something a test already holds, and cannot be wrong about behaviour because it
asserts none. Claiming is the dangerous kind, and it is always spottable: it makes a falsifiable
statement about how the system behaves.

The rule, now standing:

    A comment that makes a falsifiable claim about behaviour must either become a test,
    or be rewritten so that it no longer claims.

All three of tonight's examples pass that filter cleanly. "Two signed-in users each get their
own dashboard" is testable and false, so it is rewritten. "This construct is what guarantees no
lock is left held" is testable and false, and the reviewer tested it. "A refused connection
takes about two seconds" is testable and true only on this machine, so it becomes "on this
machine" and the load-bearing reasoning moves to an argument that needs no measurement at all.

Why it matters more than an untested branch: a wrong comment survives every mutation test that
can be run. It is not merely unprotected — it is unreachable by the entire technique. Which is
the argument for the rule: if a claim can be tested, a comment should not be the thing carrying
it.

Worth adding that four of six findings landing on comments was not carelessness. Every one was a
sentence that had quietly taken on the job of a test, in the commit where the actual tests were
the best we have produced.

## D30 — A measurement disagreement I did not settle from the chair, and how it ended
The reviewer measured a closed IPv6 loopback port refusing a connection in 5.5 milliseconds and
concluded that only IPv4 is slow — which would point at a local filtering layer on this machine.
The coder re-measured with nothing above the raw socket and got about 2045 milliseconds on BOTH
address families, twice.

I sent the discrepancy back to the reviewer to reconcile rather than ruling on it. Our own
standing rule is that a diagnosis of why somebody else's experiment came out differently is a
claim like any other, and I was burned this week relaying exactly that kind of claim unchecked.

Nothing in the design depends on it: both agree the code should ask "may I have this port" by
trying to take it rather than by connecting, and the load-bearing argument now needs no
measurement at all. The coder's reason for not using the reviewer's figures is one worth keeping
— they would not write a number into a comment that they had not taken themselves, and having
taken it, would not write somebody else's either.

## D31 — A trap in one of the fixes, and a rule that reverses on one side
The obvious test for the CLAUDE_DASHBOARD_HOME variable does not work: a test that sets the
name from the same constant the code reads is self-consistent under any rename. It would set the
broken name, read the broken name, and pass while the documented variable did nothing at all.
So that test writes the name out as a literal — which is the exact opposite of what I required
an hour earlier for the single-instance identity, where a literal would have been a second copy
of a rule we own.
The distinction the coder drew: there the naming rule is ours, so a copy can drift. Here the name
is fixed outside the code, by the specification and by what a person types, so the literal is the
only independent check there is. Both comments now say which case they are. I have asked the
reviewer to judge whether that distinction holds, because it is the kind that is either exactly
right or a rationalisation and I cannot tell which from where I sit.

## D32 — T1.19 packages the application but does not install it over the one you are using
Your dashboard is running from `%LOCALAPPDATA%\ClaudeDashboardApp\` and its file is locked while
it runs. T1.19 will publish to a staging folder beside it and stop there.

Swapping the live build is a separate, deliberate step, and I have kept it out of the task on
purpose. It stops your dashboard, and under T1.18's new lifecycle the replacement rewrites your
`~/.claude/settings.json` on its first start and again on its first quit. That is the feature
working as designed, but it is a change to your working environment that should happen once,
knowingly, and not as a side effect of a packaging task at four in the morning.

You will get a package and the exact commands to install it. Whether I run them before you wake
is a separate entry in this log if I do — with the exact way to put the old one back.

## D30 (concluded) — the coder was right, and the reviewer found its own error
The reviewer's IPv6 control was broken. It used a connection type that is an IPv4 socket, aimed
it at an IPv6 address, and got an instant address-family error rather than a refusal. Its own
results table recorded the wrong error code and it read past it. Re-measured properly: about
2047 milliseconds, matching the coder exactly.

It then went further than withdrawing the claim. It scoped the effect — both address families,
loopback and local network, low port and high — found about 2045 milliseconds everywhere, and
declined to offer a second theory of the cause having got the first one wrong. That scoping is
what settled the thing that mattered: a two-second delay on every refused connection would be
famous if it were how Windows behaves generally. It is not. So "these figures describe one
machine" stopped being a hedge and became a supported statement.

Worth recording that the whole correction started from the coder declining to write a number
into a comment that it had not measured itself.

## D33 — Two rules sharpened, both now standing
1. **The comment filter bites on claims of capability, not claims of limitation.** A sentence
   saying the system WORKS a certain way invites a reader to rely on it instead of a test. A
   sentence saying something DOES NOT work removes reliance. Both must be true; only the first
   quietly takes a test's job. Without this the rule I recorded an hour earlier would have
   condemned the best paragraph in the commit.
2. **Use a literal when an external authority owns the value; compute it when the code owns it.**
   The test for which is which: if substituting the constant makes the test pass under a rename,
   the constant is the thing under test and cannot also be the answer key. That is a proof rather
   than a preference. It resolves what looked like the coder contradicting itself — computing the
   single-instance name in one test and writing the environment variable out longhand in another.
   The first name is ours to invent; the second is fixed by the specification and by what you
   type into a shell.

## D34 — My own two-rejections rule did not fire, and I am saying so rather than ignoring it
I set a rule earlier tonight that two rejections on one task stop it for you. T1.15 has now been
sent back twice. It does not fire, for two reasons I want on the record rather than assumed:
the verdicts were CHANGES_REQUESTED and not REJECT, which the reviewer distinguishes carefully;
and the cycles are converging hard — six items, then two, and the two are one comment and one
eight-line test. A rule against grinding is not a rule against finishing.

## D35 — Something worth you knowing about how the night actually went
Three times tonight one session's claim was checked by another and found wrong, and every time
the person who was wrong found it or accepted it without argument:
  • I ruled that the application should ask "is anyone on this port" by connecting, with a
    one-second limit. The coder measured and showed my rule would have made every ordinary
    first start deaf. It changed the design and told me why.
  • The reviewer attributed a two-second delay to one network address family. The coder
    re-measured and disagreed. I sent it back rather than ruling. The reviewer found its own
    control was broken, withdrew, and then scoped the effect properly instead of guessing again.
  • The coder reported three failing tests where there were two. I checked and said so. It
    re-ran, confirmed two, could not reproduce the third, and said "here is my best guess"
    rather than "it was two all along".

None of these was caught by a test. All three were caught by somebody checking somebody else's
claim. That is the argument for the three-session arrangement, and it is worth more than any
single defect the night produced.

## D36 — A flaky test found because a new test described the mechanism the old one depended on
The reviewer hit a failure in the crash-recovery test that neither of its two mutations could
reach. Isolated, the test passes six times running. In the full suite it passes too — it is rare
and it depends on load, which is the shape people write off as noise.

It proved the cause rather than guessing: the test discards the object it creates, so the
operating-system handle behind it survives only until the garbage collector runs a finaliser. If
a collection lands in that one window, the handle closes, Windows destroys the underlying object,
and there is nothing left for the test to observe. Forcing a collection there makes it fail every
time; keeping a reference makes it pass every time.

The interesting part is how it became visible. The coder had just written a test that
deliberately closes that handle in order to observe the opposite result. The two tests are the
same experiment differing only in how long the handle lives — and the older one left that to the
garbage collector. Writing the second is what exposed the first. Nothing had described the
mechanism before, so nothing could show the dependence.

Fixed in two lines. Worth a whole review cycle for a reason worth keeping: a test that guards
crash recovery and fails rarely, under load, with a message that reads like an environment
problem, is worse than no test at all. People stop believing it — and the next time it goes red
it may be right.

## D37 — The fourth checked claim of the night, and this one was mine to pass on
The reviewer prescribed a two-line fix for the flaky test and named the mechanism. The coder
measured it and found the named mechanism was not the one doing the work — deleting the line
the reviewer identified changes nothing, six runs across both build configurations. The real
fix is simply assigning the object rather than discarding it; the thread that holds it keeps it
reachable on its own.

The coder kept the line anyway, as insurance against a future change that stops holding the
thread, but wrote beside it that removing it does not fail and that something else is the
mechanism. Its reason is the part worth keeping: presenting that line as the fix would have been
a comment claiming more than the code delivers — the exact defect the previous two cycles were
spent removing — and it would not add a fresh one in the commit that closes them.

It also improved on the prescription. It left the forced garbage collection in the test. Without
it the test passes because nothing happened to collect, which is not a guarantee; it is the same
"it worked this time" that produced the flake, landing the other way. With the collection forced,
the test now asserts that the thing survives one.

That is four times tonight a claim was checked by someone other than the person who made it, and
found wrong. I relayed this one, so it is also the second of mine.
## D38 — Two live steps of T1.18 are deliberately NOT applied to your machine tonight
T1.18 builds and tests everything. Two things it will NOT do to your actual environment.

**The security token is not switched on.** T1.18 is supposed to generate `CLAUDE_DASHBOARD_TOKEN`
and set it for your user account, and register hooks that send it. The coder found the hazard
before building: a Claude Code session that was already running when the variable is set never
inherits it. So the moment a dashboard restarts with a token configured, it answers "unauthorised"
to every hook from every session that started earlier — which is issue #4's symptom exactly, and
self-inflicted.

The direction of the danger decided it. It does not bite while the current dashboard keeps
running. It bites on the next restart — which your logon task would perform tomorrow morning,
unattended, with nobody watching. Setting it tonight would arm a fault that fires when you are
not there, in your own sessions, with a symptom indistinguishable from the bug we are fixing.

So: the code ships and is tested against a scratch environment. Switching it on is one deliberate
step for you, after your sessions have turned over. Nothing is broken meanwhile — the dashboard
accepts unauthenticated posts on loopback, which the specification permits and which is exactly
what it has been doing all along.

**The logon task is not registered.** The coder did not ask about this; I stopped it. The task
must point at the packaged executable, and packaging is T1.19. Registering it tonight would point
your logon at a development build and start that automatically tomorrow. It gets built and proven
by reading the registration back, under a scratch name that is deleted afterwards; the real one
belongs with T1.19.

## D39 — Removing a hook of ours that does nothing
Your live settings file has our dashboard hooks on eight events, hand-installed. One of them,
`PermissionRequest`, is an event the dashboard deliberately refuses — there is a test that says
so by name. It fires, posts, gets a polite acknowledgement, and nothing happens, on every
permission prompt you answer.
It is being removed and will not be recreated. It matches our URL, so our own rule already
decides it; we settled on 24 August that this event is corroboration and not the signal the
dashboard acts on; and a hook that does nothing still costs a round trip every time you are
asked to approve something.
**Your own `notify.ps1` is also on that event and stays.** So do your other three, and so do the
`matcher` keys on their groups.

## D40 — Documentation is not measurement, in both directions
The published documentation says Claude Code reloads settings while running and names hooks
explicitly among the things that take effect without a restart. That is what the whole feature
rests on, so the coder is measuring it rather than citing it.
I added a condition: measure it in BOTH directions. "A running session notices a hook we added"
and "a running session notices a hook we removed" are two separate claims, and the second is the
one issue #4 actually needs. Proving only the first would have left the important half assumed.

## D41 — Issue #4 is measured, not argued, and the measurement was hard to earn
Both directions proven on your live settings file:
  • A hook we ADD starts firing in sessions that were already running. Ten events reached a
    dashboard on a port that did not exist in your file until 02:43 tonight.
  • A hook we REMOVE stops firing in those same sessions. Nothing arrived across a window with a
    proven-healthy receiver at both ends.
So a dashboard that quits cleanly takes its hooks with it, and every Claude Code session you
already have open stops calling it — no restart, no error, nothing left behind. Your bug is
fixed for the ordinary case. The residual is a hard kill, which no switch can close because no
suppression field exists.

The second measurement took four attempts and the reason is worth keeping. The coder twice
refused to claim a result it could not defend: the receiver was demonstrably alive on both sides
of the silence, but nothing showed WHEN my trigger landed inside that window — only the order of
its own tool calls. It named the flaw itself as the same species as reading a summary count
instead of the failure messages. I fixed it by reading my machine's clock immediately before
sending and putting the reading in the message. One line turned an ordering that could only be
inferred into one that can be shown.
I also made it worse once, by firing an unrequested trigger that landed at a moment neither of us
could place — destroying the very property the coder was establishing.

## D42 — One line in your settings file that neither of us would write
`allowedHttpHookUrls` now carries a second entry, `http://127.0.0.1:59918/hook`, left over from
tonight's scratch dashboard. It is harmless: an allowlist entry pointing at a port nothing will
ever listen on does nothing, which is exactly why the specification leaves allowlists in place.

The coder tried to remove it and its own permission checks refused the write. It stopped rather
than working around them. **I then declined to do it on its behalf**, because performing an
action for another session that its own guard denied is the same manoeuvre we refuse when it
comes the other way, and it does not become acceptable because I am the one who asked for it.
Your authorisation to write that file was given to a session; it is not a general licence for
whichever session finds a way through.

So it is yours: delete `http://127.0.0.1:59918/hook` from `allowedHttpHookUrls` in
`~/.claude/settings.json`, or tell me to and I will. One line, and nothing depends on it.

## D43 — A branch we wrote for a hypothesis met the real thing the same night
T1.15 added a case for "something is on our port that answers, but cannot say who it is" — an
old build, or a stranger. Your currently running dashboard is exactly that: it predates T1.15,
so it answers the health check with a bare "ok" and no identity. Correctly classified as
unrecognised rather than mistaken for one of ours. The hypothetical was sitting on the machine
the whole time.

## D44 — A finding worth keeping, because the obvious repair is the dangerous one
Windows does not store the "run at normal privilege" setting when you register a scheduled task
with it — read the task back and that element is simply absent. So a check looking for it fails
on a task that is perfectly correct.
The obvious repair is to delete the check. That then passes a task somebody had edited to run
elevated, which is the thing we are guarding against and the reason the specification says
"never elevated". The only statement true of both what we write and what Windows stores is the
negative one: the task is elevated only if it explicitly says so.
Both halves are tested, including the control that catches an elevated task — without which a
check that always answered "not elevated" would satisfy everything else.

## D45 — I stalled for seven hours, and the cause was mine
I handed T1.18 to the reviewer around 03:00 and waited for a verdict without subscribing to its
idle signal. I had done that for the coder on every single hand-off and forgot it for the
reviewer. My earlier fallback timers had expired. So the reviewer finished a turn, nothing woke
me, and I waited until you asked why I had stopped at 10:30.

Nothing was lost — the tree stayed clean, T1.18's code and tests were already committed, and your
dashboard ran untouched throughout. What was lost is seven hours of the night you told me to use.

The lesson is not "remember the subscription". It is that **the arrangement had a single point of
failure I never noticed: my only way of learning anything is a message from a peer, and a peer
that goes quiet is indistinguishable from a peer that is working.** I used fallback timers early
in the night for exactly this and stopped setting them once the rhythm felt reliable. The rhythm
was not the protection; the timer was.

## D45a — The stall was two failures, not one
The reviewer finished its verdict at 03:14 and sent it. **The message was dropped in transit** —
a relay was cut and it never reached me. It had been told not to retry in a loop, so it folded
the verdict into its next contact, which was the one I finally prompted at 10:30.

So half was mine (no idle subscription) and half was a lost message. Either alone would have been
survivable; together they were indistinguishable from work in progress. That is the point worth
keeping: **a peer that has gone quiet looks exactly like a peer that is working**, and nothing in
the arrangement could tell the two apart.

Two changes, both adopted. I subscribe to the idle signal on every hand-off in both directions.
And the reviewer's rule, which is better than mine: state in your next message whether the
previous one was acknowledged, so a dropped send surfaces as a question instead of as silence.

## D46 — The most serious finding of the project so far, and it is a missing test rather than a bug
`HookLifecycle` — the class that decides whether to register hooks at all, builds the URL, and
writes the port file — had **no test class**. The pieces were tested; the thing that assembles
them was not.

The consequence that matters: the dashboard deliberately still starts when another process holds
its port, and it registers hooks straight after starting. The only thing stopping it writing a
hook address that points at **that other process** is one `if`. Hook payloads carry your prompt
text. So a single line stands between your typing and an unknown program, and the reviewer showed
that removing it leaves all 994 tests green, the dashboard looking healthy, and the registration
reporting success.

Not a bug — the guard is there and it works. But an unguarded guard, with the widest blast radius
in the phase behind the quietest possible failure. Two tests fix it.

## D47 — A verification that proved nothing, and the trap that produced it
The coder withdrew its own earlier confirmation as worthless. It had disabled a guard by writing
"if (false)", which the compiler flags as unreachable code — and this project treats every
compiler warning as an error. So the build failed. Its script hid the failure, and the test run
that followed used the previous, unmodified program still sitting on disk. It reported a clean
pass on a change that had never been built.

It found out only because a later attempt gave an impossible answer: a failure in a test the
change could not possibly affect, which was the previous attempt's leftovers.

**A change that does not compile produces exactly the same green result as a change that the
tests failed to catch.** Two states that look identical where only one needs action — the same
shape we have removed six times from the product, this time inside the method we use to check
the product. Passed to the reviewer as a warning about its own technique, not only as a note
about the coder's.

## D48 — The two intermittent failures, and neither was where anyone guessed
Both found, both real, neither in new code.
One test waited for a counter to change and then read a log line written a few lines later. It
was waiting on a stand-in that arrives first. It now waits for the thing it actually checks.
The other was a comment written last cycle claiming that the logging system "flushes on write, so
an open file is a readable file". It does not — it flushes every two seconds. Access to a file
and content in a file are two different things, and the comment conflated them. This is the
fourth comment tonight asserting something nobody tested, and the first to cause a real
intermittent failure rather than merely being wrong.

## D49 — The dropped verdict, verified from the transcripts rather than reconstructed
You asked me to check whether the reviewer really sent its T1.18 verdict at 03:14 or whether
something else was wrong. It sent. `SendMessage` at 03:13:23 local, matching its account, and the
tool reported success — not an error, not a retry.
It never arrived. Reviewer messages in my session run to 02:23, then nothing until 10:31.
The message body exists in no other session's transcript on this machine.

**Where I nearly filed a wrong answer.** The success result carried a note saying another live
session is also named "Director" — and there is one: the original interactive Director session I
succeeded, dormant since 24 August. My own session carries no name at all. I had the misrouting
explanation half written.
Then I ran the control: does that note appear on the messages that DID arrive? It appears on all
eighteen. Constant background, not a distinguishing feature. Misrouting refuted.

So: one message, reported successful, with a result identical to fifteen delivered ones, silently
lost. Not the reviewer's fault and not a fault in how it ends its turns.

A latent hazard found on the way and worth fixing anyway: two live sessions answer to the same
name, one of them two days dormant. A name that resolves to two places is the same shape as
everything else we removed this week.

## D50 — T1.18 approved; the flake it uncovered becomes its own task
The reviewer found a third intermittent test failure during 33 consecutive runs — one failure,
never reproduced in 12 isolated runs or 18 further soaks. It has the name and not the message,
and said so plainly rather than reasoning from the name.

**It is not T1.18's.** It is a test from T1.13a in August, which the reviewer itself reviewed and
approved at the time, recording it as "racy but the product is correct" — and recording that it
could not explain why the test passed and would not invent a reason. The thing it could not
explain has now failed.

Its own lesson, and I am adopting it: **a test whose passing you cannot explain is a test you
should not approve, whatever the code under it does.** "Racy but correct" describes a suite that
will lie to somebody later.

My ruling: approve T1.18, which is complete and did not cause this, and start the flake
immediately as its own small task rather than filing it. A flaky test degrades every verdict that
comes after it, so it does not go on a list.

## D51 — Worktrees re-priced, and I have changed my recommendation
In D1 I dropped worktrees, on the grounds that our one-session-at-a-time rule already prevented
the collisions and the setup cost was not worth it. The night has re-priced that. Three separate
environment failures in this shared tree, all different: a partly-written assembly reporting 824
tests in 633 milliseconds, missing generated interface sources, and a stuck test host that hung
for ten minutes.
None corrupted a result that reached you, because all three were caught. But each one cost a
re-run and a diagnosis, and each one was initially indistinguishable from a real defect.
**I now recommend separate working folders before Phase 2.** Not tonight — it changes how all
three sessions work, and T1.19 and T1.20 are still to do. D1 stands as the right call on the
evidence I had; this is the same question with three more data points.
## D50 CORRECTION — T1.18 was approved by the reviewer, not approved over its objection
My entry read as though I approved T1.18 while the reviewer still wanted changes. That is not
what happened and the difference matters.

The reviewer offered a condition: approve without the flake fix, provided the flake was not
buried. I met the condition and went further by starting the work rather than listing it. On its
own stated terms its verdict converted to APPROVE. "The reviewer approved" and "the reviewer
asked for changes and the director overruled it" are different facts about the same night, and
only the first is true.

My two standing conditions — that an escalation keeps its name, and that my decision does not
close a finding — were not needed here.

## D52 — Your plan edit gave us the likely cause of the lost verdict, and it was our doing
Your amendment to Appendix B says the channel has a loop guard that drops a message resembling
one it has already passed. Neither the reviewer nor I knew that. It is now the leading
explanation for the verdict lost at 03:13, and it points at us rather than at the channel: my
dispatches and the coder's reports had both grown long and structurally near-identical — the same
headers, the same task blocks, the same standing rules pasted message after message.

All three sessions have switched to references. The review request I sent immediately after is a
fifth the size of the one before it.

Two observations worth keeping. Mine: I had concluded "a message reported successful was silently
lost", which was true and useless, because it named no cause and suggested no fix. Yours named a
mechanism we could act on. The coder's: its reports were as repetitive as my dispatches, so if
resemblance is the trigger, both of us fed it.

## D53 — Why a backwards comment survived a review
The comment explaining why the flaky test was safe was not merely unverified — it was inverted.
It said a stray update could only carry a LATER time; the danger is one carrying an EARLIER time.
So it did not just fail to protect, it pointed the next reader away from the real failure.
The coder's explanation of how it passed review is the part worth keeping: **a reader checking a
comment against the code sees a plausible sentence; only a reader checking it against the failure
sees that it is inverted.** Which is why the rule has to be "could this be tested" and not "does
this look right".

## D54 — The reviewer's own analysis was a wrong starting point, and it said so
I handed the coder the reviewer's August analysis of the flaky test as "a starting point, not a
conclusion". The reviewer has now corrected a third thing that nobody else caught: **its model of
the failure was wrong**, not merely incomplete. The interleaving it predicted cannot produce the
failure at all. The real one is narrower and spans two statements, and no amount of waiting
creates it — the coder had to construct it deliberately.

That is why its August experiment did not confirm its own hypothesis, and it recorded the
discrepancy at the time without resolving it. Its point, which I am recording in its words: the
phrase "attached as a starting point" reads generously, and the honest version is that the
starting point was wrong and the coder was right to discard it rather than build on it.

## D55 — A test that detects is not a test that prevents
The new test asserts that no stray update reaches the screen. The reviewer measured what that
assertion is actually worth by deliberately reintroducing the fault: **caught in 3 runs out of
20**. So the claim "the isolation is asserted rather than hoped" is true but misleading — anyone
who reintroduces the fault gets a suite that is rarely red, not one that is red. What makes the
behaviour reliable is that the second caller was removed; the assertion is a backstop with a
fifteen percent hit rate.
This is the detector-versus-guard distinction landing on a test written the same morning we
adopted it as a lens. One sentence is being added to say so.

## D56 — A decision made safe rather than merely reasoned
The coder declined to add a two-line guard to production code, on the grounds that it would exist
only to accommodate a situation a test can create but the product cannot. I agreed and so did the
reviewer. But the decision rests on "only one thing calls this", and nothing tested that — add a
second caller and nothing goes red.
The fix is not the guard. It is a test asserting the property the decision depends on, which
changes no product code at all. Same argument as the shared-constant fix earlier: protect the
construction the reasoning rests on, mechanically rather than by discipline.

## D57 — A rule about source checks, with the caveat that makes it safe
A check that reads the source files cannot be fooled by a build that failed quietly — the trap
that produced a worthless result during T1.18. True, and useful, because our checking method
breaks the product on purpose and a broken product often means a broken build.

The reviewer attached the qualification that makes it safe to write down, and it is bigger than
the rule. **The property that makes such a check immune is the same one that makes it weaker: it
never observes what actually runs.** So the rule is not "prefer checks that read the source". It
is: read the source for structural facts, where the source is the authority anyway; use the
compiled program for questions about behaviour, and answer a build that can fail quietly by
checking the build rather than by changing instrument.

Today supplied its own counterexample. The very test that proved the immunity **also missed a
real defect**, because its search pattern was wrong. Immunity to one kind of failure says nothing
about correctness — which is the detector-versus-guard distinction once more.

## D58 — I got the correction backwards, and the coder said so
I told the coder its comment overclaimed. It pointed out the overclaiming sentence was in its
message to me, not in the file — and that the file was worse than I thought: the remark never
mentioned the assertion at all, so a reader would meet it with nothing explaining its purpose.
Recorded because the pattern is now familiar: a diagnosis of somebody else's work is a claim like
any other, and mine was wrong about where the defect lived while being right that there was one.

## D59 — A test written to close a gap had the same gap
The new test asserts that only one component reaches the screen-update path — the property a
deferral decision rested on. It searched for the interface name. **Every place in the code that
actually uses it refers to the concrete type instead**, so the test was blind to all of them.
The reviewer proved it rather than arguing it: it added a genuine second caller in a file the
test did not expect, and the test passed.
One line of pattern fixes it. Worth recording because of where it landed — a test written
specifically to protect a claim that nothing tested turned out to make a claim that nothing
tested. That is the second time in two days a comment or test asserting a capability has been the
defect itself.

## D60 — Possibly the most useful thing found today: a test of a test can be shaped to pass
The way we prove a test is worth having is to break the thing it guards and check that it goes
red. That method has been our foundation all week.

Today it lied. The coder broke the guarded property by writing the deliberate fault in terms of
the *interface* name — because that is what its search pattern looked for. Every real use in the
code refers to the concrete type instead. So the fault was shaped to fit the checker rather than
to resemble a real mistake, and it confirmed the pattern instead of testing it. The test looked
verified for two days while being blind to every genuine case.

Its own statement of the rule: **a deliberate fault must be shaped like the defect, not like the
detector.**

This is the same trap as computing an expected answer from the very constant under test — a check
that cannot fail. What makes it worth recording separately is where it appeared: inside the
technique we had been treating as the one thing that cannot be fooled.

It adds a second question to the one we already ask of every assertion. Not only *what else could
have produced this observation*, but **was my experiment capable of producing the other outcome at
all**. Put to the reviewer before adopting it, rather than adopted on one instance.

The coder also noted that its own earlier measurement had already listed the relevant file outside
the matched set. The evidence was on its screen and it did not read it as evidence of anything —
the third variant this week of the data being present and nobody asking it the right question.

## D60a — The plant rule, adopted with the boundary that keeps it useful
The reviewer confirmed the rule is new rather than a restatement, and explained why in a way
worth keeping. **Every trap we had caught before corrupts the judge** — computing the expected
answer from the thing under test, asserting something that could be true for other reasons. This
one corrupts the *input*. The judge was fine; the deliberate fault was derived from the checker.
A test can have a perfect judge and still be worthless if its input could only ever produce one
answer.

The keeper is the operational form, not the slogan: **was my experiment capable of producing the
other outcome at all?** That is a question you can ask beforehand, where "shaped like the
detector" is a diagnosis you can only make afterwards.

**The boundary, without which it would be applied to everything and mean nothing.** When you
break behaviour directly — delete a line, disable a check — the deliberate fault IS the defect
and there is nothing to choose. That is why all this week's other checking was sound. The trap
appears only where the check is a *pattern* over the code — a search, an allowlist, a schema —
because then the defect has many possible shapes and you must pick one, and the criterion is
visible while the real shape is not. So: **when you must choose a specimen, take it from the code,
not from the checker.**

And a guard against misreading it: this is not "avoid reading the implementation when writing a
test". You have to read it to know what the defect looks like. It is narrower — do not derive the
specimen from the criterion.

## D61 — The failure mode that leaves no trace
Three variants this week of the same thing: the data was present and nobody asked it the right
question. The coder's own file listing already showed the relevant file outside the matched set.
The reviewer's status output was printed directly above its own incorrect "clean" label. The
reviewer's error code was in its own results table.

Unlike the others this one leaves no artefact. A wrong assertion sits in a file; a badly shaped
deliberate fault sits in a transcript; this is an absence — nobody asked, so there is nothing to
find later. The only defence anyone has proposed is the one that worked today: **when a check
comes back green, ask what the green depended on, and go and look at that.**

## D62 — The watchdog earned its keep in three hours
The reviewer's T1.19 verdict was dropped in transit, exactly as its T1.18 verdict was. The
difference: I pinged at three minutes instead of discovering it at seven hours. That is the whole
value of the rule you added to the plan this morning.

**A hypothesis that points at us rather than at the channel.** Both lost messages were the
reviewer's, both long, both structurally near-identical — same headers, same opening line, same
table, same section names, every time. If the channel discards a message resembling one it has
already passed, the reviewer's own format is the trigger. It reformatted this message deliberately
as a test. It landed. If the next few also land, we have the answer cheaply and the fix is ours.

## D63 — CORRECTED. A guard that fires, protecting against a change that is completely quiet
**This entry was wrong when I first wrote it and I am correcting it in place rather than
appending, because you will read it once.** What I originally recorded: that one of the packaging
assertions could never fire, because the defect it guards against breaks the build first. That
came from the reviewer and I committed it before it was challenged.

The coder could not reproduce it and measured the opposite. I sent it back rather than picking a
side. The reviewer then put its prediction on the record before testing — that the coder was
right and its own observation had been contaminated — and settled it with three measurements,
build servers stopped and every project's build folders deleted each time:

    unmutated            → 1013 pass, output in the normal place
    defect planted       → BUILD SUCCEEDS, 1012 pass, exactly ONE failure — this assertion.
                           Every build artefact silently relocated
    restored, unmutated  → 1013 pass, output back where it started

The third measurement is one I asked for and it is what makes the other two mean anything: the
two unmutated runs match, so the tree held still and the difference belongs to the change.

**So the assertion fires, and it is the only thing in the project that notices.** Both
explanations we had written were wrong — mine said the build stops you, the coder's said every
other test goes red, and neither is true. **The real reason is better than either: the change is
quiet.** It builds, it passes everything else, and it silently moves every artefact the operator's
logon task might one day point at.

And the reviewer could not reproduce its own original failure, tried four ways, and **retracted
the mechanism it had offered for it** rather than reaching for a second guess. What caused it
remains unexplained and is recorded as such.

The part of its self-diagnosis that survives, and it is the durable one: **"I deleted the build
folders and it still failed" proves nothing, because it never established that the clean was
clean.** That reasoning was unsound before anyone knew which way the answer went.

## D64 — "Single file" ships as three files, and that is deliberate
The packaged folder holds the executable, the sound files, and one symbol file for the shared
library. The sounds are by design. The symbol file is being kept for a better reason than
tidiness: without it a crash in the shared library cannot be turned back into readable source
locations. What needed fixing was the comment, which called loose files "a lie" without noting
that two of them are there on purpose.

## D65 — Three lost failure messages, and a two-word fix
Three times now a rare test failure has been seen and its message lost — the reviewer's unnamed
one during T1.18, the screen-update one, and a fourth found today. Each time the count survived
and the message did not, so each was diagnosed on the fourth sighting instead of the first.

The reviewer's fix, adopted for all three sessions: add `--logger trx` to the standard test
command. Every failure and its assertion text is written to a file, so the next rare one is
diagnosable the first time it fires. That is the smallest possible fix for something that has now
cost us three investigations.

## D66 — A flaky test that may be telling us something about your machine, not our code
A test that writes a settings file failed once and has not been reproduced in twenty-five further
runs. The message was lost, which is what prompted D65.

The reviewer's hypothesis, labelled as a hypothesis: our file writer gives up quietly if the file
is locked, after retrying for about two hundred milliseconds. A virus scanner opening the
temporary file for a fraction of a second would produce exactly this — and if that is what
happened, it is not only a test problem. **It says a transient lock on your real settings file
makes hook registration abandon silently**, and two hundred milliseconds may be a thin budget on a
machine running a scanner.
Filed for investigation rather than acted on, because nobody has the failure message.

## D67 — Where our rules do not reach
Three of today's failures were not in the code or in the tests but in the conditions the
measurement ran under: a stale program left by a build that failed, a corrupted comparison, and a
build error nobody can now explain. Our rules cover the thing being judged and the thing being
fed in. None of them reaches the environment the measurement happened in.
Recorded as an open weakness rather than a solved one. The only defence used successfully today
was to measure the unchanged state twice, on both sides of the change, and check the two agree.
## D68 — The phase gate ran before two tasks it depends on, and that has a cost
In D14 I reordered the remaining work so that the feature you asked for and the packaging came
before display scaling and the event log. That was the right call and I would make it again. It
has a consequence I should state plainly rather than let you discover.

**The gate's own dependency list includes those two tasks.** So the acceptance document covers the
system as it stands without them. When they land, the document is out of date in two specific
places: display scaling and window placement are things it never exercised, and the event log adds
a write on every event that the concurrency and burst measurements did not include.

What that costs: not a full re-run. Those two tasks need their own criteria evidenced and folded
into the document as a supplement, plus the republish that every task after packaging now carries.

Phase 1 is therefore **gated, not finished**, until either those two land and the supplement is
written, or you decide to close the phase without them.

## D69 — Two findings from the gate that outlast it, both filed
**GitHub #9.** An unrecognised notification type is discarded with no log line at all. We classify
four of twelve; the rest correctly change nothing, but leave no trace. Every other unhandled shape
logs something — the code deliberately distinguishes "malformed JSON" from "an event we do not
consume" because at two in the morning those need different fixes. Same case, missing line. And it
is the likeliest upstream change there is: a new notification type arrives, the first one that
should light a session up is discarded silently.

**GitHub #10.** The dashboard has no way to say what it currently believes. From outside the
process a correct dashboard and a completely wrong one are indistinguishable. This shaped the gate
rather than merely being noticed — states, bands and the tray colour had to be checked from inside
a test harness, because the running program cannot be asked. The replay proved events were
received; it could not prove they were understood.
The second one constrains how every later phase can be tested, which is why it is filed as work
rather than as a note.

## D70 — Silence in a gate reads as coverage
I asked the reviewer to judge one thing about the acceptance document: not whether its claims are
true, but whether its list of what it does NOT cover is complete. A gate that overstates its
coverage is worse than no gate.

It found that **acknowledgement is missing from the document entirely.** Not claimed, not listed
as unevidenced — absent. Two of the six things the plan asks the gate to check concern
acknowledgement, and the gate would have passed without either being mentioned.

The sentence worth keeping: **the document does not claim it works; it is silent, and silence in
a gate reads as coverage.** Anybody checking the exit criteria against the document would find a
section about states and the tray light and no reason to suspect a whole criterion had been
skipped.

It found it by deriving the required list independently from the plan and then comparing, rather
than by reading the document and asking whether it looked complete. That is the difference
between a check and an impression.

Two smaller gaps in the same family: the plan asks that a crashed dashboard **relaunch itself**,
and the run relaunched it by hand — so half a criterion read as satisfied; and a section headed
"under load" reports a volume that cannot reach the overflow path it might be taken to cover.

## D71 — A no-op change and a blind test produce the same green
The reviewer's own attempt to verify the new harness got this wrong. It removed a line expecting
the code to fall through into a different behaviour; the fall-through landed somewhere that
behaves identically, so its deliberate defect changed nothing at all — and the test passed. For a
minute it had what looked like proof that the harness was blind.
The rule, in a form nobody had stated: **a change that does nothing and a test that notices
nothing produce the same result, and only reading the code you changed tells them apart.** That is
the third time today the plant rule has caught its own authors.

## D72 — Phase 1 is GATED, not finished, and the document now says so itself
The acceptance run is approved after six review cycles. What it evidences, and what it does not,
are both written down in `docs/claude-dashboard-phase1-acceptance.md`.

Four of the five things the reviewer singled out in that document report weaknesses rather than
results: that a replay stands in for real terminals and what that costs; that from outside the
process a correct dashboard and a completely wrong one are indistinguishable; that the assembled
system's nudge behaviour is evidenced by nothing at all; and that two Phase 1 tasks were never
started. **The document is materially better than the run it describes.**

The line that will still be doing work in six months: **"we could not observe it" and "there is
nothing to observe" are different claims, and only the first is closed by running again.**

## D73 — A blind spot is not a mistake, and they want different fixes
The reason the missing acknowledgement criterion and the two missing tasks are not the same kind
of error, which the reviewer put better than I would:

The gaps list was built from behaviours the code has. Acknowledgement was one of those, merely
unexercised — so it fell out of that method, and more care would have caught it. Display scaling
and the event log are behaviours the code **does not have**, so they could not fall out of that
method at any level of care. **The method had a blind spot diligence could not reach.**

You correct a mistake by trying harder. You correct this by changing where the list comes from.

## D74 — The sentence that covers the whole day
From the reviewer, and it is the general form of everything we chased:

> **The more thoroughly a claim is untestable, the more confidently it can be written, and the
> longer it survives.**

One sentence covering all of it — the comment that was not merely unverified but backwards; the
guard we thought could never fire; the test whose search pattern matched a name nothing used; the
enumeration standing in for a rule; and the section that was missing entirely. **Every one of them
was safe precisely because nothing could contradict it.**
