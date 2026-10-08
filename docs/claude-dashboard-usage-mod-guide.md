# Usage Mod Development Guide

Oct 8, 2026 · @David

This guide builds a Claude Code mod that sends your plan usage to Claude Dashboard over HTTP. It sends the 5-hour session percentage and the weekly percentage each time Claude Code measures them. The event is `session.measure`, and it does carry the percent-used numbers. The mod and its 28 tests were run on Claude Code 2.1.294 on Linux on 8 October 2026. The C# was compiled and run in a lab project on .NET 10, also on Linux. Nothing was built in the dashboard's solution, and nothing was run on Windows.

## What you get

`session.measure` is the right event, and it carries the percent-used numbers for the two plan limits. Claude Code calls your hook with them at the end of each turn.

Two tags say where a fact comes from. **\[documented\]** means Claude Code's documentation or its type declarations for version 2.1.294. **\[measured\]** means seen in a lab on 8 October 2026: Claude Code 2.1.294, Linux, `claude -p` runs, model Haiku. A line that names a different version was seen on that version.

**The numbers**

| On the claude.ai Usage page | In the event |
| --- | --- |
| Current session | The `rateLimits` entry with `kind` `five_hour`: `percentUsed` and `resetsAt` |
| This week, all models | The `rateLimits` entry with `kind` `seven_day`: the same two fields |
| This week for one model (Fable in your screenshot) | Not in the event |
| Usage credits | Not in the event |

The declared kinds are `five_hour`, `seven_day` and a Claude gateway's `spend_limit` **\[documented\]**. The lab saw the first two only **\[measured\]**. The event also carries the context window's fill (`context`), the session's cost in US dollars (`cost.usd`) and the list of parts that changed since the last call (`changed`).

**When Claude Code calls the hook**

| When | How we know |
| --- | --- |
| At the end of each turn of the main conversation | **\[documented\]** and **\[measured\]**: three prompts in one session gave three calls, each right after its turn ended. The turn with a tool call gave one call, not two |
| One more time in a session's first turn, if that turn has a tool call | **\[measured\]**: the first reading arrives after the first model reply, so that turn gave two calls |
| When a limit moves a whole point in the middle of a turn | **\[documented\]**. Not seen in the lab |

Claude Code makes no call when nothing changed since the last one **\[documented\]**.

**One real body**

This is the first body the mod sent in the lab, as the receiver got it **\[measured\]**. The mod adds `sessionId`. The rest is the event, unchanged.

```json
{
  "sessionId": "ab86443d-84b5-4342-85b4-a13111a93020",
  "context": { "tokens": 3575, "window": 1000000, "percent": 0 },
  "rateLimits": [
    { "kind": "five_hour", "percentUsed": 24, "resetsAt": "2026-10-08T23:10:00.000Z" },
    { "kind": "seven_day", "percentUsed": 13, "resetsAt": "2026-10-14T13:00:00.000Z" }
  ],
  "cost": { "usd": 0.0006151500000000001 },
  "changed": ["context", "rateLimits", "cost"]
}
```

**What a receiver must allow for**

- `percentUsed` is 0 to 100 with at most one decimal. It can pass 100 on a gateway's spend limit **\[documented\]**.
- `resetsAt` is an ISO 8601 time, and it can be absent **\[documented\]**.
- `rateLimits` is empty for a session that is not on a subscription, and before the first reading **\[documented\]**.
- `cost.usd` is a raw floating-point number, as in the body above **\[measured\]**.
- `kind` can be a value that your code does not know **\[documented\]**.

## How it works

Claude Code calls a function of the plugin at the end of each turn and hands it the figures. The function sends them to the dashboard. Nothing polls, and no settings file changes.

&#91;embedded content: The path of a usage figure · two processes, seven parts\]

Read the left column from the top: a model reply brings the figures, Claude Code calls the mod's hook, and the hook's timer starts the post. The right column is the dashboard: it writes the two files that the mod needs, and it receives the post.

**Where the numbers come from**

Anthropic's servers send the plan's usage with the model's replies, and Claude Code keeps the latest figures in memory **\[documented\]**. The mod asks nobody for them. No extra request goes to Anthropic.

**What a mod is**

A mod is a plugin whose `hooks.json` names a JavaScript or TypeScript file. When the plugin loads, Claude Code calls the file's `register` function, and `register` names the events that it wants. From then on Claude Code calls your function when one of those events happens, inside its own process **\[documented\]**. It is a callback.

The function gets three things: `$`, the calls that it can make; `e`, the event's data; and `next`, which hands the event on. A mod can reach a file or the network only through `$` **\[documented\]**.

**How this differs from the eight hooks that the plugin has today**

|  | The eight hooks today | The usage mod |
| --- | --- | --- |
| What Claude Code runs | A command, `cmd.exe /c post-status.cmd`: a new process for each event | A function inside Claude Code's own process |
| The events | Settings hook events, such as `Stop` and `Notification` | Mod events. `session.measure` is one of them |
| How the data leaves | The script reads `listening.txt` and posts with `curl.exe` | The function reads `listening.txt` and posts with `$.http.fetch` |
| Where it is named | The plugin's `hooks.json`, under `hooks` | The same file, under `modules` |

No settings hook event carries the usage figures. The type declarations for 2.1.294 give the input of each of the 33 settings hook events, and none has a usage field **\[documented\]**. The repository's hooks reference agrees. In the lab the input of the `Stop` hook had eleven fields, and none was a usage figure **\[measured\]**.

**Why not the status line**

Claude Code also gives these figures to a status line script. But one settings file holds one status line, a plugin cannot carry one, and the dashboard never writes Claude Code's settings (Impl §9.3). The first two facts are **\[documented\]**. A mod needs no setting.

**Why the three faults of the HTTP hook do not come back**

The first builds used Claude Code's HTTP hook. It was removed for three reasons (TS §II.1). None applies to the mod:

| The fault of the HTTP hook | The mod |
| --- | --- |
| The URL held the port, and the port must be free to move | The mod reads the port from `listening.txt` at each call |
| The URL had to be in an allow-list in Claude Code's settings | No setting was needed. A new configuration folder posted to the loopback address **\[measured\]** |
| With the dashboard closed, each event showed an error in the session | With no `listening.txt`, and with a port that nothing held, nothing was shown **\[measured\]** |

One thing can still refuse the post: an organization that turns off web fetching **\[documented\]**. The mod then drops the reading. "Shipping it" lists this with the other things that stop the mod.

## The mod

The mod is one TypeScript file of 57 lines, named by one line in the plugin's `hooks.json`. Claude Code loads the TypeScript directly: no Node.js and no build step **\[documented\]**.

**The files**

```text
mods/usage/                      proposed home in the repository
├── .claude-plugin/
│   └── plugin.json              manifest of the development copy
├── hooks/
│   ├── hooks.json               "modules": ["./register.ts"]
│   ├── register.ts              the mod
│   └── listening-file.ts        one constant: the path of listening.txt
├── tests/
│   └── register.test.ts         28 tests
└── tsconfig.json                points at the types that Claude Code writes
```

