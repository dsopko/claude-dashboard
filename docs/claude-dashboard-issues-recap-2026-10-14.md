# Claude Dashboard: open issues recap, 2026-10-14

Oct 14, 2026 · @David

17 issues are open: 9 problems and 8 enhancements. Each was checked against the code on `main` at `a7e48b1` (0.0.28 and the documents after it), the git log, the open pull requests and the two previous recaps (2026-09-29, 2026-10-09).

**In short:**
- **Three enhancements are already built and only need closing:** #144, #145 and, apart from your own check, #133.
- **Two problems have a fix waiting in a pull request:** #155 (PR #156) and #19 (PR #158).
- **One problem is mostly fixed:** #143.
- **Six problems are still true with no fix started:** #157, #49, #134, #142, #140 and #123.
- **Five enhancements are not built:** #147, #100, #38, #116 and #117.

**How to read the tables.**
- *Still an issue?*: **Yes** means it is true today. **Partly** means later work covers some of it. **Built** means the code does it, and the issue was never closed.
- *Severity*: my rating today, for problems only.
- *Who notices*: **User** means you can see it in the app. **Dev** means only someone working on the code can.

---

## Part 1: Problems (not enhancements)

| # | In one sentence | Still an issue? | Severity | Who notices | Recommended |
| --- | --- | --- | --- | --- | --- |
| 157 | The Activity button opens the Activity window still filtered to one session, days after you used Show activity on a row. | Yes, filed today | Medium | User | Fix it: one small task |
| 49 | After you press Escape, the row says "Working" for ten minutes. | Yes, unchanged | Medium | User | 2 minutes, as a setting: one task |
| 134 | The plugin's name `claude-dashboard` is reserved by Claude Code; a later version may stop loading it, and the dashboard would go deaf. | Yes, not started | Medium | User (when it breaks) | Do it next; start with the measuring step |
| 142 | Hovering the counts in the caption shows no tooltip; the same counts on the row below do. | Yes, unchanged | Low | User | One attribute; do it with #147 |
| 143 | A missing usage figure could not be explained from the log file. | Partly: mostly fixed in 0.0.27 | Low | User | Close |
| 140 | A background task with a status other than "running" is dropped with no line in the log file. | Yes, unchanged | Low | Dev | Keep; do it in the next small log task |
| 155 | A test fails when another program happens to hold one of its fixed port numbers. | Fix waiting: PR #156 | Low | Dev | Merge #156, then close |
| 19 | Code comments link to other code by name, and nothing checks the links. | Fix waiting: PR #158 | Low | Dev | Merge #158, then close |
| 123 | A test of the Activity window's click sometimes fails: the main window did not come up. | Yes, cause unknown | Low | Dev | Keep; one short investigation |

### #157: The Activity button opens a filtered list

**Simpler:** you click **Activity** and see one session's lines only. The other lines are still there, hidden by a filter you set days ago with **Show activity** on a row. Closing the window only hides it, so the filter stays. The bar at the top names the session by a short code (`Only 457d3a3e`), which you don't recognise.

**Still an issue: yes.** Filed today, cause found. In the code, the toolbar's **Activity** and the tray's **Activity…** only show the window again (`TrayViewModel.OpenActivity` raises `ActivityRequested`). Only **Show all** clears the filter (`ActivityViewModel.ShowAll`).

**What it means for you:** the Activity window looks like it lost everything. Nothing is lost: **Show all** brings it back.

**Recommended:** the issue's two fixes, as one small task. **Activity** always opens the full list, and the bar names the session the way its lines do, with the project after it. It has no label; it should be `bug`.

### #49: After Escape, the row says "Working" for ten minutes

**Simpler:** Claude Code sends nothing when you press Escape. The dashboard can only notice that a session has gone quiet, and it waits ten minutes before it calls quiet "interrupted". So a quick Escape leaves the row blue and "Working" for ten minutes, or until your next prompt.

