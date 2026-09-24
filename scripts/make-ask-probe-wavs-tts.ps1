<#
.SYNOPSIS
    Generates the plan 011 T1 ask-probe WAVs (24 kHz, mono, PCM16) with the gpt-audio-1.5 model (presenter-cli tts).

.DESCRIPTION
    Writes, into -OutDir (which must be outside the repository), en\ and vi\ WAVs plus manifest.txt (file, text, voice,
    duration). Every call reads its text verbatim; presenter-cli tts fails if the model's transcript differs.

      en\ and vi\: part1.wav, part2.wav, reply-yes.wav ("Yes." / "Có."), absent.wav (a question the deck does not cover)
      en\preamble-1..9.wav    neutral, keyword-free background the asker gives before the question
      en\cap-<N>-part1.wav    preamble tail + part 1, sized by measurement (presenter-cli compose-ask-wav) so that with
                              part2.wav and the probe's 10 s gap the recorder keeps about N s (N = 16, 24, 28, 40)
      en\sweep-<N>-part1.wav  the same for the earlier sweep lengths (N = 20, 24, 28, 40, 60, 100)
      en\part1-slow.wav       the near-cap variant (about 106 s kept); en\part2-slow.wav is a copy of part2.wav

    The SAPI script (make-ask-probe-wavs.ps1) stays as an offline fallback; the suite uses these files.

.PARAMETER OutDir
    Target directory. Must not be inside the repository.

.PARAMETER Provider
    azure (default) or openai: the route that serves -Model.

.PARAMETER Voice
    Output voice (default marin).

.PARAMETER Force
    Regenerate files that already exist.

.EXAMPLE
    pwsh -File scripts/make-ask-probe-wavs-tts.ps1 -OutDir $env:TEMP\ask-probe-wavs
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $OutDir,
    [ValidateSet('azure', 'openai')] [string] $Provider = 'azure',
    [string] $Model = 'gpt-audio-1.5',
    [string] $Voice = 'marin',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\') + '\'
