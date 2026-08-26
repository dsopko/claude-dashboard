# Decisions I had to make because you were not available

You went to bed on the night of 2026-08-25 and told me to run to the end of
Part 3 without waking you. Every choice below is one I would usually have put
to you first. Each entry says what I decided, why, and how to undo it.

Read the entries in order. The newest is at the bottom.

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
