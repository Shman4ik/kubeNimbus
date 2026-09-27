#!/usr/bin/env bash
# Builds the solution and runs both test suites by launching their executables
# directly, which is the invocation that cannot silently run nothing.
#
# `dotnet test` has run zero tests in this repo twice without failing, in two
# different ways, and both were read as green for a while:
#
#   1. A positional project path (`dotnet test tests/X/X.csproj`) under the
#      Microsoft.Testing.Platform runner pinned in global.json prints "Specifying
#      a project for 'dotnet test' should be via '--project'" and exits 0 having
#      run NOTHING.
#   2. On SDK 10.0.400-preview, `dotnet test --project ...` reports "Zero tests
#      ran" (exit 5) on a clean checkout — the SDK, not the suite.
#
# A TUnit project is an ordinary executable, so running it is the whole test run,
# with the runner's own summary. This script also refuses a run that reports
# zero tests, so a third way of running nothing cannot pass either.
#
# Usage: scripts/test.sh [-c Release] [-- extra runner args, e.g. --treenode-filter "/*/*/DemoRowsTests/*"]
# Integration tests use ./.sandbox/kubeconfig.yaml or $KUBENIMBUS_TEST_KUBECONFIG
# and skip (reported as skipped) when no cluster answers.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="Debug"
if [[ "${1:-}" == "-c" ]]; then
  configuration="$2"
  shift 2
fi
if [[ "${1:-}" == "--" ]]; then
  shift
fi

dotnet build "$root/KubeNimbus.slnx" -c "$configuration" -nologo -v q

# Exit code 8 is the runner's "zero tests ran". With no arguments every suite must
# run something; with a filter, one suite matching nothing is expected, but all of
# them matching nothing is a filter that selects nothing, and fails.
status=0
ran=0
for suite in KubeNimbus.Core.Tests KubeNimbus.App.Tests; do
  exe="$root/tests/$suite/bin/$configuration/net10.0/$suite"
  [[ -x "$exe" ]] || exe="$exe.exe"
  echo "== $suite"
  code=0
  "$exe" "$@" || code=$?
  if [[ $code -eq 8 ]]; then
    if [[ $# -eq 0 ]]; then
      echo "!! $suite ran zero tests — treating that as a failure." >&2
      status=1
    fi
  elif [[ $code -ne 0 ]]; then
    status=1
  else
    ran=1
  fi
done

if [[ $ran -eq 0 ]]; then
  echo "!! No suite ran a single test." >&2
  status=1
fi

exit "$status"
