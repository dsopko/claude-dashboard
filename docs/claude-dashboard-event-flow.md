# Claude Dashboard — How session events reach the dashboard

**Describes the code at commit `0488527` · Written 2026-09-29, brought up to date 2026-10-02 for T1.49 to T1.51 (the plugin)**

This document follows one event from a Claude Code session to the window, the speaker and the database. For each step it gives the rule, the file that holds the rule, and the result when the step fails.

**The code is the authority for this document.** The [Technical Specification](claude-dashboard-spec.md) and the [Implementation Specification](claude-dashboard-impl-spec.md) give the reasons for the design. The [hook events reference](claude-code-hooks-reference.md) gives the hook contract. Section 14 lists the known limits of this path.

`GET /state` (T1.46) reads the dashboard's state from outside. It is not a part of the path that this document describes. Section 5 lists it with the other endpoints.

---

## 1. The path in one picture

```
 Claude Code session
        │  (1) runs the hook in the background:
        │      cmd.exe /c post-status.cmd, with the event JSON on stdin
        ▼
 post-status.cmd                          in the data folder
        │  (2) reads listening.txt for the port and the token
        │      POST http://127.0.0.1:<port>/hook
        ▼
 Ingress (Kestrel)                        many request threads
        │  (3) checks the token, reads the body, answers 200 with an empty body
        │  (4) maps the body to an event and stamps the arrival time
        ▼
 Event channel                            1,024: noise shed after; 16,384: drop-oldest
        │  (5) one reader
        ▼
 Event consumer                           one thread, the only writer
        │  (6) SessionRegistry.Apply
        │
        ├──▶ Sound engine                 same thread: notice, nudge schedule
        ├──▶ Session projection ──▶ WPF dispatcher ──▶ window and tray
        ▼
 Archive channel                          1,024 records, drop-oldest
        │  (7) one reader
        ▼
 Archive writer ──▶ dashboard.db          one events row and its decisions rows,
                                          in one transaction
```

Three properties hold on the full path:

- **Claude Code starts every exchange.** The dashboard does not poll, and it does not ask a session for its state.
- **The dashboard cannot stop or change a turn.** The hook runs in the background. The script always exits 0 and prints nothing. Ingress always answers `200` with an empty body.
- **One thread changes the world model.** All producers write to one channel, and one consumer reads it.

---

## 2. What must be in place

