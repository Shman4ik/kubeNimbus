#!/bin/bash
# Prepares a Claude Code on the web container to actually build kubeNimbus.
#
# Without this, every session — and every backlog-loop cycle inside it — pays
# the same 3-4 minutes to rediscover the same two facts: that the container has
# no .NET SDK, and that every host the dotnet-install script reaches
# (builds.dotnet.microsoft.com, aka.ms, dot.net) answers 403 through the agent
# proxy. Ubuntu's own archive is not blocked and carries dotnet-sdk-10.0, which
# is the route that works here.
#
# Local sessions are left alone: `dotnet` is already on a developer's machine,
# and apt-installing a second SDK under one would be rude.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

echo "[kubeNimbus] preparing the container…"

# Third-party PPAs in the base image (deadsnakes, ondrej, docker) are blocked by
# the egress policy. Nothing here needs them, so retry without them rather than
# disabling anything up front — a future image may not carry them at all.
#
# `APT::Update::Error-Mode=any` is load-bearing and was arrived at the hard way:
# plain `apt-get update` exits **0** with an unreachable repository, printing the
# failure to stderr and carrying on. The blocked PPA would then surface much
# later as "package not found" from `apt-get install`, long past the point where
# this function could do anything about it.
apt_update() {
  if apt-get update -o APT::Update::Error-Mode=any -qq >/dev/null 2>&1; then
    return 0
  fi
  echo "[kubeNimbus] a repository is unreachable; retrying without third-party sources"
  mkdir -p /var/backups/claude-apt-sources.d
  find /etc/apt/sources.list.d -maxdepth 1 -type f \
    \( -name '*.list' -o -name '*.sources' \) ! -name 'ubuntu.sources' \
    -exec mv -t /var/backups/claude-apt-sources.d/ {} +
  apt-get update -o APT::Update::Error-Mode=any -qq >/dev/null 2>&1
}

# dotnet-sdk-aot-10.0 is the half that makes `-p:PublishAot=true` work; clang and
# the zlib headers are what ILCompiler links against. xvfb/xdotool/imagemagick are
# how a headless session drives and photographs the real window — the screenshot
# harness needs no display, but "does the published binary actually open a
# window" does, and that is the check three broken release binaries went out for
# want of.
PACKAGES=(dotnet-sdk-10.0 dotnet-sdk-aot-10.0 clang zlib1g-dev xvfb xdotool imagemagick)

MISSING=()
for pkg in "${PACKAGES[@]}"; do
  dpkg -s "$pkg" >/dev/null 2>&1 || MISSING+=("$pkg")
done

# Nothing below is allowed to abort the session. A container that starts without
# a toolchain is worth a loud warning and the recipe; a session that refuses to
# start at all is not, and the agent can still install by hand.
if [ ${#MISSING[@]} -gt 0 ]; then
  echo "[kubeNimbus] installing: ${MISSING[*]}"
  if ! { apt_update && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "${MISSING[@]}"; }; then
    echo "[kubeNimbus] WARNING: could not install ${MISSING[*]}."
    echo "[kubeNimbus] Install by hand with: apt-get update && apt-get install -y ${PACKAGES[*]}"
    echo "[kubeNimbus] Do NOT reach for the dotnet-install script — every host it uses is blocked here."
  fi
else
  echo "[kubeNimbus] toolchain already present"
fi

# The container image is cached after this hook completes, so a warm NuGet cache
# is paid for once and reused by every later session. `restore`, not `build`:
# restoring is the slow, network-bound half, and building here would bake an
# artifact from whatever commit happened to be checked out.
cd "${CLAUDE_PROJECT_DIR:-$(dirname "$0")/../..}"
if dotnet --version >/dev/null 2>&1; then
  echo "[kubeNimbus] restoring packages…"
  dotnet restore KubeNimbus.slnx || echo "[kubeNimbus] WARNING: restore failed; the first build will retry it"
fi

# Kept out of the agent's way rather than repeated in every command line. The
# telemetry opt-out is not decoration: this repo promises the app makes no
# network connection other than to the clusters you point it at, and its build
# should hold the same line.
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1'
    echo 'export DOTNET_NOLOGO=1'
    echo 'export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1'
  } >> "$CLAUDE_ENV_FILE"
fi

echo "[kubeNimbus] ready — $(dotnet --version)"
