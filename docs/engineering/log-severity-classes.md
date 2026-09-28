# Log severity is three classes, not a brush binding

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


**Since the log-viewer pass the classes colour the level keyword, not the line.** On an
ASP.NET pod every line is `info:`, and a pane that coloured whole lines turned blue from top
to bottom — colour on every line says nothing about any of them, and it was the first thing
the owner asked to change. No viewer surveyed colours an info line's text (Grafana, VS Code's
log grammar and stern colour the level word; Dozzle and Chrome mark the row;
`docs/research/2026-09-28-log-viewers.md`). So the classes now set
`LogLineText.LevelBrush`, which `LogLineText.CreateTextLayout` applies as a style override on
the keyword's span only (`LogLineViewModel.DisplayLevelStart`/`LevelLength`), and the row is
marked by `Border.logRow`: a 2px bar for an error or a warning, a faint wash for an error
only. The rest of this page still holds, and for the same reason: a null `LevelBrush` adds no
override at all, so an unclassified line's text inherits exactly as before.

The log pane's severity colouring is `SelectableTextBlock.logError` / `.logWarn` /
`.logInfo`, set from three bools on `LogLineViewModel` and styled in `Styles/Theme.axaml`.
`LogSeverityToBrushConverter` is **gone**, and the reason is worth the paragraph because
this is the second time the same bug shipped:

- The first version returned `null` for the default (no keyword) case. A null
  `Foreground` is a *local* value, it beats inheritance, and Avalonia's glyph-run draw
  early-returns on a null brush — so every line without a severity keyword rendered
  invisible. Fixed by returning `AvaloniaProperty.UnsetValue`, on the stated reasoning
  that "unset means no value here, so inheritance wins".
- **It does not.** Measured on the rendered dark-theme pane, a line whose `Foreground`
  *binding* produces `UnsetValue` falls back to `TextElement.Foreground`'s own default,
  which is opaque black — not to the inherited foreground. On the dark theme's `#080808`
  card that is invisible, exactly as before. The pixels: plain lines rendered at
  `(0,0,0)`/`(7,8,8)` on an `(8,8,8)` background while `INFO` lines rendered at
  `(78,158,242)`. Replacing the default case with a bright red confirmed the binding was
  what governed those lines rather than anything above them.
- Classes have no such failure mode: an unclassified line carries **no `Foreground`
  binding at all**, so it inherits the way every other `TextBlock` in the window does.
  The three colours are the converter's own, unchanged.

The same three severities drive the log panes' **Levels** filter (FEAT-36,
[log-pane-reading](log-pane-reading.md)), and it inherits this page's lesson directly: a line
with no severity keyword is *always* shown whatever the filter says, because that population
is most real output and is the one this pane has already lost once. The search highlight is
painted under the glyphs by `Controls/LogLineText`, a `SelectableTextBlock` that keeps
`StyleKeyOverride => typeof(SelectableTextBlock)` so these classes still reach it.

Two things about how this survived so long, both of which generalize. It is invisible in
the **light** theme, whose own text is nearly black — so a light-theme screenshot of a
correct pane and of a broken one are identical. And the population it hits is exactly the
lines that carry no severity keyword: nginx access logs, `log.Print`, anything JSON, i.e.
most real output — which is why `DemoLogs` is required to keep carrying them (demo rule 4)
and why *looking at the dark screenshot* is not optional for anything that colours text.