| Item | Location | Written by | When |
|---|---|---|---|
| The plugin `claude-dashboard`: three files, with one hook entry for each of 8 events | The data folder, in `plugin\` | The dashboard | At every start, if a file is different from the text in the build |
| The registration of the plugin | Claude Code's own settings | **Claude Code**, when the dashboard runs `claude plugin marketplace add` and `claude plugin install` | At a start that finds the plugin absent, or on `--install-hooks` |
| `post-status.cmd` | The data folder | The dashboard | At every start, if the file is different from the text in the build |
| `listening.txt` | The data folder | The dashboard | After the socket is bound and the script is written. Holds the port and the token. Deleted on exit |
| `port.txt` | The data folder | The dashboard | After the socket is bound. Never deleted |

The operator sets nothing. The dashboard makes a new token at every start (section 2.3).

**The dashboard never writes Claude Code's settings file.** It reads that file to learn if the plugin is enabled.

The data folder is `%LocalAppData%\ClaudeDashboard`. The variable `CLAUDE_DASHBOARD_HOME` moves it. The variable `CLAUDE_CONFIG_DIR` moves Claude Code's settings, and the dashboard obeys it.

### 2.1 The hook entry

The plugin's file `plugin\hooks\hooks.json` has one entry of this shape for each of the 8 events:

```json
{
  "hooks": {
    "Stop": [
      { "hooks": [ { "type": "command",
        "command": "C:\\Windows\\System32\\cmd.exe",
        "args": ["/c", "C:\\Users\\<user>\\AppData\\Local\\ClaudeDashboard\\post-status.cmd"],
        "async": true } ] }
    ]
  }
}
```

- **`command` with `args`** starts `cmd.exe` directly. No shell runs, so the two paths are absolute. The dashboard resolves them when it writes the file.
- **`async: true`** runs the hook in the background. A turn does not wait for it.
- **No port and no URL.** The entry names a script. The script finds the port when it runs. Thus the entry stays correct when the port moves and when the dashboard is closed.
- **The plugin is a pointer.** It names `post-status.cmd` by its absolute path in the data folder. Claude Code loads the plugin from the data folder in place.

### 2.2 When the dashboard registers the plugin

At each start, the dashboard reads Claude Code's settings. It asks Claude Code to register the plugin only if all of these are true:

1. Claude Code's configuration folder exists.
2. Claude Code's settings file is absent, or it can be read and parsed.
3. Those settings hold no hook from a build before the plugin. Both together would post each event twice.
4. The plugin is not enabled already, is not turned off, and is not held by a different data folder.
5. The dashboard's own settings file is absent, or it can be read. This start's first load decides: a file that did not parse is kept aside and a fresh one written (Impl §8.2), and that start still registers nothing.
6. `installHooksAtStart` is `true` in the dashboard's settings. This is the default.

To register, the dashboard runs the `claude` program two times: `claude plugin marketplace add <the plugin folder>` and `claude plugin install claude-dashboard@claude-dashboard`.

**In each case where the dashboard is left not connected, it shows a notice** in the window and in the tray tooltip, with what to do. Section 9.4 of the Implementation Specification has the full table.

A session that was open before the registration does not have the plugin. It reports after it restarts.

Only `--remove-hooks` removes the plugin. It also sets `installHooksAtStart` to `false`, so the next start does not put it back. Nothing is removed when the dashboard exits.

### 2.3 The two port files

| File | Meaning | Read by |
|---|---|---|
| `listening.txt` | A dashboard listens on this port now, and accepts this token. | `post-status.cmd`, and a second launch that sends `/show` |
| `port.txt` | This user last bound this port. | The next start, and a second launch that looks for the first |

`listening.txt` has two lines: the port, then the token. The token is 43 characters of base64url, made from 32 random bytes at every start and held in memory for the life of the process (T1.48). No Claude Code session holds a copy, so a restart of the dashboard never cuts a running session off.

The dashboard writes the script first and `listening.txt` second, so an old script never meets a dashboard that requires the token. It writes `listening.txt` to a temporary file and then renames it. Thus the script cannot read half of the file. The dashboard deletes `listening.txt` at four points: the usual exit, a Windows logoff, an unhandled fault that stops the process, and the `finally` block of `Main`.

### 2.4 How the dashboard chooses the port

The dashboard tries to bind each candidate. The first free port is used.

1. The port that the operator set as `port` in the dashboard's settings. If this port is in use, the dashboard does not try another.
2. The port in `port.txt`. If another program holds it, it is skipped like any taken candidate: the start goes on to the next one (the T1.57 review; until then the start stayed deaf here).
3. A port derived from the user's SID: `52789` plus an offset from 0 to 999. The offset comes from SHA-256.
4. The next ports above the derived port, 32 at most.

If no port is free, or the pinned port is taken, the dashboard starts and cannot receive events. It writes no `listening.txt`. The log, the tray tooltip and the window's notice row say what to do: free a port, or change the pin, then restart (T1.57).

---

## 3. Step 1: Claude Code runs the hook

The dashboard registers these 8 events:

`SessionStart` · `UserPromptSubmit` · `Notification` · `Stop` · `StopFailure` · `SessionEnd` · `CwdChanged` · `PostToolBatch`

When one of them occurs, Claude Code starts `cmd.exe /c post-status.cmd` and writes the event to its standard input as JSON. Each call starts two processes, `cmd.exe` and `curl.exe`. The hook events reference gives the measured cost: 97 ms with a dashboard that listens, and 65 ms without.

The list comes from one place, `HookEventNames.Accepted`. The plugin's hooks file and ingress both read it. Thus the dashboard cannot register an event that ingress refuses.

---

## 4. Step 2: The script forwards the payload

`post-status.cmd` does these steps in sequence:

1. If `listening.txt` is not in the script's folder, exit. The script opens no socket.
2. Read the first line of `listening.txt` as the port, and the second line as the token.
3. Convert the port to an integer. Exit if the integer is different from the text, or if it is not between 1 and 65535.
4. Exit if the token is not exactly 43 characters, each from `A–Z a–z 0–9 - _`. The check is pure `cmd`: two substring tests for the length, a `for /f` whose delimiters are the 64 characters, and a second `for /f` that refuses a bare line feed inside the value.
5. Send standard input, unchanged, with `%SystemRoot%\System32\curl.exe`: a `POST` to `http://127.0.0.1:<port>/hook` with the content type `application/json` and the header `X-Dashboard-Token`.
6. Exit 0.

