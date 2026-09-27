<#
.SYNOPSIS
  Builds the solution and runs both test suites by launching their executables
  directly, which is the invocation that cannot silently run nothing.

.DESCRIPTION
  `dotnet test` has run zero tests in this repo twice without failing, in two
  different ways, and both were read as green for a while:

    1. A positional project path (`dotnet test tests/X/X.csproj`) under the
       Microsoft.Testing.Platform runner pinned in global.json prints
       "Specifying a project for 'dotnet test' should be via '--project'" and
       exits 0 having run NOTHING.
    2. On SDK 10.0.400-preview, `dotnet test --project ...` reports
       "Zero tests ran" (exit 5) on a clean checkout - the SDK, not the suite.

  A TUnit project is an ordinary executable, so running it is the whole test
  run, with the runner's own summary. This script also refuses a run that
  reports zero tests, so a third way of running nothing cannot pass either.

  Integration tests use ./.sandbox/kubeconfig.yaml or $env:KUBENIMBUS_TEST_KUBECONFIG
  and skip (reported as skipped) when no cluster answers.

.EXAMPLE
  ./scripts/test.ps1
.EXAMPLE
  ./scripts/test.ps1 -Configuration Release -RunnerArgs '--treenode-filter', '/*/*/DemoRowsTests/*'
#>
param(
    [string] $Configuration = 'Debug',
    [string[]] $RunnerArgs = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet build (Join-Path $root 'KubeNimbus.slnx') -c $Configuration -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Exit code 8 is the runner's "zero tests ran". With no arguments every suite must
# run something; with a filter, one suite matching nothing is expected, but all of
# them matching nothing is a filter that selects nothing, and fails.
$failed = $false
$ran = $false
foreach ($suite in 'KubeNimbus.Core.Tests', 'KubeNimbus.App.Tests') {
    $exe = Join-Path $root "tests/$suite/bin/$Configuration/net10.0/$suite"
    if (-not (Test-Path $exe)) { $exe = "$exe.exe" }

    Write-Host "== $suite"
    & $exe @RunnerArgs
    $code = $LASTEXITCODE
    if ($code -eq 8) {
        if ($RunnerArgs.Count -eq 0) {
            Write-Host "!! $suite ran zero tests - treating that as a failure." -ForegroundColor Red
            $failed = $true
        }
    }
    elseif ($code -ne 0) { $failed = $true }
    else { $ran = $true }
}

if (-not $ran) {
    Write-Host '!! No suite ran a single test.' -ForegroundColor Red
    $failed = $true
}

if ($failed) { exit 1 }

exit 0
