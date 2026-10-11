# Microsoft Store listing text

The text of the Store listing (Partner Center → kubeNimbus → the submission → Store listings →
English), kept in the repository so that the listing is reviewed like the rest of the project
instead of living only in a web form. Nothing in the release workflow uploads it: pasting it
into Partner Center is a manual step, described in
[`docs/RELEASE-CHECKLIST.md`](../../../docs/RELEASE-CHECKLIST.md).

Product ID `9MZ3C28M65PB`. Field limits are Partner Center's own: What's new 1,500 characters,
description 10,000, short description 1,000, 20 product features of 200 characters each, 7 search
terms. Every fenced block below is pasted as it stands.

**Written for 0.7.0.** "What's new" describes the changes since 0.6.0; the rest of the listing
describes the whole app. (0.4.0 was submitted and failed certification on policy 10.5.1, below,
which is why 0.6.0's text carried everything since 0.4.0.) Keep this file's date in step with the
release it describes.

## Privacy policy URL

Partner Center → Properties → Privacy policy URL:

```text
https://github.com/Shman4ik/kubeNimbus/blob/main/PRIVACY.md
```

The submission of 2026-09-28 failed **10.5.1 Personal Information – Privacy Policy** with "The
privacy policy link provided resolves to a webpage that doesn't display a privacy policy", for
the URL `…/blob/main/SECURITY.md`. `SECURITY.md` is a security policy: it states what the app
does not do, and it says nothing about what is collected, why, where it is kept or how to have it
removed. [`PRIVACY.md`](../../../PRIVACY.md) is the page that answers those, and the same address
is linked from the app's About box (10.5.1 also wants the policy reachable from inside the
product). Do not point the field back at another file, and do not rename `PRIVACY.md` without
changing the field, the About box and the README together: the address has to keep resolving to a
page that reads as a privacy policy for as long as a listing points at it.

Also on the Properties page: the answer to "does this product access, collect or transmit personal
information" is **Yes** (Partner Center forces it for a `runFullTrust` package, and the policy
covers the kubeconfig the app reads); support contact `https://github.com/Shman4ik/kubeNimbus/issues`,
website `https://github.com/Shman4ik/kubeNimbus`, category **Developer tools**. Age rating and
data-collection answers stay as they are (no data is collected).

## What's new in this version

```text
0.7: Work on several things at once. Choose several namespaces in either list, select several resources and delete them with one confirm, and pick the columns each kind shows. Paste a manifest into "New resource from YAML". Port-forwards keep running after their dock tab closes, and a Service can be forwarded to a ready pod behind it.

Argo CD: terminate a running sync, open ApplicationSets and AppProjects, and see every connected cluster's applications in one dashboard. Syncs now carry the application's own sync options.

Also new: a capacity strip above the Nodes list (CPU and memory used and requested, no Prometheus needed), how long each condition has held, sorting by any column in the Applications list, pod lists, Helm releases and Argo CD, exec and node shells in your own terminal, and "Signed in as" in the access review.

Safer connections: the API server's certificate is checked for its host name as kubectl does, kubeconfig impersonation and token files are honoured, and a delete on a production cluster always asks. Actions you can take back, such as Sync or Cordon, now act on the click.

Fixed: a sign-in plugin runs once per connect instead of twice, copying from the exec terminal and the log menu works, the last log line clears the scroll bar, and the keyboard reaches every part of the window.
```

## Short description

```text
A fast, free, open-source Kubernetes desktop client. Opens on a health view of your applications, follows pod logs, opens a real terminal in containers and edits YAML safely. Native, opens in about 150 ms, no telemetry, no account.
```

## Description

