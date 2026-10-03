# Privacy Policy

Effective 3 October 2026. This policy covers kubeNimbus, the desktop Kubernetes
client, in every form it is distributed: the Microsoft Store package, the installers
and archives on the GitHub Releases page, and builds made from this repository.

kubeNimbus has no account, no cloud service and no telemetry. It sends nothing to the
developer or to any third party. Everything it keeps stays on your computer, and this
page lists all of it.

## The short version

kubeNimbus collects nothing. It has no analytics, no crash reporting, no update check
and no account. The only network connections it makes are the ones you ask for, to the
Kubernetes clusters you choose to open. It stores its settings and your window layout in
a folder on your computer, and it never stores a password, token or certificate: it reads
your kubeconfig each time it connects.

## What the app reads on your computer

To connect to a cluster, kubeNimbus reads your kubeconfig files: the files named in
`$KUBECONFIG`, `~/.kube/config`, and any files or folders you add in Preferences.
Kubeconfig files can contain personal information and credentials, such as user names,
access tokens, client certificates and the names of credential programs. kubeNimbus uses
them only to connect to the cluster you open, and only on your computer. It never modifies
them.

When a kubeconfig names a credential program (for example `aws eks get-token`,
`gke-gcloud-auth-plugin` or `kubelogin`), kubeNimbus runs that program the same way
`kubectl` does, and the program may contact its own sign-in service. That contact is made
by the program your kubeconfig names, under that provider's own privacy terms. kubeNimbus
keeps the token such a program returns in memory for that connection and does not write it
anywhere. When you ask for a terminal on a cluster, kubeNimbus starts your system terminal.

## Where the app connects

- **The Kubernetes API servers of the clusters you open**, using the addresses and
  credentials in your kubeconfig, and through the proxy a cluster's `proxy-url` names when
  it names one. What travels is what you ask for: resource lists and watches, logs, exec
  sessions, port-forwards, YAML you apply, and the actions you take (scale, restart, delete,
  cordon, drain, sync, adding a debug container). All of it goes to your cluster. A debug
  container's image is pulled by your cluster's node from the registry the image name
  points at, not by kubeNimbus.
- **Nowhere else.** kubeNimbus makes no other network connection of its own. There is no
  update check, no usage analytics, no automatic crash report and no remote configuration,
  and nothing is downloaded while it runs: fonts, icons and themes are built into the app.
  The built-in demo cluster is sample data inside the program and uses no network at all.

A few controls open a web page in your browser, and only when you click them. The browser
makes that request, not kubeNimbus:

- **kubeNimbus on GitHub** and **Privacy policy** in the About box open this project's pages.
- **Open in Argo CD** on an application opens the address your own cluster's Argo CD
  configuration names, and the compare link on an application opens the comparison of two
  commits on the Git host that application's repository lives on.
- An Ingress route's address, and the local address of a port-forward, open that address.

## What your cluster sees

- Your IP address, or the proxy's.
- Whatever credential your kubeconfig supplies: a client certificate, a token, or the
  output of the credential program.
- Every request you make, as `kubectl` would make it, with the standard headers of the .NET
  Kubernetes client library.
- The name `kubenimbus` as the field manager of YAML you apply, and as the initiator of an
  Argo CD sync you start. The cluster records both in the objects concerned.

## What the app stores

| System | Preferences and session | Cache |
| --- | --- | --- |
| Windows | `%AppData%\kubeNimbus` | `%LocalAppData%\kubeNimbus` |
| macOS and Linux | `~/.config/kubeNimbus`, or `$XDG_CONFIG_HOME/kubeNimbus` | `~/.local/share/kubeNimbus`, or `$XDG_DATA_HOME/kubeNimbus` |

The version from the Microsoft Store can keep these under
`%LocalAppData%\Packages\DmitriiShmanev.kubeNimbus_5cjm84wd2pj14\LocalCache\Roaming\kubeNimbus`
and `…\LocalCache\Local\kubeNimbus` instead. Which one Windows uses depends on how it runs
packaged apps, so check both.

| File | What it holds | Encrypted |
| --- | --- | --- |
| `settings.json` | Your preferences: theme, keyboard scheme, which sidebar sections are open, sidebar width, log buffer size, metrics refresh interval, whether to confirm deletes and preview applies, whether one click opens an application, interface and code fonts, log display options, and the paths of the kubeconfig files or folders you added. | No |
| `workspace.json` | What the window looked like: your open tabs (context name, kubeconfig path, the kind and namespace showing), pinned and recent contexts, environment labels you corrected, recent namespaces and kinds per cluster, column layouts per kind, and which mode the window was in. Names and paths only. | No |
| `discovery/*.json` (cache folder) | Which resource kinds a cluster serves (kind, API group, version, verbs), so the next connect is faster. The file name is a hash of the server address, context, user name and kubeconfig path. Refreshed after six hours or a server upgrade. | No |
| `terminal/` | Only if you open a terminal on a cluster: a small kubeconfig holding one context **name**, and on macOS a launcher script holding file paths. No cluster address, user or credential. | No |

None of these files contains a password, token, certificate or other credential. The app
never copies credentials out of your kubeconfig, which stays where it is. kubeNimbus writes no
log file and no crash report.

Logs, metrics history and resource lists live in memory while the app is open and are not
saved. Secret values are shown masked until you ask for them, and revealing one decodes it in
memory only. Text goes to a file or the clipboard only when you save or copy it: copying puts
it on the system clipboard, where clipboard history and clipboard managers can keep it, and
saved logs go where you choose in the save dialog.

## Deleting your data

Uninstalling kubeNimbus does not remove its files. To remove them, quit the app and delete
the two folders listed above. That loses your tabs and preferences and nothing else: your
kubeconfig and your clusters are not touched.

## What the developer receives

Nothing. The developer does not receive, collect, store, sell or share any information about
you or your use of the app. There is no server of ours for the app to talk to.

If you contact the project yourself, by opening a GitHub issue or sending an email, what you
send is handled by GitHub or your email provider, and used only to answer you.

## Where you get kubeNimbus

The Microsoft Store and WinGet install through Microsoft, and the other downloads come from
GitHub Releases. Those services handle the download under their own privacy terms. The
Microsoft Store can share aggregate install and crash numbers with the developer in Partner
Center, based on your Windows diagnostic data settings. They contain nothing from your
clusters.

## Building from source

When you build kubeNimbus yourself, Avalonia's build tooling (`Avalonia.BuildServices`)
sends anonymous build statistics to Avalonia, and the .NET SDK sends its own usage data to
Microsoft unless `DOTNET_CLI_TELEMETRY_OPTOUT` is set. Both run on the build machine only.
Neither is part of the app.

## Children

kubeNimbus is a developer tool, is not directed at children, and collects no information
from anyone, children included.

## Changes

If this policy ever changes, the new version replaces this file in the repository and its
history stays visible in Git. The app's no-data-collection stance is a permanent design
goal, stated in the [security model](SECURITY.md#security-model), not a default that might
change. The code behind every statement here is open source under the MIT license at
<https://github.com/Shman4ik/kubeNimbus>.

## Contact

Questions about this policy: open an issue at
<https://github.com/Shman4ik/kubeNimbus/issues>, or email <shman4ik@gmail.com> with
`kubeNimbus privacy` in the subject. For a security problem, use
[private vulnerability reporting](https://github.com/Shman4ik/kubeNimbus/security/advisories/new)
instead.