**Still an issue: yes, unchanged since 2026-09-25.** The wait is still `SilenceWatch.DefaultThreshold = 10 minutes`, with no setting. The 2026-10-09 recap recommended 2 minutes; no decision was recorded.

**What it means for you:** a wrong "Working" after every quick Escape, at exactly the moment you are looking.

**Why 2 minutes is safe:** a week of real use showed that a normal turn is quiet for more than 2 minutes only 28 times in 5,501 gaps, each a long build or test. In those cases the row would go grey, then back to blue by itself when the tool finished.

**Recommended:** 2 minutes, as a settings value. One task. It has no label; it should be `bug`.

### #134: The plugin's name is reserved by Claude Code

**Simpler:** the plugin that carries Claude Code's messages to the dashboard is named `claude-dashboard`. Claude Code now keeps names that start with `claude-` for Anthropic. Today that only fails a check. If a later Claude Code refuses to load the plugin, the dashboard hears nothing, with no error.

**Still an issue: yes, not started.** The issue now holds your decision: the new name is `agent-dashboard`, and the install's first step uninstalls `claude-dashboard` (plugin and marketplace) completely. It also holds the eight steps to build it.

**What it means for you:** nothing today. When it breaks, every row stops changing.

**Recommended:** do it next among the bigger items. Its first step only measures Claude Code's behaviour on a scratch folder, and needs your word before `claude` runs.

### #142: The counts' tooltip in the caption never opens

**Simpler:** when the window is narrow, the caption shows the counts as bare numbers (`11 · 3 · 5 · 8`). Hovering is meant to spell them out ("11 sessions · 3 need you…"). In the caption nothing appears, because the caption is the window's drag area and the counts are not marked to receive the mouse there.

**Still an issue: yes, unchanged.** `CaptionCounts` in `MainWindow.xaml` still lacks `WindowChrome.IsHitTestVisibleInChrome`. The usage figures beside it have it, and their tooltips work.

**What it means for you:** small. The colours name the numbers, and the counts row below shows the tooltip.

**Recommended:** one attribute, in the same task as #147. A real mouse hover must confirm it: a test passes even when the tooltip can't open.

### #143: A missing usage figure could not be explained

**Simpler:** on 2026-10-09 the 5-hour figure vanished for two hours, and nothing said why.

**Still an issue: partly; the cause is gone.**
- 0.0.27 (MOD.8) built two of the three asks. The log file now writes one normal line when a usage post changes the picture, and the Debug line carries each reading in full, with its reset time.
- #145, in the same release, removed the blank itself: a limit past its reset time now shows `100%` instead of vanishing.
- Not built: a line in the Activity window when a figure resets. It needs a new kind of record.

**What it means for you:** the blank that started this no longer happens, and the log file now explains a usage post.

**Recommended:** close it. The Activity line explains a blank that no longer happens. If you still want it, it can be its own small enhancement.

### #140: An unknown task status leaves no trace

**Simpler:** Claude Code tells the dashboard which background tasks are still running. A task whose status is anything but `running` is skipped, and the log file says nothing. If Claude Code ever adds a status such as `pending`, a session could show "finished" while it is still working, and nothing would say why.

**Still an issue: yes, unchanged.** `BackgroundTaskReader.Read` skips the entry (line 56) before the place that logs unknown task types.

**What it means for you:** nothing today. Every task seen so far has status `running`. This is an early warning, not a fault.

**Recommended:** keep it, and fold it into the next small log-file task. It is about a quarter of a day.

### #155: A test fails when another program holds its port

**Simpler:** a test listens on fixed port numbers. Windows hands those numbers to any program's outgoing connections, so once in a while one is taken and the test fails for no fault of the dashboard.

**Still an issue: being fixed.** PR #156 (two commits) makes the test move to another port and try again, and name every port it tried. It is open and waiting for review.

**What it means for you:** a false red test run, rarely.

**Recommended:** merge #156 when its review passes, then close.

### #19: Code comments link to other code, and nothing checks the links

