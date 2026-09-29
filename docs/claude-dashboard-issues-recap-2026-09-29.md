# Open issues recap, 2026-09-29

29 issues were open. Each one was checked against the code on `main` at `e86a92c`, the git log and the event archive.

**Closed on 2026-09-29:** #4 and #20 as `obsolete`, #41 as `duplicate` of #26. All three closed as "not planned", because none of them was fixed. **26 remain open.**

**How to read the table**

- **Still relevant?** — **No** means the problem is gone. **Partly** means later work covers some of it. **Yes** means it is still true today.
- **Severity** applies to bugs only, and it is my rating as of today. Where it differs from the label on GitHub, the label is in brackets. Enhancements show "—".
- **Who notices** — **User** means a person using the app can see it. **Dev** means only a person working on the code can see it.

## Summary

| Result | Issues |
|---|---|
| **No longer relevant: closed** | ~~#4~~, ~~#20~~, ~~#41~~ (a duplicate of #26) |
| **Partly relevant: close or rule** | #9, #10, #13, #30, #38, #40 |
| **Still true, user-facing** | #3, #7, #14, #21, #22, #26, #36, #46, #49 |
| **Still true, developer-only** | #8, #11, #12, #19, #23, #24, #25, #27, #31, #32, #34 |

- **High:** #26.
- **Medium:** #3, #49.
- **Every other bug is Low.**

## The table

| # | What the problem is, in plain words | Still relevant? | Severity | Who notices |
|---|---|---|---|---|
| 3 | Incoming messages wait in a queue that holds 1,024. When the queue is full, the dashboard deletes the oldest message. If the deleted message is "this session needs your permission", the row never turns red and never beeps. This needs the dashboard to stall while 1,024 messages pile up. That has never happened. | Yes | Medium (High) | User |
| ~~4~~ | **Closed.** In the old design, the hook entries in Claude Code's settings held the dashboard's port number. When the port changed, the stale entries stayed. Every Claude turn then showed a connection error. | **No.** Since T1.28 the hook holds no port: one command reads the current port from a file. The installer also removes every old port entry. | — | — |
| 7 | The dashboard saves its own settings file by erasing it first and then writing it again. A crash or power loss at that instant leaves the file empty. | Yes. Fix it with #26. | Low | User |
| 8 | The dashboard sometimes cannot add its hook to Claude Code's settings file. After five tries, the log always blames "another process", even when that is not the cause. Nothing on screen says that the hook is missing. | Yes | Low (Medium). Seen once, in a test. | Dev |
| 9 | Claude Code sends some notification types that the dashboard does not know, for example `auth_success`. The dashboard ignores them and writes no log line. | **Partly.** Since T1.37 the decisions table records each ignored event, but not that the type was unknown. 2 such events arrived in a month. | Low | Dev |
| 10 | *Enhancement.* No tool outside the app can ask the running dashboard what state it thinks each session is in. | **Partly.** Since T1.37 every state change goes into `dashboard.db`, which any tool can read. | — | Dev |
| 11 | A future log line that prints a whole event would copy your prompts and Claude's replies into the log file. No such line exists today. A test lists every text field so that a new one is caught. | Yes. It is a risk, not a fault. | Low (Medium) | Dev |
| 12 | Once, the test runner crashed part-way through and still printed "Passed!". 110 tests did not run. The build script cannot see the difference. | Yes. Seen once, on 2026-08-26, and not since. | Low (High) | Dev |
| 13 | When you unplugged a headset, the dashboard went silent until you restarted it. | **Partly.** T1.22 fixed it in the code. The only open item is a manual unplug test on real hardware. | Low (Medium) | User |
| 14 | When no port is free, the tray tooltip says "port taken · not receiving hooks". It does not say what to do: free a port and restart. | Yes | Low (Medium) | User |
| 19 | Code comments link to other code by name. The build does not check these links, and one link is wrong. | Yes | Low | Dev |
| ~~20~~ | **Closed.** Claude Code never delivered the "session started" message to the dashboard. | **No.** T1.28 fixed it. These messages arrive from 2026-08-30. | — | — |
| 21 | The prompt preview on a row stops at 140 characters. The cut can split an emoji in two, and the row then ends in a broken symbol. | Yes | Low | User |
| 22 | An invisible direction character in a session title can make the prompt beside it read backwards. | Yes | Low | User |
| 23 | When you switch between Grouped and Flat, the app writes harmless binding errors to the debug output. The user sees nothing. Because of this, no test can click that switch. | Yes | Low | Dev |
| 24 | Git has no line-ending rule for `.xaml` files. Such a file can change bytes when nobody edited it. The fix is one line. | Yes | Low | Dev |
| 25 | The test that stops prompts from reaching the log sees only single text fields. It does not see lists of text. | Yes. Pair it with #11. | Low | Dev |
| 26 | Make one typo when you edit the dashboard's settings file by hand, and the app starts on defaults. At the next save, when you quit or save a group, it writes those defaults over your file. All your settings are lost. | Yes | **High** | User |
| 27 | Two data types compare their lists by identity, not by content. Two equal copies can then read as "different". Nothing uses this comparison today. | Yes | Low | Dev |
| 30 | *Enhancement.* Ship the hook as a Claude Code plugin, so that the app does not edit Claude's `settings.json`. | **Partly.** The main reason is smaller now: the app writes one hook entry, only when it is missing, with a safe write. | — | User |
| 31 | *Enhancement.* Rename `DefaultPort`. It is no longer a default port; it is the start of a range. | Yes | — | Dev |
| 32 | No test proves that a start with no pinned port picks a per-user port. The alternative is the shared base port, which issue #5 fixed. | Yes | Low | Dev |
| 34 | No test pins the two timing values for group settling, 1.5 s and 5 s. A change to either value passes every test. | Yes | Low | Dev |
| 36 | *Enhancement.* Start the dashboard when Windows starts. | Yes. The code for this exists but nothing calls it. It must start `current\ClaudeDashboard.App.exe`. | — | User |
| 38 | *Enhancement.* After a session finishes, show a countdown to when Claude's short-term cache for that session expires. It starts green and turns red in the last minute. The issue text stops mid-sentence. | **Partly.** Finished rows already show the time since finish, counting up. The countdown and the colours are new. | — | User |
| 40 | A database test fails about 1 run in 30, because it reads the file before the file is ready. | **Partly.** Not seen in recent test runs. It can be stale. | Low | Dev |
| ~~41~~ | **Closed.** The save when you quit writes defaults over an unreadable settings file. | **No: a duplicate of #26.** It is the same fault on one of the save paths. | — | — |
| 46 | In select mode, a selected row that moves into a collapsed section stays counted. "Group these" can then group a session that you cannot see. | Yes | Low | User |
| 49 | After you press Escape to stop Claude, the row keeps "Working" for 10 minutes. | Yes. The cd-observability session owns it. | Medium | User |

