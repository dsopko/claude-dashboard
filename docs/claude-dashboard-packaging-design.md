# Claude Dashboard — Packaging Design (Install Path, Step 1)

**Status:** Proposed 2026-09-01. Design authority for the packaging workstream; the companion [Packaging Execution Plan](claude-dashboard-packaging-execution-plan.md) derives its tasks from the decisions here.

## Purpose

Step 1 of the six-step install path: make the build produce a distributable installer, locally. One command turns a clean checkout into a `Setup.exe` and a portable `.zip` in a local folder, and that Setup installs onto a clean Windows machine — per-user, into LocalAppData, with zero elevation prompts.

**Out of scope:** in-app hook wiring (Step 2 — the start-time check-and-install change is already in flight on its own track), GitHub releases and update-feed wiring (Step 3), Scoop (Step 4), announcement (Step 5), winget and code signing (Step 6).

## Decisions

### D1. Velopack is the packager

Velopack takes the `dotnet publish` output directory and produces, in one command, everything the install path will ever need: a per-user `Setup.exe` that installs without UAC, a portable zip, and the update packages (full + delta) that Step 3 will later serve from GitHub Releases. It also gives the app an update client (`UpdateManager`) for free when we want it.

**This supersedes half of the 2026-08-22 packaging decision.** That decision called for a bespoke "small first-run setup" that (a) registers the logon scheduled task and (b) merges the hook config. Velopack replaces the bespoke installer; the two first-run duties relocate to where they now belong:

- *Hook merge* → in the app itself since T1.32 ([issue #39](https://github.com/dsopko/claude-dashboard/issues/39)): a start whose handler is missing installs it, unless the operator opted out. T1.33 adds the guard this workstream needs — **no `~/.claude` directory means no Claude Code, and the app installs nothing** rather than creating that directory on a machine that never had it. The installer knows nothing about hooks.
- *Logon scheduled task* → an app-owned concern (a settings toggle, Step 2 territory), enabled by the stable executable path in D3.

The installer stays dumb; the app owns its own configuration.

### D2. Publish shape: self-contained, `win-x64`, **not** single-file

**This supersedes the "single-file" half of the 2026-08-22 decision.** Velopack packages and updates a *directory of files* — its delta updates diff at the file level, and a single-file bundle collapses every release into one opaque blob. Single-file publish is not a supported shape for it, and nothing of value is lost: the exe's neighbors live inside Velopack's managed install directory, which no user browses.

Self-contained stays, as previously decided: it deletes the entire ".NET Desktop Runtime not found" support category at a cost of roughly 80–100 MB, a trade a developer tool should take. Trimming and AOT remain non-options for WPF regardless.

### D3. `packId` = `dsopko.ClaudeDashboard`, `packTitle` = "Claude Dashboard"

Velopack installs per-user under `%LocalAppData%\<packId>\`, with a stable `current\` directory it rewrites on update and deletes on uninstall. That directory is *Velopack's* — nothing of ours may live there.

A naïve `packId` of `ClaudeDashboard` would claim `%LOCALAPPDATA%\ClaudeDashboard` — which is already the app's **data root** (settings JSON, the SQLite event log, Serilog files, and the port file the command hooks read). Updates would churn around the data; uninstall would delete it; the hook rediscovery file would sit inside a directory the packager owns.

Rather than migrate the data root (a path change that would have to be coordinated with the in-flight hook rewrite), the packId takes the qualified form — which is also Velopack's own recommended convention (`<Company>.<App>`) and matches the eventual winget identifier. Consequences:

| Path | Owner | Contents |
|---|---|---|
| `%LocalAppData%\dsopko.ClaudeDashboard\` | Velopack | `current\` (binaries), `Update.exe`, packages |
| `%LocalAppData%\ClaudeDashboard\` | the app | settings, SQLite, logs, port file — **unchanged** |

No collision, no data migration, no coordination burden on the hook workstream. Uninstall removes the binaries and leaves the user's data in place (deliberate; a "remove my data too" affordance can join the Step 2 hook-removal toggle later). Display surfaces — Start Menu, the Apps list — show the `packTitle`, so no user ever sees the dotted id. And the scheduled task of the future gets a path that survives every update: `%LocalAppData%\dsopko.ClaudeDashboard\current\ClaudeDashboard.App.exe`.

### D4. One version number, supplied at invocation

The package script takes a single `-Version` parameter and feeds it to both `dotnet publish` (`-p:Version=`) and `vpk pack` (`--packVersion`). Nothing in the repo hardcodes a release number; the tag applied in Step 3 will be the same value. Velopack requires full semver (`0.1.0`, not `0.1`).

### D5. `vpk` is pinned as a repo-local dotnet tool

`vpk` versions should track the Velopack NuGet package version referenced by the app. A `.config/dotnet-tools.json` manifest pins it in-repo, so `dotnet tool restore` on any machine — or any agent — yields the matching tool, instead of whatever a global install happened to fetch.

### D6. Unsigned, deliberately

Signing is Step 6 (Azure Trusted Signing, once release cadence settles). Consequence to expect during testing: SmartScreen interposes on the downloaded/copied `Setup.exe`. The test protocol names this so it is recorded as *expected*, not filed as a defect.

### D7. Test vehicles: Windows Sandbox for the loop, a Hyper-V VM for the gate

Windows Sandbox gives a disposable clean machine in seconds — the iteration loop. But the Sandbox account is an administrator inside the sandbox, so it cannot *prove* the no-elevation claim; a per-user install won't prompt there no matter what. The acceptance gate therefore runs once on a Hyper-V VM **as a standard (non-admin) user** — the only configuration that demonstrates "no admin rights demanded" is true.

## Interfaces to later steps

- **Update artifacts.** `vpk pack` emits the full package and the release manifest alongside the Setup. Step 3 uploads these with the release — they are what makes `v0.1.1` a delta rather than a re-download — and future packs will fetch the previous release (`vpk download github`) before packing so deltas can be produced. Nothing to build now; the script just doesn't discard them.
- **Lifecycle callbacks.** Velopack exposes hooks such as `OnFirstRun` and uninstall-time callbacks — the natural seam for Step 2's "remove hooks on uninstall" behavior. Noted, not wired.
- **Application icon.** Already delivered: `Assetspp.ico` is compiled into the executable through `ApplicationIcon` (T1.27, [issue #17](https://github.com/dsopko/claude-dashboard/issues/17)). The pack step passes the same file to `--icon` so the Setup and the Start Menu shortcut carry it. Nothing here waits on artwork.
- **arm64.** Out of scope; a second RID is a one-line extension of the script when it matters.

## Step-level acceptance

1. From a clean checkout, one command (`dotnet tool restore` + `build\package.ps1 -Version 0.1.0`) produces a local `artifacts\releases\` containing a Setup executable, a portable zip, and the update package.
2. On a clean Windows VM, logged in as a **standard user**: the Setup installs with zero elevation prompts; the app launches and its tray icon appears; binaries land under `%LocalAppData%\dsopko.ClaudeDashboard\`; the data root `%LocalAppData%\ClaudeDashboard\` is created by the app on first run; Start Menu and installed-apps entries exist; uninstalling removes the install root and leaves the data root untouched.
3. The portable zip, extracted to an arbitrary folder, runs without installing anything.
