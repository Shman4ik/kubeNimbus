# Security Policy

kubeNimbus is a desktop client that talks to Kubernetes API servers with your
credentials. That makes its security posture worth stating explicitly rather
than leaving implied.

## Supported versions

Only the **latest release** receives security fixes; from 1.0 on, that is the
latest 1.x release. There are no maintained release branches: a fix ships in
the next release, and an older release is not patched in place, so the remedy
for a vulnerable version is to update. Releases before 1.0 (0.x) stop being
supported when 1.0 is out.

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
(401), the plugin is run again rather than its previous output reused. The
same holds for the terminal "Open a terminal on this cluster" starts: the
shells Windows ships are taken from its system folder by full path, and
every other terminal is found on `PATH` before it is started, never in the
current directory. A kubeconfig folder added in Preferences trusts every
kubeconfig placed in it, the same way a directory on `PATH` trusts every
program in it; kubeNimbus lists the contexts it finds there but never
connects to one of them on its own.

**The app is read-mostly, and every write is one you started.** Nothing changes
on a cluster except through an action you take on a named object: server-side
apply from the YAML editor (shown as a server-side dry run first, unless you
turn that off), delete, scale, rollout restart, cordon and uncordon, drain, a
CronJob's run-now, suspend and resume, an Argo CD sync or refresh, adding a
debug container to a pod, and the exec sessions and port-forwards you open. An
action that destroys, disrupts or cannot be taken back (delete, drain, rollout
restart, an Argo CD sync with prune, a CronJob's run-now and resume) names its
object and its cluster and asks first; one that is taken back by its twin or
only moves the cluster toward what is already declared (cordon, uncordon,
suspend, an Argo CD sync without prune, a refresh) runs on the click and
reports in place. Scale takes its number in the same strip, and adding a debug
container is a button of its own whose text says the container stays in the
pod. "Confirm before deleting" can be turned off, except on a cluster
classified or marked as production, where a delete always asks. There is no
background mutation, no auto-apply, and no "fix it for you" behaviour.

**Data from a cluster is untrusted input.** Anyone who can write some objects
or some output in a cluster — another tenant, a CI pipeline, a compromised
workload, the author of a CRD or an Argo CD Application — may be trying to
attack whoever opens that cluster with broader rights, and that is in scope. A
name taken from another object (an owner reference, an Event, an Argo CD
status) never builds a request path unchecked, and what a lookup returns must
match the reference it came from. Terminal escape sequences in logs are
removed, characters that reorder or hide text (bidirectional overrides,
zero-width spaces) are shown as markers, the exec terminal never writes the
emulator's replies back into the container, and pasting into it is filtered and
asks before sending several lines to a shell that would run them at once. Links
taken from cluster data open only as `http`/`https`. Parsers and decoders have
limits (nesting depth, line length, decompressed size), and an object that
cannot be read is skipped and named rather than taking the rest of its list
with it.

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
clipboard sync, and is cleared after a minute if it is still there. The masking
covers a Secret's own values and the references to them; it does not cover
what other objects carry in the clear. A Helm release's values and manifest are
shown as `helm get values` and `helm get manifest` show them, an Argo CD
Application's inline Helm values as `kubectl get` does, and a container's logs
as it wrote them — each of which can hold a credential the chart, the
application or the program was given.

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

Packages come from nuget.org only. The repository's `nuget.config` clears every
inherited package source and maps every package ID to nuget.org, so a feed
listed in a developer's own NuGet configuration cannot supply a package to a
build of this repository.

## Release integrity

Release binaries are built and published by `.github/workflows/release.yml` on
GitHub-hosted runners, never on a developer's machine. They are **not
code-signed** yet: Authenticode and Apple Developer ID signing are planned for
after 1.0, and until then Windows users who want a signed build can install
from the Microsoft Store, whose package Microsoft signs. What a download can be
checked against instead (the commands are in the README, "Verifying the
download"):

- **`SHA256SUMS.txt`** on the release page. It detects a damaged download, but
  it sits beside the files it describes and proves nothing on its own.
- **Release attestations.** Releases are immutable: once published, a release's
  tag and files cannot be changed, and GitHub signs a record of the files it was
  published with. `gh release verify-asset` checks a downloaded file against it.
- **Build provenance.** The release job signs a provenance attestation for every
  file (Sigstore, recorded in its public transparency log), stating the workflow
  run and commit that built it. `gh attestation verify` checks it.

Every action a workflow uses is pinned to a full commit SHA. Tools a release
downloads and runs are pinned by version and SHA-256 and checked before they
run; today that is appimagetool and the AppImage runtime it embeds. Workflow
checkouts do not keep the job's token in the working copy, and a real release
can only be dispatched from `main`.
