# Generates the dashboard's four notification sounds as 16-bit mono WAV files.
#
# These are placeholders with a deliberate shape, not final assets. TS §IV.5 and Design §8
# describe a sound *language* — "finished" is a two-note "bee-boop", and permission, question
# and error each get their own distinct sound — but the corpus ships no audio. Simple shaped
# tones realise that language well enough to hear the policy working, and every one of them can
# be replaced by dropping a like-named .wav into the config directory's sounds folder without
# touching any code (Impl Part 8).
#
# Run from the repo root:  powershell -NoProfile -File tools/make-sounds.ps1

$ErrorActionPreference = 'Stop'

$rate = 44100
$out  = Join-Path $PSScriptRoot '..\src\ClaudeDashboard.App\Assets\sounds'
New-Item -ItemType Directory -Force -Path $out | Out-Null

function New-Tone {
    param(
        # Each note is @{ Hz = <double>; Ms = <int> }; a note with Hz = 0 is a rest.
        [object[]] $Notes,
        [string]   $Path
    )

    $samples = New-Object System.Collections.Generic.List[Int16]

    foreach ($note in $Notes) {
        $count = [int]($rate * $note.Ms / 1000)

        # A raised-cosine envelope over the whole note: no clicks at either end, which at these
        # durations is most of what separates "a beep" from "a tick".
        for ($i = 0; $i -lt $count; $i++) {
            if ($note.Hz -le 0) {
                $samples.Add([Int16]0)
                continue
            }

            $t   = $i / $rate
            $env = 0.5 - 0.5 * [Math]::Cos(2 * [Math]::PI * $i / [Math]::Max(1, $count - 1))
            $v   = [Math]::Sin(2 * [Math]::PI * $note.Hz * $t) * $env * 0.6

            $samples.Add([Int16][Math]::Round($v * 32767))
        }
    }

    $bytes = New-Object byte[] ($samples.Count * 2)
    for ($i = 0; $i -lt $samples.Count; $i++) {
        [BitConverter]::GetBytes($samples[$i]).CopyTo($bytes, $i * 2)
    }

    $stream = [System.IO.File]::Create($Path)
    $writer = New-Object System.IO.BinaryWriter($stream)

    try {
        $writer.Write([char[]]'RIFF')
        $writer.Write([int](36 + $bytes.Length))
        $writer.Write([char[]]'WAVE')
        $writer.Write([char[]]'fmt ')
        $writer.Write([int]16)                 # PCM chunk size
        $writer.Write([int16]1)                # PCM
        $writer.Write([int16]1)                # mono
        $writer.Write([int]$rate)
        $writer.Write([int]($rate * 2))        # byte rate
        $writer.Write([int16]2)                # block align
        $writer.Write([int16]16)               # bits per sample
        $writer.Write([char[]]'data')
        $writer.Write([int]$bytes.Length)
        $writer.Write($bytes)
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }

    Write-Host ("{0}  {1} samples" -f (Split-Path $Path -Leaf), $samples.Count)
}

# finished — the "bee-boop" of Design §8: up then down, friendly, over quickly.
New-Tone -Path (Join-Path $out 'finished.wav') -Notes @(
    @{ Hz = 880; Ms = 110 }, @{ Hz = 0; Ms = 20 }, @{ Hz = 660; Ms = 150 })

# permission — rising and unresolved: it is asking for something.
New-Tone -Path (Join-Path $out 'permission.wav') -Notes @(
    @{ Hz = 587; Ms = 110 }, @{ Hz = 0; Ms = 20 }, @{ Hz = 880; Ms = 190 })

# question — a lift at the end, the shape of a spoken question.
New-Tone -Path (Join-Path $out 'question.wav') -Notes @(
    @{ Hz = 740; Ms = 120 }, @{ Hz = 0; Ms = 20 }, @{ Hz = 988; Ms = 140 })

# error — low and falling; distinct from the other three at a glance and at a listen.
New-Tone -Path (Join-Path $out 'error.wav') -Notes @(
    @{ Hz = 440; Ms = 130 }, @{ Hz = 0; Ms = 20 }, @{ Hz = 330; Ms = 220 })
