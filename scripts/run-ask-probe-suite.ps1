<#
.SYNOPSIS
    Runs the plan 011 T1 ask-probe matrix against the live upstream and prints the T1 verdict.

.DESCRIPTION
    Matrix (every run unpaced, with --trace and the "yes" reply):
      en x3, vi x1, cap 24 s x3, cap 28 s x1, cap 40 s x1 (over the 25 s cap: expected TRUNCATED or FAIL),
      raw control x1, observe-interrupt x1.
    Each run writes <LogDir>\NN-<kind>-<n>.log. Then `presenter-cli ask-probe-summary <LogDir>` prints the table
    (per-run (i)-(vi), TRUNCATED, provenance, latency, usage), the total usage, AnswerStartBudgetMs and the T1 verdict.
    The exit code is the summary's: 0 means T1 passes.

    Missing WAVs are generated first with scripts/make-ask-probe-wavs-tts.ps1 (gpt-audio-1.5).

.PARAMETER WavDir
    The TTS WAV directory (en\, vi\). Must be outside the repository.

.PARAMETER LogDir
    Where the logs go (default: a new timestamped directory under the system temp directory).

.PARAMETER Provider
    The live route for the probe: azure (default) or openai.

.PARAMETER DryRun
    Print the matrix commands without running them (no upstream time). If -LogDir already holds logs, the summary still
    runs over them, which validates the table and verdict offline.

.EXAMPLE
    pwsh -File scripts/run-ask-probe-suite.ps1 -WavDir $env:TEMP\ask-probe-wavs
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $WavDir,
    [string] $LogDir,
    [ValidateSet('azure', 'openai')] [string] $Provider = 'azure',
    [ValidateSet('azure', 'openai')] [string] $TtsProvider = 'azure',
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$WavDir = [System.IO.Path]::GetFullPath($WavDir)
if (-not $LogDir) {
    $LogDir = Join-Path ([System.IO.Path]::GetTempPath()) ("presenter-ai-ask-probe\suite-{0:yyyyMMdd-HHmmss}" -f (Get-Date))
}
$LogDir = [System.IO.Path]::GetFullPath($LogDir)

$en = Join-Path $WavDir 'en'
$vi = Join-Path $WavDir 'vi'
$needed = @('part1.wav', 'part2.wav', 'reply-yes.wav', 'absent.wav', 'cap-24-part1.wav', 'cap-28-part1.wav', 'cap-40-part1.wav' | ForEach-Object { Join-Path $en $_ }) +
    @('part1.wav', 'part2.wav', 'reply-yes.wav' | ForEach-Object { Join-Path $vi $_ })
$missing = @($needed | Where-Object { -not (Test-Path $_) })
if ($missing.Count -gt 0) {
    if ($DryRun) {
        Write-Host "dry run: $($missing.Count) WAVs missing; a real run would generate them with make-ask-probe-wavs-tts.ps1"
    }
    else {
        Write-Host "Generating $($missing.Count) missing WAVs with gpt-audio-1.5 ..."
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'make-ask-probe-wavs-tts.ps1') -OutDir $WavDir -Provider $TtsProvider
        if ($LASTEXITCODE -ne 0) { throw 'WAV generation failed' }
    }
}

function Probe([string] $Dir, [string] $Part1, [string[]] $Extra = @()) {
    @('ask-probe', '--provider', $Provider, '--part1', (Join-Path $Dir $Part1), '--part2', (Join-Path $Dir 'part2.wav'),
        '--reply', (Join-Path $Dir 'reply-yes.wav'), '--trace') + $Extra
}

$matrix = [ordered]@{
    '01-en-1'        = Probe $en 'part1.wav'
    '02-en-2'        = Probe $en 'part1.wav'
    '03-en-3'        = Probe $en 'part1.wav'
    '04-vi-1'        = Probe $vi 'part1.wav' @('--lang', 'vi')
    '05-cap24-1'     = Probe $en 'cap-24-part1.wav'
    '06-cap24-2'     = Probe $en 'cap-24-part1.wav'
    '07-cap24-3'     = Probe $en 'cap-24-part1.wav'
    '08-cap28-1'     = Probe $en 'cap-28-part1.wav'
    '09-cap40-1'     = Probe $en 'cap-40-part1.wav'
    '10-raw-1'       = Probe $en 'part1.wav' @('--variant', 'raw')
    '11-interrupt-1' = Probe $en 'part1.wav' @('--observe-interrupt', '--absent', (Join-Path $en 'absent.wav'))
}

Push-Location $repoRoot
try {
    dotnet build src/PresenterAi.Cli --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'presenter-cli build failed' }
    $cli = Join-Path $repoRoot 'src\PresenterAi.Cli\bin\Debug\net10.0\presenter-cli.exe'
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    Write-Host "Logs: $LogDir"

    foreach ($name in $matrix.Keys) {
        $arguments = $matrix[$name]
        $log = Join-Path $LogDir "$name.log"
        if ($DryRun) {
            Write-Host "dry run: presenter-cli $($arguments -join ' ') > $log"
            continue
        }

        $started = Get-Date
        Write-Host ("{0:HH:mm:ss} {1} ..." -f $started, $name) -NoNewline
        & $cli @arguments *>&1 | Out-File -Encoding utf8 $log
        Write-Host (" exit {0} in {1:0} s" -f $LASTEXITCODE, ((Get-Date) - $started).TotalSeconds)
    }

    if (@(Get-ChildItem $LogDir -Filter '*.log').Count -eq 0) {
        Write-Host 'dry run: no logs to summarise'
        exit 0
    }

    & $cli ask-probe-summary $LogDir
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