The limits are 1 second to connect and 2 seconds in total.

Two rules make the script safe:

- **It prints nothing.** One redirect covers all of the script. On `UserPromptSubmit` and `SessionStart`, Claude Code adds a hook's standard output to the model's context. One line of output would change each prompt in each session.
- **It always exits 0.** Exit code 1 shows a hook error to the operator. Exit code 2 stops the turn.

The script ignores the HTTP status. If ingress refuses the post, the script still exits 0 and the session sees nothing.

---

## 5. Step 3: Ingress receives the post

Ingress is an ASP.NET Core minimal API on Kestrel, in the dashboard's process. It listens on the loopback interface only, so no other machine can post to it. It has four endpoints:

| Endpoint | Purpose | Token |
|---|---|---|
| `POST /hook` | Receives a hook event. | Always checked |
| `POST /show` | A second launch asks the first to show its window. | Always checked |
| `GET /state` | Reports what the dashboard believes now (T1.46). It is not a part of the event path. | Always checked |
| `GET /health` | Answers `{"status":"ok","instance":"<gate name>"}`. A start uses it to identify the owner of a port. | Never checked |

`POST /hook` does these steps on the request thread:

1. Compare the header `X-Dashboard-Token` with the token of this run.
2. Read the full body as text.
3. Parse the body into `HookPayload`. All fields are optional.
4. Map the payload to an event (Step 4).
5. Write the event to the event channel.
6. Answer `200` with an empty body.

Ingress does not touch the Registry.

| Condition | Answer | Log line |
|---|---|---|
| The token is wrong or absent | `401` | Warning |
| The body is not valid JSON | `200` | Warning, with the parser's message |
| The body is empty | `200` | Warning |
| The event name is not one of the 8 | `200` | Information |
| There is no `session_id` | `200` | Warning |
| The channel is closed, which occurs only at shutdown | `200` | Warning |
| Any other exception | `200` | Error |
| The event is accepted | `200` | None |

`401` is the only answer that is not `200`.

---

## 6. Step 4: The mapper makes an event

`HookEventMapper` changes the payload into an `InboundEvent`. This is the only place where a value from the wire becomes a value in the domain.

- **An allow-list decides.** The mapper accepts the 8 names and refuses all others. The internal events `Ack`, `SoundCommand` and `RostersChanged` cannot come from the wire. Thus a post cannot forge an acknowledgment.
- **The mapper stamps the time.** A hook payload has no timestamp. The mapper takes the time from the clock when the post arrives. Thus the sequence of events is the sequence of arrival.
- **The raw body travels with the event.** It is in `PayloadJson`, a type that cannot be printed. Only the archive's insert reads it.

The mapper reads these fields and no others:

| Event | Fields read |
|---|---|
| All events | `hook_event_name`, `session_id`, `prompt_id`, `transcript_path`, `cwd`, `session_title` |
| `SessionStart` | `source` |
| `UserPromptSubmit` | `prompt` |
| `Notification` | `notification_type` |
| `Stop` | `last_assistant_message`; `background_tasks` (`id`, `type`, `status`, `description` of each entry); `session_crons` (`prompt` of each entry) |
| `StopFailure` | `error`, then `error_type` |
| `SessionEnd` | `reason` |
| `CwdChanged`, `PostToolBatch` | None |