In the product, the dashboard writes `register.ts` and `listening-file.ts` into the plugin folder that it already writes at each start. That plugin's `hooks.json` then holds two keys: `modules` for the mod, and `hooks` for the eight command hooks that it has today. One `hooks.json` can hold both **\[documented\]**, and both ran side by side in the lab **\[measured\]**.

**`hooks/register.ts`**

This is the whole mod, as it was validated, type-checked, tested and run.

```ts
// Claude Dashboard - usage reporter.
//
// THE COPY IN THE DASHBOARD'S DATA FOLDER IS GENERATED. ClaudeDashboard.App writes it at every
// start from mods/usage/hooks/register.ts in its repository, so an edit to the copy is reverted.
//
// WHAT IT DOES. Claude Code raises session.measure when it measures the session: at the end of
// each turn of the main conversation, and when a plan limit moves a whole point. The hook below
// posts those figures to the dashboard when one is listening, and does nothing when none is. It
// only observes: it shows nothing, changes nothing, never throws, and hands the event on as it
// came.
//
// THE POST IS STARTED FROM A TIMER, AND THAT IS NOT A PREFERENCE. A $.http.fetch that starts
// while the hook runs holds the end of a `claude -p` run until the dashboard answers, awaited
// or not. Measured on Claude Code 2.1.294, 2026-10-08, against a server that accepts the
// request and never answers: the run ended after 32 s in place of 2 s. A timer's callback runs
// outside the event, and the same server then added nothing: the run ended in 2 to 3 s.
import type { EngineInterface, Register, SessionMeasureInput } from 'claude-code'

import { listeningFile } from './listening-file'

// The two lines of listening.txt, held to the rules post-status.cmd holds them to: a port from
// 1 to 65535 with no sign, space or leading zero, and a token of exactly 43 base64url characters.
const PORT = /^[1-9][0-9]{0,4}$/
const TOKEN = /^[A-Za-z0-9_-]{43}$/

export const register: Register = on => {
  on('session.measure', ($, e, next) => {
    $.clock.after(0, () => void report($, e))
    return next(e)
  })
}

/**
 * Sends one measurement to the dashboard, if one is listening. Resolves on every path.
 *
 * listening.txt is read at every call, never kept: the dashboard makes a new token at each
 * start, and a session must go on reporting through any number of dashboard restarts.
 */
export async function report(
  $: Pick<EngineInterface, 'fs' | 'http' | 'session'>,
  e: SessionMeasureInput,
): Promise<void> {
  try {
    // No listening.txt means no dashboard: the read rejects, and nothing is opened.
    const [port = '', token = ''] = (await $.fs.read(listeningFile)).split('\r\n')
    if (!PORT.test(port) || Number(port) > 65535 || !TOKEN.test(token)) return

    // The answer is never read. The dashboard answers 200 with an empty body, or 401.
    await $.http.fetch(`http://127.0.0.1:${port}/usage`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Dashboard-Token': token },
      body: JSON.stringify({ sessionId: await $.session.id(), ...e }),
    })
  } catch {
    // A dashboard that is closed, slow or gone is not this session's problem.
  }
}
```

**The two small files**

The product's `hooks.json` gains the `modules` line. The `hooks` entries stay as they are: one of this form for each of the eight events (Impl §9.2).

```json
{
  "description": "Written by Claude Dashboard at every start; an edit here is reverted. …",
  "modules": ["./register.ts"],
  "hooks": {
    "SessionStart": [
      { "hooks": [ { "type": "command",
        "command": "C:\\Windows\\System32\\cmd.exe",
        "args": ["/c", "C:\\Users\\<user>\\AppData\\Local\\ClaudeDashboard\\post-status.cmd"],
        "async": true } ] }
    ]
  }
}
```

`listening-file.ts`, as the dashboard writes it, holds the absolute path of its own `listening.txt`:

```ts
export const listeningFile = "C:\\Users\\<user>\\AppData\\Local\\ClaudeDashboard\\listening.txt"
```

The path is absolute for the same reason that `hooks.json` names `post-status.cmd` by its absolute path (Impl §9.4): it then does not matter where Claude Code runs the plugin from. An absolute path outside the session's folder worked in the lab on Linux **\[measured\]**. The Windows form above is not yet run.

**Five rules that the code holds**

| Rule | Why |
| --- | --- |
| It only observes. The hook returns `next(e)` with the event as it came | The dashboard must never block, delay or change a turn (Impl §3.3) |
| It never throws, and it shows nothing | Each failure ends in the `catch`. A closed dashboard must cost a session nothing and print nothing, as with `post-status.cmd` (Impl §9.2) |
| It reads `listening.txt` at every call | The dashboard makes a new token at each start (Impl §3.4). No file means no dashboard, and then nothing is opened |
| It checks the port and the token before it uses them | They go into a URL and a header. The rules are those of `post-status.cmd`: a port from 1 to 65535, and a token of exactly 43 base64url characters |
| It starts the post from a timer, not inside the hook | A post that starts inside the hook holds the end of a `claude -p` run until the dashboard answers. The table below shows it |

The mod sends the event whole and lets the dashboard pick what it reads. A change in what the dashboard reads then needs no change in the mod.

**Why the timer**

Wall time of one `claude -p` run, one run for each cell **\[measured\]**:

| How the post starts | Dashboard answers at once | Answers after 5 s | Accepts, never answers |
| --- | --- | --- | --- |
| Inside the hook, awaited | 2.5 s | 6.8 s | 32.2 s |
| Inside the hook, not awaited | 2.1 s | 7.1 s | 31.7 s |
| From a 0 ms timer, as above | 1.9 s | 2.2 s | 3.1 s |

With no `listening.txt` the same run took 2.0 s. A timer's callback runs outside any event **\[documented\]**, so nothing waits for it. Claude Code's own HTTP call gave up after about 30 s **\[measured\]**, which is the 32 s in the table.

A slow hook does not slow the turn itself. With a hook that slept 3 s, the next model request went out on time **\[measured\]**. What a slow hook holds is the end of the process.

**What one call costs**

The hook took 16 to 38 ms on a session's first call and 3 to 6 ms after that, with one first call at 85 ms **\[measured\]**. The post took 3 to 38 ms against the lab's receiver. No process is started.

## Run it by hand

You can run the mod today, before any change to the dashboard. A lab kit holds the mod's development copy and a small receiver that stands where the dashboard will stand.

The kit is `usage-mod-lab.zip`, sent in the chat with this guide. Unpack it outside the repository: the repository's architecture tests read each project file under it. You need Claude Code 2.1.287 or later and the .NET 10 SDK. Type each command in the kit's folder.

**1. Check what Claude Code reads from the mod**

```text
claude plugin validate --strict mods/usage
```

The answer must hold these two lines, and then `Validation passed`:

```text
> ./register.ts hooks: session.measure
> ./register.ts calls: $.clock.after, $.fs.read (via report), $.http.fetch (via report), $.session.id (via report)
```

Those two lines are all that the mod can do: one event and four calls. A mod has no other way to reach a file or the network **\[documented\]**. The first mark of each line depends on the terminal.

**2. Run the mod's tests**

```text
claude plugin test mods/usage
```

The last lines must read `28 pass` and `0 fail`. This command needs no session, no sign-in and no network **\[documented\]**.

**3. Run the receiver's checks**

```text
dotnet run --project lab/usage-receiver -- --check
```

The last line must read `28 pass, 0 fail`.

**4. Start the receiver**

```text
dotnet run --project lab/usage-receiver
```

It prints its port and the path of the `.mod-lab/listening.txt` that it wrote. It never prints the token.

**5. Start Claude Code in the same folder, in a second terminal**

```text
claude --plugin-dir mods/usage
```

Send a prompt. At the end of the turn the receiver prints one line. This one is from the lab **\[measured\]**:

```text
19:46:37Z  five_hour 47% (resets 2026-10-08 23:10Z) seven_day 18% (resets 2026-10-14 13:00Z)  session a41da150
```

The lab used `claude -p` runs. An interactive session is not yet run, and it is the first thing to see on your machine.

The dashboard's own plugin can stay installed while you do this. The development copy has a different name, `dashboard-usage-dev`, and it reads the kit's `listening.txt`, not the dashboard's.

**6. Read the state, as a caller of the dashboard will**

```powershell
$port, $token = Get-Content .mod-lab/listening.txt
curl.exe -s -H "X-Dashboard-Token: $token" "http://127.0.0.1:$port/state"
```

The answer is the `usage` object that the server section shows. The lab made the same request from a Linux shell; these two PowerShell lines were not run.

**7. Stop the receiver with Ctrl+C**

It deletes `listening.txt`. From then on the mod opens nothing, as with a closed dashboard.

**When nothing arrives**

Start Claude Code with a debug log, and read the lines that name the mod:

```text
claude --debug-file mod-debug.log --plugin-dir mods/usage
```

| Line in the debug log | What it means |
| --- | --- |
| `hooks module dashboard-usage-dev@inline loaded (worker, environment 1, tier user); events: session.measure` | The mod loaded **\[measured\]** |
| `$.http.fetch (dashboard-usage-dev): POST http://127.0.0.1:44483/usage`, then `200 in 165ms, 0 chars` | A post went out and was answered **\[measured\]** |
| `$.fs.read (dashboard-usage-dev): … listening.txt failed: ENOENT` | There is no `listening.txt`, so no receiver. This is the quiet path, not a fault **\[measured\]** |
| `hooks module dashboard-usage-dev@inline not loaded:` and a reason | Claude Code refused the mod. The reason names the setting **\[documented\]** |

