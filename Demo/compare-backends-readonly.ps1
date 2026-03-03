param(
    [string]$SharpFastboot = "d:\Source Code\SharpFastboot\FastbootCLI\bin\Release\net10.0\win-x64\fastboot.exe",
    [string]$OutputDir = "d:\Source Code\SharpFastboot\tmp-compare-backends",
    [string]$Serial = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SharpFastboot)) {
    throw "SharpFastboot executable not found: $SharpFastboot"
}

if (Test-Path $OutputDir) {
    Remove-Item -Recurse -Force $OutputDir
}
New-Item -ItemType Directory -Path $OutputDir | Out-Null

$commonPrefix = @()
if (-not [string]::IsNullOrWhiteSpace($Serial)) {
    $commonPrefix += @("-s", $Serial)
}

$cases = @(
    @{ Name = "devices"; Args = @("devices") },
    @{ Name = "getvar_product"; Args = @("getvar", "product") },
    @{ Name = "getvar_current_slot"; Args = @("getvar", "current-slot") },
    @{ Name = "getvar_slot_count"; Args = @("getvar", "slot-count") },
    @{ Name = "getvar_is_userspace"; Args = @("getvar", "is-userspace") },
    @{ Name = "getvar_all"; Args = @("getvar", "all") }
)

function Invoke-Capture {
    param(
        [string]$Exe,
        [string[]]$CmdArgs,
        [string]$Prefix
    )

    $stdout = Join-Path $OutputDir "$Prefix.stdout.txt"
    $stderr = Join-Path $OutputDir "$Prefix.stderr.txt"
    $exitCodeFile = Join-Path $OutputDir "$Prefix.exitcode.txt"

    $safeArgs = @()
    foreach ($arg in $CmdArgs) {
        if ($null -ne $arg -and "$arg" -ne "") {
            $safeArgs += [string]$arg
        }
    }

    $escapedArgs = @()
    foreach ($arg in $safeArgs) {
        $escapedArgs += ('"' + ($arg -replace '"', '""') + '"')
    }
    $argumentLine = [string]::Join(' ', $escapedArgs)

    $proc = Start-Process -FilePath $Exe -ArgumentList $argumentLine -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
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

$rows = @()
foreach ($case in $cases) {
    $nativePrefix = "native_$($case.Name)"
    $libusbPrefix = "libusb_$($case.Name)"

    $nativeArgs = @($commonPrefix + $case.Args)
    $libusbArgs = @($commonPrefix + @("--libusb") + $case.Args)

    $native = Invoke-Capture -Exe $SharpFastboot -CmdArgs $nativeArgs -Prefix $nativePrefix
    $libusb = Invoke-Capture -Exe $SharpFastboot -CmdArgs $libusbArgs -Prefix $libusbPrefix

    $nativeStdoutNorm = Get-NormalizedContent $native.StdoutPath
    $nativeStderrNorm = Get-NormalizedContent $native.StderrPath
    $libusbStdoutNorm = Get-NormalizedContent $libusb.StdoutPath
    $libusbStderrNorm = Get-NormalizedContent $libusb.StderrPath

    $stdoutEqual = $nativeStdoutNorm -eq $libusbStdoutNorm
    $stderrEqual = $nativeStderrNorm -eq $libusbStderrNorm
    $exitEqual = $native.ExitCode -eq $libusb.ExitCode

    $rows += [PSCustomObject]@{
        Command = ($case.Args -join " ")
        ExitNative = $native.ExitCode
        ExitLibusb = $libusb.ExitCode
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
    Write-Host "Mismatches found. Inspect native_*.txt and libusb_*.txt in $OutputDir"
    exit 2
}

Write-Host ""
Write-Host "All compared commands match after normalization."
exit 0
