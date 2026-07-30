#!/usr/bin/env bash
# Tears down a sandbox cluster started by sandbox-up.sh.
#
#   ./scripts/sandbox-down.sh [--name N] [--kubeconfig PATH] [--keep-kubeconfig]
set -euo pipefail

NAME=kubenimbus-sandbox
KUBECONFIG_PATH=
KEEP=0

while [ $# -gt 0 ]; do
  case "$1" in
    --name)             NAME="$2"; shift 2 ;;
    --kubeconfig)       KUBECONFIG_PATH="$2"; shift 2 ;;
    --keep-kubeconfig)  KEEP=1; shift ;;
    -h|--help)          sed -n '2,4p' "$0"; exit 0 ;;
    *)                  echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${KUBECONFIG_PATH:=$(dirname "$SCRIPT_DIR")/.sandbox/kubeconfig.yaml}"

if [ -n "$(docker ps -a --filter "name=^/${NAME}$" --format '{{.Names}}')" ]; then
  docker rm -f "$NAME" >/dev/null
  echo "Removed container '$NAME'."
else
  echo "No container named '$NAME'."
fi

if [ "$KEEP" = 0 ] && [ -f "$KUBECONFIG_PATH" ]; then
  rm -f "$KUBECONFIG_PATH"
  echo "Removed $KUBECONFIG_PATH."
fi

# A copy installed by --install-kubeconfig is not ours to delete (the user may
# have merged other clusters into it since), but a config still pointing at a
# cluster that no longer exists is worth saying out loud.
if [ -f "$HOME/.kube/config" ] && grep -q "$NAME" "$HOME/.kube/config"; then
  echo "Note: $HOME/.kube/config still references context '$NAME', which no longer exists."
fi