For the installed plugin the name in these lines is `claude-dashboard@claude-dashboard` **\[measured\]**.

**The types for your version**

At the first `--plugin-dir` load, Claude Code writes `.claude-plugin/types/` beside the mod: the declarations for the version that you run, with a `.gitignore` of their own. Search `claude-code/index.d.ts` there for `'session.measure'` to read the event's own description. `tsc -p mods/usage` then type-checks the mod **\[documented\]**. The lab ran it with TypeScript 5.9.3, with no error **\[measured\]**.

## Automated tests

The mod has 28 tests in one file, and a planted fault in each rule of the code makes at least one of them fail.

`claude plugin test mods/usage` runs them. The command exits with status 1 when a test fails, and it needs no session, no sign-in and no network **\[documented\]**. The whole run took about 0.4 s in the lab **\[measured\]**. The full test file is in the appendix.

**Two kinds of test in the one file**

| Tests | How they run | What they hold |
| --- | --- | --- |
| 24 call `report` directly | With a `$` made of plain functions. No engine is in the way, so "nothing was sent" is known when `report` returns | The request: URL, method, headers and body. `listening.txt` is read at every call. Fifteen forms of a bad `listening.txt` send nothing. Each failure is swallowed |
| 4 go through Claude Code's test kit, `claude-code/testing` | The mod is loaded as it ships. The test's hooks stand where the clock, the file, the dashboard and Claude Code's own measure would be | The event goes on as it came. Nothing is read or sent inside the event. A dashboard that never answers holds nothing |

**Why `report` is tested directly**

Claude Code skips a hook that throws, and goes on **\[documented\]**. So a test through the engine does not fail when the `catch` is missing. The module therefore exports `report`, and 24 tests call it with no engine between. With the `catch` taken out, four of those tests fail, and each names the path that threw. None of the four engine tests fails. The runner adds one failing line of its own, `the file ran to its end`, for a rejection that nothing handled **\[measured\]**.

**What the test kit needs you to know**

- Register each of the test's hooks before the test's first call on `$` **\[documented\]**.
- A relative path reaches a test's `fs.read` hook as an absolute path **\[documented\]**. So the engine tests hold the count of reads, and a direct test holds the path.
- `mock.clock(on)` gives a clock that only the test moves, and `clock.settle()` runs the timers that are due **\[documented\]**. The test for the timer rule depends on it: between the event's answer and `settle()`, nothing is read and nothing is sent.
- The token in the tests must be exactly 43 characters, and the first test holds that. With a test token of 44 characters, the first test fails and says why, and eight more fail for a reason that is not their rule **\[measured\]**.

**The planted faults**

Each row is one change to a copy of `register.ts`. The tests were then run **\[measured\]**.

| Planted fault | Tests that fail |
| --- | --- |
| The token is not checked | 5 |
| A port above 65535 is used | 1 |
| The port can start with a zero | 2 |
| A failure is thrown, not swallowed | 4, and one line of the runner's own: `the file ran to its end` |
| The event is answered, not handed on | 3 |
| The file is split at LF, not at CRLF | 8 |
| The post is awaited inside the hook | 2 |
| The post starts inside the hook and is not awaited | 1, in each of 3 runs |
| `listening.txt` is read one time and kept | 17 |
| The post goes to `/hook` | 2 |
| The session ID is left out of the body | 3 |
| The token goes in a different header | 1 |
| The content type is left out | 1 |
| The host is `localhost`, not `127.0.0.1` | 2 |
| The request is a GET | 1 |
| The timer waits one second | 4, in each of 3 runs |

**The dashboard's side**

The C# gets xUnit tests in the solution's test project, as all product code does. The lab kit's 28 checks have the names that those tests can take. A fault planted in each of 14 places of the C# made at least one check fail **\[measured\]**. The build plan lists the tests and the plants for each task.

**The mod's tests and `build.ps1`**

The mod's tests are not .NET tests. `dotnet test` does not count them, so the verdict line of `build.ps1` does not see them. Ruling R4 in the build plan decides how they join the verdict.

## The server endpoint

The dashboard gains one endpoint, `POST /usage`. It keeps the newest reading of each limit and shows it in `GET /state`. It is a pure observer, as `/hook` is.

**The contract**

| Endpoint | Purpose | Token | Answers |
| --- | --- | --- | --- |
| `POST /usage` | Receives one measurement from the mod | Necessary, in `X-Dashboard-Token` | `200` with an empty body, always. `401` for a bad token |

- **It answers `200` with an empty body on every path after the token check**: a good body, a body that is not JSON, a body with no limits in it, and any exception. It never answers a decision field (Impl §3.3).
- **It reads two fields:** `sessionId` and `rateLimits`. It does not bind `context`, `cost` or `changed`, so nothing can store, show or log them.
- **All of it is data.** A kind is kept as text and compared with nothing.
- **Each thing it keeps has a bound:** 8 kinds, 64 characters for a kind, 128 for a session ID, and the first 16 entries of a post.
- **It is not the Registry.** A limit belongs to the account, not to a session. A reading makes no event, no row in the history, no sound and no notice. The event channel never sees one.

