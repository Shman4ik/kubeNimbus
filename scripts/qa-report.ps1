<#
.SYNOPSIS
    Builds the QA report from the verdicts qa-result.ps1 recorded: header, counts, failures,
    one table per batch, and the surprises.

.DESCRIPTION
    Reads $env:TEMP\kubenimbus-qa\results\*.jsonl and writes Markdown. The summary at the top
    counts PASS / FAIL / UNSURE per batch and overall and lists every FAIL (with the issue or
    PR the check pays) and every UNSURE with its reason, so the orchestrating session reads
    the top of the file and nothing else unless it wants the evidence.

    Batches are ordered as docs/qa/desktop-checks.md lists them; a batch it does not know
    comes last. `-Clear` deletes the recorded results (start of a new run).

.EXAMPLE
    ./scripts/qa-report.ps1                       # to the Desktop, and printed
    ./scripts/qa-report.ps1 -Out report.md -Quiet
    ./scripts/qa-report.ps1 -Clear
#>
[CmdletBinding()]
param(
    [string]$Out = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'kn-qa-report.md'),
    [switch]$Quiet,
    [switch]$Clear
)

$ErrorActionPreference = 'Stop'
$dir = Join-Path ([IO.Path]::GetTempPath()) 'kubenimbus-qa/results'
if ($Clear) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue; 'results cleared'; return }
if (-not (Test-Path $dir)) { throw "No results in $dir. Record verdicts with ./scripts/qa-result.ps1." }

$order = 'C', 'L1', 'L2', 'Y', 'G1', 'G2', 'T', 'P', 'K', 'S', 'W', 'A', 'M'
$batches = Get-ChildItem $dir -Filter *.jsonl | ForEach-Object {
    $records = @(Get-Content $_.FullName | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    [pscustomobject]@{
        Name = $_.BaseName
        Checks = @($records | Where-Object type -eq 'check')
        Surprises = @($records | Where-Object type -eq 'surprise')
    }
} | Sort-Object { $i = [array]::IndexOf($order, $_.Name); if ($i -lt 0) { 99 } else { $i } }

function Cell([string]$text) { ($text -replace '\r?\n', ' ' -replace '\|', '\|').Trim() }

$repo = (git rev-parse --show-toplevel 2>$null)
$sha = if ($repo) { (git -C $repo log --oneline -1) } else { '?' }
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$gfx = [System.Drawing.Graphics]::FromHwnd([IntPtr]::Zero); $scale = [math]::Round($gfx.DpiX / 96 * 100); $gfx.Dispose()

$all = @($batches | ForEach-Object { $_.Checks })
$count = { param($set, $v) @($set | Where-Object verdict -eq $v).Count }

$md = New-Object System.Collections.Generic.List[string]
$md.Add('# kubeNimbus desktop QA report')
$md.Add('')
$md.Add("- Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
$md.Add("- VM: $env:COMPUTERNAME")
$md.Add("- Clone: $sha")
$md.Add("- Screen: $($screen.Width)x$($screen.Height) px at $scale % scaling")
$md.Add('')
$md.Add('## Summary')
$md.Add('')
$md.Add('| Batch | PASS | FAIL | UNSURE |')
$md.Add('|---|---|---|---|')
foreach ($b in $batches) { $md.Add("| $($b.Name) | $(& $count $b.Checks PASS) | $(& $count $b.Checks FAIL) | $(& $count $b.Checks UNSURE) |") }
$md.Add("| **Total** | **$(& $count $all PASS)** | **$(& $count $all FAIL)** | **$(& $count $all UNSURE)** |")
$md.Add('')
$md.Add('### FAILs')
$md.Add('')
$fails = @($batches | ForEach-Object { $b = $_.Name; $_.Checks | Where-Object verdict -eq 'FAIL' | ForEach-Object { [pscustomobject]@{ B = $b; C = $_ } } })
if ($fails) { foreach ($f in $fails) { $md.Add("- **$($f.C.id)** ($(Cell $f.C.pays)): $(Cell $f.C.observation)") } } else { $md.Add('None.') }
$md.Add('')
$md.Add('### UNSURE, and why')
$md.Add('')
$unsure = @($batches | ForEach-Object { $_.Checks | Where-Object verdict -eq 'UNSURE' })
if ($unsure) { foreach ($u in $unsure) { $md.Add("- **$($u.id)**: $(Cell $u.observation)") } } else { $md.Add('None.') }
$md.Add('')
foreach ($b in $batches) {
    $md.Add("## Batch $($b.Name)")
    $md.Add('')
    $md.Add('| Check | Pays | Verdict | Observation (quoted) | Evidence |')
    $md.Add('|---|---|---|---|---|')
    foreach ($c in $b.Checks) { $md.Add("| $($c.id) | $(Cell $c.pays) | $($c.verdict) | $(Cell $c.observation) | $(Cell $c.evidence) |") }
    if ($b.Surprises) {
        $md.Add('')
        $md.Add('Surprises:')
        foreach ($s in $b.Surprises) { $md.Add("- $(Cell $s.text)") }
    }
    $md.Add('')
}
Set-Content $Out $md -Encoding utf8
if (-not $Quiet) { $md }
"wrote $Out" | Write-Host
