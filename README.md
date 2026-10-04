# Claude Dashboard

No more babysitting terminals. Claude Dashboard gives you sound and visual notifications when a Claude Code agent finishes its turn, so you can stay heads-down on other work without letting critical tasks stall.

A Windows tray application for developers running many concurrent Claude Code sessions. It answers, at a glance, the three questions a wall of terminals can't: **what needs me right now**, **what finished that I haven't seen**, and **what's still working**.

Sessions don't always work alone. Claude Dashboard lets you group multi-agent orchestrations working together into a single status, so you get one notification when the whole team finishes instead of one at every handoff.

![The Claude Dashboard panel, grouped by working directory](docs/claude-dashboard-screenshot.png)

**Status:** Phase 1 is in pre-release (0.0.x). The tray app runs, receives Claude Code hooks, and shows the panel above. Windows integration — click-to-navigate, focus acknowledgment, virtual-desktop grouping — begins at Phase 2 and is not built.

## What it does

- A single resident tray app whose icon is an overall **status light** (red permission · amber error or question · green unread · blue working · grey quiet), rolled up from every session.
- A panel that sorts sessions into **attention bands** — needs-you (permissions, then errors, then questions; the oldest first in each), unread (newest first), working, quiet — grouped by working directory, with each row showing the prompt *and*, when finished, the answer, so most checks resolve without switching to the terminal.
- **Sound that carries meaning.** Four distinct notices — permission, question, error, finished — so a beep tells you what happened before you look at anything. A nudge re-raises something still waiting. All of it can be silenced, until you say or for the next thirty minutes.
- **Group related sessions, and the group chimes as one.** Sessions working one job — agents passing messages back and forth, or parallel runs across repositories — go quiet as individuals: no chime per handoff, none per member to count. You hear a single notice when the last one finishes, however many there are and whatever order they land in.
- Its world is **event-sourced from Claude Code hooks** — it never polls, and it never blocks a Claude turn (hooks are pure observers).
- Later phases add click-to-navigate to the right terminal tab, focus-based acknowledgment, virtual-desktop grouping, searchable history, and a phone view.

## Install

**Pre-release.** Every 0.0.x version is an early build: it works on the developer's machine and has not yet been tried on a clean one. Expect rough edges, and please [open an issue](https://github.com/dsopko/claude-dashboard/issues) when you hit one.

