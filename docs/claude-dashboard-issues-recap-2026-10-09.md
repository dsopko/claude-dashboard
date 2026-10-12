# Claude Dashboard: open issues recap, 2026-10-09

Oct 9, 2026 · @David

**Since then:** #58 was built in T1.84 (PR #148) and closed on 2026-10-11. The rest of this page is as written on 2026-10-09.

All five are still open and still true. Each was checked against the code on `main` at `37abc6c` (0.0.27), the git log and the previous recap of 2026-09-29. Nothing has touched any of them since they were filed. Two are visible to you (#38, #49); three are visible only in the code (#19, #32, #58).

**How to read the table.** *Still an issue?* — **Yes** means it is true today. *Severity* is for bugs only; an enhancement shows "—". *Who notices* — **User** means you can see it in the app; **Dev** means only someone working on the code can.

| # | In one sentence | Still an issue? | Severity | Who notices | Recommended |
| --- | --- | --- | --- | --- | --- |
| 19 | Code comments link to other code by name, nothing checks the links, and one is wrong. | Yes, unchanged | Low | Dev | Fix the one wrong link (one line); leave the rest, labelled `wontfix` as it already is |
| 32 | No test proves that a start with no chosen port picks a per-user port rather than the one shared port. | Yes, unchanged | Low | Dev | One small test; pair it with the `DefaultPort` rename (#31) |
| 38 | *Enhancement.* After a session finishes, count down the minutes until Claude's short-term memory of it expires. | Yes; wanted or not is your call | — | User | Rule: build it with a settings value (5 minutes default), or close it |
| 49 | After you press Escape, the row says "Working" for ten minutes. | Yes, unchanged | Medium | User | Lower the wait to 2 or 3 minutes and make it a setting; one task |
| 58 | `/state` says `null` for a session's next reminder when it means two different things. | Yes, unchanged | Low | Dev | Keep open, fix when a tool other than you reads `/state` |

## #19 — Code comments link to other code, and nothing checks the links

**Still an issue: yes, exactly as filed on 2026-08-29.** The build setting that would check the links (`GenerateDocumentationFile`) is still off in `Directory.Build.props`, and the one known-wrong link is still there, now on line 87 of `HealthProbe.cs`. It has the labels `bug`, `wontfix` and `severity:low`, so someone has already decided not to spend time on it, but it was never closed.

**What it means for you:** nothing. No one using the app can see it. A developer reading the code can be sent by a comment to a function that does not exist, or to one of two with the same name.

**Why it is not a one-line fix:** turning the check on also turns on two other checks (a public function with no comment, a comment that skips a parameter), and the product projects treat every warning as a build error. At filing that was 92 items to tidy before the build would pass again.

**Recommended:** fix the one wrong link now (name the right one of the two functions; one line, no build change) and close the issue as `wontfix` with that note. The full tidy-up is a day's work with no user-visible result; it can be its own issue if ever wanted.

**Rewritten text, ready to paste as the issue's lead:**

> Code comments in this repository point at other code by name ("see X"). The build never checks those names, so a comment can point at something that was renamed, removed, or exists twice. One already does: a comment in `HealthProbe.cs` names a function that exists in two versions, so it names neither. Nothing in the app is affected; only a developer reading the code is misled. Turning the check on would also require writing about 90 missing comments first, because the product projects treat warnings as errors.

## #32 — No test proves that each user gets a port of their own

**Still an issue: yes, exactly as filed on 2026-08-30.** No test starts the dashboard with no port chosen in the settings and then checks that the port it picked is *not* the one shared starting port. The closest test (`A_malformed_settings_file_falls_back_to_defaults_and_still_serves`) starts a host with no port and reads the port back, but it only checks that the host answers; it does not check which port that is. Its own remark says the unpinned start is "exactly the case the derivation exists for", which is the claim this issue wants held by a test.

**What it means for you:** nothing today; the code is right. The risk is for later: if someone ever puts the old shortcut back ("no port in the file, so use the shared one"), every test still passes, and two people signed in on one machine would both start, one listening and one hearing nothing, with no error. That is the fault #5 fixed, and this test is the guard that keeps it fixed.

**Recommended:** one small test (start with no port, read the port from the host, assert it is not the shared one). Do it in the same task as #31, the rename of `DefaultPort` to a name that says "start of the range", since both are about the same line of code.

**Rewritten text, ready to paste as the issue's lead:**

> When no port is written in the settings file, the dashboard must pick a port of its own for this user, never the one shared starting port, or two users on one machine would collide silently (that was #5). The code does this. No test proves it: every existing test writes a port into the settings first, so putting the old shortcut back passes the whole suite. The fix is one test that starts the dashboard with no port set and checks that the port it picked is not the shared one.

## #38 — Count down until Claude forgets a finished session

**Still an issue: it is a request, not a fault, and it is still open and unbuilt.** Nothing in the code or the documents mentions it; the previous recap put it under "decide whether it is wanted", and no decision was recorded.

**The idea, checked:** the premise is right in kind. Anthropic keeps a session's prompt in a short-lived cache; a next turn that arrives before the cache expires is faster and cheaper. The default life is **5 minutes**; some sessions get **1 hour** (this director session, for one). So the right number is not the same for every session, and the dashboard has no way to learn which applies. A setting with 5 minutes as the default is the honest answer.

**What exists today:** a finished row already shows the time since it finished, counting up ("3m ago"). The countdown, the green-to-red colour, and the switch back to "ago" after the deadline are all new. It is small: the row's clock already ticks every second, so this is a second reading of the same clock with a colour.

**What is unclear and needs your word:** (1) wanted at all; (2) 5 minutes as the default; (3) the colours: the issue says green, then red in the last minute, which uses the row's attention colours for something that is not attention. A plain grey countdown that turns red in the last minute is less confusing.

**Recommended:** if you want it, rule the three points above and it becomes a short task; if not, close it as `not planned`.

**Rewritten text, ready to paste as the issue's lead:**

> After a session finishes, Claude keeps its context ready for a few minutes (5 by default). If you send the next prompt inside that time, the answer comes faster and costs less. Ask: on a finished row, replace "3m ago" with a countdown to that deadline, red in the last minute, and go back to "x minutes ago" once it has passed. The number of minutes is a setting, 5 by default, because some plans keep the context for an hour.

## #49 — After Escape, the row says "Working" for ten minutes

**Still an issue: yes, unchanged since 2026-09-25.** The wait is still ten minutes (`SilenceWatch.DefaultThreshold`), there is still no setting for it, and the Implementation Specification still says "ten minutes is a guess (issue #49)". The previous recap said another session owned it; no commit mentions #49 and nothing changed. One related thing did land: since T1.47 an interrupted row counts its "ago" time from the last message heard, not from the moment the dashboard noticed, so once the row does flip, its time is right.

**What it means for you:** press Escape a few seconds into a turn and the row stays blue and "Working" for ten minutes, or until you send the next prompt. The dashboard cannot see the Escape: Claude Code sends no message for it. It can only notice silence, and it waits ten minutes before calling silence an interrupt.

**What the numbers said at filing** (a week of real use): inside a normal turn, the gap between two messages is 7 seconds in the middle, 24 seconds at the 90th percentile, and over two minutes only 28 times in 5,501 gaps. Only three turns in two weeks went ten minutes silent without ending. So ten minutes protects against a rare case at the price of a wrong row after every quick Escape. A two-minute wait would have been wrong 28 times that week, each time for a long build or test, and each time the row goes back to blue on its own when the tool finishes.

**Recommended:** lower the wait to **2 minutes** (3 if you often run tools longer than that) and make it a settings value, so you can tune it to your own tool calls. One task; the sweep and its log line already exist, only the number and the setting change. This is the one issue of the five that is worth doing soon.

**Rewritten text, ready to paste as the issue's lead:**

> Press Escape to stop Claude a few seconds into a turn and the row keeps saying "Working" for ten minutes. Claude Code sends no message when you press Escape, so the dashboard can only notice that the session has gone quiet, and today it waits ten minutes before it does. A week of real use shows that normal turns are almost never quiet for more than two minutes, and that a quick Escape is common. Ask: wait two minutes instead, and make the number a setting, because people with long builds may want more.

## #58 — `/state` cannot say why a session has no reminder time

**Still an issue: yes, unchanged since 2026-09-29.** `/state` is the web address a tool can ask for everything the dashboard believes. For each session it gives `nextNudgeAt`, the time of the next reminder sound, or `null`. The `null` still has two meanings: no reminder is coming, or a reminder *is* coming but the group (a roster of several sessions) owns it, which happens only for a finished member of a roster. The code is as it was, and the Implementation Specification (§3.5) and the README carry the explanation instead of the data.

**What it means for you:** nothing on screen. The window shows the group's reminder on the group, which is right. Only a tool reading `/state` is affected, and today the only readers are you and the acceptance run. A related wrinkle, same issue: a muted session still shows a time, because mute silences the sound and does not stop the schedule.

**Why it was filed and not fixed:** the task that built the field (T1.46) was asked for the per-session time only. Fixing it means deciding the shape: one field plus a reason, two fields, or a small object; and whether the group's time is reported on the member, the group, or both.

**Recommended:** keep it open and do nothing until something other than a person reads `/state` (a web or phone surface, Phase 7). The documents already say what `null` means. When that reader arrives, it decides the shape.

**Rewritten text, ready to paste as the issue's lead:**

> `/state` tells a tool when each session's next reminder sound is due, or `null`. But `null` means two things: "no reminder is coming", or "a reminder is coming, and the group this finished session belongs to will play it". A tool cannot tell which. Nothing on screen is wrong; the window shows the group's reminder on the group. The fix needs a decision on the shape of the field (a reason beside it, or a second field), so it is filed for when a tool other than a person reads `/state`.

## What you decide

One word per issue is enough; the director does the rest (rewrites the issue text, files the task, or closes with a comment that gives the evidence).

- [ ] **#49** — say **2** or **3** (minutes) and it becomes a task with a settings value. Recommended: 2.
- [ ] **#38** — say **build** (with 5 minutes as the default and a grey countdown that turns red in the last minute) or **close**.
- [ ] **#32** — say **task** and it goes with the `DefaultPort` rename (#31) as one small developer task.
- [ ] **#19** — say **fix the line** and the one wrong link is corrected and the issue closed as `wontfix`; or **leave** it open as it is.
- [ ] **#58** — say **keep** (recommended) and it waits for a tool that reads `/state`; or **close** as not planned, since the documents already explain the `null`.
- [ ] **Rewrite the issue text** — say **rewrite** and each issue's body gets the plain lead above, with the original kept below a line as the technical record.

Severity and labels today: #19 `bug, wontfix, severity:low`; #32 `bug`; #38 `enhancement`; #49 no label (should be `bug`, Medium); #58 `bug, severity:low`.
