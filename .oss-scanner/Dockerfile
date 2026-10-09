# The image Anthropic's OSS Scanner audits kubeNimbus in (https://github.com/anthropics/oss-scanner).
#
# The scanner builds this with network access and then audits the image with none, so everything a build, a test run
# or a reproducer needs is fetched here: every NuGet package the solution restores, and k3s, whose API server
# `. .oss-scanner/services.sh` starts for reproducers that want real cluster objects. threat_model.md beside this file
# tells the scanner what to look at.
#
# Nothing in CI builds this. To check it after a change, from the repository root (wslc works the same as docker):
#
#   docker build -f .oss-scanner/Dockerfile -t kubenimbus-scan .
#   docker run --rm -it --network=none kubenimbus-scan bash
#   dotnet test --project tests/KubeNimbus.Core.Tests/KubeNimbus.Core.Tests.csproj -c Release --no-build
#
# The SDK tag floats on purpose: global.json takes any 10.0 SDK from 10.0.100 up, and this image ships nowhere.
FROM mcr.microsoft.com/dotnet/sdk:10.0

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# libfontconfig1 is what Skia needs for the screenshot harness; procps for ps and pgrep.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl libfontconfig1 procps \
 && rm -rf /var/lib/apt/lists/*

# k3s, the version scripts/sandbox-up.sh runs, checked against the digests GitHub publishes for the release assets.
# kubectl is the same binary under another name.
ARG K3S_VERSION=v1.33.4+k3s1
RUN set -e; \
    case "$(dpkg --print-architecture)" in \
      amd64) asset=k3s;       sha=10da34c350ab8a02e4513a6021046db9e9afecc31bae77419bc6444cbd7b1400 ;; \
      arm64) asset=k3s-arm64; sha=0ba95d3933c90f4c72a502e1a0b39f3d1af311cc9961d2e049e321dd6db88be6 ;; \
      *) echo "no k3s build for $(dpkg --print-architecture)" >&2; exit 1 ;; \
    esac; \
    curl -fsSL -o /usr/local/bin/k3s \
      "https://github.com/k3s-io/k3s/releases/download/$(echo "$K3S_VERSION" | sed 's/+/%2B/')/$asset"; \
    echo "$sha  /usr/local/bin/k3s" | sha256sum -c -; \
    chmod 755 /usr/local/bin/k3s; \
    ln -s k3s /usr/local/bin/kubectl

COPY . /src
WORKDIR /src

# Every project in the solution: the app, both test suites and the screenshot harness. That leaves every package an
# offline rebuild needs in the NuGet cache. Release, as CI builds it (portable pdbs are on by default).
RUN dotnet build KubeNimbus.slnx -c Release

# The two suites CI runs. Their live tests skip, as in CI: no sandbox cluster is configured. A failing test is
# reported, not fatal: the image is still useful.
RUN (dotnet test --project tests/KubeNimbus.Core.Tests/KubeNimbus.Core.Tests.csproj -c Release --no-build \
       || echo "WARNING: KubeNimbus.Core.Tests reported failures") \
 && (dotnet test --project tests/KubeNimbus.App.Tests/KubeNimbus.App.Tests.csproj -c Release --no-build \
       || echo "WARNING: KubeNimbus.App.Tests reported failures")

# Start the API server once, so k3s unpacks itself now rather than during the audit, then throw its state away so
# every scan starts from an empty cluster. Not fatal either: the suites above need no cluster.
RUN . .oss-scanner/services.sh; \
    kubectl get namespaces || echo "WARNING: the k3s API server did not start"; \
    pkill -f "k3s server" || true; \
    sleep 2; \
    rm -rf /var/lib/rancher/k3s/server /etc/rancher/k3s /var/log/k3s.log
