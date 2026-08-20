#!/usr/bin/env bash
# Brings up a throwaway k3s-in-Docker cluster pre-loaded with demo workloads and
# writes its kubeconfig to .sandbox/kubeconfig.yaml.
#
# POSIX-shell twin of sandbox-up.ps1 — same container name, same port, same
# manifests. Keep the two in step.
#
#   ./scripts/sandbox-up.sh [--name N] [--port P] [--k3s-version V]
#                           [--kubeconfig PATH] [--install-kubeconfig [--force]]
#                           [--recreate] [--skip-apps]
#
# --install-kubeconfig also writes ~/.kube/config (the classic path), so the app
# and kubectl find the cluster with no $KUBECONFIG set — which is what a GUI
# launched from a file manager or an IDE actually sees. An existing
# ~/.kube/config is left alone unless --force (which backs it up first).
set -euo pipefail

NAME=kubenimbus-sandbox
PORT=6550
K3S_VERSION=v1.33.4-k3s1
KUBECONFIG_PATH=
RECREATE=0
SKIP_APPS=0
INSTALL_KUBECONFIG=0
FORCE=0

while [ $# -gt 0 ]; do
  case "$1" in
    --name)         NAME="$2"; shift 2 ;;
    --port)         PORT="$2"; shift 2 ;;
    --k3s-version)  K3S_VERSION="$2"; shift 2 ;;
    --kubeconfig)   KUBECONFIG_PATH="$2"; shift 2 ;;
    --recreate)     RECREATE=1; shift ;;
    --skip-apps)    SKIP_APPS=1; shift ;;
    --install-kubeconfig) INSTALL_KUBECONFIG=1; shift ;;
    --force)        FORCE=1; shift ;;
    -h|--help)      sed -n '2,17p' "$0"; exit 0 ;;
    *)              echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
MANIFEST_DIR="$SCRIPT_DIR/manifests"
: "${KUBECONFIG_PATH:=$REPO_ROOT/.sandbox/kubeconfig.yaml}"

step() { printf '\033[36m==> %s\033[0m\n' "$1"; }
note() { printf '\033[90m    %s\033[0m\n' "$1"; }
kube() { docker exec "$NAME" kubectl "$@"; }

step 'Checking Docker'
docker version --format '{{.Server.Version}}' >/dev/null 2>&1 ||
  { echo 'Docker is not available or the daemon is not running.' >&2; exit 1; }

EXISTING="$(docker ps -a --filter "name=^/${NAME}$" --format '{{.Names}} {{.State}}')"
if [ -n "$EXISTING" ] && [ "$RECREATE" = 1 ]; then
  step "Removing existing container '$NAME'"
  docker rm -f "$NAME" >/dev/null
  EXISTING=
fi

if [ -z "$EXISTING" ]; then
  step "Starting k3s ($K3S_VERSION) as '$NAME', API on 127.0.0.1:$PORT"
  docker run -d --name "$NAME" --privileged -p "${PORT}:6443" \
    "rancher/k3s:${K3S_VERSION}" server --tls-san 127.0.0.1 >/dev/null
elif ! printf '%s' "$EXISTING" | grep -q running; then
  step "Starting existing container '$NAME'"
  docker start "$NAME" >/dev/null
else
  step "Reusing running container '$NAME'"
fi

step 'Waiting for the API server'
RAW=
for _ in $(seq 1 120); do
  if RAW="$(docker exec "$NAME" cat /etc/rancher/k3s/k3s.yaml 2>/dev/null)" &&
     printf '%s' "$RAW" | grep -q 'clusters:'; then
    break
  fi
  RAW=
  sleep 1
done
[ -n "$RAW" ] || { echo "Timed out waiting for k3s. Check: docker logs $NAME" >&2; exit 1; }

# Point at the published host port, and give the context a name worth showing in
# the app's tab strip (k3s calls everything "default").
mkdir -p "$(dirname "$KUBECONFIG_PATH")"
printf '%s' "$RAW" \
  | sed -e "s|https://127.0.0.1:6443|https://127.0.0.1:${PORT}|" -e "s/\bdefault\b/${NAME}/g" \
  > "$KUBECONFIG_PATH"
note "kubeconfig → $KUBECONFIG_PATH (context: $NAME)"

if [ "$INSTALL_KUBECONFIG" = 1 ]; then
  # The classic path. An app started from a file manager or an IDE inherits no
  # $KUBECONFIG, so this is the only place it will look.
  CLASSIC="$HOME/.kube/config"
  mkdir -p "$(dirname "$CLASSIC")"
  if [ -f "$CLASSIC" ] && [ "$FORCE" = 0 ]; then
    echo "warning: $CLASSIC already exists — not touching it. Re-run with --force to replace it (a backup is kept), or export KUBECONFIG=$KUBECONFIG_PATH instead." >&2
  else
    if [ -f "$CLASSIC" ]; then
      BACKUP="$CLASSIC.$(date +%Y%m%d-%H%M%S).bak"
      cp "$CLASSIC" "$BACKUP"
      note "backed up existing config → $BACKUP"
    fi
    cp "$KUBECONFIG_PATH" "$CLASSIC"
    chmod 600 "$CLASSIC"
    note "kubeconfig → $CLASSIC (classic path)"
    note '--recreate mints a new CA and client certs; re-run with --install-kubeconfig --force to refresh this copy.'
  fi
