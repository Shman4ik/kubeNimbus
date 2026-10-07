# Security Policy

kubeNimbus is a desktop client that talks to Kubernetes API servers with your
credentials. That makes its security posture worth stating explicitly rather
than leaving implied.

## Supported versions

kubeNimbus is pre-1.0. Only the **latest release** receives security fixes.
There are no maintained release branches yet.

## Reporting a vulnerability

**Do not open a public issue for a security vulnerability.**

Report it privately through GitHub's
[private vulnerability reporting](https://github.com/Shman4ik/kubeNimbus/security/advisories/new)
(Security → Report a vulnerability). If that is unavailable to you, email
<shman4ik@gmail.com> with `kubeNimbus security` in the subject.

Please include the version, platform, what an attacker gains, and steps to
reproduce. A proof-of-concept helps but is not required.

**What to expect:** an acknowledgement within 7 days, and an assessment within
30. This is a small volunteer-run project — there is no bug bounty and no
paid on-call, so those are honest targets, not an SLA. Fixes ship in the next
release, credited to you unless you ask otherwise. Please give us a chance to
release a fix before disclosing publicly.

## Security model

These are the properties kubeNimbus intends to hold. A break in any of them is
a vulnerability worth reporting.

**Credentials are never persisted by the app.** Kubeconfig is the single source
of truth. kubeNimbus reads every `$KUBECONFIG` entry plus `~/.kube/config` at
connect time and re-resolves through that chain on every connection. Tokens,
client certificates and exec-plugin output are never copied into application
storage. The files the app does write hold context *names*, paths and display
choices, never credential material; the [privacy policy](PRIVACY.md) lists every
one of them. On Linux and macOS they are readable by your user only.

**The API server is verified the way `kubectl` verifies it.** Its certificate
must chain to the kubeconfig's `certificate-authority` — only that CA, never the
system store, when the kubeconfig names one — and must be valid for the server's
host name, or for the cluster's `tls-server-name` when it sets one. kubeNimbus
performs this check itself rather than relying on its Kubernetes client
library's, for every request including exec and port-forward. A cluster entry
with `insecure-skip-tls-verify: true` is not verified, as with `kubectl`, and
the app says so for as long as such a cluster is connected.

**Impersonation in the kubeconfig is honoured.** A user entry's `as`,
`as-uid`, `as-groups` and `as-user-extra` are sent as `kubectl` sends them, so
the app acts as the identity the context names, never as the more privileged
base identity. An entry kubeNimbus cannot send exactly (more than one group, or
more than one value for an extra) is refused rather than half-honoured.

**Exec-plugin auth runs external programs.** Contexts using
`aws eks get-token`, `gke-gcloud-auth-plugin`, `azure kubelogin` and friends
work by kubeNimbus executing the command your kubeconfig names, exactly as
`kubectl` does. A malicious kubeconfig can therefore run arbitrary code —
treat a kubeconfig from an untrusted source the way you would treat a shell
script from one. This is inherent to the kubeconfig format, not specific to
kubeNimbus. A command named bare (`command: aws`) that is not on the app's own
`PATH` is also looked for in the directories a login shell adds (`/usr/local/bin`,
`/opt/homebrew/bin`, `/opt/local/bin`, `~/.local/bin`, `~/bin`) — the same
program a terminal on the same machine would run. A command that is found in
neither is refused rather than looked up in the current directory, and `PATH`
entries that are not absolute are ignored. When a credential is rejected
(401), the plugin is run again rather than its previous output reused.

**The app is read-mostly, and every write is explicit.** Writes happen only
through actions you take: server-side apply from the YAML editor, delete
(two-step confirm), exec, and port-forward. There is no background mutation,
no auto-apply, and no "fix it for you" behaviour.

**No telemetry, ever.** kubeNimbus makes no network connection other than to
the Kubernetes API servers of the contexts you connect to (through the proxy a
cluster's `proxy-url` names, when it names one). No analytics, no
crash reporting, no update pings. This is a permanent non-goal, not a default
that might change. What the app stores on your computer, and everything it does
send, is listed in the [privacy policy](PRIVACY.md).

**RBAC answers come from the API server where one exists.** "My permissions"
is a real `SelfSubjectRulesReview` and per-subject verification is a real
`SubjectAccessReview`; kubeNimbus never re-implements authorization locally
for those. The cluster-wide "who can do X?" view *is* a local scan of RBAC
objects (Kubernetes serves no endpoint for that direction), which is why it is
labelled in-app as provenance rather than an authorization decision — it
cannot see webhook or node authorizers. Treat its output accordingly.

**Secret values stay masked until asked for.** Secret `data` renders base64 in
the YAML editor, as `kubectl` does; decoding is a separate, explicit toggle,
and env-var references reveal one key at a time on demand. A decoded value
copied to the clipboard is kept out of Windows clipboard history and cloud
clipboard sync, and is cleared after a minute if it is still there.

## Out of scope

- An attacker who already controls your kubeconfig, your machine, or an
  account with cluster-admin. kubeNimbus has exactly the access your
  credentials do — it is not a security boundary.
- Anything a cluster's own RBAC permits your user to do.
- Vulnerabilities in Kubernetes itself, or in a cluster's workloads.

## Dependencies

Dependency vulnerabilities are treated as security issues. Builds run with
`NuGetAuditMode=all` and NuGet audit warnings (`NU1902`/`NU1903`/`NU1904`) are
errors, so a known-vulnerable package fails CI rather than shipping quietly.
Dependabot watches NuGet and GitHub Actions.
