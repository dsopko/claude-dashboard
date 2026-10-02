# Claude Dashboard — Packaging Design (Install Path, Step 1)

**Status:** Proposed 2026-09-01. **Step 1 is built** (PKG.1 to PKG.3, September 2026). Brought up to date 2026-10-02 for what changed after it: the hook is a Claude Code plugin, and the start with Windows is the `Run` key. Design authority for the packaging workstream; the companion [Packaging Execution Plan](claude-dashboard-packaging-execution-plan.md) derives its tasks from the decisions here.

**Where the install path stands at 2026-10-02:**

| Step | Content | State |
|---|---|---|
| 1 | A local installer | Built. `build\package.ps1` |
| 2 | The application connects itself to Claude Code and starts with Windows | Built (T1.32, T1.33, T1.49 to T1.51). The Implementation Specification §9.4 and §10.1 are the authority |
| 3 | GitHub releases and the update feed | Releases are published by hand as pre-releases (0.0.19 on 2026-10-01). **The application does not look for updates: not built** |
| 4 | Scoop | Not built |
| 5 | Announcement | Not done |
| 6 | winget and code signing | Not built. The Setup is not signed (D6) |

The gate of PKG.4 (a clean machine, a standard user) is the operator's, and its record is in the execution plan.

## Purpose

Step 1 of the six-step install path: make the build produce a distributable installer, locally. One command turns a clean checkout into a `Setup.exe` and a portable `.zip` in a local folder, and that Setup installs onto a clean Windows machine — per-user, into LocalAppData, with zero elevation prompts.

**Out of scope for Step 1:** the connection to Claude Code from inside the application (Step 2), GitHub releases and the update feed (Step 3), Scoop (Step 4), announcement (Step 5), winget and code signing (Step 6).

## Decisions

### D1. Velopack is the packager

Velopack takes the `dotnet publish` output directory and produces, in one command, everything the install path will ever need: a per-user `Setup.exe` that installs without UAC, a portable zip, and the update packages (full + delta) that Step 3 will later serve from GitHub Releases. It also gives the app an update client (`UpdateManager`) for free when we want it.

**This supersedes half of the 2026-08-22 packaging decision.** That decision called for a bespoke "small first-run setup" that (a) registers a logon scheduled task and (b) merges the hook configuration into Claude Code's settings. Velopack replaces the bespoke installer. The two first-run duties moved into the application, and both changed form on the way:

- *The connection to Claude Code* → the application registers a Claude Code **plugin** at a start that finds it absent, unless the operator opted out (issues #39 and #30; T1.32, T1.49). **No `~/.claude` directory means no Claude Code, and the application registers nothing and creates nothing** (T1.33). The application never writes Claude Code's settings file (T1.51). The installer knows nothing about hooks.
- *The start with Windows* → the application writes a value under the `Run` key of the current user, at the stable path of D3 (T1.50). There is no scheduled task. The operator can turn it off in the Settings window, in Windows Settings, or in Task Manager.

The installer stays dumb; the application owns its own configuration.

### D2. Publish shape: self-contained, `win-x64`, **not** single-file

**This supersedes the "single-file" half of the 2026-08-22 decision.** Velopack packages and updates a *directory of files* — its delta updates diff at the file level, and a single-file bundle collapses every release into one opaque blob. Single-file publish is not a supported shape for it, and nothing of value is lost: the exe's neighbors live inside Velopack's managed install directory, which no user browses.

Self-contained stays, as previously decided: it deletes the entire ".NET Desktop Runtime not found" support category at a cost of roughly 80–100 MB, a trade a developer tool should take. Trimming and AOT remain non-options for WPF regardless.

### D3. `packId` = `dsopko.ClaudeDashboard`, `packTitle` = "Claude Dashboard"

Velopack installs per-user under `%LocalAppData%\<packId>\`, with a stable `current\` directory it rewrites on update and deletes on uninstall. That directory is *Velopack's* — nothing of ours may live there.

A naïve `packId` of `ClaudeDashboard` would claim `%LOCALAPPDATA%\ClaudeDashboard` — which is already the app's **data root** (the settings file, the SQLite event log, the log files, the hook script, the port files, and the Claude Code plugin). Updates would churn around the data; uninstall would delete it; the files that the hook reads would sit inside a directory the packager owns.

Rather than migrate the data root, the packId takes the qualified form — which is also Velopack's own recommended convention (`<Company>.<App>`) and matches the eventual winget identifier. Consequences:

| Path | Owner | Contents |
|---|---|---|
| `%LocalAppData%\dsopko.ClaudeDashboard\` | Velopack | `current\` (binaries), `Update.exe`, packages |
| `%LocalAppData%\ClaudeDashboard\` | the app | settings, SQLite, logs, the hook script, the port files, the plugin — **unchanged** |

No collision and no data migration. Uninstall removes the binaries and the `Run` value, and leaves the user's data and the Claude Code plugin in place. That is deliberate: the plugin's hook finds no dashboard and does nothing, and `--remove-hooks` takes it out. A "remove my data too" option is *not built*. Display surfaces — Start Menu, the Apps list — show the `packTitle`, so no user ever sees the dotted id. **No desktop shortcut** (D113): the pack passes `--shortcuts StartMenuRoot`, and `--packAuthors` names the person, since the Apps list shows it as Publisher. The `Run` value names a path that survives every update: `%LocalAppData%\dsopko.ClaudeDashboard\current\ClaudeDashboard.App.exe`.

**The plugin must stay in the data root, never in the install root.** Claude Code loads a plugin from its folder in place, and `current\` is replaced at every update.

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
- **Lifecycle callbacks.** Velopack exposes callbacks such as `OnFirstRun` and one that runs at uninstall. The uninstall callback is wired: it removes the `Run` value (T1.50). It does not remove the Claude Code plugin; that is `--remove-hooks`.
- **Application icon.** Already delivered: `Assetspp.ico` is compiled into the executable through `ApplicationIcon` (T1.27, [issue #17](https://github.com/dsopko/claude-dashboard/issues/17)). The pack step passes the same file to `--icon` so the Setup and the Start Menu shortcut carry it. Nothing here waits on artwork.
- **arm64.** Out of scope; a second RID is a one-line extension of the script when it matters.

## Step-level acceptance

1. From a clean checkout, one command (`dotnet tool restore` + `build\package.ps1 -Version 0.1.0`) produces a local `artifacts\releases\` containing a Setup executable, a portable zip, and the update package.
2. On a clean Windows VM, logged in as a **standard user**: the Setup installs with zero elevation prompts; the app launches and its tray icon appears; binaries land under `%LocalAppData%\dsopko.ClaudeDashboard\`; the data root `%LocalAppData%\ClaudeDashboard\` is created by the app on first run; Start Menu and installed-apps entries exist; uninstalling removes the install root and leaves the data root untouched.
3. The portable zip, extracted to an arbitrary folder, runs without installing anything.
