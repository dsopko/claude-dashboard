<#
.SYNOPSIS
  Replays recorded Claude Code hook traffic at a dashboard's ingress (T1.20).

.DESCRIPTION
  The Phase 1 exit criteria ask for roughly fifteen real Claude Code terminals. This posts the
  same shapes at the same endpoint instead, because fifteen live sessions would spend the
  operator's usage allowance on a test.

  WHAT THIS DOES AND DOES NOT EVIDENCE.
  It exercises everything from the wire inward: Kestrel, the token check, the mapper, the
  channel, the consumer, the Registry, the projection and the view models. It does NOT prove
  that Claude Code delivers correctly from fifteen terminals — the hop from Claude Code to the
  socket is evidenced only at one or two concurrent sessions, from the operator's own dogfooding
  on 24-25 August. Every run of this script is a claim about the dashboard, never about the
  integration above it.

  THE SHAPES ARE DELIBERATELY NOT ALL ONES WE HANDLE.
  A replay built only from traffic the dashboard classifies is shaped to the thing it is meant
  to test, and would pass against a build that silently dropped everything else. So the scenario
  carries unclassified notification matchers, an event name ingress refuses, a session that ends
  without finishing, events that arrive out of timestamp order, and two events sharing an
  instant. What the dashboard does with each is recorded rather than asserted here — the point
  is to see it, not to bless it.

.PARAMETER Port
  The dashboard's ingress port. Never the operator's 52789 unless you mean it.

.PARAMETER Token
  The value for X-Dashboard-Token, when the target has one configured.

.PARAMETER Sessions
  How many concurrent sessions to simulate. The exit criteria say about fifteen.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int] $Port,
    [string] $Token = $null,
    [int] $Sessions = 15,
    [string] $ReportPath = $null
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 does not load this by default.
Add-Type -AssemblyName System.Net.Http
$url = "http://127.0.0.1:$Port/hook"
$results = [System.Collections.Generic.List[object]]::new()
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(10)

function Send-Hook {
    param([hashtable] $Payload, [string] $Label)

    $json = $Payload | ConvertTo-Json -Depth 6 -Compress
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $url)
    $req.Content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
    if ($Token) { [void]$req.Headers.Add('X-Dashboard-Token', $Token) }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = $client.SendAsync($req).GetAwaiter().GetResult()
        $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $results.Add([pscustomobject]@{
            Label = $Label; Event = $Payload.hook_event_name; Session = $Payload.session_id
            Status = [int]$resp.StatusCode; BodyLength = $body.Length; Ms = $sw.ElapsedMilliseconds
        })
    }
    catch {
        $results.Add([pscustomobject]@{
            Label = $Label; Event = $Payload.hook_event_name; Session = $Payload.session_id
            Status = -1; BodyLength = -1; Ms = $sw.ElapsedMilliseconds
        })
    }
    finally { $req.Dispose() }
}

function Stamp { param([int] $SecondsAgo = 0) (Get-Date).ToUniversalTime().AddSeconds(-$SecondsAgo).ToString('o') }

$cwds = @(
    'C:\dev\PennCustQuote', 'C:\dev\PennCustQuote', 'C:\dev\PennCustQuote',
    'C:\projects\Claude\claude-dashboard', 'C:\projects\Claude\claude-dashboard',
    'C:\dev\ledger', 'C:\dev\ledger', 'C:\work\intake', 'C:\work\intake',
    'C:\work\intake', 'C:\dev\spike', 'C:\dev\spike', 'C:\dev\reports',
    'C:\dev\reports', 'C:\dev\reports'
)

Write-Output "replaying $Sessions sessions at $url"

# ---- 1. Ordinary traffic ---------------------------------------------------------------------
# Every session starts, prompts, works, and reaches one of the ends a real session reaches.
for ($i = 0; $i -lt $Sessions; $i++) {
    $s = "replay-$i"
    $cwd = $cwds[$i % $cwds.Count]
    Send-Hook @{ hook_event_name = 'SessionStart'; session_id = $s; cwd = $cwd; source = 'startup'; timestamp = (Stamp 600) } 'start'
    Send-Hook @{ hook_event_name = 'UserPromptSubmit'; session_id = $s; cwd = $cwd; prompt_id = "p-$i-1"; prompt = "run the tests"; timestamp = (Stamp 590) } 'prompt'
    Send-Hook @{ hook_event_name = 'PostToolBatch'; session_id = $s; cwd = $cwd; prompt_id = "p-$i-1"; timestamp = (Stamp 585) } 'batch'
    Start-Sleep -Milliseconds (Get-Random -Minimum 5 -Maximum 40)
}

# ---- 2. The four states the dashboard exists to show ------------------------------------------
Send-Hook @{ hook_event_name = 'Notification'; session_id = 'replay-0'; cwd = $cwds[0]; notification_type = 'permission_prompt'; timestamp = (Stamp 500) } 'needs-permission'
Send-Hook @{ hook_event_name = 'Notification'; session_id = 'replay-1'; cwd = $cwds[1]; notification_type = 'agent_needs_input'; timestamp = (Stamp 495) } 'needs-question'
Send-Hook @{ hook_event_name = 'StopFailure'; session_id = 'replay-2'; cwd = $cwds[2]; prompt_id = 'p-2-1'; error_type = 'rate_limit'; timestamp = (Stamp 490) } 'error'
Send-Hook @{ hook_event_name = 'Stop'; session_id = 'replay-3'; cwd = $cwds[3]; prompt_id = 'p-3-1'; last_assistant_message = 'done'; timestamp = (Stamp 485) } 'unread'

