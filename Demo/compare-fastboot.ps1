param(
    [string]$GoogleFastboot = "fastboot",
    [string]$SharpFastboot = "d:\Source Code\SharpFastboot\FastbootCLI\bin\Release\net10.0\win-x64\fastboot.exe",
    [string]$OutputDir = "d:\Source Code\SharpFastboot\tmp-compare-report",
    [string]$StageFile = "C:\adb\stage.bin"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SharpFastboot)) {
    throw "SharpFastboot executable not found: $SharpFastboot"
}

if (-not (Get-Command $GoogleFastboot -ErrorAction SilentlyContinue)) {
    throw "Google fastboot not found in PATH or invalid path: $GoogleFastboot"
}

if (-not (Test-Path (Split-Path -Parent $StageFile))) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $StageFile) | Out-Null
}
Set-Content -Path $StageFile -Value "SharpFastboot compare stage payload" -NoNewline

if (Test-Path $OutputDir) {
    Remove-Item -Recurse -Force $OutputDir
}
New-Item -ItemType Directory -Path $OutputDir | Out-Null

$cases = @(
    @{ Name = "devices"; Args = @("devices") },
    @{ Name = "getvar_product"; Args = @("getvar", "product") },
    @{ Name = "getvar_all"; Args = @("getvar", "all") },
    @{ Name = "gsi_status"; Args = @("gsi", "status") },
    @{ Name = "snapshot_update"; Args = @("snapshot-update") },
    @{ Name = "stage"; Args = @("stage", $StageFile) }
)

function Invoke-Capture {
    param(
        [string]$Exe,
        [string[]]$Args,
        [string]$Prefix
    )

    $stdout = Join-Path $OutputDir "$Prefix.stdout.txt"
    $stderr = Join-Path $OutputDir "$Prefix.stderr.txt"
    $exitCodeFile = Join-Path $OutputDir "$Prefix.exitcode.txt"

    $safeArgs = @()
    foreach ($arg in $Args) {
        if ($null -ne $arg -and "$arg" -ne "") {
            $safeArgs += [string]$arg
        }
    }

    if ($safeArgs.Count -eq 0) {
        throw "Argument list is empty for $Prefix"
    }

    $proc = Start-Process -FilePath $Exe -ArgumentList $safeArgs -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Set-Content -Path $exitCodeFile -Value $proc.ExitCode

    return @{
        StdoutPath = $stdout
        StderrPath = $stderr
        ExitCode = $proc.ExitCode
    }
}

function Get-NormalizedContent {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return "" }

    $text = Get-Content -Path $Path -Raw
    if ($null -eq $text) { return "" }

    $normalized = $text
    $normalized = [Regex]::Replace($normalized, "Finished\. Total time: [0-9]+\.[0-9]+s", "Finished. Total time: <time>")
    $normalized = [Regex]::Replace($normalized, "\[[ ]*[0-9]+\.[0-9]+s\]", "[ <time> ]")
    $normalized = [Regex]::Replace($normalized, "battery-voltage:?\s*[0-9]+", "battery-voltage:<dynamic>")
    $normalized = [Regex]::Replace($normalized, "\r\n", "`n")

    return $normalized.TrimEnd("`n")
}

function Compare-Text {
    param(
        [string]$Left,
        [string]$Right
    )
    return $Left -eq $Right
}

$rows = @()
foreach ($case in $cases) {
    $googlePrefix = "google_$($case.Name)"
    $sharpPrefix = "sharp_$($case.Name)"

    $g = Invoke-Capture -Exe $GoogleFastboot -Args $case.Args -Prefix $googlePrefix
    $s = Invoke-Capture -Exe $SharpFastboot -Args $case.Args -Prefix $sharpPrefix

    $gStdoutNorm = Get-NormalizedContent $g.StdoutPath
    $gStderrNorm = Get-NormalizedContent $g.StderrPath
    $sStdoutNorm = Get-NormalizedContent $s.StdoutPath
    $sStderrNorm = Get-NormalizedContent $s.StderrPath

    $stdoutEqual = Compare-Text $gStdoutNorm $sStdoutNorm
    $stderrEqual = Compare-Text $gStderrNorm $sStderrNorm
    $exitEqual = $g.ExitCode -eq $s.ExitCode

    $rows += [PSCustomObject]@{
        Command = ($case.Args -join " ")
        ExitGoogle = $g.ExitCode
        ExitSharp = $s.ExitCode
        ExitMatch = $exitEqual
        StdoutMatch = $stdoutEqual
        StderrMatch = $stderrEqual
    }
}

$reportPath = Join-Path $OutputDir "summary.json"
$rows | ConvertTo-Json -Depth 4 | Set-Content -Path $reportPath -Encoding UTF8

$rows | Format-Table -AutoSize
Write-Host ""
Write-Host "Detailed outputs are in: $OutputDir"
Write-Host "Summary JSON: $reportPath"

$allMatch = $true
foreach ($row in $rows) {
    if (-not ($row.ExitMatch -and $row.StdoutMatch -and $row.StderrMatch)) {
        $allMatch = $false
        break
    }
}

if (-not $allMatch) {
    Write-Host ""
    Write-Host "Mismatches found. Inspect *_stdout/stderr.txt files in $OutputDir"
    exit 2
}

Write-Host ""
Write-Host "All compared commands match after normalization."
exit 0