## Suggested next steps

1. ~~**Close #4, #20 and #41** with a comment that gives the evidence.~~ **Done 2026-09-29.**
2. **Rule on the six "Partly" issues.** My recommendations:
   - close #10, because the decisions table covers it;
   - close #9 and #40 as not worth the work;
   - run the headset unplug test for #13, then close it;
   - decide whether #30 and #38 are wanted.
3. **Fix #26 and #7 together** before the first release. #26 is the only High.
4. **Consider #36** (start with Windows) for the first release. A tray app is expected to do this.
5. **Reduce the developer-only list.** Eleven issues are only visible in the code. You can put a label such as `internal` on them, or collect them in one tracking issue and close the rest. The public list then shows only what a user can see.

## Labels for an issue closed without a fix

GitHub records every close as **completed** or **not planned**. The rule: a fixed issue closes as completed, everything else closes as not planned. One label then says why.

| Label | Means | Exists already |
|---|---|---|
| `obsolete` | The code changed. The problem cannot happen now. Other work removed it; nobody fixed this issue. | Created 2026-09-29 |
| `duplicate` | The same fault as another issue. The comment names the survivor. | GitHub default |
| `wontfix` | Real, but too small to spend time on. | GitHub default |
| `can't reproduce` | It stopped happening, and nobody can make it happen again. | Not yet created |

All four are gray, because they are bookkeeping. They must not compete with `bug` and `enhancement`.

**Applied so far:** `obsolete` on #4 and #20, `duplicate` on #41.
