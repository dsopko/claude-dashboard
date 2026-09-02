# Claude Dashboard — Packaging Execution Plan (Install Path, Step 1)

**Status:** Proposed 2026-09-01. Executes the decisions in the [Packaging Design](claude-dashboard-packaging-design.md); where this document and the design disagree, the design wins and the conflict is escalated.

**Workflow:** the director/coder/reviewer roles, handoff contract, dispatch rules (including `notify_when_idle` on every dispatch and the standing watchdog), and launch runbook are defined in the main [Execution Plan](claude-dashboard-execution-plan.md), Appendices B and C. They apply here unchanged and are referenced, not re-quoted. Task IDs below use the `PKG.` prefix so Status Reports and Review Requests are unambiguous alongside `T1.x` work.

**Dependency order:** PKG.1 → PKG.2 → PKG.3 → T1.33 → PKG.4. PKG.4 is executed by the human. PKG.5 was folded into PKG.3 on 2026-09-02, because the icon it waited for already exists. T1.33 is a main-plan task (Milestone 1E) that PKG.4's gate depends on: without it the app creates `~/.claude` on a machine that never had Claude Code.

**Ruled 2026-09-02:** this workstream runs ahead of T2.1 in the main plan's order. Recorded there under Milestone 1E.

---

## PKG.1 — Velopack runtime integration

**Depends:** none.

**Work:**

1. Add the `Velopack` NuGet package to `ClaudeDashboard.App` (note the version; PKG.3 pins the `vpk` tool to match).
2. The entry point is already explicit — `Program.Main`, `[STAThread]`, with `App.xaml` compiled as a `Page` and **no `<StartupObject>`, deliberately** (the csproj records why: it does not prevent the CS0017 collision, removing the `ApplicationDefinition` does; naming one would imply otherwise). Do not add one. The work is one statement: `VelopackApp.Build().Run()` as the **first statement of `Program.Main`**, ahead of `HookSwitches.Requested(args)` and ahead of `SingleInstanceGate.Acquire`. Record in `Program`'s remarks why it is first.
3. **Ordering constraint:** `VelopackApp.Build().Run()` must execute before *everything* — in particular before the hook switches, before the single-instance gate (`SingleInstanceGate.Acquire`, corroborated by the port), and before Generic Host construction. During install, update, and uninstall, Velopack launches the executable with lifecycle arguments and expects `Run()` to handle them and exit; if the single-instance guard runs first, it can shoot down those invocations and corrupt install/update behavior.
4. No other startup behavior changes. Exception handlers, Serilog, tray construction, ingress — all untouched.
5. **Verify .NET 10 at restore, do not assume it.** Report the `Velopack` package version that restored against `net10.0-windows` and whether its own target frameworks include it or it is running on an older target under roll-forward. If the current release does not restore or does not run on .NET 10, stop and report — that is a blocker for the director, not something to work around.

**Acceptance:**

- Solution builds; the app runs from the IDE and via `dotnet run` with behavior identical to before the change.
- Code review confirms `VelopackApp.Build().Run()` is the first statement in `Program.Main`, ahead of the hook switches and the gate — and a source-text guard in `StartupHookGuardTests`' style pins it there, so a later edit that moves the gate above it fails a test rather than an install.
- The `--install-hooks` and `--remove-hooks` switches still work after the change (they run in `Main` before the gate, and `Run()` must not swallow them).
- The architecture tests still pass (the Velopack reference lives in `ClaudeDashboard.App` only; `Core` remains free of it).

---

## PKG.2 — Publish step

**Depends:** PKG.1.

**Work:**

1. Create `build\package.ps1` with a mandatory `-Version` parameter (full semver). Part one of the script publishes the app:
   `dotnet publish src\ClaudeDashboard.App -c Release -r win-x64 --self-contained -p:Version=$Version -o artifacts\publish`
2. Explicitly do **not** set `PublishSingleFile` or any trimming property, in the script or the csproj (Design D2). If either is present in the csproj today, remove it.
3. Add `artifacts/` to `.gitignore`.
4. **Log the version once at start.** Nothing in the app names its version today. One Information line, first thing after the logger exists, carrying the informational version (`Version+sha`) — the line PKG.4 and every later support question reads.
5. **Amend the two authoritative documents this contradicts, in the same commit** (ruled 2026-09-02): Impl **§1** and **§10.2** say single-file; the main plan's **T1.19** block is titled "self-contained single-file". Correct them to the directory shape with the reason (Design D2: Velopack diffs at the file level), and record the old reason beside the change rather than deleting it — `DashboardSettings.DefaultPort`'s remark is the pattern. The csproj comment saying "one file" goes the same way.