# idle_prompt must change nothing (issue #1) — a session that finished must not turn red.
Send-Hook @{ hook_event_name = 'Notification'; session_id = 'replay-3'; cwd = $cwds[3]; notification_type = 'idle_prompt'; timestamp = (Stamp 480) } 'idle-after-finish'

# PostToolBatch resumes a blocked turn (issue #2) but must not disturb an unread one.
Send-Hook @{ hook_event_name = 'PostToolBatch'; session_id = 'replay-0'; cwd = $cwds[0]; prompt_id = 'p-0-1'; timestamp = (Stamp 470) } 'resume-after-permission'
Send-Hook @{ hook_event_name = 'PostToolBatch'; session_id = 'replay-3'; cwd = $cwds[3]; prompt_id = 'p-3-1'; timestamp = (Stamp 465) } 'batch-on-unread'

# ---- 3. A burst: every session posting at once ------------------------------------------------
# Concurrency at the socket, which sequential posting never exercises.
$burst = @()
for ($i = 0; $i -lt $Sessions; $i++) {
    $s = "replay-$i"; $cwd = $cwds[$i % $cwds.Count]
    $json = (@{ hook_event_name = 'PostToolBatch'; session_id = $s; cwd = $cwd; prompt_id = "p-$i-1"; timestamp = (Stamp 400) } | ConvertTo-Json -Compress)
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $url)
    $req.Content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
    if ($Token) { [void]$req.Headers.Add('X-Dashboard-Token', $Token) }
    $burst += $client.SendAsync($req)
}
[System.Threading.Tasks.Task]::WaitAll($burst)
$burstStatuses = $burst | ForEach-Object { [int]$_.Result.StatusCode }
Write-Output "burst of $($burst.Count) concurrent posts -> statuses $(($burstStatuses | Sort-Object -Unique) -join ',')"

# ---- 4. Shapes the dashboard does NOT claim to handle ------------------------------------------
# Recorded, not asserted. A replay made only of handled traffic is shaped to the detector.
$unclassified = @('permission_denied','tool_error','plan_ready','compact_started','usage_limit',
                  'subagent_blocked','mcp_disconnected','something_new_next_release')
foreach ($m in $unclassified) {
    Send-Hook @{ hook_event_name = 'Notification'; session_id = 'replay-4'; cwd = $cwds[4]; notification_type = $m; timestamp = (Stamp 300) } "unclassified:$m"
}

Send-Hook @{ hook_event_name = 'SomeEventFromTheFuture'; session_id = 'replay-5'; cwd = $cwds[5]; timestamp = (Stamp 290) } 'unknown-event'
Send-Hook @{ hook_event_name = 'PermissionRequest'; session_id = 'replay-5'; cwd = $cwds[5]; timestamp = (Stamp 289) } 'refused-event'

# A session that ends without ever finishing its turn.
Send-Hook @{ hook_event_name = 'SessionEnd'; session_id = 'replay-6'; cwd = $cwds[6]; reason = 'prompt_input_exit'; timestamp = (Stamp 280) } 'end-without-stop'

# Out of order: a Stop stamped BEFORE the prompt it answers.
Send-Hook @{ hook_event_name = 'UserPromptSubmit'; session_id = 'replay-7'; cwd = $cwds[7]; prompt_id = 'p-7-2'; prompt = 'second'; timestamp = (Stamp 200) } 'ooo-prompt'
Send-Hook @{ hook_event_name = 'Stop'; session_id = 'replay-7'; cwd = $cwds[7]; prompt_id = 'p-7-2'; timestamp = (Stamp 260) } 'ooo-stale-stop'

# Two events sharing an instant.
$tie = Stamp 150
Send-Hook @{ hook_event_name = 'Notification'; session_id = 'replay-8'; cwd = $cwds[8]; notification_type = 'permission_prompt'; timestamp = $tie } 'tie-a'
Send-Hook @{ hook_event_name = 'Stop'; session_id = 'replay-8'; cwd = $cwds[8]; prompt_id = 'p-8-1'; timestamp = $tie } 'tie-b'

# Malformed body, and a missing session id — both must still answer 200 (Impl 3.3).
$req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $url)
$req.Content = [System.Net.Http.StringContent]::new('{ not json', [Text.Encoding]::UTF8, 'application/json')
if ($Token) { [void]$req.Headers.Add('X-Dashboard-Token', $Token) }
$r = $client.SendAsync($req).GetAwaiter().GetResult()
$results.Add([pscustomobject]@{ Label='malformed-json'; Event='(none)'; Session='(none)'; Status=[int]$r.StatusCode; BodyLength=$r.Content.ReadAsStringAsync().GetAwaiter().GetResult().Length; Ms=0 })
Send-Hook @{ hook_event_name = 'Stop'; cwd = 'C:\dev\nowhere'; timestamp = (Stamp 100) } 'no-session-id'

# ---- 5. Report ---------------------------------------------------------------------------------
$client.Dispose()

$total = $results.Count
$nonOk = @($results | Where-Object { $_.Status -ne 200 })
$bodies = @($results | Where-Object { $_.BodyLength -gt 0 })

Write-Output ""
Write-Output "posts            = $total"
Write-Output "non-200          = $($nonOk.Count)"
Write-Output "non-empty bodies = $($bodies.Count)   (Impl 3.3 requires an empty body on every path)"
Write-Output "slowest post     = $(($results | Measure-Object -Property Ms -Maximum).Maximum) ms"
if ($nonOk.Count -gt 0) { $nonOk | Format-Table -AutoSize | Out-String | Write-Output }

if ($ReportPath) {
    $results | Export-Csv -NoTypeInformation -Path $ReportPath
    Write-Output "detail written to $ReportPath"
}
