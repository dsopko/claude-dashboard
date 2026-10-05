# Claude Code hook events — reference

**Source:** <https://code.claude.com/docs/en/hooks> (canonical; `docs.claude.com/en/docs/claude-code/hooks` 301s here)
**Transcribed:** 2026-08-24 · **Events documented: 33** (was 31; `PreModelSwitch` and `PostModelSwitch` added 2026-08-31, issue #28) · **Consumed by the dashboard: 8** · **Command hook mechanics added 2026-08-30 (issue #29)**

**Wire facts brought up to date 2026-10-02**, from the measurement of the operator's archive on 2026-09-29: 21,407 hook events from 2026-08-27 to 2026-09-29. The [event flow](claude-dashboard-event-flow.md) §13 has the full table.

This document exists because "I don't know whether there is a hook for that" is not an acceptable answer in a project whose entire input surface *is* the hook contract. Everything below is transcribed from the source page on the date above, not recalled. **It is a snapshot: re-fetch and re-check before relying on it for a new integration.**

**The documentation and the wire disagree in places.** Where they do, the wire is what the dashboard must read. Each such place is marked **[wire]** below, and [Discrepancies](#discrepancies-documentation-versus-what-we-observe) lists them together.

Field names are quoted exactly as documented. Where our own observations disagree with the documentation, both are recorded — see [Discrepancies](#discrepancies-documentation-versus-what-we-observe), which is the most useful section in this file.

**Reading the annotations:**

| | meaning |
|---|---|
| **✅ USED** | the dashboard registers this hook and maps it to a state or action today |
| **⭐ CANDIDATE** | not used, but bears on a known open problem — see the note |
| ○ | not used, no current relevance |
| **[verified]** | we have observed this in production against live Claude Code |
| **[documented]** | from the source page only; not yet observed by us |

---

## Common input fields — present on every event

| Field | Notes |
|---|---|
| `session_id` | The session's identity. **The dashboard's primary key.** |
| `prompt_id` | UUID identifying the user prompt. **Absent until first user input.** |
| `transcript_path` | Path to the conversation JSON. Written asynchronously — fallback only, never the live read. |
| `cwd` | Working directory. **The dashboard's Phase 1 grouping key.** |
| `permission_mode` | `default` · `plan` · `acceptEdits` · `auto` · `dontAsk` · `bypassPermissions` |
| `effort` | Object with `level`: `low` · `medium` · `high` · `xhigh` · `max`. Present in tool-use contexts. |
| `hook_event_name` | Which event fired. **The dashboard's dispatch key.** |
| `agent_id` | Subagent contexts only. |
| `agent_type` | Subagent contexts, or `--agent`. |

`prompt_id` being common to every event is what T1.2's correlation guard assumes — that a `Stop` carries the `prompt_id` of the prompt it is answering. **Confirmed on the wire [wire]:** 2,318 of 2,335 archived `Stop` events carry the `prompt_id` of the session's last prompt. 15 carry a different one, and 2 have no prompt before them.

Most events also carry `scratchpad_dir`, which the documentation does not list. The dashboard does not read it.

`permission_mode` and `effort` are not consumed today and are worth remembering: a session in `plan` or `bypassPermissions` behaves differently enough that the operator might want to see it.

---

# Part 1 — Events the dashboard consumes

Eight of thirty-three. This is the whole integration surface.

### ✅ `SessionStart`

**Fires:** a session begins or resumes.
**Matchers:** `startup` · `resume` · `clear` · `compact` · `fork`
**Documented fields:** `model` *(optional, not guaranteed)*
**Blocking:** no — exit 2 shows stderr to the user only.

> **Dashboard:** create or refresh a Registry entry. An Ended session becomes quiet again. A known session keeps its state.
> **We read `source` and `session_title`, neither of which the source page lists as JSON fields. Both are on the wire [wire].** 86 archived events: `source` is `startup` 22, `resume` 22, `compact` 21, `fork` 20. The wire also carries `model`, `context_tokens`, `seconds_since_last_response`, `prompt_cache_likely_expired` and `estimated_cache_write_usd`, which the dashboard does not read.

### ✅ `UserPromptSubmit`

**Fires:** you submit a prompt, before Claude processes it.
**Matchers:** none — always fires.
**Documented fields:** `user_input` — the prompt text.
**Blocking:** **yes** — exit 2 blocks the prompt and erases it.

> **Dashboard:** → **Working**; store the prompt as the session's context line. A prompt that a person typed is also an acknowledgment of any Unread, Needs-You or Error state. A prompt that Claude Code submitted by itself is not (TS §IV.1).
> **We read `prompt`, not `user_input` [wire].** The 2,565 archived events carry `prompt`, and `user_input` is not on the wire. The wire also carries `permission_mode` and `session_title`.
> Note this hook *can* block a prompt. The dashboard must never use that power (pure-observer, Impl §3.3), and returning `200` with an empty body is what guarantees it does not.

### ✅ `Notification`

**Fires:** Claude Code raises a notification.
**Matchers — all twelve:** `permission_prompt` · `idle_prompt` · `auth_success` · `elicitation_dialog` · `elicitation_url_dialog` · `elicitation_complete` · `elicitation_response` · `agent_needs_input` · `agent_completed` · `quota_auto_resume_fired` · `quota_auto_resume_stale` · `quota_auto_resume_disabled`
**Documented fields:** `notification_type` · `notification_text`
**Blocking:** no — exit code and stderr ignored.

> **Dashboard:** `permission_prompt` → **NeedsPermission** [verified]. `agent_needs_input` → **NeedsQuestion**. `idle_prompt` → **nothing** (issue #1 — it was mapped to NeedsQuestion and turned every finished session red). `agent_completed` → nothing.
> **We knew four of twelve matcher values.** The other eight parse as `Unknown` and change no state, which is safe — but it was safe by luck rather than by knowledge. `quota_auto_resume_*` in particular describes a session waiting on a quota reset, which is arguably an operator-relevant state the dashboard has no way to show.
> **The documented text field is `notification_text`. The wire sends `message` [wire].** The dashboard reads neither. It is the human-readable message — plausibly the best thing to put on a Needs-You row, since it is what Claude is actually saying. 1,481 archived events.

### ✅ `Stop`

**Fires:** Claude finishes responding.
**Matchers:** none — always fires.
**Documented fields:** `last_assistant_message` — the final assistant text of the turn.
**Blocking:** **yes** — exit 2 prevents Claude stopping and continues the conversation.

> **Dashboard:** → **Unread**, or **Waiting** when background work is still running (below); store the answer. `last_assistant_message` arriving inline is what lets an expanded row show the answer beside the question without reading the transcript [verified].
> Another hook with blocking power the dashboard must never exercise.

**Undocumented fields, observed on the wire** (the operator's archive, measured 2026-09-27: both present on 2,149 of 2,150 archived `Stop`s):

- **`background_tasks`** — an array of the background work still running as the turn ended. Each entry is an object:
  - `id` (string) — stable across the `Stop`s that list the same task.
  - `type` (string) — `shell` (292 entries), `subagent` (10), `monitor` (25). Set by Claude Code, not the agent.
  - `status` (string) — `running` on every entry observed. A finished task drops off the list rather than changing status.
  - `description` (string) — what the agent says the task is. Agent-written text.
  - `command` (string) — **on `shell` entries only.** The command line itself. It can carry prompts or secrets: **the dashboard never reads it** (T1.24, T1.41).
  - `agent_type` (string) — on `subagent` entries only.
- **`session_crons`** — an array of the scheduled wake-ups the session has set. Empty on most `Stop`s; 292 entries across 3 sessions, first seen 2026-09-03. Each entry is an object:
  - `id` (string).
  - `schedule` (string) — when it fires.
  - `prompt` (string) — the prompt it will wake the session with. **Prompt text:** the dashboard reads it only to compare it (T1.44): it is held in memory for that and is never shown. The raw payload, like every payload, is archived verbatim.
  - `recurring` (boolean).

  #52 files it under case 4 (a cron that wakes the agent, then a false "finished"), not the Waiting state. T1.44 (issue #56) is that rule, below.

> **Dashboard (T1.41, issue #52):** a `Stop` whose `background_tasks` lists at least one running `shell` or `subagent` → **Waiting**, not Unread, with no sound. It is an allow-list: `monitor` and any unseen type change nothing, and an unseen type is recorded in the decisions record by count. Only `id`, `type`, `status` and `description` are read; a malformed list reads as empty.

> **Dashboard (T1.44, issue #56): the quiet-tick sentinel.** A `UserPromptSubmit` whose `prompt` **exactly equals** the `prompt` of an entry in `session_crons` on the same session's **previous** `Stop` is a **tick** of that scheduled job — identified by structure, never by keywords (203 of 203 watchdog ticks on the operator's archive). A tick is a machine prompt: it does not acknowledge anything. If the tick's own `Stop` has a `last_assistant_message` that, trimmed of surrounding whitespace, is **exactly** `WATCHDOG-QUIET`, the tick is **quiet**: no finished sound, no nudge, and the row goes back to what it showed before the tick. Any other reply beeps as today. A job opts in by ending its prompt with the line in [Quiet scheduled jobs](quiet-scheduled-jobs.md). Measured on 2,222 archived `Stop`s: no `last_assistant_message` had leading or trailing whitespace, and the one-word replies all ended in punctuation — so `WATCHDOG-QUIET.` is not quiet. Only each entry's `prompt` is read; a malformed list reads as none, and then no prompt is a tick.

### ✅ `StopFailure`

**Fires:** the turn ends due to an API error.
**Matchers — all ten:** `rate_limit` · `overloaded` · `authentication_failed` · `oauth_org_not_allowed` · `billing_error` · `invalid_request` · `model_not_found` · `server_error` · `max_output_tokens` · `unknown`
**Documented fields:** `error_type` · `error_message`
**Blocking:** no — output and exit code ignored (except `terminalSequence`).

> **Dashboard:** → **Error**; record the kind.
> **[wire]: the wire sends the kind in a field named `error`, not `error_type` (discrepancy 4).** All 18 archived events have `error`, and none has `error_type` or `matcher`. The dashboard reads `error`, then `error_type`, then `matcher` (T1.53, issue #67). Before T1.53 it read only `error_type` and `matcher`, so the kind of an Error row was always empty, though the state was correct. The archive holds three kinds: `rate_limit` 14 times, `server_error` 3 times, `authentication_failed` once (measured 2026-10-03). The wire also carries `last_assistant_message` and `effort`.
> **The dashboard knows four of the ten kinds by name**: the three in the archive and `overloaded`. The row shows any kind as it arrives. The full list is above. `max_output_tokens` and `billing_error` are notably different in kind from a rate limit — one is a turn that produced too much, the other needs a human with a credit card, and neither is fixed by waiting.
> **`error_message` is documented and we do not read it.** An Error row could show a reason where it shows a category.

### ✅ `SessionEnd`

**Fires:** a session terminates.
**Matchers:** `clear` · `resume` · `logout` · `prompt_input_exit` · `other`
**Documented fields:** `end_reason`
**Blocking:** no.

> **Dashboard:** → **Ended**. Removal of the row after a time is not built.
> **We read `reason`; the documented field is `end_reason`. The wire sends `reason` [wire].** 60 archived events, and none carries `end_reason`.

### ✅ `CwdChanged`

**Fires:** the working directory changes, e.g. Claude runs `cd`.
**Matchers:** none — fires on every change.
**Documented fields:** *(common fields only — the new directory arrives as `cwd`)*
**Blocking:** no.

> **Dashboard:** re-derive the session's **Group**, from `cwd`.
> **The wire also sends `old_cwd` and `new_cwd`, and `cwd` is not always the new directory [wire].** 2,392 archived events. `cwd` is equal to `new_cwd` on 784, to `old_cwd` on 1,447, and to neither on 161. In the 1,447, the session's next event has the old directory again: these are changes of directory for one command. The dashboard reads `cwd` only, so it does not move the session for them. That is the correct result.

### ✅ `PostToolBatch`

**Fires:** after a full batch of parallel tool calls resolves, **before the next model call**.
**Matchers:** none — always fires.
**Documented fields:** `tool_calls` (array of results) · `batch_id`
**Blocking:** **yes** — exit 2 stops the agentic loop before the next model call.

> **Dashboard:** the turn is running, so a session that was blocked, in error or silent returns to **Working** — or to **Waiting**, if it still waits on background work. A session that is already Working, and one that is Unread, do not change. The most frequent event by far: 12,470 of 21,407 archived events. This is the signal [issue #2](https://github.com/dsopko/claude-dashboard/issues/2) needed — *the agent is between model calls, therefore executing* — and it fires **once per batch rather than once per tool**, which answered the volume objection that made `PostToolUse` unattractive. It covers a resolved permission, a resolved question and an error that recovers on retry, which a permission-specific hook would not.
> **It carries blocking power and we never use it.** Ingress answers `200` with an empty body and the command hook exits 0 on every path, so nothing here can stop a turn (Impl §3.3).

---

# Part 2 — Candidates that bear on open problems

Not used today. Each one is here because it answers a question we are currently stuck on.

### ⭐ `PostToolUse`

**Fires:** after a tool call succeeds.
**Matchers:** tool names.
**Documented fields:** `tool_name` · `tool_input` · `tool_use_id` · `tool_output`
**Blocking:** no — exit 2 shows stderr to Claude.

> The obvious resumed-working signal, and the higher-volume one — every tool call, which in this project's own logs is far more traffic than every other hook combined. Prefer `PostToolBatch` unless per-tool granularity turns out to be needed.

### ⭐ `PermissionRequest`

**Fires:** a tool call needs a permission decision.
**Matchers:** tool names.
**Documented fields:** `tool_name` · `tool_input` · `tool_use_id` · `permission_level`
**Blocking:** no via exit code — **use a JSON `decision` object** (exit 2 is not honoured).

> **Not registered by the dashboard, and deliberately not consumed.** Production evidence from August 2026, when the operator's own settings still registered it: every `PermissionRequest` is followed ~6s later by a `Notification(permission_prompt)`, which is the path the dashboard uses. So this is corroboration, not the primary signal.
> It carries `tool_name` and `tool_input`, which `Notification` does not — **so a Needs-You row could say *what* permission is being asked for** rather than only that one is. That is a real product improvement, at the cost of correlating two events.
> **It can render a decision.** The dashboard must never do so.

### ⭐ `PermissionDenied`

**Fires:** auto mode denies a tool call, including denials with no classifier verdict.
**Matchers:** tool names.
**Documented fields:** `tool_name` · `tool_input` · `tool_use_id` · `denial_reason`
**Blocking:** no — `hookSpecificOutput.retry: true` tells the model it may retry.

> A session whose tool was auto-denied is in a state the dashboard cannot currently see. Whether that deserves surfacing is a product question, but it is the closest thing to a "permission was decided" event, and it only covers the *denied* branch — **there is no documented `PermissionGranted`.** That absence is itself the answer to "is there a hook for when I answer the question": no, not directly. Infer resumption from `PostToolBatch`.

### ⭐ `SubagentStart` / `SubagentStop`

**Fires:** a subagent is spawned / finishes.
**Matchers:** agent type — `general-purpose`, `Explore`, `Plan`, custom names, plugin-scoped like `^my-plugin:reviewer$`.
**Documented fields:** `agent_type` · `agent_id`; `SubagentStop` adds `last_assistant_message`.
**Blocking:** `SubagentStop` **yes** — exit 2 prevents the subagent stopping.

> Design §12 lists "subagents: roll up into the parent, or hide entirely?" as an open question. These are the events that would answer it, and `agent_id`/`agent_type` are already common fields, so a subagent's events are *already distinguishable* from its parent's in everything the dashboard receives today.

### ⭐ `TeammateIdle`

**Fires:** an agent-team teammate is about to go idle.
**Matchers:** none.
**Documented fields:** `teammate_name`
**Blocking:** **yes** — exit 2 prevents the teammate going idle.

> Directly relevant to how *this project is built* — director/coder/reviewer are exactly this. Not relevant to Phase 1 scope.

---

# Part 3 — The remaining events

Complete, for the avoidance of another "I don't know". None are consumed and none currently bear on a known problem.

| Event | Fires | Matchers | Documented fields | Can block? |
|---|---|---|---|---|
| `Setup` | `--init-only`, or `--init`/`--maintenance` in `-p` mode | `init` · `maintenance` | *(common only)* | no |
| `UserPromptExpansion` | a typed command expands into a prompt, before it reaches Claude | your skill/command names | `command_name` · `expanded_prompt` | **yes** — blocks the expansion |
| `PreToolUse` | before a tool call executes | tool names, incl. `mcp__memory__.*` | `tool_name` · `tool_input` · `tool_use_id` | **yes** — blocks the call |
| `PostToolUseFailure` | after a tool call fails | tool names | `tool_name` · `tool_input` · `tool_use_id` · `tool_error` | no |
| `MessageDisplay` | while assistant message text is displayed | none | `message_text` | no |
| `TaskCreated` | a task is being created via `TaskCreate` | none | `task_id` · `task_description` | **yes** — rolls back creation |
| `TaskCompleted` | a task is being marked completed | none | `task_id` · `completion_notes` | **yes** — prevents completion |
| `InstructionsLoaded` | a `CLAUDE.md` or `.claude/rules/*.md` loads into context | `session_start` · `nested_traversal` · `path_glob_match` · `include` · `compact` | `file_path` · `load_reason` | no |
| `ConfigChange` | a configuration file changes mid-session | `user_settings` · `project_settings` · `local_settings` · `policy_settings` · `skills` | `config_source` · `config_path` | **yes** — except `policy_settings` |
| `DirectoryAdded` | a directory is added mid-session | `slash_command` · `register_repo_root` | `directory_path` · `add_method` | no |
| `FileChanged` | a watched file changes on disk | literal filenames, e.g. `.envrc\|.env` — see note | `file_path` | no |
| `WorktreeCreate` | a worktree is being created | none | `worktree_path` | **yes** — any non-zero exit fails creation |
| `WorktreeRemove` | a worktree is being removed | none | `worktree_path` | no |
| `PreModelSwitch` | before a requested model switch is applied | canonical model names, e.g. `claude-opus-5` · `.*opus.*` | `to_model` · `from_model` | **yes** — blocks the switch |
| `PostModelSwitch` | after the session's model changes, including changes Claude Code makes itself when resuming | canonical model names, as above | `to_model` · `from_model` | no |
| `PreCompact` | before context compaction | `manual` · `auto` | `compaction_trigger` | **yes** — blocks compaction |
| `PostCompact` | after compaction completes | `manual` · `auto` | `compaction_trigger` · `tokens_removed` | no |
| `Elicitation` | an MCP server requests user input during a tool call | your MCP server names | `server_name` · `elicitation_prompt` · `elicitation_type` | **yes** — denies it |
| `ElicitationResult` | after a user responds to an elicitation, before it returns to the server | your MCP server names | `server_name` · `user_response` · `elicitation_id` | **yes** — response becomes decline |

**`FileChanged` matcher note:** matching is narrower than elsewhere — exact match on letters, digits, `_` and `|` only. Hyphens, spaces and commas keep it on the regex path, with `|` separating alternatives.

---

# There is no interrupt hook [verified]

**Re-checked against <https://code.claude.com/docs/en/hooks> on 2026-08-31, for issue #28.** The
question is whether anything at all fires when the operator presses Escape mid-turn. It does not,
and this section exists so nobody has to establish that a third time.

**Nothing fires on an interrupt.** There is no `Abort`, `Cancel`, `Interrupt`, `TurnEnd` or
`PostTurn` event in the documented set of 33.

**`Stop` does not say why it stopped.** Its only documented event-specific field is
`last_assistant_message`. There is no `stop_reason`, no `interrupted`, no `cancelled`. And the
archive shows `Stop` does not fire on an interrupt in any case.

**None of the twelve `Notification` matchers concerns interruption**, and the full list is recorded
here so the next reader can check rather than take it on trust:

`permission_prompt` · `idle_prompt` · `auth_success` · `elicitation_dialog` ·
`elicitation_url_dialog` · `elicitation_complete` · `elicitation_response` · `agent_needs_input` ·
`agent_completed` · `quota_auto_resume_fired` · `quota_auto_resume_stale` ·
`quota_auto_resume_disabled`

**The idle notification does not arrive either** [verified, from the archive]. A turn that ends
normally is followed about a minute later by a `Notification` carrying `idle_prompt`. Issue #28
shows that notification absent from both interrupted turns, including one idle for twelve minutes.
So the one event that might have served as a late signal is not available.

**What the dashboard does instead** is measure silence: a `Working` session with no event for ten
minutes stops reading as busy (`SessionState.Interrupted`, `SilenceWatch`). It detects *silence*,
not interruption, and the code says so wherever it can be read.

## `MessageDisplay` was considered for this and rejected [verified]

It looks like the answer. It fires while assistant message text is displayed, so a streaming turn
would emit a continuous heartbeat and the silence threshold could be seconds and confident instead
of ten minutes and hedged.

**Do not register it.** Since T1.28 the dashboard's hook is a *command* hook: every event runs
`cmd.exe /c post-status.cmd`, which starts two processes and costs about 60 ms — measured at 97 ms
with a dashboard listening and 65 ms without. `MessageDisplay` fires during streaming and carries a
10-second default timeout precisely because it is expected to be hot, so subscribing would spawn
**hundreds of processes per turn** on a machine the operator is working on.

The two designs interact, and neither task can see it alone: T1.28 made every event expensive, and
T1.29 and T1.30 are where somebody would reach for a cheap-looking heartbeat. **The cheap-looking
fix is the expensive one**, and it will look attractive again the moment this paragraph is deleted.

---

# Discrepancies: documentation versus what we observe

**The most valuable section in this file.** Each of these is a place where the source page and the wire disagree, and where a wrong guess is silent: a field that is read under the wrong name is empty, with no error.

All were measured on the operator's archive on 2026-09-29. **The wire is the authority. Do not "correct" the code to the documented name.**

| # | Event | The documentation says | The wire sends | The dashboard reads | Result |
|---|---|---|---|---|---|
| 1 | `UserPromptSubmit` | `user_input` | `prompt` | `prompt` | Correct |
| 2 | `SessionEnd` | `end_reason` | `reason` | `reason` | Correct |
| 3 | `SessionStart` | `model` only; the source is a matcher | `source`, `session_title`, `model` and four more | `source`, `session_title` | Correct |
| 4 | `StopFailure` | `error_type`, `error_message` | `error` | `error`, then `error_type`, then `matcher` | Correct since T1.53. Before it, the dashboard read `error_type` and the kind of an Error row was always empty (issue #67) |
| 5 | `Notification` | `notification_text` | `message` | Neither | The message is not shown |
| 6 | `CwdChanged` | Common fields only; `cwd` is the new directory | `old_cwd`, `new_cwd`; `cwd` is the session's directory | `cwd` | Correct: a change of directory for one command does not move the session |
| 7 | `Stop` | `last_assistant_message` | Also `background_tasks`, `session_crons` | All three | Correct. The two extra fields are not documented at all |
| 8 | Any event | No title field | `session_title`, on some events and never on `Stop` | `session_title`, on each event | Correct: the dashboard keeps the last title that it saw |

### What settled 1, 2 and 3

Until September 2026 these three were open, because `SessionStart` and `SessionEnd` had not fired in production and nobody had captured a payload. The archive now holds 86 `SessionStart` and 60 `SessionEnd` events. `prompt`, `reason` and `source` are on the wire. `user_input` and `end_reason` are not.

`session_title` used to be read on `SessionStart` only, which is why issue #18 found the title feature dead. The documentation does not say which events carry it, so the dashboard reads it wherever it appears.

### Matcher lists the dashboard knows in part

`Notification`: the dashboard knows **four of twelve** types by name. `StopFailure`: **four of ten**. An unknown value changes no state. That is safe, and it is not the same as known: `quota_auto_resume_*` describes a session that waits for a quota reset, which the dashboard cannot show. An unknown `Notification` type writes no log line (issue #9).

### Fields on the wire that could improve a row, and are not read

`message` on `Notification` (what Claude says), and — for candidates — `tool_name` and `tool_input` on `PermissionRequest`. Each would put *what occurs* on a row that shows only *that something occurs*. `error` on `StopFailure` was on this list until T1.53, which reads it.

---

# HTTP hook mechanics

Per-handler configuration beyond the common fields:

- **`url`** — where to POST. Required.
- **`headers`** — additional headers; values support `$VAR_NAME` and `${VAR_NAME}`.
- **`allowedEnvVars`** — env var names that may be interpolated into header values.
- Plus the global **`allowedHttpHookUrls`** allowlist and **`httpHookAllowedEnvVars`**, without which the hook does not run at all.

**Response handling — this is the contract the dashboard's ingress is built on:**

| Response | Effect |
|---|---|
| 2xx, JSON object body | parsed as JSON output — **can carry decisions** |
| **2xx, empty body** | **success, no output** |
| non-2xx, or connection failure | **non-blocking error; execution continues** |
| timeout | hook cancelled, no decision rendered |

Two things this confirms:

**The pure-observer design is exactly right.** `200` with an empty body is the documented way to say "I observed this and I am deciding nothing". Anything else — a JSON body in particular — would put the dashboard in a position to alter a turn, which Impl §3.3 forbids unconditionally.

**A dead dashboard cannot break a session** [verified]. Connection failure is explicitly a non-blocking error. Observed in production: with the app stopped, Claude Code prints `UserPromptSubmit hook error / connect ECONNREFUSED 127.0.0.1:52789` and continues normally. Noisy, harmless.

---

# Command hook mechanics

**The dashboard uses a command hook, not an HTTP one, since issue #29.** The section above stays because it documents what the ingress contract is built on, and why the HTTP hook was left.

**The handler reaches Claude Code in a plugin, since issue #30.** The dashboard keeps the plugin's `hooks.json` in its own data folder and asks Claude Code to register it. It never writes Claude Code's settings (Impl §9.3, §9.4).

Per-handler configuration:

- **`command`** — the executable to run. Required.
- **`args`** — an array of arguments. **Its presence is what selects the exec form**: with `args` given, the executable is spawned directly, "with no shell involved". Without it, `command` is a command line run under a shell.
- **`shell`** — which shell runs a `command` with no `args`. On Windows it defaults to `bash`, or to `powershell` when Git Bash is not installed. **This is why the dashboard always supplies `args`**: the default varies by machine, cannot be chosen by us, and bash and PowerShell disagree about backslash paths and quoting — so one settings block would behave differently on two operators' machines.
- **`async`** — run in the background. The turn does not wait for the hook.
- **`asyncRewake`** — act on an async hook's result. Deliberately unset by the dashboard: it exists to react to an exit code, and the dashboard's hook exits `0` on every path by design.
- **`timeout`** — as for HTTP hooks.

**Nothing expands an environment variable in `command` or `args`** [verified]. With no shell there is nothing to do the expanding, so `%SystemRoot%` and `%LOCALAPPDATA%` arrive as literal text. Both paths in our handler are resolved in C# at install time. Inside the `.cmd` file expansion works normally — that *is* a shell.

**The payload arrives on stdin** [verified], as the same JSON an HTTP hook receives as its POST body. `post-status.cmd` pipes stdin straight through to `curl --data-binary @-` and the body arrives byte for byte.

**Exit codes:**

| Exit | Effect |
|---|---|
| **0** | success |
| 1 | **non-blocking error**, reported to the operator |
| **2** | **BLOCKS the turn** — the dashboard must never do this |
| other | non-blocking error |

**Stdout is not always discarded, and this is the trap** [documented]. For **`UserPromptSubmit`** and **`SessionStart`** Claude Code adds a hook's stdout to the model's context, as if the operator had typed it. Both are events the dashboard registers. Every other event throws stdout away.

So a stray line from a hook — a `curl` progress meter, `The system cannot find the path specified.` — **silently alters every prompt in every session, and nothing in the transcript shows it**. It is not a crash, not an error, and not observable from inside the session. It is the reason `post-status.cmd` redirects both streams on the `call` that wraps its whole body rather than per line, and the reason `HookScriptBehaviourTests` asserts empty streams for five arranged failures.

A hook's JSON stdout carries decisions, which makes this the same rule as `200`-with-an-empty-body on the ingress side, at the other end of the wire.

## What we have confirmed on the wire

**The exec form is honoured** [verified] against **Claude Code 2.1.251**, 2026-08-30. An isolated dashboard on port 52889, a settings file carrying only the command handler, and one `claude -p` run: `SessionStart`, `UserPromptSubmit` and `Stop` arrived through `cmd.exe /c post-status.cmd` and were archived. Verified because `args` is documented rather than observed, and a Claude Code that ignored it would run `command` alone — the hook would do nothing, silently, which is indistinguishable from a quiet day.

**Cost per invocation on this machine** [verified], 2026-08-30, 10 runs each: **97 ms** with a dashboard listening and the payload delivered; **65 ms** with no `listening.txt`, which is the ordinary case whenever the dashboard is closed. Both are background work under `async: true`, so no turn waits for either.

**A stale announcement costs about 1.09 s per invocation on this machine** [verified]. A post to a *free* loopback port here spends the whole `--connect-timeout` rather than being refused — 0.34 s at `--connect-timeout 0.25`, so it is a timeout and not a refusal. That is not normal loopback behaviour and is probably a firewall dropping the SYN; on a machine that refuses fast the cost is near zero. It applies only between a hard kill and the next start.

**`SessionEnd` was not observed** in the `claude -p` run above. Not investigated, and not needed for T1.28 — recorded so nobody reads the three events as a complete list.

# `GET /state` is not a hook

The ingress host also serves `GET /state` (T1.46, issue #10). **Claude Code never calls it**, and no hook configuration names it. It is here so nobody mistakes it for part of the hook contract, and so its token rule sits beside the one for `/hook`.

- **It reports what the dashboard believes now:** one entry per session with its state, band, group, working directory, timestamps, error kind, title, waiting tasks and next nudge time, then the band counts, the session count and the tray roll-up. The decisions table records history; this is the present.
- **The token is required, and it travels in `listening.txt`** (T1.48, issue #57). The dashboard makes a new token at every start, 43 characters of base64url, and writes it as the second line of `listening.txt`, below the port, in one step. `post-status.cmd` reads both lines fresh at every event and always sends `X-Dashboard-Token`, so a Claude Code session never holds the token and keeps reporting through any number of dashboard restarts. A missing or wrong token gets `401` on `/hook`, `/show` and `/state`; `/health` answers without one. `CLAUDE_DASHBOARD_TOKEN` is retired: the script does not read it, and the dashboard logs once at start that it is set and ignored. The script sends nothing unless line 2 is exactly 43 characters of `A–Z a–z 0–9 - _`.
- **`nextNudgeAt` is the schedule, not what is audible.** A muted session still shows a time. `null` means either that no nudge is coming or that the session is a finished (Unread) member of a roster group, and the group owns the notice.
- **It never carries prompt or answer text**, and never a background task's command.
- It is read-only and changes nothing. `/hook` still answers `200` with an empty body and no decision field.

---

## Refreshing this document

Re-fetch <https://code.claude.com/docs/en/hooks> and diff against Part 1 and Part 2 first — those are the events whose contract we depend on. A new `Notification` matcher or a renamed field will not fail a build or redden a test; it will show up as a session in the wrong state, which is the hardest kind of bug this project has.