**What the dashboard keeps**

| When | Then |
| --- | --- |
| A reading of a kind arrives | It replaces the held reading of that kind, also when it shows less |
| A reading of an older window arrives late | Nothing changes. A window is known by its reset time, and this one's is earlier than the held one's |
| A post does not carry a kind | The held reading of that kind stays |
| A limit's reset time has passed | The reading is left out when the state is read. Its percentage is no longer true |
| A reading has no reset time | It stays until a newer one replaces it |
| A kind arrives that this build does not know | It is kept, as text |
| A ninth kind arrives, or a kind of more than 64 characters, or a percentage that is negative or not a number | Nothing changes |

The newest reading wins also when it shows less, because a limit that was raised lowers the percentage inside one window. In the lab the reset time of each limit was the same in all 32 logged posts, over 35 minutes **\[measured\]**, so a reset time does identify a window. Claude Code's status line treats a window the same way: it drops one when its reset time passes **\[documented\]**.

**What `/state` gains**

One member, `usage`, beside `health`. This is the lab receiver's answer after a real post from the mod **\[measured\]**:

```json
{
  "usage": {
    "lastHeardAt": "2026-10-08T19:43:48.7214724Z",
    "windows": [
      {
        "kind": "five_hour",
        "percentUsed": 47,
        "resetsAt": "2026-10-08T23:10:00Z",
        "heardAt": "2026-10-08T19:43:48.7214724Z",
        "sessionId": "02762de4-186e-4b8a-a10f-ce3c5dae2fe3"
      },
      {
        "kind": "seven_day",
        "percentUsed": 18,
        "resetsAt": "2026-10-14T13:00:00Z",
        "heardAt": "2026-10-08T19:43:48.7214724Z",
        "sessionId": "02762de4-186e-4b8a-a10f-ce3c5dae2fe3"
      }
    ]
  }
}
```

- `usage` is null before the first post.
- `lastHeardAt` is when a usage post last arrived, with or without a reading in it. It is not the `lastHeardAt` of `health`, which stays the time of the last hook message (ruling R5).
- `windows` is in the order of the kinds' names. Each instant is in UTC and ends in `Z`.
- It is read when the request is served, as `health` is, because request threads write it (Impl §3.5).
- No other member of the report moves.

**The three C# files**

| File | Goes to | Holds |
| --- | --- | --- |
| `UsageReadings.cs` | `src/ClaudeDashboard.Core/` | The rule in the table above: `UsageWindow` and `UsageReadings`, both immutable |
| `UsageReader.cs` | `src/ClaudeDashboard.App/Ingress/` | Reads the two fields out of a body. No body can make it throw |
| `UsageBoard.cs` | `src/ClaudeDashboard.App/Ingress/` | Holds the readings as `HookHealth` holds its values: one small lock for the writers, one published reference for the readers. `Report(now)` makes the `usage` object |

The three files are in the appendix and in the lab kit. How far they were checked **\[measured\]**:

- `UsageReadings.cs` builds inside `ClaudeDashboard.Core` at commit `9155b7e`, in Debug and Release, with no warning under the project's own settings.
- All three build in the lab receiver with the same analyzer settings and warnings as errors.
- They pass the kit's 28 checks, and they received the mod's posts.
- The SDK was .NET 10.0.112 on Linux. They were not built in `ClaudeDashboard.App`, which is a Windows project, and they have no xUnit test yet.

**What the handler does**

1. It checks the token, as `/hook` does. For a bad token it counts the refusal and answers `401` (ruling R3).
2. It reads the body as text.
3. `UsageReader.Read(body, now)` gives the readings, or none.
4. `UsageBoard.Heard(readings, now)` keeps them.
5. It answers `200` with an empty body. One catch-all round steps 2 to 4 answers the same.

Map it with the cast that `/hook` has: `app.MapPost("/usage", (Delegate)HandleUsage)`. Without the cast the build stops with ASP0016 under warnings as errors **\[measured\]**. The lab's handler is `Receiver.HandleUsage` in the kit.

`HandleState` then adds `Usage = board.Report(now)` to the report, as it adds `Health`.

**What one post costs the dashboard**

The first post to a receiver that had just started was answered in about 170 ms, and a later one in 12 ms **\[measured\]**. The session waits for neither: the mod's post runs outside the event.

## Shipping it

The mod ships inside the plugin that the dashboard already writes at each start. It reaches a new session with no install step and no update step.

**How it reaches a session**

1. A new build of the dashboard starts. It finds its plugin registered and writes the plugin's files again, because they differ (`StartupHookInstall.Run`, `HookPlugin.EnsureWritten`). The folder now holds `register.ts`, `listening-file.ts` and the new `hooks.json`.
2. Claude Code reads this plugin in place, from the dashboard's data folder (Impl §9.4). A plugin that is loaded in place is not pinned by its version **\[documented\]**, so the version in `plugin.json` stays as it is.
3. The next session that starts loads the mod. A session that was open keeps what it loaded, until it restarts or until `/reload-plugins` is run in it **\[documented\]**.

No `claude plugin` command runs, and nothing is asked of the operator.

**One `hooks.json` serves every version**

The same plugin files, with `modules` and `hooks` in one `hooks.json`, on five versions **\[measured\]**:

| Claude Code | The plugin's command hooks | The mod |
| --- | --- | --- |
| 2.1.241 | Fire | Not loaded. This version does not read `modules` |
| 2.1.286 | Fire | Loaded, and posted. The documented minimum is 2.1.287 |
| 2.1.287 | Fire | Loaded, and posted |
| 2.1.294 | Fire | Loaded, and posted |
| 2.1.295 | Fire | Loaded, and posted |

So the dashboard needs no check of Claude Code's version before it writes the mod.

On 8 October 2026 npm has 2.1.295 as `latest` and 2.1.286 as `stable`. An operator on the stable channel is thus below the documented minimum. The lab's 2.1.286 loaded the mod, but the documentation does not promise that.

**A broken mod does not take the command hooks with it**

Three faults were planted in the installed plugin, one at a time: `register.ts` not there, `listening-file.ts` not there, and half a `register.ts`. In each case the session ran as usual, the plugin's command hook still fired, and nothing was printed. The debug log named the fault **\[measured\]**. A fault in the mod thus costs the usage reading and nothing more.

**What turns the mod off, with no word on screen**

| Cause | What stops |
| --- | --- |
| The operator sets `disableAllHooks` in their own settings | The mod, and every hook with it |
| A session starts with `--safe-mode` | Every installed plugin |
| A session starts with `--bare` | The mod |
| An organization sets `allowManagedModsOnly` | The mod. The plugin's command hooks keep running |
| An organization sets `allowManagedHooksOnly` | The mod and the plugin's command hooks |
| An organization turns off web fetching | The post. The mod loads, and it drops each reading |
| Anthropic turns installed mods off remotely | The mod. No setting on the machine turns it on again |
| A WSL session in the Desktop app | Every plugin |

