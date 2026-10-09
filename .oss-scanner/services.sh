#!/bin/sh
# Starts a Kubernetes API server inside the OSS Scanner image (see the Dockerfile beside this file), for reproducers
# that need real cluster objects rather than the loopback stand-in the tests use (ScriptedApiServer). Source it:
#
#   . .oss-scanner/services.sh
#
# It is k3s with no agent: a real API server that validates and stores objects, but no node, so a pod is created and
# never runs (no logs, no exec, no port-forward). kubectl reads /etc/rancher/k3s/k3s.yaml, which this writes. Nothing
# here turns the suite's live tests on; KUBENIMBUS_TEST_KUBECONFIG=/etc/rancher/k3s/k3s.yaml does, and the ones that
# need a running pod then fail. Running it again is harmless: a server that is already up is left alone. POSIX sh,
# because the Dockerfile's RUN steps source it from /bin/sh.

# The three 127.0.0.1 addresses are not optional: with no network there is no default route, and k3s refuses to start
# when it has to pick an address from one.
if ! k3s kubectl get --raw /readyz >/dev/null 2>&1; then
    nohup k3s server --disable-agent \
        --node-ip 127.0.0.1 --advertise-address 127.0.0.1 --bind-address 127.0.0.1 \
        --disable=traefik,servicelb,metrics-server,local-storage,coredns \
        --disable-network-policy --disable-helm-controller --disable-cloud-controller \
        --write-kubeconfig-mode 600 >/var/log/k3s.log 2>&1 &
    waited=0
    until k3s kubectl get --raw /readyz >/dev/null 2>&1; do
        waited=$((waited + 1))
        if [ "$waited" -ge 90 ]; then
            echo "k3s did not answer within 90 s; see /var/log/k3s.log" >&2
            break
        fi
        sleep 1
    done
fi