fi

step 'Waiting for the node to become Ready'
# `kubectl wait --all` fails outright when the collection is still empty, and the
# node object appears a few seconds after the API server starts serving.
for _ in $(seq 1 120); do
  [ -n "$(kube get nodes --no-headers 2>/dev/null)" ] && break
  sleep 1
done
kube wait --for=condition=Ready node --all --timeout=180s >/dev/null

if [ "$SKIP_APPS" = 1 ]; then
  step 'Skipping demo workloads (--skip-apps)'
else
  step 'Applying demo workloads'
  docker exec "$NAME" rm -rf /kubenimbus-manifests
  docker cp "$MANIFEST_DIR" "${NAME}:/kubenimbus-manifests" >/dev/null

  note '00-namespaces.yaml'
  kube apply -f /kubenimbus-manifests/00-namespaces.yaml >/dev/null

  # A brand-new namespace has no `default` ServiceAccount for a second or two,
  # and a pod created before it exists is rejected outright.
  for ns in demo-shop demo-data demo-batch demo-broken; do
    for _ in $(seq 1 60); do
      kube get serviceaccount default -n "$ns" >/dev/null 2>&1 && break
      sleep 1
    done
  done

  for f in 10-shop.yaml 20-data.yaml 30-batch.yaml 40-broken.yaml 50-crds.yaml 70-argocd-crds.yaml; do
    note "$f"
    kube apply -f "/kubenimbus-manifests/$f" >/dev/null
  done

  # CRs need their CRD's endpoint to exist first.
  kube wait --for=condition=Established \
    crd/widgets.shop.kubenimbus.io crd/widgets.factory.kubenimbus.io \
    crd/backups.demo.kubenimbus.io crd/applications.argoproj.io crd/appprojects.argoproj.io \
    --timeout=60s >/dev/null
  note '51-custom-resources.yaml'
  kube apply -f /kubenimbus-manifests/51-custom-resources.yaml >/dev/null
  note '60-rbac.yaml'
  kube apply -f /kubenimbus-manifests/60-rbac.yaml >/dev/null
  note '71-argocd-applications.yaml'
  kube apply -f /kubenimbus-manifests/71-argocd-applications.yaml >/dev/null

  # k3s stores its own bundled charts as real Helm release Secrets, but each at
  # revision 1. This one carries three revisions so the release history view has
  # something to page through. It is a record only — nothing is installed by it.
  step 'Seeding a multi-revision Helm release (demo-shop/checkout)'
  TEMPLATE="$(cat "$MANIFEST_DIR/helm-release.template.json")"
  FIRST="$(date -u -d '6 days ago' +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u +%Y-%m-%dT%H:%M:%SZ)"
  # revision:status:chart:app:replicas:daysAgo:description
  for SPEC in \
    '1:superseded:0.1.0:1.4.0:1:6:Install complete' \
    '2:superseded:0.2.0:1.5.0:2:2:Upgrade complete' \
    '3:deployed:0.2.1:1.5.1:3:0:Upgrade complete'
  do
    IFS=: read -r REV STATUS CHART APP REPLICAS AGO DESC <<< "$SPEC"
    DEPLOYED="$(date -u -d "$AGO days ago" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u +%Y-%m-%dT%H:%M:%SZ)"
    PAYLOAD="$(printf '%s' "$TEMPLATE" \
      | sed -e "s|__REVISION__|$REV|g" -e "s|__STATUS__|$STATUS|g" \
            -e "s|__CHART_VERSION__|$CHART|g" -e "s|__APP_VERSION__|$APP|g" \
            -e "s|__REPLICAS__|$REPLICAS|g" -e "s|__DESCRIPTION__|$DESC|g" \
            -e "s|__FIRST_DEPLOYED__|$FIRST|g" -e "s|__LAST_DEPLOYED__|$DEPLOYED|g" \
      | gzip -c | base64 | tr -d '\n')"

    # Helm's storage format: base64(gzip(json)) — Kubernetes then base64s the
    # Secret value on top, which is what stringData does for us here.
    kube apply -f - >/dev/null <<YAML
apiVersion: v1
kind: Secret
metadata:
  name: sh.helm.release.v1.checkout.v${REV}
  namespace: demo-shop
  labels:
    owner: helm
    name: checkout
    version: "${REV}"
    status: ${STATUS}
type: helm.sh/release.v1
stringData:
  release: ${PAYLOAD}
YAML
  done
fi

step 'Cluster contents'
kube get pods -A --no-headers 2>/dev/null | awk '{print $1}' | sort | uniq -c |
  while read -r count ns; do note "$(printf '%-14s %s pods' "$ns" "$count")"; done

cat <<EOF

$(printf '\033[32mSandbox is up.\033[0m')

  Run the app against it:
    export KUBECONFIG="$KUBECONFIG_PATH"
    dotnet run --project src/KubeNimbus.App

  Run the integration tests (auto-discovers .sandbox/kubeconfig.yaml):
    dotnet test tests/KubeNimbus.Core.Tests/KubeNimbus.Core.Tests.csproj

  Tear down:  ./scripts/sandbox-down.sh --name $NAME

  Note: some demo workloads are broken on purpose (demo-broken namespace) so the
  error/pending/crashloop states have something to render.
EOF