**Acceptance:**

- `build\package.ps1 -Version 0.1.0` (part one) produces `artifacts\publish\` containing `ClaudeDashboard.App.exe` plus its file set — many files, not one.
- The published exe launches from that folder on the dev machine, the startup log's first line names the passed version, and the File Properties details tab agrees.
- Impl §1, Impl §10.2 and T1.19 no longer say single-file, and each says why it changed.

---

## PKG.3 — Pack step

**Depends:** PKG.2.

**Work:**

1. Pin `vpk` as a repo-local dotnet tool: `.config/dotnet-tools.json` with the `vpk` version matching the `Velopack` package from PKG.1 (Design D5). Document `dotnet tool restore` as the one-time setup in the script header.
2. Part two of `build\package.ps1` packs the publish output:
   `vpk pack --packId dsopko.ClaudeDashboard --packVersion $Version --packDir artifacts\publish --mainExe ClaudeDashboard.App.exe --packTitle "Claude Dashboard" --outputDir artifacts\releases`
3. The script does not delete or filter the pack output: Setup, portable zip, update package, and release manifest all remain in `artifacts\releases` (Step 3 uploads them together).
4. Pass `--icon src\ClaudeDashboard.App\Assets\app.ico` to `vpk pack`. The file exists and `ApplicationIcon` is already set (T1.27); this is the whole of what was PKG.5.
5. **Rewrite T1.19's guardrail** in the main plan — "republish and repoint the logon task" — for the new shape: republish now means `build\package.ps1` and installing the resulting Setup; there is no logon task to repoint until Step 2 wires one. Correct, do not delete.

**Acceptance:**

- From a clean checkout: `dotnet tool restore` then `build\package.ps1 -Version 0.1.0` completes without errors or interactive prompts.
- `artifacts\releases\` contains a Setup executable, a portable zip, and the update package/manifest files.
- Running the script twice in a row succeeds (idempotent over its own output).
- The Setup and the Start Menu shortcut carry the application icon.

---

## PKG.4 — Clean-machine verification *(human-executed)*

**Depends:** PKG.3 and T1.33. Executed by David; the director treats it as a phase gate and dispatches nothing past it until the checklist passes. Findings are filed in the standard report format and become fix tasks routed back through PKG.1–PKG.3.

**Loop (Windows Sandbox, per iteration):** copy `Setup.exe` in, run it, confirm: install completes, tray icon appears, no error dialogs. Fast, disposable; not the elevation proof (Design D7).

**Gate (Hyper-V VM, standard non-admin user, no .NET runtime installed):**

1. Run `Setup.exe`. SmartScreen interposition is expected (Design D6) — click through; record, don't file.
2. Zero UAC/elevation prompts at any point.
3. App launches; tray icon appears.
4. Binaries under `%LocalAppData%\dsopko.ClaudeDashboard\` (`current\`, `Update.exe`); no files written to Program Files or other machine locations.
5. Data root `%LocalAppData%\ClaudeDashboard\` created by the app on first run (settings/logs appear).
6. Start Menu entry and installed-apps (Settings → Apps) entry present, titled "Claude Dashboard".
7. With no Claude Code on the machine: the app runs without crashing, **no `~/.claude` directory appears**, and the log says the install was refused because Claude Code is not installed (T1.33). *(Observation item — the behaviour is T1.32/T1.33's; file findings against them, not this plan.)*
8. Quit and relaunch from the Start Menu shortcut: normal start.
9. Uninstall from Settings → Apps: install root removed; data root untouched; no elevation prompt.
10. Portable zip: extract to Desktop, run the exe — app runs; no Start Menu entry or installed-apps entry appears.

**The first loop iteration is the operator's own machine** (ruled 2026-09-02): install the Setup, confirm the tray comes up on the Velopack path with the existing data root and hooks untouched, then delete the legacy `%LocalAppData%\ClaudeDashboardApp\` folder and its `.staging` twin. The old publish-and-swap routine retires with them.

**Acceptance:** every gate item passes on the VM. This is also the step-level acceptance of the design — passing PKG.4 closes Step 1.

---

## PKG.5 — Application icon wiring *(folded into PKG.3, 2026-09-02)*

The icon this task waited on already exists: `Assets\app.ico` was delivered by T1.27 ([issue #17](https://github.com/dsopko/claude-dashboard/issues/17)) and `ApplicationIcon` is set in the csproj. What remained — `--icon` on `vpk pack` — is PKG.3 step 4. Kept as a heading so the numbering above stays true.