$target = [System.IO.Path]::GetFullPath($OutDir).TrimEnd('\') + '\'
if ($target.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutDir '$OutDir' is inside the repository; write the WAVs to a scratch directory instead."
}

Push-Location $repoRoot
try {
    dotnet build src/PresenterAi.Cli --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'presenter-cli build failed' }
    $cli = Join-Path $repoRoot 'src\PresenterAi.Cli\bin\Debug\net10.0\presenter-cli.exe'

    $texts = @{
        en = @{
            part1  = 'What did the programme change at the Da Nang office in its first year,'
            part2  = 'and how much did the Hanoi expansion cost?'
            reply  = 'Yes.'
            absent = "Who was the programme's external auditor last year?"
        }
        vi = @{
            part1  = 'Chương trình đã thay đổi gì ở văn phòng Đà Nẵng trong năm đầu tiên,'
            part2  = 'và việc mở rộng ở Hà Nội tốn bao nhiêu?'
            reply  = 'Có.'
            absent = 'Ai là đơn vị kiểm toán độc lập của chương trình năm ngoái?'
        }
    }
    $preamble = @(
        'Before I ask my question, let me give you some background on why this matters to our team.',
        'We are preparing next year''s plan, and our managers want concrete examples from the regional offices rather than general impressions.',
        'Over the last few months we have collected notes from several teams, and most of them asked for the same kind of detail.',
        'Some people care about how the daily work changed, and others care mostly about the budget and whether it was well spent.',
        'I also want to be able to explain the results to colleagues who were not part of the programme and who only know it by name.',
        'Our finance group will review the numbers again next quarter, so it helps to have a clear and simple summary before then.',
        'I have read the slides once already, but I would like to hear it from you in your own words, briefly and precisely.',
        'If some of this is not covered in your material, that is fine, just tell me, and I will follow up with the right people.',
        'So, keeping all of that in mind, here is what I would like to understand about the programme and its first year.'
    )

    $manifest = New-Object System.Collections.Generic.List[string]
    function Get-WavSeconds([string] $Path) {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
        $offset = 12
        while ($offset + 8 -le $bytes.Length) {
            $id = [System.Text.Encoding]::ASCII.GetString($bytes, $offset, 4)
            $size = [System.BitConverter]::ToInt32($bytes, $offset + 4)
            if ($id -eq 'data') { return [math]::Round([math]::Min($size, $bytes.Length - $offset - 8) / 48000.0, 2) }
            $offset += 8 + $size + ($size -band 1)
        }
        throw "no data chunk in $Path"
    }

    function Add-Manifest([string] $Path, [string] $Text) {
        $relative = $Path.Substring($target.Length)
        $manifest.Add(("{0}`t{1:0.00} s`t{2}`t{3}" -f $relative, (Get-WavSeconds $Path), $Voice, $Text))
    }

    function New-Speech([string] $Text, [string] $Path) {
        if ($Force -or -not (Test-Path $Path)) {
            & $cli tts --provider $Provider --model $Model --voice $Voice --text $Text --out $Path
            if ($LASTEXITCODE -ne 0) { throw "tts failed for $Path" }
        }
        else {
            Write-Host "  exists: $Path"
        }
        Add-Manifest $Path $Text
    }

    foreach ($lang in 'en', 'vi') {
        $dir = Join-Path $target $lang
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Write-Host "$lang WAVs with $Model ($Voice, $Provider route) in $dir"
        New-Speech $texts[$lang].part1 (Join-Path $dir 'part1.wav')
        New-Speech $texts[$lang].part2 (Join-Path $dir 'part2.wav')
        New-Speech $texts[$lang].reply (Join-Path $dir 'reply-yes.wav')
        New-Speech $texts[$lang].absent (Join-Path $dir 'absent.wav')
    }

    $en = Join-Path $target 'en'
    $preambleFiles = @()
    for ($index = 0; $index -lt $preamble.Count; $index++) {
        $file = Join-Path $en ("preamble-{0}.wav" -f ($index + 1))
        New-Speech $preamble[$index] $file
        $preambleFiles += $file
    }

    Copy-Item (Join-Path $en 'part2.wav') (Join-Path $en 'part2-slow.wav') -Force
    Add-Manifest (Join-Path $en 'part2-slow.wav') "$($texts.en.part2) (copy of part2.wav)"

    # Kept-length variants: measured with the probe's recorder (part 1 + 10 s gap noise + part 2), not by sentence steps.
    # The preamble is used three times over for the long sweep and near-cap variants.
    $longPreamble = $preambleFiles + $preambleFiles + $preambleFiles
    $variants = [ordered]@{
        'cap-16-part1.wav' = 16; 'cap-24-part1.wav' = 24; 'cap-28-part1.wav' = 28; 'cap-40-part1.wav' = 40
        'sweep-20-part1.wav' = 20; 'sweep-24-part1.wav' = 24; 'sweep-28-part1.wav' = 28; 'sweep-40-part1.wav' = 40; 'sweep-60-part1.wav' = 60; 'sweep-100-part1.wav' = 100
        'part1-slow.wav' = 106
    }
    foreach ($name in $variants.Keys) {
        $path = Join-Path $en $name
        $kept = $variants[$name]
        if ($Force -or -not (Test-Path $path)) {
            $preambleArgs = @($longPreamble | ForEach-Object { '--preamble'; $_ })
            & $cli compose-ask-wav @preambleArgs --question (Join-Path $en 'part1.wav') --part2 (Join-Path $en 'part2.wav') --kept-seconds $kept --gap-seconds 10 --out $path
            if ($LASTEXITCODE -ne 0) { throw "compose-ask-wav failed for $path" }
        }
        Add-Manifest $path "preamble tail + '$($texts.en.part1)' (composed for about $kept s kept with part2.wav and a 10 s gap)"
    }

    $manifestPath = Join-Path $target 'manifest.txt'
    @("# file`tduration`tvoice`ttext  (model $Model, $Provider route, 24 kHz mono PCM16)") + $manifest | Set-Content -Encoding utf8 $manifestPath
    Write-Host "Manifest: $manifestPath ($($manifest.Count) files)"
}
finally {
    Pop-Location
}