For `SessionStart`, `Notification`, `StopFailure` and `SessionEnd`, the mapper reads the field `matcher` if the named field is absent.

The dashboard does not interpret the fields that it does not read, but the archive keeps them, because it stores the body as it arrived. The `command` of a background task is never read: it can contain prompts or secrets.

---

## 7. Step 5: The channel and the consumer

**The event channel** holds 1,024 events before it sheds anything. A write never blocks. At 1,024 or more queued, it refuses only noise at the door: a `PostToolBatch`, and a `Notification` whose kind moves no state (`idle_prompt`, `agent_completed`, an unrecognised type). Every other event is still written, in order. Each shed event records an `EventDropped` decision with the reason `noise`. Only at 16,384 queued does it discard its oldest event, with the reason `pipeline`. The log has one Warning when shedding starts and one Information line when the queue is short again, and the window says "fell behind" or "events lost" (T1.58, issue #3). Until T1.58 a full channel discarded its oldest event, which could be a permission prompt.

**The event consumer** is the one reader. It is also the only thread that changes the Registry and the sound engine. Neither has a lock. For each wake-up, the consumer takes all the events that are available and handles them in sequence:

1. Open a decisions scope for the event.
2. Call `SessionRegistry.Apply`.
3. Record the result as decision rows.
4. Close the scope. This gives the event and its decisions to the archive as one record.

Step 4 is in a `finally` block. Thus an event is archived when the Registry declines it, and when `Apply` throws. An exception is logged, and the consumer continues.

The same loop runs **the tick** each 15 seconds. The tick does three tasks: the silence sweep, the nudge schedule, and the roster groups. It then publishes the dashboard's counts for `/state`'s `health` object, and at the first tick after each full UTC clock hour writes the hourly summary (T1.65). The loop also wakes when a roster group is due to settle. It uses no second timer, because a second thread would write to the Registry.

---

## 8. Step 6: The Registry decides the state

`SessionRegistry.Apply` does these checks in sequence:

1. An event with no session id is declined as `Ignored`.
2. An event for an unknown session creates the session, if the event can (see the table). If it cannot, it is declined as `Ignored`.
3. An event with a time before the session's last activity is declined as `Stale`.
4. The session's `LastHeardAt` takes the event's time. This occurs for all events but `Ack`, and also when the event is then declined. The silence sweep reads this value. When it moves a session to Interrupted, it also gives the value to the row's clock, so the row counts from the silence and not from the sweep (T1.47).
5. The transition table decides the new state.
6. A `session_title` that is not empty, and that is different from the session's title, replaces it. This occurs on any event, and also when the transition is declined.

| Event | A new session starts as | An existing session |
|---|---|---|
| `SessionStart` | Acked | The state does not change. The directory and the group change if `cwd` is different. An Ended session becomes Acked |
| `UserPromptSubmit` | Working | Becomes Working from any state. The exchange takes the new prompt. The list of work that the session waits on is cleared |
| `Notification`, `permission_prompt` | NeedsPermission | Becomes NeedsPermission |
| `Notification`, `agent_needs_input` | NeedsQuestion | Becomes NeedsQuestion |
| `Notification`, all other types | No session | No change |
| `Stop` | Unread, or Waiting | Becomes Unread and stores the answer. Becomes Waiting if the payload lists a `shell` or a `subagent` that runs |
| `StopFailure` | Error | Becomes Error with the error kind |
| `SessionEnd` | Ended | Becomes Ended |
| `CwdChanged` | Acked | The state does not change. The directory and the group change if `cwd` is different |
| `PostToolBatch` | No session | NeedsPermission, NeedsQuestion, Error and Interrupted become Working. Other states do not change |
| `Ack` (internal) | No session | Unread, NeedsPermission, NeedsQuestion and Error become Acked |

More rules for an existing session:

- **An Ended session** changes state only on `SessionStart` and `UserPromptSubmit`.
- **A `Stop` must agree with the turn.** If the `prompt_id` of the `Stop` and of the session's exchange are both present and different, the `Stop` is declined as `Uncorrelated`. The log gets a Warning, because this is the one decline that must not be frequent.
- **An event that changes nothing** is declined as `Duplicate`. It does not change `LastActivity`, so a second delivery cannot move a row.
- **A task notification continues the work.** A prompt that starts with `<task-notification>` keeps the text and the start time of the exchange.
- **A prompt that nobody typed is not an acknowledgment.** The session becomes Working, but the transition log and the decision row record it as a machine prompt.
- **A quiet tick puts the row back.** If a prompt is equal to the prompt of one of the session's scheduled jobs, the Registry keeps a snapshot of the row. If the reply is exactly `WATCHDOG-QUIET`, the `Stop` restores the snapshot. See [Quiet scheduled jobs](quiet-scheduled-jobs.md).
- **A session that waits goes back to Waiting.** If a `PostToolBatch` arrives while the session has work that it waits on, the session becomes Waiting and not Working.

The Registry does not read a clock. Each time that it writes comes from the event or from the caller. Nothing removes an Ended session from the Registry at this commit. The Registry is in memory only: after a restart, a session appears when its next event arrives.

---

## 9. Step 7: The screen, the sound and the archive

When an event changes a session, the Registry raises `SessionChanged` on the consumer thread. Three listeners receive it. The third, the board behind `GET /state` (T1.46), keeps a copy of the session for a reader outside the process and is not described further here.

**The sound engine** runs on the consumer thread. When a session enters a state that has a notice, the engine plays the notice or records the cause of its silence. NeedsPermission, NeedsQuestion, Error and Unread each have a notice. Waiting and Interrupted have none. The engine also starts, continues or cancels the nudge schedule of the session.

**The session projection** takes the session from the event arguments. The session is an immutable record, so it is safe to pass to a different thread. The projection posts it to the WPF dispatcher and returns. On the UI thread, the projection replaces that one session in its collection. The window and the tray read the collection. This is the only point where the background work touches the UI thread.

**The archive** gets one record for each event: the event and all its decisions. The archive channel holds 1,024 records and discards the oldest when it is full. One writer thread owns `dashboard.db`. For each record it writes, in one transaction:

- one row in `events`: `id`, `session_id`, `ts`, `event_type`, `payload_json`, `cwd`
- one row in `decisions` for each decision, with `event_id` set to the id of that `events` row

The consumer never waits for the disk. The decisions of a tick have no event, so their `event_id` is NULL.

**Every `ts` is UTC,** in one form: seven fractional digits and `Z`, for example `2026-10-02T12:03:11.1230000Z`. Rows written before T1.62 had the local offset; the store converts them once, the first time it opens the file, and sets `PRAGMA user_version` to 1 (T1.62).

**The run.** The same writer also writes one row in `runs` for each start of the dashboard, when the host has started: the time in UTC, the version, the port that ingress bound and the data folder. A clean stop sets `stopped_at` after the last records are written; a kill leaves it empty (T1.60).

---

## 10. Events that do not come from Claude Code

Three internal events use the same channel. Thus they arrive on the consumer thread, in sequence with the hook events.

| Event | Source | Effect |
|---|---|---|
| `Ack` | The **Ack** button of a row, and **Ack all** | The Registry moves the session to Acked. The archive stores the event with an empty payload |
| `SoundCommand` | The tray menu and the header's **Mute all**: mute, unmute, pause, resume | The consumer sends it to the sound engine, then refreshes the screen at once, so the labels change at the click (T1.47). The Registry does not see it. It gets decision rows and no `events` row |
| `RostersChanged` | An edit of a roster | It wakes the consumer, so the roster groups are read again immediately. It gets a decision row and no `events` row |

The tick is not an event. It causes changes that no hook causes:

- **The silence sweep.** A Working session that sent no event for 10 minutes becomes Interrupted. Claude Code sends nothing when the operator stops a turn, so silence is the only signal.
- **Nudges.** The sound engine plays a nudge that is due.
- **Roster groups.** A group settles 1.5 seconds after its last member stops, and the group plays one notice.
- **The counts.** The health snapshot for `/state`, and once an hour an Information line and an `HourlySummary` decision row with the counts since the previous summary (T1.65).

---

## 11. When a part fails

| Condition | Result |
|---|---|
| The dashboard is closed | There is no `listening.txt`, so the script exits and opens no socket. The events of that time are lost. The dashboard does not get them later |
| The dashboard was killed | `listening.txt` stays and names the old port and a token that nothing accepts now. The script posts to that port, and each post fails in the background. If a different program takes the port, it receives the payloads, which contain prompts. The next start writes the file again, with a new token |
| A different program has a port at start | The dashboard skips that port and binds the next free one (section 2.4). Only when no port is free, or the pinned port is taken, does it start without one: it writes no `listening.txt`, and the log, the tray and the window say what to do. Until the T1.57 review a program on the port in `port.txt` left the dashboard deaf although its choice had found a free port |
| The plugin is not registered, or is turned off | No event arrives. The next start registers it if the rules of section 2.2 permit. If not, the window and the tray show a notice with what to do |
| An old hook from a build before the plugin is in Claude Code's settings | Events arrive through the old hook. The dashboard registers no plugin beside it, and shows a notice that asks the operator to remove the hook |
| A hook reads `listening.txt` just before a restart replaces it | That one post carries the old token and gets `401`. The next hook reads the new file. One refusal shows nothing; 3 within 10 minutes show "messages refused" until 10 minutes after the last. Refusals are `HookRefused` rows: at most one row a second, with the count (T1.61) |
| The script, `curl.exe`, the token or the port fails | The self-test at each start runs the script and finds it: the window and the tray say messages from Claude Code cannot arrive, with the cause. The Settings window's Test connection button runs the same test (T1.61) |
| Claude Code stops sending | Nothing alarms: a quiet afternoon is not a fault. The tooltip's last item says when the dashboard last heard from Claude Code |
| The script cannot be rewritten at start | The dashboard tries three times, then writes one Error line. An old script sends no token, so its hooks get `401` until the next start |
| The event channel is full | Only noise is shed: tool batches, and notifications that move no state. The window says "fell behind" for 5 minutes after the last shed. At the hard limit of 16,384 the oldest event is discarded and the window says "events lost" until the next start. The decisions table records each, and the log the start and the end (T1.58) |
| The disk is slow | The archive channel fills and discards its oldest records. The count goes into the log at shutdown. The window and the sound continue |
| `dashboard.db` cannot be opened or written | The store writes one Warning when it fails (not for a failed retry), and the window and the tray say "history not recorded". It tries again each minute; the events of that minute are lost. The write that succeeds clears the notice and writes one line with the count lost (T1.54, issue #71) |
| Windows has no sound output | Each sound is dropped and recorded as `SoundDropped`, not as played. The window and the tray say "no sound device", and the notice clears at the tick after a device returns. Nothing is replayed (T1.55, issue #72) |
| Claude Code sends an event type or a field that the dashboard does not know | The event changes no state. The archive keeps the payload |
| Claude Code is not installed | The start registers nothing and creates nothing. It writes one Information line and shows the notice "no Claude Code install detected" |

---

## 12. How to see the path work

**The log.** The file is `%LocalAppData%\ClaudeDashboard\logs\dashboard-<date>.log`. At each start it shows the port and its source, and the announcement in `listening.txt`. It shows a line about the plugin only if the plugin was absent, was turned off, was registered at this start, or could not be checked. The file keeps lines at the level `logging.minimumLevel` sets, Information by default. Set it to `Debug` to see each decision as it is made, and each event the Registry declined. The decisions table holds the same record, and is the one to query. Once an hour the log has a line `Hourly summary, … applied=… declined=… …`, and the decisions table an `HourlySummary` row with the same counts; `/state`'s `health.counts` has them at any moment (T1.65).

**The self-test.** At each start the log has `The test message from the hook script arrived in <n> ms`: the cost of one message on this machine, measured. The Settings window's **Test connection** button runs it again and shows the result beside it. `/state` has `health.lastHeardAt` and `health.selfTest` (T1.61).

**The database.** Copy `dashboard.db` and its `-wal` file, then query the copy. This query shows the last events of one session and the decision that each caused:

```sql
SELECT e.id, datetime(e.ts, 'localtime') AS local_time, e.event_type, d.kind, d.from_state, d.to_state, d.reason
FROM events e LEFT JOIN decisions d ON d.event_id = e.id
WHERE e.session_id = $session
ORDER BY e.id DESC LIMIT 40;
```

The times are stored in UTC; `datetime(e.ts, 'localtime')` shows them in local time. A range on `ts` takes UTC text in the same form. An event that the Registry declines has an `EventDeclined` row with the outcome as the reason. Part 4 of the Implementation Specification has the query for a sound.

**One test event.** This command posts one event to the dashboard that runs:

```powershell
$port, $token = Get-Content "$env:LOCALAPPDATA\ClaudeDashboard\listening.txt"
$body = '{"hook_event_name":"SessionStart","session_id":"event-flow-test","cwd":"C:\\Temp"}'
Invoke-WebRequest "http://127.0.0.1:$port/hook" -Method Post -ContentType 'application/json' -Body $body -Headers @{ 'X-Dashboard-Token' = $token }
```

A new quiet row appears for the session `event-flow-test`. The row stays until the dashboard starts again. The token changes at every start, so read the file again after a restart.

**Many events.** `tools\replay-hooks.ps1 -Port <port>` posts a recorded set of events for many sessions. Point it at a second dashboard that uses a different `CLAUDE_DASHBOARD_HOME`, so that your own rows do not change.

---

## 13. What Claude Code sends, as measured

Measured on a copy of the operator's archive on 2026-09-29. The events are from 2026-08-27 to 2026-09-29. The archive held 21,407 hook events at that time. The measurement read field names, and the values of these fields only: `source`, `notification_type`, `error`, `reason`, `prompt_id` and the three directory fields. It read no prompt and no answer.

| Event | Count | Fields on the wire, apart from the common fields |
|---|---|---|
| `PostToolBatch` | 12,470 | `tool_calls`, `permission_mode`, `effort` |
| `UserPromptSubmit` | 2,565 | `prompt`, `permission_mode`, `session_title` |
| `CwdChanged` | 2,392 | `old_cwd`, `new_cwd` |
| `Stop` | 2,335 | `last_assistant_message`, `background_tasks`, `session_crons`, `stop_hook_active`, `permission_mode`, `effort` |
| `Notification` | 1,481 | `notification_type`, `message` |
| `SessionStart` | 86 | `source`, `session_title`, `model`, `context_tokens`, `seconds_since_last_response`, `prompt_cache_likely_expired`, `estimated_cache_write_usd` |
| `SessionEnd` | 60 | `reason` |
| `StopFailure` | 18 | `error`, `last_assistant_message`, `effort` |

The common fields on the wire are `session_id`, `hook_event_name`, `cwd`, `transcript_path` and `prompt_id`. Most events also have `scratchpad_dir`. In a subagent, `agent_id` and `agent_type` are added.

Four results are important:

- **`prompt_id` connects a `Stop` to its prompt.** 2,318 of 2,335 `Stop` events have the `prompt_id` of the last prompt of the session. 15 have a different one. 2 have no prompt before them.
- **`SessionStart` has `source` in the payload.** The values are `startup` 22, `resume` 22, `compact` 21 and `fork` 20.
- **`StopFailure` sends `error`, not the documented `error_type`.** All 18 events have `error`, and none has `error_type` or `matcher`. Until T1.53 the mapper read `error_type`, so the error kind of an Error row was always empty. T1.53 reads `error` first (issue #67).
- **`cwd` on `CwdChanged` is the directory of the session, and not always the new directory.** `cwd` is equal to `new_cwd` on 784 events, to `old_cwd` on 1,447, and to neither on 161. In the 1,447, the next event of the session has the old directory again, or a third one, and never the new one. These are changes of directory for one command. The dashboard reads `cwd` only, so it does not move the session for them.

---

## 14. Known limits of this path

These are true as of T1.53, which reads the error kind from `error` (issue #67). T1.52 removed the log-level limit (issue #68). The Technical Specification, Appendix C, lists what is specified and not built.

| Limit | Effect |
|---|---|
| Nothing removes an Ended session (section 8) | An Ended row stays until the dashboard starts again |
| The Registry starts empty (section 8) | After a restart, a session shows again at its next event. Nothing is read from `dashboard.db` at start |
| A hard stop leaves `listening.txt` (section 11) | Until the next start, each hook posts to the old port |
| An unknown `Notification` type changes no state and writes no log line (issue #9) | Only the archive shows that it arrived |

Until 2026-10-02 this section listed the places where the other documents disagreed with the code. Those documents are corrected.

---

## 15. Source map

| Step | File |
|---|---|
| The plugin: its files, its registration and its removal | `src/ClaudeDashboard.App/Setup/HookPlugin.cs`, `HookHandlers.cs`, `PluginInstaller.cs`, `ClaudeCli.cs`, `StartupHookInstall.cs`, `HookSwitches.cs` |
| The read of Claude Code's settings, and the notice | `src/ClaudeDashboard.App/Setup/HookCheck.cs`, `HookNotice.cs`; `src/ClaudeDashboard.App/Configuration/ClaudeCodePaths.cs` |
| The script | `src/ClaudeDashboard.App/Setup/HookScript.cs` |
| The port files | `src/ClaudeDashboard.App/Configuration/ListeningFile.cs`, `PortFile.cs`; `src/ClaudeDashboard.App/Hosting/IngressAnnouncement.cs` |
| The port choice | `src/ClaudeDashboard.App/Hosting/PortSelection.cs`, `HealthProbe.cs` |
| The start sequence | `src/ClaudeDashboard.App/Program.cs`, `src/ClaudeDashboard.App/Hosting/AppHost.cs` |
| Ingress | `src/ClaudeDashboard.App/Ingress/IngressEndpoints.cs`, `IngressToken.cs` |
| The payload and the mapper | `src/ClaudeDashboard.App/Ingress/HookPayload.cs`, `HookEventMapper.cs`, `HookEventNames.cs`, `BackgroundTaskReader.cs`, `SessionCronReader.cs` |
| The events | `src/ClaudeDashboard.Core/Events/InboundEvent.cs`, `Variants.cs`, `Matchers.cs`, `PayloadJson.cs` |
| The channel and the consumer | `src/ClaudeDashboard.App/Pipeline/EventPipeline.cs`, `EventConsumer.cs` |
| The Registry | `src/ClaudeDashboard.Core/SessionRegistry.cs`, `SessionState.cs`, `ApplyOutcome.cs`, `Acknowledgment.cs`, `QuietTicks.cs` |
| The sound | `src/ClaudeDashboard.Core/SoundPolicyEngine.cs`; `src/ClaudeDashboard.App/Adapters/NAudioSoundPlayer.cs`, `ISoundOutput.cs`, `SoundDeviceNotice.cs` |
| The screen | `src/ClaudeDashboard.App/Ui/SessionProjection.cs`, `AckPublisher.cs`, `TrayViewModel.cs`, `NoticeBoard.cs` |
| The archive and the decisions | `src/ClaudeDashboard.App/Pipeline/DecisionRecorder.cs`; `src/ClaudeDashboard.App/Storage/EventArchive.cs`, `EventArchiveWriter.cs`, `SqliteEventStore.cs`, `Decisions.cs`, `HistoryNotice.cs` |
| The tests of the path | `tests/ClaudeDashboard.Tests/Setup/HookScriptBehaviourTests.cs`, `tests/ClaudeDashboard.Tests/Ingress/`, `tests/ClaudeDashboard.Tests/Pipeline/` |
