<!--
  Thanks for the PR. Keep this short — the goal is to tell a reviewer what
  changed, why, and how much to trust it. Delete sections that don't apply.
-->

## What and why

<!-- What this changes, and the problem it solves. Link the issue if there is one. -->

## Verified

<!--
  Tick what you actually ran, and say what you didn't. "Not verified against a
  live cluster" is a useful, welcome answer — an unverified claim of
  verification is not.
-->

- [ ] `dotnet build KubeNimbus.slnx`
- [ ] `dotnet test tests/KubeNimbus.Core.Tests/KubeNimbus.Core.Tests.csproj`
      — against a live cluster? <!-- yes / no, tests skip cleanly without one -->
- [ ] NativeAOT publish, no new trim/AOT warnings beyond the known DataGrid
      `IL2104`/`IL3053` — RID: <!-- win-x64 (shipping) / linux-x64 -->
- [ ] `dotnet run --project tools/Screenshot -- <dir>` (UI changes)
- [ ] Ran against a real cluster, not just fixtures

Anything left unverified:

## Screenshots

<!-- Before/after for any UI change, both themes if the change is visual. -->

## Checklist

- [ ] [CLAUDE.md](../CLAUDE.md) updated if this changes anything it describes
      — it's the contract, not a record of the past
- [ ] **Touches `shared/nimbusUi`?** Then the change is pgNimbus's too: push
      the subtree up (`git subtree push --prefix shared/nimbusUi …`), open the
      matching pgNimbus PR, and link it here. A shared change that lands in one
      app only is how the copies drifted in the first place — see
      [DESIGN.md](../shared/nimbusUi/DESIGN.md).
- [ ] Anything general enough for pgNimbus (a token, a style class, a window
      behaviour) went into `shared/nimbusUi` rather than this app's
      `Styles/Theme.axaml`
- [ ] `CHANGELOG.md` `[Unreleased]` updated for a user-visible change
- [ ] No new UI state that renders as a blank rectangle (loading / empty /
      disconnected / partial / error each have an explicit visual)
- [ ] `KubeNimbus.Core` still has zero UI dependencies
- [ ] Nothing about the server's API surface hardcoded — kinds, groups and
      versions still come from discovery
- [ ] No credentials persisted anywhere
- [ ] New sandbox workload added to `scripts/manifests/` if this introduces a
      state nothing there produces