**Simpler:** code comments point at other code by name. The build never checked those names, so a comment could point at something renamed or duplicated, and one did.

**Still an issue: being fixed.** PR #158 turns the compiler's check on for every project and fixes every finding: one wrong link and three comments on the wrong member. Both test configurations pass at 2,545 tests, and the release package does not change. It is open and waiting for review.

**What it means for you:** nothing in the app.

**Recommended:** merge #158 when its review passes, then close. Its labels still include `wontfix`, which the fix makes untrue.

### #123: The Activity click test sometimes fails

**Simpler:** a test clicks a line in the Activity window and checks that the main window comes up. Once in a while it doesn't, and nobody knows why.

**Still an issue: yes, cause unknown.**
- Seen five times in early October (once in T1.77, four times in T1.78's 86 saved runs), the last on 2026-10-06, with the output saved.
- That time the click test and the Enter-key test failed together, at the same check. So the cause is something both share: the line's command (`ShowDashboard`), or the window's state when the test checks it. It is not the mouse press.
- The test file has not changed since 2026-10-09.

**What it means for you:** an occasional false red test run.

**Recommended:** keep it. One short investigation: run the two tests in a loop until they fail, and record the window's state at the failure.

---

## Part 2: Enhancements

| # | In one sentence | Still an issue? | Who notices | Recommended |
| --- | --- | --- | --- | --- |
| 144 | The usage hover says "Usage this session…", and usage past 100% shows `-10%` in red. | **Built** in 0.0.28 | User | Close once you've seen the hover |
| 145 | A usage limit past its reset time shows `100%` instead of a blank. | **Built** in 0.0.27 | User | Close |
| 133 | Show the plan's usage: the plugin's mod posts it, and `/state` answers it. | **Built**, except your Windows check (MOD.6) | User | Tick the check, then close |
| 147 | The caption's usage label `Current` should read `Session`. | Not built | User | Do it with #142 |
| 100 | A session in a git worktree shows the worktree's folder name, not the project's. | Not built | User | Rule the three questions; a small task |
| 38 | After a session finishes, count down the minutes until Claude's cache of it expires. | Not built | User | Rule it, or close |
| 116 | Show a session's message details as formatted Markdown, not raw text. | Not built | User | Decide the scope first |
| 117 | Nicer-looking scroll bars. | Not built | User | Pick a style |

### #144: The new usage hover and usage past 100%

**Simpler:** you asked for the hover to start with "Usage" and for a limit used past 100% to show `-10%` in red.

**Status: built in 0.0.28** (PR #154, merged as `1ed0c10`). Testing was the short version you asked for: the usage tests only, with no review and no real hover.

**Recommended:** hover over a figure in the installed 0.0.28. If the words are right, close it.

### #145: A reset limit shows as fresh, not blank

**Simpler:** when a limit resets, show it as `100%` left instead of leaving a gap until the next reading.

**Status: built in 0.0.27** (MOD.8, PR #146). The hover says the reset happened and that the next reset time comes with the next reading. Never closed.

**Recommended:** close.

### #133: Show the plan's usage

**Simpler:** the big usage feature. A small add-on that rides in the dashboard's plugin sends the 5-hour and weekly figures to the dashboard after each turn, and `/state` answers them.

**Status: built.**
- MOD.1 to MOD.5 built it, MOD.7 put it in the window, and MOD.8 turned it into what is left.
- The only open part is MOD.6: your own checklist on Windows, 13 items. Most of them you already see every day: the figures arrive and match claude.ai, and rows still change.
- Two items need an action: `npx -p typescript@5.9.3 tsc -p mods/usage`, which downloads TypeScript and needs your word, and the Desktop app or VS Code check, if you use either.

**Recommended:** say **done** for the items you've seen, decide the TypeScript check, and close.

### #147: Rename the caption label Current to Session

**Simpler:** the caption reads `Current 95%`; you want `Session 95%`.

**Status: not built.** The hover part was built by #144, and the issue was rewritten today to the label only. It is queued with #142.

**Recommended:** one small task with #142. It needs a check of the caption widths, because "Session" may not be the same width as "Current".

### #100: A worktree session shows the wrong project name

**Simpler:** a session running in a git worktree (for example under `.claude\worktrees\`) shows the worktree's folder name, such as `plugin-only-route`, instead of `claude-dashboard`. It also gets a group of its own.

**Status: not built.** Nothing in the code looks for worktrees. The name is the last folder of the path.

**What it means for you:** more than when it was filed. Your coder and reviewer sessions run in worktrees, so they show folder names you didn't choose.

**Recommended:** three rulings, then a small task.
1. **Only `.claude\worktrees\`**, the folder Claude Code uses. A worktree anywhere else needs git, which the dashboard doesn't run.
2. **Show the project's name and join the project's group.** Orchestrations are grouped by rosters, not by folder.
3. **Put the worktree's name in the hover.**

### #38: Countdown until Claude forgets a finished session

**Simpler:** after a session finishes, Claude keeps its context ready for a few minutes (5 by default, an hour on some plans). A prompt inside that time is faster and cheaper. The idea: on a finished row, replace "3m ago" with a countdown to that deadline.

**Status: not built, and not ruled.** The 2026-10-09 recap asked for three decisions; none was recorded:
1. Do you want it at all?
2. Is 5 minutes the right default, as a setting?
3. Which colours? A grey countdown that turns red in the last minute is clearer than green, which the row already uses for something else.

**Recommended:** rule the three points and it becomes a short task, or close it as not planned.

### #116: Show message details as Markdown

**Simpler:** Claude Code shows its answers formatted (headings, lists, code blocks). The dashboard shows the same text raw, with the `#` and `*` marks visible.

**Status: not built.** Nothing in the app renders Markdown.

**What it takes:**
- A Markdown library (for example Markdig) and a way to draw its output in WPF.
- The rule "text is data" still holds: formatting only, never running anything. Links are drawn as text, or open only on a click you make.
- Long answers must stay fast to draw.

**Recommended:** decide the scope first. Which places get formatting: the expanded row's prompt and answer only, or the Activity window too? And do links open? With those answered, it is a medium task.

### #117: Nicer scroll bars

**Simpler:** you don't like the standard Windows scroll bars and want something more elegant.

**Status: not built.** The window uses the default scroll bars; no style is set.

**Options:**
- (a) A thin bar, about 4 pixels, that widens when the mouse is over it, like Windows 11 and VS Code. This one is recommended.
- (b) A thin bar always.
- (c) A bar that appears only while you scroll.

**Recommended:** pick (a), (b) or (c). It is a small styling task, for the main window and the Activity window together. The designer session is a good fit.

---

## What you decide

One word per item is enough.

**Problems**
- [ ] **#157**: say **fix** and it becomes one small task.
- [ ] **#49**: say **2** or **3** (minutes), and it becomes a task with a setting. Recommended: 2.
- [ ] **#134**: say **start** and the measuring step runs on a scratch Claude folder.
- [ ] **#143**: say **close** (recommended), or **keep** for the Activity line.
- [ ] **#140**: say **keep** (recommended), or **close** as not planned.
- [ ] **#123**: say **investigate** for one short loop run, or **keep** as it is.
- #142, #155 and #19 need nothing from you: #142 is queued with #147, and #155 and #19 close when their pull requests merge.

**Enhancements**
- [ ] **#144** and **#145**: say **close**.
- [ ] **#133**: say **done** for MOD.6, and **yes** or **no** to the TypeScript check.
- [ ] **#100**: say **as recommended**, or give your three answers.
- [ ] **#38**: say **build** with your three answers, or **close**.
- [ ] **#116**: name the places and whether links open.
- [ ] **#117**: say **a**, **b** or **c**.

**Labels to fix:** #157 and #49 have no label (both should be `bug`). #19 still has `wontfix`, which PR #158 makes untrue.