All of these rows are **\[documented\]**. The lab saw two of them: with `--safe-mode` neither the plugin's command hook nor the mod ran, and with `--bare` the debug log had the line that refuses the mod **\[measured\]**. A bare run does not use the subscription sign-in **\[documented\]**, so it has no plan limits to report in any case. For the dashboard, a mod that is off looks the same as a quiet day.

**One page and the type declarations disagree**

The page for administrators says that a session with nonessential traffic turned off also refuses a mod's `$.http.fetch`. The type declarations for 2.1.294 say that `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` refuses a plugin's request only when the request carries a credential. The mod's request carries none. In the lab, with the variable set to 1, the post went out and was answered **\[measured\]**. The documentation says to trust the declarations when they and a page disagree **\[documented\]**. A later version can change this, so MOD.6 has a check for it.

**How to tell that it is off**

- `claude plugin test`, typed in an empty folder, says if mods can load at all **\[documented\]**. `no hooks module to load` means that they can. `hooks modules are turned off here` means that a setting stops them. `hooks modules are turned off in this process` means that Anthropic stopped them. The command does not report `allowManagedModsOnly`.
- `/plugin` in a session shows a line such as `1 mod active` with the name of the plugin **\[documented\]**.
- The debug log has `hooks module claude-dashboard@claude-dashboard loaded` **\[measured\]**, or `not loaded:` and the reason **\[documented\]**.
- On the dashboard's side the one sign is the age of the last reading: `usage.lastHeardAt`. It is information, never an alarm.

**The plugin's name: a finding**

`claude plugin validate` fails the name `claude-dashboard` on Claude Code 2.1.287, 2.1.294 and 2.1.295 **\[measured\]**:

```text
name: Plugin name "claude-dashboard" is reserved: it passes as one of Anthropic's own. A third party's plugin name cannot start with "claude-", "anthropic-", "anthropics-", or "cc-plugin-" …
```

- The documentation says the same: `claude plugin validate` fails a name that looks like one of Anthropic's own, such as one that starts with `claude-` **\[documented\]**.
- It passed on 2.1.241 and 2.1.286 **\[measured\]**.
- `claude plugin marketplace add` and `claude plugin install claude-dashboard@claude-dashboard` still exit 0 on all five versions, and the mod loads on 2.1.294 and 2.1.295 **\[measured\]**. Nothing is broken today.
- The risk is a later version. Claude Code 2.1.280 began to refuse a marketplace whose name imitates a reserved one, and to stop loading one that was already added **\[documented\]**. The same step for plugin names would stop the dashboard's plugin: the eight hooks and the mod.

Ruling R1 in the build plan asks for a decision. This guide changes no name.

**The API is early access**

The type declarations say: "this surface may change between releases without notice" **\[documented\]**. Four things limit what a change can cost:

- The mod is small: one event and four calls.
- With a new Claude Code version, `claude plugin validate` and `claude plugin test` say in seconds if the mod still loads and still does what its tests hold.
- A mod that does not load costs the usage reading only, as measured above.
- The endpoint takes any body and keeps only what it can read.

**The mod is code that runs as the operator**

A mod runs inside Claude Code with the operator's permissions **\[documented\]**. The plugin already runs a script on each event, so the trust is not new, but the README must say that the plugin now holds a mod. `claude plugin validate` on the plugin's folder lists the one event and the four calls. With the present name it also reports the name error above.

**The gate on Windows**

Nothing in this guide ran on Windows. Task MOD.6 in the build plan is the operator's run on a Windows machine, and it lists what to see.

## Limits and open questions

The mod gives the two main limits at the end of each turn, and nothing more. This section lists what it does not give, what was not measured and what is left to decide.

**Limits**

- **No figure for one model, and no credit balance.** The event does not carry them **\[documented\]**. This guide found no other source for them that Claude Code documents.
- **A reading arrives only when a session takes a turn.** Other apps use the same limits. The dashboard's figure can thus be behind the true one, and `heardAt` says how old it is.
- **One account for each machine.** The body names no account. Readings from two accounts would replace each other.
- **The last reading of a `claude -p` run can be lost.** The post starts a few milliseconds after the event, and the process can end first. With the default settings every lab run sent it. With `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` three runs of five lost it: in those the process ended 27 to 37 ms after the event **\[measured\]**. An interactive session stays open, so it does not have this limit.
- **A `claude -p` run can stop loading the mod.** The documentation says that `--bare` will become the default for `-p` in a later release, and a bare run loads no mod of an installed plugin **\[documented\]**.
- **Work in a subagent is not a turn of the main conversation.** The event comes at the end of a main turn, and when a limit moves a whole point **\[documented\]**.

**Not measured**

- An interactive session, in a terminal or in the Desktop app.
- Windows, in all respects.
- The VS Code extension. Hooks run there **\[documented\]**.
- A call for a limit that moves a whole point in the middle of a turn.

**Open questions**

- **What the operator sees.** This guide stops at `/state`. A figure in the window or the tray is a decision for the Design Document (ruling R7).
- **A warning near a limit.** Today nothing is louder because of a reading. A notice at 90% would be a new kind of notice, and needs a ruling.
- **A history of readings.** A chart of use over time needs each reading in the event log. This guide keeps the newest reading only.
- **A last post at the end of a `-p` run.** A `session.end` hook could send it. All such hooks share 1.5 seconds **\[documented\]**. It is not built, because the gain is one reading.

## Build plan for the agents

This section is now its own document, the [Usage Mod Execution Plan](claude-dashboard-usage-mod-execution-plan.md), with the rulings R1 to R7 taken and the record of each task as it is done. It was moved out on 2026-10-08 so that the guide stays a description and the plan stays a record.

## Appendix: the files

These are the files as they were run in the lab. `hooks/register.ts` is in "The mod". The lab kit holds the same files, and the receiver with its checks.

### `mods/usage/.claude-plugin/plugin.json`

The manifest of the development copy. The product's plugin keeps its own manifest.

```json
{
  "name": "dashboard-usage-dev",
  "version": "0.0.0",
  "description": "Development copy of Claude Dashboard's usage reporter. Loaded with --plugin-dir and by `claude plugin test`. Never installed.",
  "author": { "name": "David Sopko" }
}
```

### `mods/usage/hooks/hooks.json`

```json
{
  "description": "Development copy: the usage reporter alone.",
  "modules": ["./register.ts"]
}
```

### `mods/usage/hooks/listening-file.ts`

The development stand-in. In the product the dashboard writes this file, with the absolute path of its own `listening.txt`.

```ts
// DEVELOPMENT STAND-IN. The dashboard writes its own copy of this file into its plugin, with the
// absolute path of its data folder's listening.txt. This one is relative to the folder a lab
// session is started in, so a lab can point the module at a listener of its own.
export const listeningFile = '.mod-lab/listening.txt'
```

### `mods/usage/tsconfig.json`

Claude Code writes this file when the mod has none. It is committed so that a run leaves no new file.

```json
{
  "extends": "./.claude-plugin/types/tsconfig.json"
}
```

### `mods/usage/tests/register.test.ts`

