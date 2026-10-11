<#
.SYNOPSIS
  Measures kubeNimbus's own startup: N launches of a built binary, reported as median,
  p90, min and max. It measures kubeNimbus only, never another app.

.DESCRIPTION
  The positioning numbers in docs/engineering/mission.md and docs/comparison.md were
  measured once, on one machine (docs/research/2026-08-17-kubeui-positioning.md). This
  script is the repeatable half of that measurement (#234): it launches the binary with
  `--smoke-test`, which starts the real app, waits for the main window to composite a
  frame and exits on its own, so nothing here needs a display you can see, input, or a
  cluster.

  Each run reports up to three numbers, all in milliseconds:

    first frame (in app)    What `--smoke-test` prints on its SMOKE-OK line: time from
                            Main to the first composited frame. Excludes loading the
                            process image and the runtime.
    first frame (wall)      Process start, as seen by this script, to the moment the
                            SMOKE-OK line arrives on stdout. Includes everything the
                            in-app number leaves out, plus a pipe hop.
    window mapped (wall)    Linux only, and only with `xdotool` on PATH: process start to
                            the first visible X11 window owned by the process, polled
                            every 20 ms. This is the cross-app metric the 2026-08-17
                            research used, the only one another app could be measured on
                            identically. It is coarser than the first-frame number (a
                            window is mapped before it is painted, and the poll adds up
                            to 20 ms), and kept so the two can be compared.

  Two scenarios, both needing no cluster:

    empty                   (default) An isolated, freshly created profile directory
                            (KUBENIMBUS_PROFILE_DIR) and an empty KUBECONFIG, so the app
                            reads no settings, no workspace and no kubeconfig of whoever
                            runs it, and opens on its "no kubeconfig" state. Every run is
                            a first launch.
    unreachable-cluster     `--smoke-test=unreachable-cluster`: the app seeds a workspace
                            restoring a cluster nothing listens on and passes once the
                            tab has reported the refused connection and rendered again.
                            Slower by design; it times the connect-failure path too.

  The first -Warmup runs are discarded from the statistics and printed separately: the
  first launch after a build reads the binary from a cold page cache and is not the
  number a user sees on their second launch, nor the one they see on their first after a
  reboot. Cold start is therefore not measured by this script; see scripts/README.md.

  On Linux with no DISPLAY, the script starts one Xvfb server (1280x800x24) for the whole
  series rather than one per launch, so Xvfb's own startup is never inside a timing.

.PARAMETER Binary
  The kubeNimbus executable to launch. Defaults to publish/app/kubeNimbus(.exe), where
  CI's NativeAOT job and the documented publish command put it.

.PARAMETER Runs
  Measured launches (after the warm-up). At least 1; 10 is a reasonable minimum for a
  p90 that means anything.

.PARAMETER Warmup
  Launches run first and left out of the statistics.

.PARAMETER Scenario
  `empty` or `unreachable-cluster`, as above.

.PARAMETER Markdown
  Print the report as Markdown (for $GITHUB_STEP_SUMMARY) instead of plain text.

.EXAMPLE
  ./scripts/measure-startup.ps1 -Binary publish/app/kubeNimbus.exe -Runs 20
.EXAMPLE
  pwsh ./scripts/measure-startup.ps1 -Runs 10 -Markdown >> "$GITHUB_STEP_SUMMARY"
#>
param(
    [string] $Binary,
    [ValidateRange(1, 1000)] [int] $Runs = 10,
    [ValidateRange(0, 100)] [int] $Warmup = 1,
    [ValidateSet('empty', 'unreachable-cluster')] [string] $Scenario = 'empty',
    [switch] $Markdown
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $Binary) {
    $name = if ($IsWindows) { 'kubeNimbus.exe' } else { 'kubeNimbus' }
    $Binary = Join-Path $root "publish/app/$name"
}
if (-not (Test-Path -LiteralPath $Binary -PathType Leaf)) {
    throw "No binary at '$Binary'. Publish one first (see scripts/README.md, 'Measuring startup')."
}
$Binary = (Resolve-Path -LiteralPath $Binary).Path
$binaryDirectory = Split-Path -Parent $Binary

# A NativeAOT publish has no managed kubeNimbus.dll beside the executable; any other
# build (Debug, a framework-dependent Release) does, and its numbers are JIT numbers.
$isAot = -not (Test-Path -LiteralPath (Join-Path $binaryDirectory 'kubeNimbus.dll'))

# --- Display (Linux) --------------------------------------------------------------------

$xvfb = $null
$display = $env:DISPLAY
if ($IsLinux -and -not $display) {
    if (-not (Get-Command Xvfb -ErrorAction SilentlyContinue)) {
        throw 'No DISPLAY and no Xvfb on PATH. Install xvfb, or run under an X server.'
    }
    $display = ':97'
    $xvfb = Start-Process -FilePath Xvfb -ArgumentList $display, '-screen', '0', '1280x800x24', '-nolisten', 'tcp' -PassThru
    $socket = '/tmp/.X11-unix/X97'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path $socket)) {
        if ([DateTime]::UtcNow -gt $deadline -or $xvfb.HasExited) { throw "Xvfb did not come up on $display." }
        Start-Sleep -Milliseconds 50
    }
}
$pollMapped = $IsLinux -and [bool](Get-Command xdotool -ErrorAction SilentlyContinue)