**You need:** Windows 10 or 11, 64-bit, and [Claude Code](https://docs.anthropic.com/en/docs/claude-code) installed for the user who will run the dashboard. Nothing else — the .NET runtime is included.

1. Download `dsopko.ClaudeDashboard-win-Setup.exe` from the [releases page](https://github.com/dsopko/claude-dashboard/releases).
2. Run it. **Windows will show "Windows protected your PC"** because the build is not yet code-signed — click **More info**, then **Run anyway**. It installs for your user only, under `%LocalAppData%`, and never asks for administrator rights.
3. Start **Claude Dashboard** from the Start Menu. It runs in the tray; the panel opens from the tray icon.

That is all. On its first start the dashboard registers one small plugin with Claude Code so it can hear your sessions, and from then on every new Claude Code session reports to it. A session that was already open reports after you restart it.

**What it touches.** A Claude Code plugin named `claude-dashboard`, and nothing else of Claude Code's. The dashboard keeps the plugin's files in `%LocalAppData%\ClaudeDashboard\plugin` and asks Claude Code to register it, with `claude plugin marketplace add` and `claude plugin install`. The plugin holds one command hook, which runs `post-status.cmd` from `%LocalAppData%\ClaudeDashboard`. The hook is a pure observer: it can never block or change a Claude turn, and it does nothing at all when the dashboard is not running. Everything the dashboard records — session state, prompts, answers — stays in a SQLite file in that same folder. It listens on the loopback interface only and sends nothing anywhere.

**If the dashboard is not connected to Claude Code, it says so.** A notice appears in its window and in its tray tooltip, with what to do. It appears when:

- **No Claude Code install was detected.** Install Claude Code, then restart the dashboard.
- **The `claude` program was not found**, or Claude Code did not accept the plugin. The notice gives two commands to run yourself.
- **You turned the plugin off** (for example with `claude plugin disable claude-dashboard@claude-dashboard`). The dashboard leaves it off. To turn it back on, run `claude plugin enable claude-dashboard@claude-dashboard`.
- **You removed the plugin** with `--remove-hooks`. Run `ClaudeDashboard.App.exe --install-hooks` from the install folder to put it back.

After any of these, restart each Claude Code session that is open: an open session does not see a change to its plugins. The notice goes away by itself when a session reports.

**If you used a version before 0.0.18,** Claude Code may still hold an old hook from it. The dashboard tells you when it finds one, and waits for you to remove it before it registers the plugin. The easy way is to ask Claude: "remove all hooks for Claude Dashboard from my settings." You can also remove them with the `/hooks` command. Then restart the dashboard.

**It starts when Windows starts.** An installed dashboard adds itself to the programs Windows starts when you sign in, so it is there before your first Claude Code session. To stop that, untick *Start Claude Dashboard when Windows starts* in the dashboard's **Settings…** (right-click the tray icon). You can also turn it off in Settings → Apps → Startup or in Task Manager's Startup tab; the dashboard respects that, and its Settings window shows it. If the dashboard crashes, Windows does not restart it; start it again from the Start Menu.

**History follows Claude Code.** The SQLite file keeps prompts, answers and decisions for as many days as Claude Code keeps its own sessions: `cleanupPeriodDays` in Claude Code's settings (`%UserProfile%\.claude\settings.json`), or 30 days when the key is not there. Older records are deleted at each start and once a day, and a change to `cleanupPeriodDays` takes effect at the next cleanup. If Claude Code's settings cannot be read, or the value is not one that Claude Code would use, nothing is deleted. To keep a long history, set `cleanupPeriodDays` to a large number in Claude Code's settings, for example `3650`. The dashboard reads that file and never writes it. **The first start after updating to a version with this rule deletes history older than Claude Code's `cleanupPeriodDays`**, and a dashboard set to keep everything with `"history": { "retentionDays": 0 }` no longer does: that setting is ignored. To keep your history, raise `cleanupPeriodDays` before you update.

**Uninstall.** *Settings → Apps → Claude Dashboard → Uninstall* removes the program and its entry in the programs Windows starts, and leaves your data folder in place. To take the plugin out of Claude Code first, run `ClaudeDashboard.App.exe --remove-hooks` from the install folder. Without that, the plugin stays and is harmless — its hook finds no dashboard and exits.

**Portable.** The release also carries `dsopko.ClaudeDashboard-win-Portable.zip`: extract it anywhere and run `current\ClaudeDashboard.App.exe`. No Start Menu entry, no Apps entry, and it never starts with Windows: a portable copy has no fixed path for Windows to start, so its Settings window says so and keeps the box unticked.

## Quiet scheduled jobs

A scheduled job, such as a watchdog that checks every 30 minutes, can end its turn without the dashboard playing **finished**: add one line to its prompt, and a tick that finds nothing to do replies `WATCHDOG-QUIET` and makes no sound. Anything else it says still beeps, so an escalation is never silenced. See [Quiet scheduled jobs](docs/quiet-scheduled-jobs.md) for the line, a complete example, and how to check it is working.

## Asking the dashboard what it believes

`GET http://127.0.0.1:<port>/state` answers with what the dashboard believes now: every session with its state, band, title, waiting tasks and next nudge time, then the band counts and the tray light. It is for tests and diagnosis. It never carries a prompt or an answer. The [Implementation Specification](docs/claude-dashboard-impl-spec.md) §3.5 describes every field.

It needs the dashboard's token, in an `X-Dashboard-Token` header; without it, or with a wrong one, `/state` answers `401`. **You set nothing.** The dashboard makes a new token every time it starts and writes it, with the port, to `%LocalAppData%\ClaudeDashboard\listening.txt`: the port on the first line, the token on the second. The file exists only while a dashboard runs. The hook reads the same file at every event, so a Claude Code session keeps reporting through any number of dashboard restarts, and no session or terminal ever needs restarting for the token. Read the token again after each start. The old `CLAUDE_DASHBOARD_TOKEN` variable is no longer used; if it is set, the dashboard says so once in its log and ignores it.

`nextNudgeAt` is the schedule, not what you will hear: a muted session still shows a time. When it is `null`, either no nudge is coming or the session is a finished member of a roster group, and the group owns the notice.

```powershell
$port, $token = Get-Content "$env:LOCALAPPDATA\ClaudeDashboard\listening.txt"
Invoke-RestMethod "http://127.0.0.1:$port/state" -Headers @{ 'X-Dashboard-Token' = $token }
```

## Documents

Everything lives in [`docs/`](docs/). Read in this order:

| Document | What it is |
|---|---|
| [Design](docs/claude-dashboard-design.md) | Business-level design — the problem, principles, and product shape. |
| [Technical Specification](docs/claude-dashboard-spec.md) | Technology-agnostic architecture and mechanisms (the *why*). Reference for any future non-Windows port. |
| [Implementation Specification](docs/claude-dashboard-impl-spec.md) | The C# / .NET / WPF realization (the *how*) — projects, libraries, APIs, the Claude Code hook contract. |
| [Event flow](docs/claude-dashboard-event-flow.md) | One hook event, step by step, from Claude Code to the window, the speaker and the database, with the file for each step. |
| [Core and App](docs/claude-dashboard-core-and-app.md) | Which project holds which rule, and what a second interface (web or phone) needs. |
| [Hook events reference](docs/claude-code-hooks-reference.md) | Every Claude Code hook event, and where the documentation and the wire disagree. |
| [Quiet scheduled jobs](docs/quiet-scheduled-jobs.md) | A guide: how a scheduled job that finds nothing stays silent. |
| [Packaging Design](docs/claude-dashboard-packaging-design.md) | How the installer is made, and where the install path stands. |
| [Execution Plan](docs/claude-dashboard-execution-plan.md) | Phased task graph with acceptance criteria, plus agent role prompts (Appendix B). A plan and a record: where it disagrees with the specifications, the specifications say what is true now. |
| [Mockups](docs/claude-dashboard-mockups.html) | UI reference — open in a browser. Visuals only, never ordering. |

What is specified and not built is listed in one place: the Technical Specification, Appendix C.

## Tech stack

C# on **.NET 10 (LTS)**, **WPF**. A portable core and a Windows host:

- `ClaudeDashboard.Core` — domain (registry, state machine, attention order, groups and rosters, sound policy) with no WPF/Win32/ASP.NET.
- `ClaudeDashboard.App` — WPF tray UI, loopback ingress (Kestrel), the event loop, storage, and the Windows adapters behind interfaces.
- `ClaudeDashboard.Remote` — an empty project today. Later, the phone surface.
- `ClaudeDashboard.Tests` — xUnit.

Key libraries: NAudio (sound), H.NotifyIcon (tray), Kestrel minimal API (hook ingress), Microsoft.Data.Sqlite (history), Serilog, Velopack (install). FlaUI (UI Automation) comes with Phase 2. Full list in the Implementation Specification, Appendix A.

## How it's built

Development runs as a small agent workflow: a **director** drives the execution plan, dispatching one task at a time to a **coder** and routing each completed change to a **reviewer**, with the human as the escalation point. The role prompts are in the Execution Plan, Appendix B.

## Roadmap

| Phase | Theme |
|---|---|
| 1 | See clearly — the event-driven panel, tray, sound, and ack. **No Windows integration; independently useful and testable without a desktop.** |
| 2 | Go there — click a row, jump to its terminal tab. |
| 3 | It notices — looking at a terminal acknowledges it. |
| 4 | Task lens — grouping by virtual desktop. |
| 5 | Memory — searchable history and wait-time stats. |
| 6 | Polish — settings UI, sound editor, themes. |
| 7 | Anywhere — authenticated phone read/ack. |

## Repository layout

```
claude-dashboard/
├── README.md
├── CLAUDE.md                     # always-loaded project context + orchestration pointer
├── ClaudeDashboard.slnx
├── .gitignore
├── .gitattributes
├── .claude/
│   └── skills/
│       └── dashboard-orchestration/
│           └── SKILL.md          # loadable guide to the director/coder/reviewer workflow
├── build/
│   └── package.ps1               # publish and pack: Setup.exe, portable zip, update packages
├── src/
│   ├── ClaudeDashboard.Core/     # the domain: state machine, attention ordering, grouping, sound policy
│   ├── ClaudeDashboard.App/      # the Windows host: WPF panel, tray, ingress, event loop, storage, adapters
│   └── ClaudeDashboard.Remote/   # empty; the phone surface of Phase 7
├── tests/
│   └── ClaudeDashboard.Tests/    # xUnit, including the architecture and dependency rules
└── docs/
    ├── claude-dashboard-design.md
    ├── claude-dashboard-spec.md
    ├── claude-dashboard-impl-spec.md
    ├── claude-dashboard-event-flow.md
    ├── claude-dashboard-core-and-app.md
    ├── claude-code-hooks-reference.md
    ├── quiet-scheduled-jobs.md
    ├── claude-dashboard-packaging-design.md
    ├── claude-dashboard-packaging-execution-plan.md
    ├── claude-dashboard-execution-plan.md
    ├── claude-dashboard-phase1-acceptance.md
    ├── claude-dashboard-mockups.html
    └── claude-dashboard-screenshot.md   # how the README screenshot is retaken
```

`ClaudeDashboard.Core` holds no WPF, Win32 or ASP.NET reference, and nothing references `ClaudeDashboard.App` — a rule the test suite enforces rather than merely states.

## License

[MIT](LICENSE).