```ts
import { describe, expect, mock, test } from 'claude-code/testing'
import type { HttpInit, HttpResponse, On, SessionMeasureInput } from 'claude-code'

import { listeningFile } from '../hooks/listening-file'
import { report } from '../hooks/register'

const TOKEN = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNO-_' // 43 characters, as the dashboard makes them
const SESSION = '3f6c1d1e-0000-4000-8000-000000000001'
const LISTENING = `52789\r\n${TOKEN}`

const MEASURE: SessionMeasureInput = {
  context: { window: 200000, tokens: 41200, percent: 21 },
  rateLimits: [
    { kind: 'five_hour', percentUsed: 17, resetsAt: '2026-10-08T18:10:00.000Z' },
    { kind: 'seven_day', percentUsed: 8.5, resetsAt: '2026-10-14T13:00:00.000Z' },
  ],
  cost: { usd: 0.42 },
  changed: ['rateLimits', 'cost'],
}

type Post = { url: string; init?: HttpInit }
type Dollar = Parameters<typeof report>[0]

const OK: HttpResponse = { status: 200, ok: true, headers: {}, text: '' }
const REFUSED: HttpResponse = { status: 401, ok: false, headers: {}, text: '' }
const never = <T>() => new Promise<T>(() => {})

/**
 * A `$` of plain functions, for calling `report` directly. Each call is counted as it is made,
 * so "nothing was sent" is known the moment `report` resolves.
 */
function fake(listening: string | Error, answer: () => Promise<HttpResponse> = async () => OK) {
  const posts: Post[] = []
  const reads: string[] = []
  const $ = {
    fs: {
      read: async (path: string) => {
        reads.push(path)
        if (listening instanceof Error) throw listening
        return listening
      },
    },
    session: { id: async () => SESSION },
    http: {
      fetch: (url: string, init?: HttpInit) => {
        posts.push({ url, init })
        return answer()
      },
    },
  } as unknown as Dollar
  return { $, posts, reads }
}

test('the token these tests use is the length the dashboard makes', () => {
  // Every case below leans on this: a 44-character "good" token would fail them for the wrong reason.
  expect(TOKEN.length).toBe(43)
})

describe('report, with a dashboard listening', () => {
  test('the figures go to its port, with its token', async () => {
    const { $, posts, reads } = fake(LISTENING)

    await report($, MEASURE)

    expect(reads).toEqual([listeningFile])
    expect(posts.length).toBe(1)
    expect(posts[0]?.url).toBe('http://127.0.0.1:52789/usage')
    expect(posts[0]?.init?.method).toBe('POST')
    expect(posts[0]?.init?.headers).toEqual({
      'Content-Type': 'application/json',
      'X-Dashboard-Token': TOKEN,
    })
    expect(JSON.parse(posts[0]?.init?.body ?? '')).toEqual({ sessionId: SESSION, ...MEASURE })
  })

  test('a line ending after the token is still the token', async () => {
    const { $, posts } = fake(`${LISTENING}\r\n`)

    await report($, MEASURE)

    expect(posts.length).toBe(1)
  })

  test('listening.txt is read again at every call', async () => {
    const { $, reads } = fake(LISTENING)

    await report($, MEASURE)
    await report($, MEASURE)

    expect(reads.length).toBe(2)
  })

  test('a dashboard that refuses the token throws nothing', async () => {
    const { $, posts } = fake(LISTENING, async () => REFUSED)

    await report($, MEASURE)

    expect(posts.length).toBe(1)
  })

  test('a dashboard that is gone throws nothing', async () => {
    const { $, posts } = fake(LISTENING, async () => {
      throw new Error('ECONNREFUSED')
    })

    await report($, MEASURE)

    expect(posts.length).toBe(1)
  })
})

describe('report, with no dashboard listening', () => {
  test('with no listening.txt nothing is opened, and nothing is thrown', async () => {
    const { $, posts, reads } = fake(new Error('ENOENT: no such file or directory'))

    await report($, MEASURE)

    expect(reads.length).toBe(1)
    expect(posts).toEqual([])
  })

  test('a session that cannot say its id sends nothing, and nothing is thrown', async () => {
    const { $, posts } = fake(LISTENING)
    $.session.id = async () => {
      throw new Error('no session bound')
    }

    await report($, MEASURE)

    expect(posts).toEqual([])
  })

  test('a post that cannot even be started throws nothing', async () => {
    const { $ } = fake(LISTENING)
    $.http.fetch = () => {
      throw new Error('refused by policy')
    }

    await report($, MEASURE)
  })
})

describe('report, with a listening.txt the dashboard did not write', () => {
  const bad: Record<string, string> = {
    'an empty file': '',
    'a port alone': '52789',
    'port zero': `0\r\n${TOKEN}`,
    'a port above 65535': `65536\r\n${TOKEN}`,
    'a port of six digits': `527890\r\n${TOKEN}`,
    'a port with a leading zero': `05278\r\n${TOKEN}`,
    'a port with a sign': `+52789\r\n${TOKEN}`,
    'a port with a space': `52789 \r\n${TOKEN}`,
    'a port that is not a number': `5278x\r\n${TOKEN}`,
    'a host in place of a port': `evil.example:80\r\n${TOKEN}`,
    'a token of 42 characters': `52789\r\n${TOKEN.slice(1)}`,
    'a token of 44 characters': `52789\r\n${TOKEN}A`,
    'a token with a character outside the set': `52789\r\n${TOKEN.slice(1)}+`,
    'a token with a line break inside': `52789\r\n${TOKEN.slice(0, 20)}\n${TOKEN.slice(21)}`,
    'LF line endings': `52789\n${TOKEN}`,
  }

  for (const [what, listening] of Object.entries(bad)) {
    test(`${what}: nothing is sent`, async () => {
      const { $, posts } = fake(listening)

      await report($, MEASURE)

      expect(posts).toEqual([])
    })
  }
})

/**
 * The same, through the engine: the mod loaded as it ships, and the test's hooks standing where
 * the clock, listening.txt, the dashboard, the session and the engine's own measure would be.
 */
function world(on: On, listening: string | undefined, answer: () => Promise<HttpResponse> = async () => OK) {
  const clock = mock.clock(on)
  const measured: SessionMeasureInput[] = []
  const reads: string[] = []
  const posts: Post[] = []

  on('fs.read', ($, e) => {
    reads.push(e.path)
    return listening === undefined ? { deny: 'ENOENT: no such file' } : { value: listening }
  })
  on('session.id', () => ({ value: SESSION }))
  on('http.fetch', async ($, e) => {
    posts.push({ url: e.url, init: e.init })
    return { value: await answer() }
  })
  on('session.measure', ($, e) => {
    measured.push(e)
    return { changed: e.changed }
  })

  return { clock, measured, reads, posts }
}

describe('register', () => {
  test('a measurement reaches the dashboard, and the event goes on as it came', async ($, on) => {
    const { clock, measured, posts } = world(on, LISTENING)

    const result = await $.session.measure(MEASURE)
    await clock.settle()

    expect(posts.length).toBe(1)
    expect(posts[0]?.url).toBe('http://127.0.0.1:52789/usage')
    expect(JSON.parse(posts[0]?.init?.body ?? '')).toEqual({ sessionId: SESSION, ...MEASURE })
    // It only observes: the engine's own measure got the event as it was raised, and answered.
    expect(measured).toEqual([MEASURE])
    expect(result).toEqual({ changed: ['rateLimits', 'cost'] })
  })

  test('nothing is read or sent inside the event: the post starts from a timer', async ($, on) => {
    const { clock, reads, posts } = world(on, LISTENING)

    await $.session.measure(MEASURE)

    // The event has been answered and the timer has not run: the mocked clock has not moved.
    expect(reads).toEqual([])
    expect(posts).toEqual([])

    await clock.settle()

    // Through the engine a relative path arrives made absolute, so only the count is held here.
    // The direct test above holds the path itself.
    expect(reads.length).toBe(1)
    expect(posts.length).toBe(1)
  })

  test('a dashboard that never answers does not hold the event', async ($, on) => {
    const { clock, measured, posts } = world(on, LISTENING, never)

    const result = await $.session.measure(MEASURE)
    await clock.settle()

    expect(posts.length).toBe(1)
    expect(measured).toEqual([MEASURE])
    expect(result).toEqual({ changed: ['rateLimits', 'cost'] })
  })

  test('with no dashboard the event still goes on as it came', async ($, on) => {
    const { clock, measured, reads, posts } = world(on, undefined)

    const result = await $.session.measure(MEASURE)
    await clock.settle()

    expect(reads.length).toBe(1)
    expect(posts).toEqual([])
    expect(measured).toEqual([MEASURE])
    expect(result).toEqual({ changed: ['rateLimits', 'cost'] })
  })
})
```