# --- One launch ------------------------------------------------------------------------

function Invoke-Launch {
    $profileDirectory = $null
    $info = [System.Diagnostics.ProcessStartInfo]::new($Binary)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.WorkingDirectory = $binaryDirectory
    if ($display) { $info.Environment['DISPLAY'] = $display }

    if ($Scenario -eq 'empty') {
        $info.ArgumentList.Add('--smoke-test')
        $profileDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("kubenimbus-startup-" + [Guid]::NewGuid().ToString('N'))
        $info.Environment['KUBENIMBUS_PROFILE_DIR'] = $profileDirectory
        # With a profile directory the app searches only $KUBECONFIG, never ~/.kube/config;
        # an empty value is no kubeconfig at all.
        $info.Environment['KUBECONFIG'] = ''
    }
    else {
        # The scenario redirects both stores to a seeded temporary directory itself.
        $info.ArgumentList.Add('--smoke-test=unreachable-cluster')
    }

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($info)
    $stderr = $process.StandardError.ReadToEndAsync()
    $result = [ordered]@{ InApp = $null; Wall = $null; Mapped = $null; Done = $null; Exit = $null; Output = [System.Collections.Generic.List[string]]::new() }

    try {
        $read = $process.StandardOutput.ReadLineAsync()
        while ($true) {
            if ($read.Wait(20)) {
                $line = $read.Result
                if ($null -eq $line) { break }
                $result.Output.Add($line)
                # The plain check passes at the first frame, so its SMOKE-OK line is the
                # first frame. The unreachable-cluster check reports the first frame on a
                # progress line and passes only after the failed connect has rendered.
                if ($line -match '^SMOKE-OK .* after (\d+) ms') {
                    if ($Scenario -eq 'empty') {
                        $result.Wall = $clock.Elapsed.TotalMilliseconds
                        $result.InApp = [double]$Matches[1]
                    }
                    else {
                        $result.Done = [double]$Matches[1]
                    }
                }
                elseif ($Scenario -ne 'empty' -and $line -match '^\[smoke (\d+) ms\] first frame rendered') {
                    $result.Wall = $clock.Elapsed.TotalMilliseconds
                    $result.InApp = [double]$Matches[1]
                }
                $read = $process.StandardOutput.ReadLineAsync()
            }
            if ($pollMapped -and $null -eq $result.Mapped -and -not $process.HasExited) {
                # By _NET_WM_PID first. On a private Xvfb this script started, the app is
                # the only client, so any visible window with a WM_CLASS is its window —
                # which still works if a toolkit version stops setting _NET_WM_PID.
                $windows = & xdotool search --onlyvisible --pid $process.Id 2>$null
                if (-not $windows -and $xvfb) { $windows = & xdotool search --onlyvisible --class '.' 2>$null }
                if ($windows) { $result.Mapped = $clock.Elapsed.TotalMilliseconds }
            }
        }
        if (-not $process.WaitForExit(120000)) {
            $process.Kill($true)
            throw 'kubeNimbus did not exit within 120 s of closing its stdout.'
        }
        $result.Exit = $process.ExitCode
        $errText = $stderr.GetAwaiter().GetResult()
        if ($result.Exit -ne 0 -or $null -eq $result.InApp) {
            throw "Launch failed (exit $($result.Exit)):`n$($result.Output -join "`n")`n$errText"
        }
    }
    finally {
        $process.Dispose()
        if ($profileDirectory -and (Test-Path -LiteralPath $profileDirectory)) {
            Remove-Item -LiteralPath $profileDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    [pscustomobject]$result
}

# --- Statistics ------------------------------------------------------------------------

function Get-Summary([double[]] $values) {
    $sorted = @($values | Sort-Object)
    $n = $sorted.Count
    $median = if ($n % 2) { $sorted[($n - 1) / 2] } else { ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2 }
    # Nearest-rank p90: the smallest value with at least 90% of the runs at or below it.
    $p90 = $sorted[[Math]::Max(0, [Math]::Ceiling(0.9 * $n) - 1)]
    [pscustomobject]@{ Median = $median; P90 = $p90; Min = $sorted[0]; Max = $sorted[-1] }
}

function Get-MachineDescription {
    $os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription.Trim()
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    $cpu = '?'
    try {
        if ($IsWindows) { $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim() }
        elseif ($IsLinux) { $cpu = ((Get-Content /proc/cpuinfo | Where-Object { $_ -like 'model name*' } | Select-Object -First 1) -split ':', 2)[1].Trim() }
        elseif ($IsMacOS) { $cpu = (& sysctl -n machdep.cpu.brand_string).Trim() }
    }
    catch { }
    "$os, $arch, $cpu, $([Environment]::ProcessorCount) logical CPUs"
}

# --- Run the series --------------------------------------------------------------------

try {
    $warm = @()
    for ($i = 0; $i -lt $Warmup; $i++) { $warm += Invoke-Launch }
    $measured = @()
    for ($i = 0; $i -lt $Runs; $i++) { $measured += Invoke-Launch }
}
finally {
    if ($xvfb -and -not $xvfb.HasExited) { Stop-Process -Id $xvfb.Id -Force -ErrorAction SilentlyContinue }
}

$metrics = [ordered]@{
    'first frame (in app)'  = 'InApp'
    'first frame (wall)'    = 'Wall'
    'window mapped (wall)'  = 'Mapped'
    'failed connect shown (in app)' = 'Done'
}
$commit = try { (& git -C $root rev-parse --short HEAD 2>$null) } catch { $null }
$sizeMb = (Get-ChildItem -LiteralPath $binaryDirectory -File -Recurse | Where-Object { $_.Extension -notin '.pdb', '.dbg' } | Measure-Object Length -Sum).Sum / 1MB

$header = [ordered]@{
    'Machine'  = Get-MachineDescription
    'Binary'   = "$Binary ($(if ($isAot) { 'NativeAOT' } else { 'not NativeAOT: JIT numbers' }), $('{0:0.0}' -f $sizeMb) MB in its folder without symbols)"
    'Checkout' = if ($commit) { $commit } else { 'unknown' }
    'Scenario' = $Scenario
    'Runs'     = "$Runs measured, $Warmup warm-up discarded"
    'Date'     = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm') + ' UTC'
}

$rows = foreach ($label in $metrics.Keys) {
    $key = $metrics[$label]
    $values = @($measured | ForEach-Object { $_.$key } | Where-Object { $null -ne $_ })
    if ($values.Count -eq 0) { continue }
    # Only the xdotool poll can miss a run (a process that maps and exits between two
    # polls); say so in the row rather than report a median over a silently smaller set.
    if ($values.Count -ne $measured.Count) { $label = "$label ($($values.Count) of $($measured.Count) runs)" }
    $s = Get-Summary $values
    [pscustomobject]@{
        Metric = $label
        Median = '{0:0}' -f $s.Median
        P90    = '{0:0}' -f $s.P90
        Min    = '{0:0}' -f $s.Min
        Max    = '{0:0}' -f $s.Max
        Runs   = ($values | ForEach-Object { '{0:0}' -f $_ }) -join ', '
        Warmup = ($warm | ForEach-Object { $_.$key } | Where-Object { $null -ne $_ } | ForEach-Object { '{0:0}' -f $_ }) -join ', '
    }
}

if ($Markdown) {
    "### kubeNimbus startup (ms)"
    ""
    foreach ($k in $header.Keys) { "- **${k}:** $($header[$k])" }
    ""
    "| Metric | Median | p90 | Min | Max | Runs | Warm-up |"
    "|---|---:|---:|---:|---:|---|---|"
    foreach ($r in $rows) { "| $($r.Metric) | $($r.Median) | $($r.P90) | $($r.Min) | $($r.Max) | $($r.Runs) | $($r.Warmup) |" }
    if ($IsLinux -and -not $pollMapped) { ""; "_window mapped not measured: xdotool is not on PATH._" }
}
else {
    foreach ($k in $header.Keys) { '{0,-9} {1}' -f "${k}:", $header[$k] }
    $rows | Format-Table Metric, Median, P90, Min, Max -AutoSize | Out-String -Stream | Where-Object { $_ }
    foreach ($r in $rows) { "$($r.Metric): runs $($r.Runs); warm-up $($r.Warmup)" }
    if ($IsLinux -and -not $pollMapped) { 'window mapped not measured: xdotool is not on PATH.' }
}
