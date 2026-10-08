# Claude Dashboard — Usage Mod Execution Plan

**Status:** Proposed 2026-10-08 from the [Usage Mod Development Guide](claude-dashboard-usage-mod-guide.md), whose "Build plan for the agents" section this document is. Tracked by [issue #133](https://github.com/dsopko/claude-dashboard/issues/133). Where this document and the guide disagree about what the mod and the endpoint do, the guide is right; where either disagrees with the specifications about what the product does today, the specifications are right.

**Workflow:** the director/coder/reviewer roles, the hand-off contract, the dispatch rules (`notify_when_idle` on every dispatch, the standing watchdog) and the green-run rule are those of the main [Execution Plan](claude-dashboard-execution-plan.md), Part 1 and Appendices B and C. They apply here unchanged. Task IDs use the `MOD.` prefix, as the packaging plan uses `PKG.`.

**Dependency order:** MOD.1 → MOD.2 → MOD.3 → MOD.4 → MOD.5 → MOD.6. MOD.3 to MOD.5 do not depend on MOD.1 and MOD.2, but the coder takes one task at a time, in this order. MOD.6 is the operator's.

**Rulings, taken by the director on 2026-10-08** (the guide's recommendation in each case; the operator may overrule any of them):

- **R1.** The plugin keeps the name `claude-dashboard`. The rename is [issue #134](https://github.com/dsopko/claude-dashboard/issues/134), a task of its own. It follows that `claude plugin validate` cannot pass on the plugin the dashboard writes; it runs on the development copy, `mods/usage/`, whose name is neutral.
- **R2.** A: the mod's source lives in `mods/usage/` and `register.ts` is embedded in the App assembly as a linked resource. The repository holds no second copy of the text.
- **R3.** Yes: a `401` on `/usage` counts as a refusal, as on `/hook` (`HookHealth.Refused`, the `HookRefused` row, the notice after three in ten minutes).
- **R4.** A, as part of MOD.1: `build.ps1` runs `claude plugin validate --strict mods/usage` and `claude plugin test mods/usage` as a step of its own, after the build and before the .NET tests, and the verdict line names the result (`mod 28 pass` or the reason it did not). **A machine with no `claude` on the path, or with mods turned off, is NOT GREEN,** with the reason named: a green verdict says that everything was verified, and every machine this repository is built on has Claude Code. The mod's count never joins `Total`. `-NoTest` skips the step with the .NET tests.
- **R5.** No: an accepted `/usage` post does not move `HookHealth.Heard`. `usage.lastHeardAt` is an instant of its own.
- **R6.** `usage` in `/state` as the guide's "The server endpoint" gives it; the type names are the coder's.
- **R7.** Nothing on screen in this plan. A figure in the window or the tray is a Design Document decision, to be ruled with the operator after MOD.6.

**On this machine (2026-10-08):** Claude Code 2.1.293, above the documented minimum 2.1.287; `claude plugin test` and `claude plugin validate` are present. The lab kit (`usage-mod-lab.zip`) is not in the repository and not needed: every file MOD.1 commits is in the guide's appendix.

---

## Build plan for the agents

This section is for the director, the coder and the reviewer. It is a proposal in the form of the Execution Plan's Part 3, with the prefix `MOD.` as the packaging plan has `PKG.`. The roles, the hand-off contract and the working agreements of Part 1 apply unchanged. Nothing in it is built.

**Order:** the rulings, then MOD.1 → MOD.2 → MOD.3 → MOD.4 → MOD.5 → MOD.6. MOD.1 and MOD.2 are the plugin's side. MOD.3 to MOD.5 are the dashboard's side and do not depend on the first two. MOD.6 is the operator's.

**In the repository** this guide can be `docs/claude-dashboard-usage-mod-guide.md`, and this section a side plan beside it, as the packaging plan is. `CLAUDE.md` then lists both.

### Rulings needed before the work starts

- **R1. The plugin's name.** `claude plugin validate` fails the name `claude-dashboard` since Claude Code 2.1.287. Does the plugin keep it?
  - **Recommended:** keep it in this work, and open an issue for a rename as a task of its own.
  - **Why:** a rename is more than text. A start must find the old plugin and have it removed, or each event posts twice, as with the old hook of Impl §9.4. Install and load work today.
  - **It follows that** `claude plugin validate` cannot pass on the plugin that the dashboard writes. It runs on the development copy, whose name is neutral.
- **R2. Where the mod's source lives.** (A) `mods/usage/` in the repository, with `register.ts` embedded in the App assembly as a linked resource. (B) A C# constant, as `HookScript.Body` is, and a test that holds a development copy equal to it.
  - **Recommended:** A.
  - **Why:** `claude plugin validate`, `claude plugin test` and `tsc` then run on the file that ships. With B there are two copies. A text that is read one time from a resource compares with the file on disk as a constant does.
- **R3. A refused `/usage` post.** Does a `401` on `/usage` count as a `401` on `/hook` does: `HookHealth.Refused`, the `HookRefused` row, and the notice after three in ten minutes?
  - **Recommended:** yes.
  - **Why:** it is the same fault with the same remedy, and the limit of one row a second then covers both endpoints. The cost: one restart of the dashboard can now give two refusals for each busy session, one from the script and one from the mod.
- **R4. The mod's tests and the verdict of `build.ps1`.** (A) `build.ps1` runs `claude plugin validate --strict mods/usage` and `claude plugin test mods/usage` as a step of its own, and the verdict line names the result. (B) An xUnit test starts `claude`.
  - **Recommended:** A. The ruling also says if a machine with no `claude`, or with mods turned off, can be GREEN.
  - **Why:** the count of the mod's tests cannot join `Total`. xUnit 2.9.3 cannot skip at run time with no new package, so on a machine with no Claude Code a test under B would pass and prove nothing. The documentation says that the test command also exits with status 1 on a machine where mods are turned off.
- **R5. "Last heard".** Does an accepted `/usage` post move `HookHealth.Heard`, the last item of the tooltip?
  - **Recommended:** no. `usage.lastHeardAt` is an instant of its own.
  - **Why:** "last heard" says that the hook's path works: the script, `curl.exe` and the token. A usage post comes by a different path and would hide a broken script.
- **R6. The form of `usage` in `/state`,** as "The server endpoint" gives it, and the rule in Core.
  - **Recommended:** as written. The names of the types are the coder's.
- **R7. What the operator sees.** Not in this plan. A figure in the window or the tray is a decision for the Design Document. This plan stops at `/state`.

### The tasks

**MOD.1 — The mod's source and its tests are in the repository**

- **Goal:** `mods/usage/` holds the mod as this guide gives it, and its 28 tests pass on the coder's machine and on the reviewer's. No product code changes.
- **Depends:** R2 as A. Claude Code 2.1.287 or later on the machine.
- **Realizes:** this guide, "The mod" and "Automated tests". Director's rulings:
  - **The files are those of the appendix, byte for byte:** `.claude-plugin/plugin.json`, `hooks/hooks.json`, `hooks/register.ts`, `hooks/listening-file.ts`, `tests/register.test.ts` and `tsconfig.json`, all under `mods/usage/`.
  - **`*.ts text eol=lf` goes into `.gitattributes`,** beside the line for `*.cs`. MOD.2 compares the module's text byte for byte.
  - **The folder is not a project:** no `.csproj`, and nothing in `ClaudeDashboard.slnx`. The lab receiver of the kit does not go into the repository.
  - **`.claude-plugin/types/` is not committed.** Claude Code writes it at a `--plugin-dir` load, with a `.gitignore` of its own.
- **Deliverables:** the six files; the `.gitattributes` line; a short README section on the three commands (validate, test, `--plugin-dir`); **the `build.ps1` step of R4:** after the build and before the .NET tests, `claude plugin validate --strict mods/usage` then `claude plugin test mods/usage`, each with its output saved in the run's folder (`mod-validate.txt`, `mod-test.txt`); the verdict line gains `mod N pass` (N from the test command's own last lines) or the reason (`no claude on the path`, `mods are turned off`, `validate failed`, `M fail`), and the run is NOT GREEN for every reason; `-NoTest` skips the step. The script's header says why the count never joins `Total`.
- **Acceptance:**
  - `claude plugin validate --strict mods/usage` exits 0. It lists `hooks: session.measure` and the calls `$.clock.after`, `$.fs.read`, `$.http.fetch` and `$.session.id`, and no other.
  - `claude plugin test mods/usage` ends with `28 pass` and `0 fail`, and exits 0.
  - After one `claude -p` run with `--plugin-dir mods/usage`, `git status` shows no new file.
  - **Plants,** each in a copy, with the count of failing tests in the report: (a) the token check out; (b) the `catch` out; (c) the post started inside the hook and not awaited, three runs; (d) `listening.txt` read one time and kept. This guide's table has sixteen, with the counts to expect.
  - `build.ps1` GREEN for Debug and Release, with `Total` as before and `mod 28 pass` in the verdict line. **Plants on the step:** (e) one mod test made to fail, and the verdict is NOT GREEN with `1 fail`; (f) `claude` hidden from the path for one run, and the verdict is NOT GREEN with `no claude on the path`, while the .NET run still reports its Total.
- **Guardrails:** no change under `src` or `tests`. No `package.json` and no need of Node.js. The mod makes the four calls above and no other. It prints nothing and shows nothing.
- **Done 2026-10-08:** PR #135, merged as `a85ced7` (the six files, `.gitattributes`), `ac9664c` (`build.ps1`), `df3e6fd` (README); approved first time. The six files are the guide's appendix byte for byte (the reviewer compared each with `cmp`; one LF at the end of each). On Claude Code **2.1.293**, Windows: `validate` and `test` gave the guide's output word for word, `28 pass`, 0.32 s; a `claude -p --setting-sources project --plugin-dir mods/usage` run left `git status` empty, loaded the mod, took the ENOENT quiet path and made no fetch; the installed dashboard plugin stayed out (0 hooks registered). Mod plants (a) 5, (b) 4 plus the runner's line, (c) 1 in each of 3 runs, (d) 17: the guide's table, on Windows. `build.ps1`: the step runs between the build and the .NET list, saves `mod-validate.txt` and `mod-test.txt`, and the verdict ends `mod 28 pass`; a NOT GREEN line also shows the counts that were read; `Invoke-Logged` gained `-Command` and `-Utf8` (the console code page 437 would otherwise save `❯` as `Γ¥»`). Step plants (e) `NOT GREEN: mod 1 fail; Total 2321, expected 2321; skipped 0` and (f) `NOT GREEN: mod not run: no claude on the path; Total 2321, expected 2321; skipped 0`, by the coder and again by the reviewer. GREEN in both configurations, Total 2321, `mod 28 pass`. **Not verified:** the "mods are turned off" path (it needs a change to Claude Code's settings, which we never make); `tsc` (no TypeScript compiler on this machine; the npx cache holds a deprecated package named `tsc`, so the form is `npx -p typescript@5.9.3 tsc -p mods/usage`, which downloads and needs the operator's word; for MOD.6); pwsh 7 (not installed; Windows PowerShell 5.1 for every run). **Observed:** the step has no expected count for the mod, by R4; a reviewer compares N with 28. Plant (b) would read `mod 5 fail` in the verdict, because the runner counts its own line.

**MOD.2 — The plugin carries the mod**

- **Goal:** the plugin that the dashboard writes holds `hooks\register.ts` and `hooks\listening-file.ts`, and its `hooks.json` names the module beside the eight handlers. A session that starts after the next start of the dashboard loads the mod.
- **Depends:** MOD.1; T1.49 and T1.51 (`HookPlugin`, `PluginInstaller`, `StartupHookInstall`). R1 and R2.
- **Realizes:** Impl §8.1, §9.2, §9.4; this guide, "The mod" and "Shipping it". Director's rulings:
  - **`register.ts` is embedded from `mods/usage/hooks/register.ts`,** as a linked `EmbeddedResource` of `ClaudeDashboard.App.csproj`. It is read one time, with its line ends made LF as `MarketplaceText` does. The repository holds no second copy of the text.
  - **`listening-file.ts` is generated:** ` export const listeningFile =  `, then `paths.ListeningFile` as a JSON string, then a line end. `JsonSerializer.Serialize` makes the string. A JSON string is a correct TypeScript string, with each backslash doubled and each character outside ASCII as an escape. The lab made such a file for a path with a space, an apostrophe and an accented letter, and the mod read `listening.txt` at that path **\[measured\]**.
  - **`HooksText` gains `"modules": ["./register.ts"]`** between `description` and `hooks`. The eight handlers do not change.
  - **`HookPlugin.Files` gives five files, with the two module files before `hooks.json`.** A `hooks.json` that names the module is then never on disk before the module is.
  - **Each write stays in `HookPlugin.cs`,** so `Exactly_the_known_files_hold_a_call_that_writes` passes as it is.
  - **The start does not change.** `StartupHookInstall.Run` already calls `EnsureFiles` for a registered plugin. The version in `ManifestText` stays `1.0.0`.
- **Deliverables:** `HookPlugin` and the project item; `HookPluginTests`; the documents: Impl §8.1 (the `plugin\` row has five files), §9.2 (the `modules` key), §9.4, and a new §9.5 "The usage mod" (what it sends and when, the timer rule with its measurement, what turns it off); TS §II.1 (a second transport, and why it does not bring back the three faults of the HTTP hook) and Appendix A (the mods API, early access); the event-flow document §2.1 and §15; a note in the hooks reference that `session.measure` is a mod's event and not one of the 33 hook events; the README, which says that the plugin now holds a mod; one row in each change history.
- **Acceptance:**
  - `HookPluginTests`: `Writing_puts_five_files_in_the_data_folder_and_a_second_write_changes_nothing` (today it says three); `The_hooks_file_names_the_module_beside_the_eight_handlers`; `The_module_file_is_the_repositorys_register_ts_byte_for_byte`; `A_module_file_that_was_edited_is_put_back`; `The_listening_file_module_names_listening_txt_absolutely`, with a data folder whose path has a space, an apostrophe and a letter outside ASCII: the text after `=` parses as JSON to the same path.
  - `The_hooks_file_carries_one_handler_on_every_accepted_event` still passes, with no change to what it asserts of the handlers.
  - **Plants:** (a) `modules` left out of `HooksText`; (b) `hooks.json` written before the module; (c) the path written with no JSON escape. Each fails a named test.
  - The coder starts the built app one time and reports the five files on disk. The coder also reports what `claude plugin validate` says of that folder: the `hooks:` and `calls:` lines of MOD.1, and the name error of R1 and no other.
  - `build.ps1` GREEN for Debug and Release, with the new `Total`.
- **Guardrails:** `post-status.cmd` and the eight handlers do not change. No write outside the data folder. A start asks nothing of `claude` that it did not ask before. The token is in no file but `listening.txt`. No change to what `HookCheck` reads.

**MOD.3 — The rule for the plan's limits, in Core**

- **Goal:** Core holds what the dashboard believes about the limits: the newest reading of each kind, by the table "What the dashboard keeps".
- **Depends:** none. R6.
- **Realizes:** this guide, "The server endpoint". Director's rulings:
  - **The appendix's `UsageReadings.cs` is the start.** It builds in Core as it is. The coder can change its form, not its rule.
  - **The rule takes its instant as an argument.** No clock is inside it.
  - **Each bound is a constant with a test:** 8 kinds, and 64 characters for a kind.
- **Deliverables:** `src/ClaudeDashboard.Core/UsageReadings.cs`; `tests/ClaudeDashboard.Tests/Domain/UsageReadingsTests.cs`; a row in the Core-and-App document for the rule.
- **Acceptance:**
  - Tests with the names of the kit's checks: `The_newest_reading_of_a_kind_replaces_the_one_held`; `A_newer_reading_that_shows_less_still_replaces_the_one_held`; `A_reading_of_an_older_window_changes_nothing`; `A_reading_of_a_newer_window_replaces_the_one_held`; `A_kind_that_a_post_does_not_carry_is_left_alone`; `A_limit_whose_reset_time_has_passed_is_left_out`; `A_limit_with_no_reset_time_stays`; `A_kind_this_build_does_not_know_is_kept`; `A_ninth_kind_is_not_held_and_a_held_kind_still_moves`; `A_reading_that_cannot_be_true_changes_nothing`; `The_readings_are_in_the_order_of_their_kinds`; `Applying_a_reading_changes_no_value_that_was_read_before`.
  - **Plants:** (a) a reading of an older window is applied; (b) a limit past its reset time is kept; (c) no bound on the kinds; (d) a percentage that is not a number is kept; (e) a newer reading does not replace a higher one. The kit's checks caught each.
  - `DependencyRuleTests` pass. `build.ps1` GREEN for Debug and Release, with the new `Total`.
- **Guardrails:** no ASP.NET and no JSON in Core. No threshold, no notice and no sound: a reading is information.

**MOD.4 — `POST /usage`**

- **Goal:** ingress accepts the mod's post and keeps its readings.
- **Depends:** MOD.3; T1.8 (`IngressEndpoints`), T1.48 (the token), T1.61 (`HookHealth`). R3 and R5.
- **Realizes:** Impl §3.2, §3.3, §3.4; this guide, "The server endpoint". Director's rulings:
  - **`UsageReader` and `UsageBoard` start from the appendix,** in `Ingress`. `UsageBoard` is one singleton in `AppHost`.
  - **The map has the cast:** `app.MapPost("/usage", (Delegate)HandleUsage)`.
  - **A `401` goes to `HookHealth.Refused`** (R3). **An accepted post does not go to `HookHealth.Heard`** (R5).
  - **The log:** one Warning for a refused post, as `/hook` has. One Debug line for an accepted post, with the kinds and the percentages. Never the token.
  - **Nothing enters the channel.** The handler calls no `IEventSink`, no mapper, and nothing of the Registry or the archive.
- **Deliverables:** the two files; the handler and the map in `IngressEndpoints`; the registration in `AppHost`; `Ingress/UsageEndpointTests.cs`, `Ingress/UsageReaderTests.cs` and `Ingress/UsageBoardTests.cs`; Impl §3.2 (the row and its rules), §3.3 and Part 4 (a usage post does not enter the channel); the event-flow document §10; one row in the change history.
- **Acceptance:**
  - On a real Kestrel, as `IngressEndpointTests` runs: `A_post_with_no_token_is_rejected`; `A_post_with_the_wrong_token_is_rejected`; `A_real_body_answers_200_empty_and_reaches_the_board`, with the body of "What you get"; `A_malformed_body_still_answers_200_empty_and_moves_no_reading`; `A_post_with_no_limits_in_it_is_still_heard`; `Two_hundred_posts_at_once_all_answer_200_and_leave_one_reading_of_each_kind`.
  - `A_usage_post_reaches_no_sink`: the `RecordingEventSink` stays empty. `A_usage_post_does_not_move_last_heard`. `A_refused_usage_post_is_counted_as_a_refusal`.
  - The reader: `The_real_body_reads_as_its_two_limits`; `A_body_of_the_wrong_shape_reads_as_nothing`; `An_entry_of_the_wrong_shape_is_passed_over_and_the_rest_are_read`; `A_reset_time_that_is_not_a_time_reads_as_none`; `A_session_id_that_is_not_a_short_string_reads_as_none`; `Only_the_first_sixteen_entries_are_read`.
  - **Plants:** (a) the token not checked; (b) the catch-all out, with a reader that throws; (c) an answer with a body; (d) a kind that is a number is bound; (e) a post with no reading is not counted as heard; (f) the cast out: the build must stop.
  - `build.ps1` GREEN for Debug and Release, with the new `Total`.
- **Guardrails:** `/hook`, `/show` and `/health` do not change. No decision field, ever. No row in the history but the `HookRefused` row of R3. The token is in no log line.

**MOD.5 — `/state` gains `usage`**

- **Goal:** `GET /state` answers the `usage` object of this guide.
- **Depends:** MOD.4; T1.46 (`/state`), T1.61 (`health`, read at the request).
- **Realizes:** Impl §3.5; TS §IV.9. Director's rulings:
  - **`StateReport` gains `UsageEntry? Usage = null`.** `HandleState` sets it at the request, from `UsageBoard.Report` and the clock, as it sets `Health`.
  - **`usage` is null before the first post.** A limit past its reset time is not in it.
  - **No other member moves.**
- **Deliverables:** `StateReport`; `IngressEndpoints.HandleState`; `StateEndpointTests`; Impl §3.5 (the row, and two lines for "What a caller must know": the figure can be behind, and it is of one account); TS §IV.9; one row in each change history.
- **Acceptance:**
  - `State_before_any_post_says_usage_is_null`; `State_carries_the_readings_in_camel_case_with_instants_in_UTC`; `State_leaves_out_a_limit_whose_reset_time_has_passed`, with the fake clock moved past the reset time.
  - `A_state_request_changes_nothing` still passes. A test holds the names of the report's other members as they are today.
  - **Plants:** (a) `Report` gives a limit past its reset time; (b) `Usage` is set when the consumer publishes and not at the request, so a post after the last publication is not in the answer.
  - `build.ps1` GREEN for Debug and Release, with the new `Total`.
- **Guardrails:** read-only. No prompt and no answer in the report. `StateBoard` does not change.

**MOD.6 — The operator's gate on Windows**

- **Goal:** the operator sees, on a Windows machine with an installed build, what this guide measured on Linux. Executed by the operator, as PKG.4 is.
- **Depends:** MOD.1 to MOD.5.
- **Record** the date, the Windows build and the Claude Code version. They go into Impl §9.5 in the form `Measured on <date> against Claude Code <version>`.

* [ ] After one start of the new build, `%LOCALAPPDATA%\ClaudeDashboard\plugin\hooks\` holds `hooks.json`, `register.ts` and `listening-file.ts`, and `listening-file.ts` names the real `listening.txt`.
* [ ] A new interactive session and one prompt: `/state` shows `usage` with `five_hour` and `seven_day`.
* [ ] The two percentages agree with the Usage page of claude.ai.
* [ ] Three prompts in one session move `usage.lastHeardAt` three times.
* [ ] The eight events still arrive: the session's row goes to Working and then to Unread.
* [ ] With the dashboard closed, a turn prints nothing and is no slower.
* [ ] With the dashboard closed and opened again, an open session's next turn gives a reading.
* [ ] `claude -p` ends in its usual time with the dashboard open, and with it closed.
* [ ] One session's debug log has `hooks module claude-dashboard@claude-dashboard loaded`.
* [ ] A session that starts with `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` in its environment still gives a reading.
* [ ] If the operator uses the Desktop app or the VS Code extension: a turn there gives a reading.