### `UsageReadings.cs`

For `src/ClaudeDashboard.Core/`. The rule.

```csharp
namespace ClaudeDashboard.Core;

/// <summary>One plan limit, as a Claude Code session last reported it.</summary>
/// <param name="Kind">
/// Which limit: <c>five_hour</c>, <c>seven_day</c>, or a value this build does not know. It is
/// data: nothing switches on it.
/// </param>
/// <param name="PercentUsed">
/// How much of the limit is used: 0 to 100, and above 100 on a spend limit that is exceeded.
/// </param>
/// <param name="ResetsAt">When the limit resets, or null when the reading gave no time.</param>
/// <param name="HeardAt">When the dashboard received the reading.</param>
/// <param name="SessionId">The session that sent the reading, or null.</param>
public sealed record UsageWindow(
    string Kind,
    double PercentUsed,
    DateTimeOffset? ResetsAt,
    DateTimeOffset HeardAt,
    string? SessionId);

/// <summary>
/// What the dashboard believes about the plan's limits: the newest reading of each kind.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Immutable.</strong> <see cref="Heard"/>, <see cref="With"/> and <see cref="At"/> answer a
/// new value and change nothing. The holder swaps one reference, so a reader never sees half a change.
/// </para>
/// <para>
/// <strong>A limit belongs to the account, not to a session.</strong> Each session of one account
/// reports the same limits. So the newest reading of a kind wins, whichever session sent it.
/// </para>
/// <para>
/// <strong>Information, never an alarm.</strong> Nothing here has a threshold. A limit that is
/// almost used changes no colour and plays no sound until a ruling says that it does.
/// </para>
/// </remarks>
public sealed record UsageReadings
{
    /// <summary>How many kinds are held: eight. A sender cannot make this value grow without end.</summary>
    public const int MaxKinds = 8;

    /// <summary>The longest kind that is kept: 64 characters.</summary>
    public const int MaxKindLength = 64;

    /// <summary>Nothing heard yet.</summary>
    public static UsageReadings Empty { get; } = new();

    /// <summary>When a usage post last arrived, with or without a reading in it. Null before the first.</summary>
    public DateTimeOffset? LastHeardAt { get; init; }

    /// <summary>The newest reading of each kind, in the order of the kinds' names.</summary>
    public IReadOnlyList<UsageWindow> Windows { get; init; } = [];

    /// <summary>A usage post arrived at <paramref name="at"/>.</summary>
    public UsageReadings Heard(DateTimeOffset at) => this with { LastHeardAt = at };

    /// <summary>These readings with <paramref name="reading"/> applied.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The newest reading of a kind replaces the one held</strong>, also when it shows less:
    /// a limit that was raised lowers the percentage inside one window.
    /// </para>
    /// <para>
    /// <strong>But a reading of an older window changes nothing.</strong> A window is known by its
    /// reset time. A reading whose reset time is earlier than the held one's was made in a window
    /// that has ended, and arrived late.
    /// </para>
    /// <para>
    /// <strong>A reading that cannot be true changes nothing</strong>: no kind, a kind longer than
    /// <see cref="MaxKindLength"/>, or a percentage that is negative or not a number. So does a new
    /// kind when <see cref="MaxKinds"/> are held.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is null.</exception>
    public UsageReadings With(UsageWindow reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        if (reading.Kind is not { Length: > 0 and <= MaxKindLength }
            || !double.IsFinite(reading.PercentUsed)
            || reading.PercentUsed < 0)
        {
            return this;
        }

        var held = Windows.FirstOrDefault(window => string.Equals(window.Kind, reading.Kind, StringComparison.Ordinal));

        if (held is { ResetsAt: { } heldReset } && reading.ResetsAt is { } readReset && readReset < heldReset)
        {
            return this;
        }

        if (held is null && Windows.Count >= MaxKinds)
        {
            return this;
        }

        return this with
        {
            Windows =
            [
                .. Windows
                    .Where(window => !ReferenceEquals(window, held))
                    .Append(reading)
                    .OrderBy(window => window.Kind, StringComparer.Ordinal),
            ],
        };
    }

    /// <summary>
    /// What stands at <paramref name="now"/>: a limit whose reset time has passed is left out,
    /// because its percentage is no longer true. A limit with no reset time stays.
    /// </summary>
    public UsageReadings At(DateTimeOffset now) =>
        Windows.All(window => Open(window, now))
            ? this
            : this with { Windows = [.. Windows.Where(window => Open(window, now))] };

    private static bool Open(UsageWindow window, DateTimeOffset now) =>
        window.ResetsAt is not { } reset || reset > now;
}
```

### `UsageReader.cs`

For `src/ClaudeDashboard.App/Ingress/`.

```csharp
using System.Globalization;
using System.Text.Json;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// Reads the plan's limits out of a usage post from the dashboard's mod, leniently.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing in the body can fail the post.</strong> It is what another program sends, and
/// ingress is a pure observer (Impl §3.3). A body that is not JSON, a list that is not a list and
/// an entry of the wrong shape all read as nothing.
/// </para>
/// <para>
/// <strong>Only <c>sessionId</c> and <c>rateLimits</c> are read.</strong> The mod sends the whole
/// <c>session.measure</c> event; <c>context</c>, <c>cost</c> and <c>changed</c> are not bound, so
/// nothing can store, show or log them.
/// </para>
/// <para>
/// <strong>All of it is data.</strong> A kind is kept as text and compared with nothing.
/// </para>
/// </remarks>
public static class UsageReader
{
    /// <summary>How many entries of <c>rateLimits</c> are read: the first sixteen.</summary>
    public const int MaxEntries = 16;

    /// <summary>The longest session id that is kept: 128 characters. A longer one reads as none.</summary>
    public const int MaxSessionIdLength = 128;

    /// <summary>The readings in <paramref name="body"/>, or none.</summary>
    /// <param name="body">The request body, as text.</param>
    /// <param name="heardAt">When the post arrived.</param>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is null.</exception>
    public static IReadOnlyList<UsageWindow> Read(string body, DateTimeOffset heardAt)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("rateLimits", out var limits)
                || limits.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var sessionId = root.TryGetProperty("sessionId", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 and <= MaxSessionIdLength } text
                    ? text
                    : null;

            var windows = new List<UsageWindow>();

            foreach (var entry in limits.EnumerateArray().Take(MaxEntries))
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("kind", out var kind)
                    && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() is { } name
                    && entry.TryGetProperty("percentUsed", out var percent)
                    && percent.ValueKind == JsonValueKind.Number
                    && percent.TryGetDouble(out var used))
                {
                    windows.Add(new UsageWindow(name, used, ResetOf(entry), heardAt, sessionId));
                }
            }

            return windows;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The entry's <c>resetsAt</c> as an instant, or null when it is absent or not a time.</summary>
    private static DateTimeOffset? ResetOf(JsonElement entry) =>
        entry.TryGetProperty("resetsAt", out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var at)
            ? at.ToUniversalTime()
            : null;
}
```

