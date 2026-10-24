#!/usr/bin/env bash
# Tears down a sandbox cluster started by sandbox-up.sh.
#
#   ./scripts/sandbox-down.sh [--name N] [--kubeconfig PATH] [--keep-kubeconfig] [--print-kubeconfig-path]
#
# The kubeconfig defaults to .sandbox/kubeconfig.yaml for kubenimbus-sandbox and to
# .sandbox/<N without kubenimbus->-kubeconfig.yaml for any other name, as in sandbox-up (#383),
# and is deleted only when its current-context is N (#379).
set -euo pipefail

NAME=kubenimbus-sandbox
KUBECONFIG_PATH=
KEEP=0
PRINT_KUBECONFIG_PATH=0

while [ $# -gt 0 ]; do
  case "$1" in
    --name)             NAME="$2"; shift 2 ;;
    --kubeconfig)       KUBECONFIG_PATH="$2"; shift 2 ;;
    --keep-kubeconfig)  KEEP=1; shift ;;
    --print-kubeconfig-path) PRINT_KUBECONFIG_PATH=1; shift ;;
    -h|--help)          sed -n '2,8p' "$0"; exit 0 ;;
    *)                  echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# The same rule as sandbox-up (#383): every sandbox but the main one has its own file; falling
# back to the shared one deleted the running kubenimbus-sandbox's kubeconfig on
# `--name kubenimbus-argo` (#379).
if [ -z "$KUBECONFIG_PATH" ]; then
  if [ "$NAME" = kubenimbus-sandbox ]; then FILE=kubeconfig.yaml; else FILE="${NAME#kubenimbus-}-kubeconfig.yaml"; fi
  KUBECONFIG_PATH="$(dirname "$SCRIPT_DIR")/.sandbox/$FILE"
fi
if [ "$PRINT_KUBECONFIG_PATH" = 1 ]; then echo "$KUBECONFIG_PATH"; exit 0; fi

if [ -n "$(docker ps -a --filter "name=^/${NAME}$" --format '{{.Names}}')" ]; then
  docker rm -f "$NAME" >/dev/null
  echo "Removed container '$NAME'."
else
  echo "No container named '$NAME'."
fi

if [ "$KEEP" = 0 ] && [ -f "$KUBECONFIG_PATH" ]; then
  # sandbox-up names the context after the container; another sandbox's file is never ours.
  CONTEXT="$(awk '/^current-context:/ { v = $2; gsub(/["'"'"']/, "", v); print v; exit }' "$KUBECONFIG_PATH")"
  if [ "$CONTEXT" != "$NAME" ]; then
    echo "Warning: kept $KUBECONFIG_PATH: its current context is '$CONTEXT', not '$NAME'. Pass the sandbox's own file with --kubeconfig, or delete it by hand." >&2
  else
    rm -f "$KUBECONFIG_PATH"
    echo "Removed $KUBECONFIG_PATH."
  fi
fi

# A copy installed by --install-kubeconfig is not ours to delete (the user may
# have merged other clusters into it since), but a config still pointing at a
# cluster that no longer exists is worth saying out loud.
if [ -f "$HOME/.kube/config" ] && grep -q "$NAME" "$HOME/.kube/config"; then
  echo "Note: $HOME/.kube/config still references context '$NAME', which no longer exists."
fi
