<#
.SYNOPSIS
    Records one check's verdict (or one surprise) for qa-report.ps1 to assemble.

.DESCRIPTION
    In the 0.7.0 run every batch's agent wrote its findings as prose, and the orchestrating
    session re-typed each row into the report by hand: about 45 tool calls and a large part
    of the run's tokens. Now the agent records each verdict as it goes, one JSON line per
    check in $env:TEMP\kubenimbus-qa\results\<Batch>.jsonl (a second record for the same
    check replaces the first, so a re-run overwrites), and qa-report.ps1 builds the report,
    the counts and the list of failures from those files.

    A PASS must quote what was observed: a find/dump/wait line, a clipboard value or a
    kubectl line, in quotes or backticks. "No quoted line, no PASS", enforced here because
    it is the one failure this process must not have.

.EXAMPLE
    ./scripts/qa-result.ps1 -Batch L1 -Id L-2 -Pays 'PR #299 c2' -Verdict PASS `
        -Observation "Ctrl+click b: Text '2 selected'; again: Text '3 selected'."
    ./scripts/qa-result.ps1 -Batch G2 -Id G-11 -Verdict UNSURE -Observation 'cannot cut the connection: qa-netcut not started'
    ./scripts/qa-result.ps1 -Batch G2 -Surprise 'Menu items take their help text as their name.'
#>
[CmdletBinding(DefaultParameterSetName = 'Check')]
param(
    [Parameter(Mandatory)][string]$Batch,
    [Parameter(ParameterSetName = 'Check', Mandatory)][string]$Id,
    [Parameter(ParameterSetName = 'Check')][string]$Pays = '',
    [Parameter(ParameterSetName = 'Check', Mandatory)][ValidateSet('PASS', 'FAIL', 'UNSURE')][string]$Verdict,
    [Parameter(ParameterSetName = 'Check', Mandatory)][string]$Observation,
    [Parameter(ParameterSetName = 'Check')][string]$Evidence = '',
    [Parameter(ParameterSetName = 'Surprise', Mandatory)][string]$Surprise
)

$ErrorActionPreference = 'Stop'
$dir = Join-Path ([IO.Path]::GetTempPath()) 'kubenimbus-qa/results'
New-Item -ItemType Directory -Force $dir | Out-Null
$file = Join-Path $dir "$Batch.jsonl"
# @() around the whole if: a one-line file would otherwise come back as a plain string, and
# adding a record to a string glues two JSON objects onto one line.
$existing = @(if (Test-Path $file) { Get-Content $file })

if ($PSCmdlet.ParameterSetName -eq 'Surprise') {
    $record = @{ type = 'surprise'; text = $Surprise } | ConvertTo-Json -Compress
    Set-Content $file ($existing + $record) -Encoding utf8
    "recorded surprise in $Batch"
    return
}

if ($Verdict -eq 'PASS' -and $Observation -notmatch "['`"``]") {
    throw 'A PASS must quote what was observed (a find/dump/wait line, a clipboard value, a kubectl line) in quotes or backticks. Record UNSURE if there is nothing to quote.'
}
$record = @{ type = 'check'; id = $Id; pays = $Pays; verdict = $Verdict; observation = $Observation; evidence = $Evidence } | ConvertTo-Json -Compress
$kept = @($existing | Where-Object { $r = $_ | ConvertFrom-Json; -not ($r.type -eq 'check' -and $r.id -eq $Id) })
Set-Content $file ($kept + $record) -Encoding utf8
"recorded $Id $Verdict ($Batch)"