### `UsageBoard.cs`

For `src/ClaudeDashboard.App/Ingress/`.

```csharp
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// Holds the plan's limits as the sessions last reported them, for <c>/state</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written on request threads and read at a request</strong>, as <c>HookHealth</c> is.
/// One small lock puts the writers in a line. A reader takes one published reference and never
/// the lock.
/// </para>
/// <para>
/// <strong>Not the Registry.</strong> A limit belongs to the account, not to a session. No event,
/// no row in the history and no sound comes from a reading, and the event channel never sees one.
/// </para>
/// <para>
/// <strong>No text from a post is kept but the kind and the session id</strong>, each with a
/// bound on its length (<see cref="UsageReadings.MaxKindLength"/>,
/// <see cref="UsageReader.MaxSessionIdLength"/>).
/// </para>
/// </remarks>
public sealed class UsageBoard
{
    private readonly Lock _gate = new();
    private UsageReadings _current = UsageReadings.Empty;

    /// <summary>What was last published. Read from any thread.</summary>
    public UsageReadings Current => Volatile.Read(ref _current);

    /// <summary>A usage post was accepted at <paramref name="at"/>, with the readings it carried.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="windows"/> is null.</exception>
    public void Heard(IReadOnlyList<UsageWindow> windows, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(windows);

        lock (_gate)
        {
            var next = _current.Heard(at);

            foreach (var window in windows)
            {
                next = next.With(window);
            }

            Volatile.Write(ref _current, next);
        }
    }

    /// <summary>
    /// The <c>usage</c> object of <c>/state</c> at <paramref name="now"/>, or null before the first
    /// post. Instants in UTC. A limit whose reset time has passed is not in it.
    /// </summary>
    public UsageEntry? Report(DateTimeOffset now)
    {
        var current = Current;

        return current.LastHeardAt is { } heard
            ? new UsageEntry(
                heard.UtcDateTime,
                [
                    .. current.At(now).Windows.Select(window => new UsageWindowEntry(
                        window.Kind,
                        window.PercentUsed,
                        window.ResetsAt?.UtcDateTime,
                        window.HeardAt.UtcDateTime,
                        window.SessionId)),
                ])
            : null;
    }
}

/// <summary><c>/state</c>'s <c>usage</c> object.</summary>
/// <param name="LastHeardAt">When a usage post last arrived, in UTC.</param>
/// <param name="Windows">The newest reading of each limit that is still open.</param>
public sealed record UsageEntry(DateTime LastHeardAt, IReadOnlyList<UsageWindowEntry> Windows);

/// <summary>One limit in <c>/state</c>.</summary>
/// <param name="Kind">The limit, as Claude Code named it.</param>
/// <param name="PercentUsed">How much of it is used.</param>
/// <param name="ResetsAt">When it resets, in UTC, or null.</param>
/// <param name="HeardAt">When the reading arrived, in UTC.</param>
/// <param name="SessionId">The session that sent it, or null.</param>
public sealed record UsageWindowEntry(
    string Kind,
    double PercentUsed,
    DateTime? ResetsAt,
    DateTime HeardAt,
    string? SessionId);
```

## Sources

Each page was opened on 8 October 2026. Where a page and the type declarations disagree, this guide follows the declarations and says so.

**Claude Code documentation**

- [Mods overview](https://code.claude.com/docs/en/plugins/mods/overview): what a mod is, where mods run, what turns them off
- [Create a mod](https://code.claude.com/docs/en/plugins/mods/create): the files, the types, `claude plugin validate`, the rule for plugin names
- [Mods reference](https://code.claude.com/docs/en/plugins/mods/reference): the events, the limits, the settings, the commands
- [Use the mods API](https://code.claude.com/docs/en/plugins/mods/api): timers, files and the network
- [Test a mod](https://code.claude.com/docs/en/plugins/mods/test): the test kit and its rules
- [Troubleshoot a mod](https://code.claude.com/docs/en/plugins/mods/troubleshoot): the messages and the debug log
- [Manage mods for your organization](https://code.claude.com/docs/en/plugins/mods/admin): the settings of an organization, the network policy
- [Plugin loading reference](https://code.claude.com/docs/en/plugins/loading): plugins that load in place, versions, reloads
- [Plugin manifest reference](https://code.claude.com/docs/en/plugins/manifest-reference): the `version` field, the settings that a plugin can carry
- [Marketplace reference](https://code.claude.com/docs/en/plugins/marketplace-reference): reserved marketplace names
- [Customize your status line](https://code.claude.com/docs/en/statusline): the same figures in the status line
- [Hooks reference](https://code.claude.com/docs/en/hooks): the settings hook events
- [CLI reference](https://code.claude.com/docs/en/cli-reference) and [Run Claude Code programmatically](https://code.claude.com/docs/en/headless): `--bare`, `--safe-mode`, `claude -p`

**Claude Code itself**

- The type declarations that Claude Code 2.1.294 wrote in the lab. Their first line is `// Written by Claude Code 2.1.294.` The [Claude Code repository](https://github.com/anthropics/claude-code/tree/main/mods) holds a copy that an earlier version wrote, and the source of the built-in mods.
- [CHANGELOG.md](https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md) of the Claude Code repository at commit `71cdddec`: the entries for 2.1.287 and 2.1.280.
- [The npm registry record](https://registry.npmjs.org/@anthropic-ai/claude-code) of `@anthropic-ai/claude-code`: 2.1.287 was published on 1 October 2026, and the `latest` and `stable` versions are from there.

**The claude-dashboard repository at commit `9155b7e`**

- `CLAUDE.md`, and in `docs/`: the Implementation Specification, the Technical Specification, the Execution Plan, the packaging plan, the event-flow document and the hooks reference.
- In `src/ClaudeDashboard.App/`: `Setup/HookPlugin.cs`, `HookScript.cs`, `PluginInstaller.cs` and `StartupHookInstall.cs`; `Ingress/IngressEndpoints.cs`, `HookHealth.cs`, `StateReport.cs` and `IngressToken.cs`; `Configuration/DashboardPaths.cs` and `ListeningFile.cs`.
- The tests of those files under `tests/ClaudeDashboard.Tests/`, and the architecture guards.
