# ConfigMaps are shown, Secrets are masked

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


Pod detail's Environment tab treats the two reference kinds differently, and the
difference is the point — they used to be one code path with one "Reveal" chip,
which was simultaneously too guarded and not guarded enough:

- A **`configMapKeyRef` resolves on open** and renders like any literal, with
  `ConfigMap/<name> · key=<k>` as the caption underneath. A ConfigMap is not
  secret; it is ordinary configuration that anyone who can read the pod can read
  anyway, and charging a click to see `LOG_LEVEL=info` bought nothing. It costs
  one GET per *distinct* ConfigMap — `PodDetailTabViewModel.ResolveConfigMapValuesAsync`
  awaits them one at a time precisely so eight keys of one object don't become
  eight parallel misses of `_secretConfigMapCache`. It is fire-and-forget from
  `RefreshEnvironment`, guarded by the same env signature, so a watch tick that
  changed nothing re-fetches nothing; a failure lands on its own row's
  `RevealError`, never on the tab.
- A **`secretKeyRef` stays masked** behind `EnvVarViewModel.MaskedValue` (a fixed
  eight dots — the *length* of a secret is worth not leaking too) with an
  eye/eye-off toggle. Nothing is fetched until it is clicked: the mask is a
  placeholder, not a hidden copy, so a secret never enters this process — or a
  screen-share — because a pane happened to be open, and an RBAC 403 on Secrets
  lands on the one row someone asked about rather than on four nobody did.
  `ToggleEnvVarCommand` keeps the fetched value and flips `IsRevealed`, so the
  eye can **hide** it again; the old chip was one-way, and a value revealed on a
  shared screen stayed there until the tab was closed.

The YAML editor's Secret "Reveal values" panel is the other half of this and is
unchanged: `data` stays base64 in the editable text, matching kubectl.
