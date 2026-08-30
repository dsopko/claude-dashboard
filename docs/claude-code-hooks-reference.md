# Claude Code hook events — reference

**Source:** <https://code.claude.com/docs/en/hooks> (canonical; `docs.claude.com/en/docs/claude-code/hooks` 301s here)
**Transcribed:** 2026-08-24 · **Events documented: 31** · **Consumed by the dashboard: 8** · **Command hook mechanics added 2026-08-30 (issue #29)**

This document exists because "I don't know whether there is a hook for that" is not an acceptable answer in a project whose entire input surface *is* the hook contract. Everything below is transcribed from the source page on the date above, not recalled. **It is a snapshot: re-fetch and re-check before relying on it for a new integration.**

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

`prompt_id` being common to every event is what T1.2's correlation guard assumes — that a `Stop` carries the `prompt_id` of the prompt it is answering. The documentation supports that reading (it is described as identifying the user prompt, not the event), but **we have not confirmed it on the wire.**

`permission_mode` and `effort` are not consumed today and are worth remembering: a session in `plan` or `bypassPermissions` behaves differently enough that the operator might want to see it.

---

# Part 1 — Events the dashboard consumes

Eight of thirty-one. This is the whole integration surface.

### ✅ `SessionStart`

**Fires:** a session begins or resumes.
**Matchers:** `startup` · `resume` · `clear` · `compact` · `fork`
**Documented fields:** `model` *(optional, not guaranteed)*
**Blocking:** no — exit 2 shows stderr to the user only.

> **Dashboard:** create or refresh a Registry entry. `resume`/`fork` surface a session that already existed elsewhere. See TS §I.2 — *surfacing* is distinct from *showing a row*.
> **We also read `source` and `session_title`, neither of which the source page lists as JSON fields** — see Discrepancies.

### ✅ `UserPromptSubmit`

**Fires:** you submit a prompt, before Claude processes it.
**Matchers:** none — always fires.
**Documented fields:** `user_input` — the prompt text.
**Blocking:** **yes** — exit 2 blocks the prompt and erases it.

> **Dashboard:** → **Working**; store the prompt as the session's context line; **auto-ack** any prior Unread or Needs-You state.
> **We read `prompt`, not `user_input`, and it works [verified].** See Discrepancies — this is the most important one in the file.
> Note this hook *can* block a prompt. The dashboard must never use that power (pure-observer, Impl §3.3), and returning `200` with an empty body is what guarantees it does not.

### ✅ `Notification`

**Fires:** Claude Code raises a notification.
**Matchers — all twelve:** `permission_prompt` · `idle_prompt` · `auth_success` · `elicitation_dialog` · `elicitation_url_dialog` · `elicitation_complete` · `elicitation_response` · `agent_needs_input` · `agent_completed` · `quota_auto_resume_fired` · `quota_auto_resume_stale` · `quota_auto_resume_disabled`
**Documented fields:** `notification_type` · `notification_text`
**Blocking:** no — exit code and stderr ignored.

> **Dashboard:** `permission_prompt` → **NeedsPermission** [verified]. `agent_needs_input` → **NeedsQuestion**. `idle_prompt` → **nothing** (issue #1 — it was mapped to NeedsQuestion and turned every finished session red). `agent_completed` → nothing.
> **We knew four of twelve matcher values.** The other eight parse as `Unknown` and change no state, which is safe — but it was safe by luck rather than by knowledge. `quota_auto_resume_*` in particular describes a session waiting on a quota reset, which is arguably an operator-relevant state the dashboard has no way to show.
> **`notification_text` is documented and we do not read it.** It is the human-readable message — plausibly the best thing to put on a Needs-You row, since it is what Claude is actually saying.

### ✅ `Stop`

**Fires:** Claude finishes responding.
**Matchers:** none — always fires.
**Documented fields:** `last_assistant_message` — the final assistant text of the turn.
**Blocking:** **yes** — exit 2 prevents Claude stopping and continues the conversation.

> **Dashboard:** → **Unread**; store the answer. `last_assistant_message` arriving inline is what lets an expanded row show the answer beside the question without reading the transcript [verified].
> Another hook with blocking power the dashboard must never exercise.

### ✅ `StopFailure`

**Fires:** the turn ends due to an API error.
**Matchers — all ten:** `rate_limit` · `overloaded` · `authentication_failed` · `oauth_org_not_allowed` · `billing_error` · `invalid_request` · `model_not_found` · `server_error` · `max_output_tokens` · `unknown`
**Documented fields:** `error_type` · `error_message`
**Blocking:** no — output and exit code ignored (except `terminalSequence`).

> **Dashboard:** → **Error**; record the kind.
> **Our specs named three of ten matchers** with a trailing "…". The full list is above. `max_output_tokens` and `billing_error` are notably different in kind from a rate limit — one is a turn that produced too much, the other needs a human with a credit card, and neither is fixed by waiting.
> **`error_message` is documented and we do not read it.** An Error row currently shows a category where it could show a reason.

### ✅ `SessionEnd`

**Fires:** a session terminates.
**Matchers:** `clear` · `resume` · `logout` · `prompt_input_exit` · `other`
**Documented fields:** `end_reason`
**Blocking:** no.

> **Dashboard:** → **Ended**; schedule removal.
> **We read `reason`; the documented field is `end_reason`** — see Discrepancies.

### ✅ `CwdChanged`

**Fires:** the working directory changes, e.g. Claude runs `cd`.
**Matchers:** none — fires on every change.
**Documented fields:** *(common fields only — the new directory arrives as `cwd`)*
**Blocking:** no.

> **Dashboard:** re-derive the session's **Group**. Accepted by ingress; not currently registered as a hook in the operator's `settings.json`, so it has never fired in production.

### ✅ `PostToolBatch`

**Fires:** after a full batch of parallel tool calls resolves, **before the next model call**.
**Matchers:** none — always fires.
**Documented fields:** `tool_calls` (array of results) · `batch_id`
**Blocking:** **yes** — exit 2 stops the agentic loop before the next model call.

> **Dashboard:** the turn is running, so the session returns to **Working**. This is the signal [issue #2](https://github.com/dsopko/claude-dashboard/issues/2) needed — *the agent is between model calls, therefore executing* — and it fires **once per batch rather than once per tool**, which answered the volume objection that made `PostToolUse` unattractive. It covers a resolved permission, a resolved question and an error that recovers on retry, which a permission-specific hook would not.
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

> **Registered in the operator's settings and deliberately not consumed.** Production evidence: every `PermissionRequest` is followed ~6s later by a `Notification(permission_prompt)`, which is the path the dashboard uses. So this is corroboration, not the primary signal.
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
| `PreCompact` | before context compaction | `manual` · `auto` | `compaction_trigger` | **yes** — blocks compaction |
| `PostCompact` | after compaction completes | `manual` · `auto` | `compaction_trigger` · `tokens_removed` | no |
| `Elicitation` | an MCP server requests user input during a tool call | your MCP server names | `server_name` · `elicitation_prompt` · `elicitation_type` | **yes** — denies it |
| `ElicitationResult` | after a user responds to an elicitation, before it returns to the server | your MCP server names | `server_name` · `user_response` · `elicitation_id` | **yes** — response becomes decline |

**`FileChanged` matcher note:** matching is narrower than elsewhere — exact match on letters, digits, `_` and `|` only. Hyphens, spaces and commas keep it on the regex path, with `|` separating alternatives.

---

# Discrepancies: documentation versus what we observe

**The most valuable section in this file.** Each of these is a place where our code and the source page disagree, and where a wrong guess is silent.

### 1. `UserPromptSubmit`: we read `prompt`, the docs say `user_input`

We map `[JsonPropertyName("prompt")]`. The source page documents the field as `user_input`.

**Our code works** — a prompt submitted at 22:46 rendered in the row as its context line [verified]. So either both fields are present on the wire, or the documentation names it differently from the payload. **Do not "fix" this to `user_input` on the strength of the docs**; that would break a working path. Settle it with a payload capture and then support whichever is real — or both.

This is the single best argument for capturing a real payload rather than reading either the code or the docs.

### 2. `SessionEnd`: we read `reason`, the docs say `end_reason`

Nothing has verified ours, because `SessionEnd` has never fired in production — it was only registered today. If `end_reason` is correct, our end reason is silently null. The dashboard still reaches **Ended** (the state comes from the event, not the field), so the failure is a missing detail rather than a missing transition. Cheap to fix, cheap to confirm.

### 3. `SessionStart`: `source` is unconfirmed, and `session_title` is undocumented

The page documents only `model` as a `SessionStart`-specific field, and gives `startup`/`resume`/`clear`/`compact`/`fork` as **matcher** values. We treat `source` as a payload field carrying that same information. Both may be true — matchers are commonly mirrored into the payload — but it is unconfirmed, and `session_title` appears nowhere in the documentation at all.

**Updated at T1.24.** `session_title` used to be read on the `SessionStart` arm alone, which is why issue #18 found the feature dead: `SessionStart` has never fired (issue #20). It is now a common field on every event. The reason is this discrepancy rather than a guess about which events carry it — the documentation says nothing, so nothing tells us the set, and reading it wherever it appears is the only shape that cannot be wrong about a set nobody has published. Measured on the live archive: 72 titles across 1,210 payloads, and `Stop` never carries one.

**`source` is the half still standing.** It is read on the `SessionStart` arm and only there, and `SessionStart` still never fires — so whether the matcher really is mirrored into the payload remains untested by anything running, and would stay untested even if it were wrong. The heading names both halves so that fixing one does not read as having closed the discrepancy.

### 4. Matcher lists we had truncated

`Notification`: we knew **four of twelve**. `StopFailure`: we listed **three of ten** with a trailing "…". Everything unknown falls to `Unknown` and changes no state, so nothing is broken — but "safe because unhandled" is not the same as "known", and one of the unknown twelve (`quota_auto_resume_*`) describes a real operator-relevant condition.

### 5. Documented fields we do not read

`notification_text` (the human-readable message), `error_message` (the reason behind an error category), and — for candidates — `tool_name`/`tool_input` on `PermissionRequest`. All three would put *what is happening* on a row that currently shows only *that something is happening*.

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

**The dashboard uses a command hook, not an HTTP one, since issue #29.** The section above stays because it documents what the ingress contract is built on and what the migration is moving away from.

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

---

## Refreshing this document

Re-fetch <https://code.claude.com/docs/en/hooks> and diff against Part 1 and Part 2 first — those are the events whose contract we depend on. A new `Notification` matcher or a renamed field will not fail a build or redden a test; it will show up as a session in the wrong state, which is the hardest kind of bug this project has.
