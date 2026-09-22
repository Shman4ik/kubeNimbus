# The theme toggle wrote a string nothing could read

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


The command bar's light/dark button persisted the **ThemeVariant names** — `"Dark"` /
`"Light"` — where `AppSettings` spells the setting `"dark"` / `"light"` / `"system"`.
`Normalized()` rejected the miscased value and `App.ThemeFromString` read it as
`ThemeVariant.Default`, so every click set the variant correctly and then, one line
later, put the app back on the OS's own theme. On a machine whose OS is dark that is a
toggle which enters dark and cannot leave — reported exactly that way, from a Mac, as
"light → dark works and dark → light does not". It is not platform-specific: the same
build fails the other direction on a light-mode OS, which is presumably why it survived.

Three things came out of it, and the third is the general one:

1. The toggle spells the variant with `App.ThemeToString` — the inverse function that
   already existed — and writes it **once**, through `PersistTheme`. It used to assign
   `Application.RequestedThemeVariant` itself *and* persist, and two writers for one
   value is what let the second one silently undo the first.
2. `AppSettings.Normalized()` canonicalizes case rather than rejecting it (`Canonical`,
   which `HotkeyScheme` shares), so a `settings.json` written by the broken build
   recovers on the next launch instead of losing the choice.
3. **A validating store turns a stringly-typed mismatch into a silent revert.** The
   clamping in `Normalized()` is right and is not the bug; the bug is that nothing
   connected the one place that wrote the string to the one place that read it. Any new
   setting spelled as a string wants its writer to go through the same helper its reader
   does — `AppSettingsTests` pins the theme and hotkey pair, including the miscased
   spellings, precisely because the failure mode is a control that appears to do nothing.
