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

**Every key-reference row opens the object it names (FEAT-45)** — a chevron at the row's
right end, the same one the `envFrom` lines carry, through the same resolve-and-open path
owner chips use (`PodDetailTabViewModel.OpenEnvVarSourceCommand`). Only rows that name an
object have it (`EnvVarViewModel.CanOpenSource`); a literal or a Downward-API ref has
nothing to open. Opening a Secret is not a way round the row's eye: it lands in the YAML
editor, where values are base64 and the decode is behind that editor's own Reveal.

The YAML editor's Secret "Reveal values" panel is the other half of this and is
unchanged: `data` stays base64 in the editable text, matching kubectl.

## A Secret's certificates are read without a Reveal (FEAT-30)

A Secret that carries a certificate names it in the YAML editor's header — the leaf's
common name and how long it has left, coloured ok / warn (30 days or less, Let's Encrypt's
own renewal window) / error (expired, or not yet valid) — and the chip opens a card with the
whole chain: each certificate's names (SANs, DNS and IP), issuer or "self-signed", "CA" on an
authority, and its validity window in UTC. `TlsCertificates` (Core) reads it with
`X509CertificateLoader` over bytes already in hand — no dependency, no reflection, and the
platform crypto the API server's own TLS already loads; the NativeAOT publish and both
launch checks pass with it.

Three rules make it safe to show without the Reveal:

- **A certificate is public by construction** — it is sent to every client that connects —
  so reading one reveals nothing the toggle guards.
- **The key is never read.** Only keys `TlsCertificates.IsCertificateKey` names are decoded
  (`tls.crt` on a `kubernetes.io/tls` Secret, `*.crt` anywhere), and only their
  `CERTIFICATE` PEM blocks (or one DER certificate). A certificate smuggled into `tls.key`
  does not appear; `SecretCertificateTests` pins that and was confirmed red when the key rule
  was loosened.
- **It reads the editor's text, on the reveal panel's debounce**, so a Reload or a pasted
  renewal is what it describes; the relative wording is re-computed to the minute.

An unreadable `tls.crt` on a TLS Secret is stated in the chip ("holds no certificate this can
read"), never an absent chip. The demo ships `checkout-tls`: a real chain generated for it (a
throwaway CA and a leaf it signed, neither key kept) and a placeholder string for `tls.key`.
Its expiry wording follows the wall clock like every Age, so its colour will change as the
binary ages — the deterministic wording is pinned by tests at a fixed instant instead.