```text
kubeNimbus is a desktop client for Kubernetes, built as a native application rather than a packaged web browser. It starts in about 150 milliseconds, ships as a self-contained program with no runtime to install, and sends no telemetry of any kind.

It opens on the question you usually have in a hurry: which service is broken, and why. Every Argo CD application, and every workload no application tracks, is listed with its health and a one-line reason read from the cluster's own status. One Enter opens the facts, the pods and the right log line. The full resource explorer is one click away.

It reads your existing kubeconfig: every context in $KUBECONFIG and ~/.kube/config, including exec-plugin sign-in for AWS EKS, Google GKE and Azure AKS, and clusters behind a proxy. Nothing is copied into the app: no token, no certificate and no credential is ever written to app storage.

What it does
• Applications view: health and a one-line reason for every app, what needs attention first, a timeline of the last hour, what the last deploy changed, and the crashing pod's last run. Works with narrow permissions and says what it could not read.
• Live resource lists for every kind your cluster serves, built from the discovery API. Custom resources are first-class, with the columns their own CustomResourceDefinition declares, as kubectl prints them.
• Pod logs with follow, search, regular expressions, level filters, error jumps, previous-container logs for a CrashLoopBackOff, and all of a workload's pods merged into one colour-keyed stream.
• Pod detail: environment variables with ConfigMap values resolved and Secret values masked until you ask; conditions, probes and QoS; CPU and memory over time.
• A real terminal for exec: vi, top and mc draw properly, and Ctrl+C reaches the container.
• Port-forward, with the local address one click from being opened.
• YAML editing with server-side apply, strict field validation and a dry-run preview that asks the API server what a change would do before it does it.
• Scale, rollout restart, delete, cordon and drain. A restart, a drain and a delete are armed and named before they fire, and a delete on a production cluster always asks; elsewhere you can turn the delete confirm off. Scale asks for the new count first.
• Service, Ingress and NetworkPolicy panes that state, in words, whether traffic can reach the pods.
• Node detail with allocatable-versus-requested headroom, events and measured usage.
• Helm releases read straight off the cluster, no Helm binary needed. Argo CD sync and refresh, with no second sign-in.
• RBAC access review in both directions: what may I do here, and who can do X.
• Several clusters at once: tabs per context, a fuzzy cluster switcher, colour that tells production from staging, and one list across the whole fleet.

No cluster yet? The app ships with a built-in demo cluster: sample data served from inside the program, with no network and no credentials, so you can look around before wiring anything up.

Your data stays yours. kubeNimbus connects only to the clusters you open. It has no account, no update check, no analytics and no crash reporting. The privacy policy is linked from this page and from the About box.

kubeNimbus is free and open source under the MIT license: https://github.com/Shman4ik/kubeNimbus
```

## Product features

One line per field.

```text
Opens on Applications: health and a one-line reason for every app
Native and fast: opens in about 150 ms, no runtime to install
No telemetry, no account; credentials are never stored
Every kind your cluster serves, custom resources included
Live pod logs: search, regex, level filters, error jump
All of a workload's pods in one merged log stream
A real terminal in the container (vi, top and mc work)
YAML editing with server-side apply and dry-run preview
Scale, restart, delete and drain; production deletes always ask
Service, Ingress and NetworkPolicy panes in plain words
Argo CD sync and Helm releases, read from the cluster
RBAC access review: what can I do, who can do X
Many clusters at once, prod/staging/dev colour, fleet lists
Built-in demo cluster: try it with no cluster at all
Free and open source (MIT)
```

## Search terms

At most seven. Product names of other companies do not go in Store metadata.

```text
kubernetes
k8s
kubectl
cluster
devops
containers
yaml
```

## Screenshots

From [`design/store/screenshots/`](../screenshots/README.md), in this order, half light and
half dark. Captions, where the field exists:

1. Applications: every app with a health verdict and a one-line reason
2. One Enter to the why: the facts behind a verdict, and the pod's last run
3. Pod detail docked along the bottom, with live logs
4. YAML editing with server-side apply
5. One list across several clusters
6. The cluster switcher, with prod, staging and dev colour
7. A real terminal in the container
8. RBAC: who can do what

Uploaded on 2026-09-29, replacing an older 1280-wide set; no captions are set in Partner Center.
Two of them carry the demo cluster's banner, which says the data is sample data. Keep it in.

## Copyright and trademark info

```text
Copyright (c) 2026 Shman4ik. Released under the MIT license.
```

## Notes for certification

Partner Center → Submission options → Notes for certification:

```text
kubeNimbus is a Kubernetes client, and no Kubernetes cluster is needed to test it. On a machine with no kubeconfig the first screen offers "Explore demo cluster": press it. The demo is sample data built into the app; it makes no network connection and needs no credentials or account. In the demo, browse the Applications list and open an application with Enter, open Resources (top bar, next to Applications), open a Pod and its logs, and press Ctrl+K for the command palette. Exec, port-forward and YAML apply need a real cluster and say so in the demo.

The app makes no network connection other than to the Kubernetes API servers of the contexts the user opens, and collects and sends no data of its own. The privacy policy (Properties page, and About box → Privacy policy) describes what is stored on the device.

The package uses the runFullTrust capability because the app is a native desktop program (NativeAOT, no packaged-app runtime). It runs the credential programs a user's own kubeconfig names (for example aws or kubelogin) and can open the user's terminal, as kubectl does.
```
