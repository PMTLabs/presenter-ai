<#
.SYNOPSIS
    Generates the plan 011 T1 ask-probe WAVs (24 kHz, mono, PCM16) with Windows SAPI (System.Speech).

.DESCRIPTION
    Writes, into -OutDir (which must be outside the repository):

      en\part1.wav        "What did the programme change at the Da Nang office in its first year,"
      en\part2.wav        "and how much did the Hanoi expansion cost?"
      en\reply-yes.wav    "Yes." (the live check-in reply, --reply)
      en\absent.wav       a question the probe deck does not cover (--absent; starts a delegation for --observe-interrupt)
      en\part1-slow.wav   near-cap run: a slow, keyword-free spoken preamble followed by part 1, read slowly
      en\part2-slow.wav   near-cap run: part 2, read slowly (also part 2 of every sweep variant)
      en\sweep-<N>-part1.wav   cap sweep: the slow preamble then part 1, sized so that with part2-slow.wav and the
                          probe's 10 s gap the recorder keeps about N s (N = 20, 40, 60, 100; see -SweepSeconds)
      vi\part1.wav, vi\part2.wav, vi\reply-yes.wav, vi\absent.wav   only when a vi-VN voice is installed

    SAPI renders straight to 24 kHz mono 16-bit PCM, so ffmpeg is not needed. To use recordings of a real voice
    instead, convert them with: ffmpeg -i <in> -ar 24000 -ac 1 -c:a pcm_s16le <out.wav>

    Near-cap choice: the recorder squeezes every pause over 0.5 s, so padding with silence would not raise the kept
    length. The slow part 1 is therefore real speech: a neutral preamble (background the asker gives before the
    question, with none of the probe's keywords or facts) repeated until part1-slow + part2-slow reach
    -NearCapSeconds of audio, then the part-1 question itself. The part-1 keyword stays at the end of part 1 and the
    part-2 keyword in part 2, so the probe's order check (iv) and both-facts check (v) keep their meaning.
    The recorder drops the pauses SAPI puts between slow sentences (about 15% of the audio), so the default 128 s of
    speech keeps about 105-110 s (measured: 124.5 s of speech kept 106.5 s). The probe prints the kept length and flags the 120 s cap if it is reached; lower
    -NearCapSeconds if it is.

.PARAMETER OutDir
    Target directory (created if missing). Must not be inside the repository.

.PARAMETER Voice
    Optional English voice name (see the list this script prints). Defaults to the first installed en-* voice.

.PARAMETER SweepSeconds
    Kept lengths of the sweep variants (default 20, 40, 60, 100). Use with --part1 sweep-<N>-part1.wav --part2 part2-slow.wav.

.PARAMETER NearCapSeconds
    Target length of part1-slow + part2-slow in seconds before compression (default 128, which keeps about 105-110 s).

.EXAMPLE
    pwsh -File scripts/make-ask-probe-wavs.ps1 -OutDir $env:TEMP\ask-probe-wavs
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $OutDir,
    [string] $Voice,
    [ValidateRange(30, 150)] [int] $NearCapSeconds = 128,
    [int[]] $SweepSeconds = @(20, 40, 60, 100)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech

$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\') + '\'
$target = [System.IO.Path]::GetFullPath($OutDir).TrimEnd('\') + '\'
if ($target.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutDir '$OutDir' is inside the repository; write the WAVs to a scratch directory instead."
}

$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(
    24000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)

$probe = New-Object System.Speech.Synthesis.SpeechSynthesizer
$voices = @($probe.GetInstalledVoices() | Where-Object { $_.Enabled } | ForEach-Object { $_.VoiceInfo })
$probe.Dispose()
Write-Host 'Installed System.Speech voices:'
foreach ($v in $voices) { Write-Host ("  {0} ({1}, {2})" -f $v.Name, $v.Culture.Name, $v.Gender) }

function Get-WavSeconds([string] $Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $offset = 12
    while ($offset + 8 -le $bytes.Length) {
        $id = [System.Text.Encoding]::ASCII.GetString($bytes, $offset, 4)
        $size = [System.BitConverter]::ToInt32($bytes, $offset + 4)
        if ($id -eq 'data') { return [math]::Round($size / 48000.0, 2) }
        $offset += 8 + $size + ($size -band 1)
    }
    throw "no data chunk in $Path"
}

function Save-Speech([string] $Text, [string] $Path, [string] $VoiceName, [int] $Rate = 0) {
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $synth.SelectVoice($VoiceName)
        $synth.Rate = $Rate
        $synth.SetOutputToWaveFile($Path, $format)
        $synth.Speak($Text)
    }
    finally {
        $synth.SetOutputToNull()
        $synth.Dispose()
    }
    $seconds = Get-WavSeconds $Path
    Write-Host ("  {0,-16} {1,6:0.00} s" -f (Split-Path -Leaf $Path), $seconds)
    return $seconds
}

$texts = @{
    en = @{
        part1  = 'What did the programme change at the Da Nang office in its first year,'
        part2  = 'and how much did the Hanoi expansion cost?'
        reply  = 'Yes.'
        absent = "Who was the programme's external auditor last year?"
    }
    vi = @{
        part1  = 'Chương trình đã thay đổi gì ở văn phòng Đà Nẵng trong năm đầu tiên,'
        part2  = 'và việc mở rộng ở Hà Nội tốn bao nhiêu tiền?'
        reply  = 'Vâng.'
        absent = 'Ai là đơn vị kiểm toán độc lập của chương trình năm ngoái?'
    }
}
$preamble = @(
    'Before I ask, let me give you some background on why this matters to our team.',
    'We are preparing next year''s plan, and our managers want concrete examples from the regional offices.',
    'They asked me to bring back clear answers rather than general impressions, so I want to be precise about what changed and what it cost.'
)
$slowRate = -4

$english = if ($Voice) { $voices | Where-Object { $_.Name -eq $Voice } | Select-Object -First 1 } else { $voices | Where-Object { $_.Culture.Name -like 'en-*' } | Select-Object -First 1 }
if (-not $english) { throw "No English voice found. Pass -Voice with a name from the list above." }

$enDir = Join-Path $target 'en'
New-Item -ItemType Directory -Force -Path $enDir | Out-Null
Write-Host "English WAVs with '$($english.Name)' in $enDir"
foreach ($name in 'part1', 'part2', 'reply', 'absent') {
    $file = if ($name -eq 'reply') { 'reply-yes.wav' } else { "$name.wav" }
    [void](Save-Speech $texts.en[$name] (Join-Path $enDir $file) $english.Name)
}

# Near-cap and sweep: measure the slow question parts and each preamble sentence, then put preamble sentences
# (cycling) before part 1 until part 1 + part 2 reach the target length of speech. The question stays at the end, so a
# truncated burst shows as a missing question.
$slow1Question = Save-Speech $texts.en.part1 (Join-Path $enDir 'part1-slow.wav') $english.Name $slowRate
$slow2 = Save-Speech $texts.en.part2 (Join-Path $enDir 'part2-slow.wav') $english.Name $slowRate
$scratch = Join-Path $enDir 'preamble-measure.wav'
$sentenceSeconds = @($preamble | ForEach-Object { Save-Speech $_ $scratch $english.Name $slowRate })
Remove-Item $scratch

function New-PreambledPart1([double] $TargetSeconds, [string] $FileName) {
    $sentences = New-Object System.Collections.Generic.List[string]
    $estimate = $slow1Question + $slow2
    while ($estimate + $sentenceSeconds[$sentences.Count % $preamble.Count] / 2 -lt $TargetSeconds) {
        $estimate += $sentenceSeconds[$sentences.Count % $preamble.Count]
        $sentences.Add($preamble[$sentences.Count % $preamble.Count])
    }
    $seconds = Save-Speech ((($sentences -join ' ') + ' ' + $texts.en.part1).Trim()) (Join-Path $enDir $FileName) $english.Name $slowRate
    return [pscustomobject]@{ Total = $seconds + $slow2; Sentences = $sentences.Count }
}

$nearCap = New-PreambledPart1 $NearCapSeconds 'part1-slow.wav'
Write-Host ("Near-cap: part1-slow + part2-slow = {0:0.0} s ({1} preamble sentences); the probe prints the kept length after compression." -f $nearCap.Total, $nearCap.Sentences)

# The recorder keeps about 85.5% of slow SAPI speech (it squeezes the pauses over 0.5 s; measured: 124.5 s -> 106.5 s),
# so a kept target of N s needs about N / 0.855 s of speech.
$keptRatio = 0.855
foreach ($kept in $SweepSeconds) {
    $sweep = New-PreambledPart1 ($kept / $keptRatio) "sweep-$kept-part1.wav"
    Write-Host ("Sweep {0} s: sweep-{0}-part1 + part2-slow = {1:0.0} s of speech ({2} preamble sentences), about {3:0} s kept." -f $kept, $sweep.Total, $sweep.Sentences, ($sweep.Total * $keptRatio))
}

$vietnamese = $voices | Where-Object { $_.Culture.Name -like 'vi*' } | Select-Object -First 1
if ($vietnamese) {
    $viDir = Join-Path $target 'vi'
    New-Item -ItemType Directory -Force -Path $viDir | Out-Null
    Write-Host "Vietnamese WAVs with '$($vietnamese.Name)' in $viDir"
    foreach ($name in 'part1', 'part2', 'reply', 'absent') {
        $file = if ($name -eq 'reply') { 'reply-yes.wav' } else { "$name.wav" }
        [void](Save-Speech $texts.vi[$name] (Join-Path $viDir $file) $vietnamese.Name)
    }
}
else {
    Write-Warning ('No Vietnamese (vi-VN) voice is visible to System.Speech, so no vi WAVs were generated. ' +
        'Record the vi question parts and the reply yourself and convert them with ffmpeg -i <in> -ar 24000 -ac 1 -c:a pcm_s16le <out.wav>, ' +
        'or install a vi-VN SAPI voice and run this script again.')
}
