# Log viewers: how the good ones show a log and let people search it

*2026-09-28. Question asked: what do the best log viewers do about presenting and searching a
log, so that kubeNimbus's two log panes (pod detail's Logs tab and the multi-pod
`WorkloadLogsView` the Applications page embeds) are good enough that nobody copies a log into
Notepad++ to find something in it? The immediate trigger is that both panes colour the whole
line by its severity keyword, so an ASP.NET pod whose every line starts `info:` turns the pane
blue from top to bottom, which the owner dislikes.*

## The short version

- **Nobody good colours the whole line of an info message.** The field has converged on three
  layers, and uses at most two of them on any one line: (1) the message text stays in the normal
  foreground; (2) the *level token* alone is coloured (Grafana 12.3, VS Code's log grammar,
  stern's `levelColor`, hl); (3) errors, and sometimes warnings, get a mark on the *row*: a
  gutter dot or bar (Dozzle, Grafana's older panel, Kibana's row indicator) or a faint
  background wash (Chrome DevTools, Dozzle). An info line gets a blue or green token, or a small
  dot, and nothing else. lnav is the one tool that recolours whole lines, and it does so only
  for errors (red) and warnings (yellow). Info lines stay plain.
- **Dozzle has written down the most useful lesson.** Its current code gives error rows a
  5 % red wash and deliberately gives warnings none, because "an orange wash on a routine retry
  line was the noisiest thing in the stream". Severity sits on a 10 px gutter marker: a dot for a
  single line, a 3 px rail for an error or a multi-line entry.
- **Search: every serious tool offers both find and filter, regex, and a way to exclude.**
  Exclusion is spelled `!term` (k9s, VS Code), `-term` (Chrome, Datadog), `not(...)` (klogg),
  `--exclude` (stern) or a filter-out rule (lnav). Context lines around a filtered match are
  standard in the hosted tools (Grafana "Show context", Kibana "View surrounding documents",
  Datadog "View in context"). Several highlight terms at once, each in its own colour, is what
  the desktop tools and Notepad++ are used for: klogg's colour labels, Notepad++'s five mark
  styles, lnav's named searches, stern's `--highlight`.
- **Navigation by severity is lnav's `e`/`E` and `w`/`W`, plus a scrollbar that shows where the
  errors and matches are** (lnav, klogg, VS Code, and Headlamp's xterm overview ruler).
- **Structured (JSON) logs are still the loudest single unmet request in this niche.** k9s
  [#364](https://github.com/derailed/k9s/issues/364) (147 reactions), Lens
  [#3045](https://github.com/lensapp/lens/issues/3045) (44, open, "json prettify **and custom
  keyword coloring**") and Lens [#4320](https://github.com/lensapp/lens/issues/4320) (24, open).
  The patterns to copy are Dozzle's inline `key=value` rendering and Grafana's "Prettify JSON"
  with key, string and number colouring.
- **Much of this is already being built.** The working tree at the time of writing (uncommitted,
  see [below](#what-kubenimbus-has-and-what-is-in-progress)) colours the level token only, dims
  the timestamp, marks error and warning rows with a bar and a wash, adds regex and match-case
  toggles, error and warning counts with previous/next jumps, and grep-style context in filter
  mode. What is left is ranked in [Recommendations](#recommendations-ranked-by-value-to-effort).
- **Outcome.** The change this note was written for built recommendations 1 to 7 (the warning
  wash dropped as Dozzle did; JSON lines opened in place rather than rendered as `key=value`)
  and left 8, bookmarks, for later. What it built is described in
  [`docs/engineering/log-pane-reading.md`](../engineering/log-pane-reading.md).

Everything recommended below runs over lines the pane has already buffered. None of it needs a
server change, an in-cluster agent, or a stored log, and all of it is deterministic.

## Method, and what could not be confirmed

- Issue reaction counts come from `api.github.com` (via `gh api`), on 2026-09-28. Where a number
  is quoted, it is GitHub's `reactions.total_count` for that issue on that day.
- Implementation details were read from source where the documentation is silent: Grafana's
  [`LogLine.tsx`](https://github.com/grafana/grafana/blob/main/public/app/features/logs/components/panel/LogLine.tsx)
  and [`grammar.ts`](https://github.com/grafana/grafana/blob/main/public/app/features/logs/components/panel/grammar.ts),
  Dozzle's [`LogLevel.vue`](https://github.com/amir20/dozzle/blob/master/assets/components/logs/entries/LogLevel.vue),
  [`LogList.vue`](https://github.com/amir20/dozzle/blob/master/assets/components/logs/LogList.vue) and
  [`ComplexLogItem.vue`](https://github.com/amir20/dozzle/blob/master/assets/components/logs/entries/ComplexLogItem.vue),
  Chrome DevTools' [`consoleView.css`](https://github.com/ChromeDevTools/devtools-frontend/blob/main/front_end/panels/console/consoleView.css),
  VS Code's [`log.tmLanguage.json`](https://github.com/microsoft/vscode/blob/main/extensions/log/syntaxes/log.tmLanguage.json),
  Headlamp's [`LogViewer.tsx`](https://github.com/kubernetes-sigs/headlamp/blob/main/frontend/src/components/common/LogViewer.tsx),
  FreeLens' [`list.tsx`](https://github.com/freelensapp/freelens/blob/main/packages/core/src/renderer/components/dock/logs/list.tsx)
  and [`search.tsx`](https://github.com/freelensapp/freelens/blob/main/packages/core/src/renderer/components/dock/logs/search.tsx),
  and k9s' [`log_items.go`](https://github.com/derailed/k9s/blob/master/internal/dao/log_items.go).
- **Not confirmed:** the *shape* of Datadog's status marker in the Log Explorer list (its docs
  say only that status is colour-coded, red for error and blue for info); how Seq marks a level
  on an event row (its docs describe the message syntax highlighting and the expanded property
  list, not the row); and what Aptakube's "Coloring" setting colours. These are stated as
  unknown below rather than guessed.

## What kubeNimbus has, and what is in progress

**Shipped on `main`** ([log-pane-reading](../engineering/log-pane-reading.md),
[log-severity-classes](../engineering/log-severity-classes.md)): find mode by default
(highlight, "n of m", Enter/Shift+Enter) with filter as the funnel chip's mode; case-insensitive
substring only; Levels flyout with unleveled lines always shown; Errors only on the Applications
page's pane; local time with UTC one click away; Wrap; Copy/Save of the raw lines; Range
(tail/since, `LogRange.cs`); ANSI escapes stripped; `.NET`'s `info:`/`warn:`/`fail:`/`crit:`
read as levels. Severity is `SelectableTextBlock.logError`/`.logWarn`/`.logInfo`, which set the
**whole line's** `Foreground`. That is the blue pane.

**In progress in the working tree** (uncommitted changes to `LogLineText.cs`, `Theme.axaml`,
`LogLineViewModel.cs`, both pane view models and views, plus new `LogQuery.cs`,
`LogProblems.cs`, `LogProjection.cs` and `LogSearch.cs`, as seen on 2026-09-28):

- The severity classes now set `LogLineText.LevelBrush`, a style override on the level keyword
  alone. A `PrefixBrush` dims the timestamp prefix. `Border.logRow.error` / `.warn` add a 2 px
  left bar and a translucent wash (`#24E5484D` / `#1FD9822B`); the comment cites Chrome
  DevTools as the model.
- `LogQuery`: plain text or regex (interpreted, 100 ms per-line timeout), match case or not,
  with VS Code's Alt+R / Alt+C.
- `LogProblems`: error and warning count chips in the bar; Alt+↑ / Alt+↓ (or a click on a count)
  jump between them, newest first, and a cursor row is drawn in the accent.
- `LogProjection`: in filter mode, 0/2/5/10/25 lines of context around each match, context lines
  dimmed and a rule between groups (grep's `--`).
- Structured level keys: `level=`, `lvl=`, `"level":`, `"severity":`, `"LogLevel":`, Serilog's
  `"@l":` now decide the level ahead of a keyword in the message.
- Comments mention an "overview ruler", but no ruler control exists in the views yet.

This note's recommendations are written so that they can be checked against that work: where
the in-progress change already matches the evidence it says so, and in one place (the warning
wash) the evidence argues for a small change to it.

## Product by product

### Grafana (Explore and the logs visualization, 12.3)

1. **Severity.** The redesigned panel shows "the timestamp, a colored string representing the
   log status, the log line body" ([logs visualization docs](https://grafana.com/docs/grafana/latest/visualizations/panels-visualizations/visualizations/logs/)).
   In source the level is a bold, uppercase token coloured per level; the timestamp is drawn in
   `text.disabled`; the body stays in the primary text colour
   ([`LogLine.tsx`](https://github.com/grafana/grafana/blob/main/public/app/features/logs/components/panel/LogLine.tsx)).
   The mapping: critical purple, error red, warning yellow, **info blue**, debug grey, trace light
   blue, unknown grey ([Explore logs docs](https://grafana.com/docs/grafana/latest/visualizations/explore/logs-integration/)).
   So an info line is a blue "INFO" and otherwise plain text. The older panel used "a small bar
   on the left hand side"; a user asked for a whole-line option instead
   ([grafana#82368](https://github.com/grafana/grafana/issues/82368)), and a newer user asked how
   to *remove* the coloured level column
   ([community thread](https://community.grafana.com/t/how-to-remove-colored-log-level-from-logs-visualization/161466)).
   Both directions are wanted by somebody, but neither ships recolouring of the body.
   "Enable logs highlighting" adds a
   [grammar](https://github.com/grafana/grafana/blob/main/public/app/features/logs/components/panel/grammar.ts)
   that colours `key=` names, quoted strings, UUIDs, sizes, durations and HTTP methods, and in
   JSON lines keys, strings and numbers. It switches itself off past 20,000 characters per line or
   a combined cost budget, "to protect the user against freezes".
2. **Search.** "Client-side filter by level and search by string"
   ([12.3 release](https://grafana.com/blog/grafana-12-3-release-all-the-latest-features/)); query
   words are highlighted in the lines. **Show context** on a line displays the surrounding lines,
   "similar to the `-C` parameter in the `grep` command", with an adjustable time window.
   Deduplication has four strengths: None, Exact, Numbers (strip numbers) and Signature (strip
   letters and numbers).
3. **Navigation.** Logs-volume histogram above the list; jump to first/last line; a shortlink
   scrolls to a line and highlights it in blue.
4. **Structured.** "Prettify JSON" pretty-prints JSON lines. Log details (inline under the
   line, or a resizable sidebar) list labels and detected fields, each with include/exclude
   filter buttons and a stats icon. "Escape newlines" turns literal `\n` in a line into real
   line breaks, and back.
5. **Other.** Font size (Default/Small), millisecond or nanosecond timestamps, download as
   txt/JSON/CSV.

### Datadog Log Explorer

1. **Severity.** "Status attributes are color-coded by status (red for `error`, blue for
   `info`)" ([search docs](https://docs.datadoghq.com/logs/explorer/search/)). The marker's shape
   is not documented.
2. **Search.** Query syntax with `-` negation, `AND`/`OR`, wildcards, `status:error`; full text
   is case-insensitive ([search syntax](https://docs.datadoghq.com/logs/explorer/search_syntax/)).
   No regex in the documentation.
3. **Navigation.** "View in context updates the search request in order to show you the log
   lines dated just before and after a selected log, even if they don't match your filter"
   ([side panel](https://docs.datadoghq.com/logs/explorer/side_panel/)).
4. **Structured.** The side panel shows the message and every extracted attribute; any
   attribute value can be added to the query as include or exclude; Ctrl/Cmd+C copies the log's
   JSON.
5. **Other.** **Patterns** cluster similar messages and highlight the variable parts in yellow
   ([patterns](https://docs.datadoghq.com/logs/explorer/analytics/patterns/)).

### Seq

1. **Severity.** Row marker not confirmed (see Method). Messages are rendered with their
   template's property values syntax-highlighted
   ([Seq 2024.2](https://datalust.co/blog/seq-2024-2-released)).
2. **Search.** Free text is matched case-insensitively; `/regex/` literals and `like` work in
   expressions ([query language](https://datalust.co/docs/the-seq-query-language)).
3. **Navigation.** Signals (saved filters, grouped, e.g. by `@Level`)
   ([signals](https://datalust.co/docs/signals)).
4. **Structured.** "Expanding an event will show the available properties, and clicking the green
   *tick* beside the property name provides some basic filtering options": *Find* writes the
   expression (`ProductId = 'product-32'`) for you
   ([search expression syntax](https://datalust.co/docs/query-syntax)). Nested properties can be
   flattened or shown nested ([Seq 2024.2](https://datalust.co/blog/seq-2024-2-released)).

### lnav

1. **Severity.** "Errors will be colored in red; warnings will be yellow"; ordinary messages are
   in default colours ([UI docs](https://docs.lnav.org/en/latest/ui.html)). The whole line is
   recoloured, but only for those two levels. Identifiers get colours derived from their content;
   IP addresses, SQL keywords and quoted strings are highlighted.
2. **Search.** PCRE2 regex as you type; filters **in** and **out** (`:filter-in`,
   `:filter-out`, `:filter-expr`), with the number of lines hidden shown in the status bar.
   **Named searches** (v0.15) "stay active and highlighted while other searches are run, so
   several patterns can be tracked at the same time"; "the text matched by a named search is given
   a background color that is derived from the name"
   ([usage](https://github.com/tstack/lnav/blob/master/docs/source/usage.rst)).
3. **Navigation.** `e`/`Shift+E` next/previous error, `w`/`Shift+W` warning, `n`/`N` search
   hit, `u`/`U` bookmark, `m` to mark
   ([hotkeys](https://github.com/tstack/lnav/blob/master/docs/source/hotkeys.rst)). "The
   scrollbar on the right is highlighted to show the position of warnings and errors", and
   bookmarks appear as tick marks on it. `i` toggles a histogram of messages over time, stacked
   by level. `Ctrl+S` pins a line as a sticky header.
4. **Structured.** `Shift+P` switches to a pretty-printed view of JSON/XML; `p` opens an overlay
   of the focused line's parsed fields.
5. **Other.** The most-reacted open issue is displaying line numbers
   ([lnav#873](https://github.com/tstack/lnav/issues/873), 15).

### klogg (and glogg)

1. **Severity.** No built-in severity; the user defines **highlighters**, regex rules that colour
   the whole line or only the matched part, in ordered sets that can be exported and shared
   ([documentation](https://github.com/variar/klogg/blob/master/DOCUMENTATION.md)).
2. **Search.** Two panes: the whole file on top, the lines matching the search underneath.
   Clicking a line in the lower pane moves the upper one to it. Extended regex, fixed strings, or
   `and`/`or`/`not(...)` combinations. **Colour labels**: select text, press `Ctrl+D`, and every
   occurrence gets the next of nine colours; `Ctrl+Shift+0` clears them.
3. **Navigation.** The **match overview** beside the scrollbar shows matches as small red lines
   and marks as blue ones. `m` marks a line, `[`/`]` jump between marks, `n`/`N` repeat a quick
   find, and `v` cycles the lower pane between marks and matches, marks only, and matches only.
4. **Structured.** A Scratchpad formats JSON/XML and decodes base64 from a selection.
5. **Asks.** Word wrap ([klogg#99](https://github.com/variar/klogg/issues/99), 17); rendering
   ANSI colours ([klogg#338](https://github.com/variar/klogg/issues/338), 8, open); highlight
   rules from selected text ([klogg#270](https://github.com/variar/klogg/issues/270), shipped as
   colour labels).

### Notepad++ (what people use it for on logs)

The [searching manual](https://npp-user-manual.org/docs/searching/) and the community threads on
log work ([extract data from logs](https://community.notepad-plus-plus.org/topic/24007/extract-specific-data-from-log-files),
[bookmark lines by regex](https://community.notepad-plus-plus.org/topic/14612/bookmark-lines-regex))
show the same few gestures every time:

- **Find All in Current Document**: a results panel listing every matching line with its line
  number; double-click jumps there, F4/Shift+F4 step through. This is a filter whose lines lead
  back into the full text.
- **Mark All** with one of five **mark styles**, so several terms are coloured at once, and
  optionally **Bookmark line** on each hit.
- **Remove Unmarked Lines** / **Copy Bookmarked Lines**: filter by bookmark and export.
- Regex, match case, whole word, and **Count**.

Its value for logs is that you can do these things *at all*: a find that keeps context, several
highlight colours, and a filtered list that leads back to the full log.

### Chrome DevTools console

1. **Severity.** Error rows are drawn on `--sys-color-surface-error` and warning rows on
   `--sys-color-surface-yellow`: a background wash on the whole row. Info and log rows have no
   background ([`consoleView.css`](https://github.com/ChromeDevTools/devtools-frontend/blob/main/front_end/panels/console/consoleView.css)).
2. **Search.** The filter box takes plain text or `/regex/`, and `-url:` for negative filters;
   a separate Ctrl+F search keeps every message and supports case and regex; Log Levels
   dropdown; "Hide network", "Preserve log", **"Group similar messages"**
   ([reference](https://developer.chrome.com/docs/devtools/console/reference)).
3. **Navigation.** The sidebar lists messages by source with counts per level.

### VS Code (output panel and editor)

1. **Severity.** The built-in log grammar colours the **level token only**. `INFO`/`[info]`
   is `markup.inserted`, `WARN` is `markup.deleted`, `ERROR`/`FATAL` is `string.regexp, strong`.
   Dates are `comment` (dimmed), and GUIDs, hashes, numbers, quoted strings, URLs and
   `*Exception` type names are all distinguished
   ([`log.tmLanguage.json`](https://github.com/microsoft/vscode/blob/main/extensions/log/syntaxes/log.tmLanguage.json)).
   No line is ever wholly recoloured.
2. **Search.** The output panel filters by level, category and text
   ([1.97](https://code.visualstudio.com/updates/v1_97)); the debug console filter "supports
   exclude patterns (for example, patterns starting with an exclamation mark `!`)"
   ([1.49](https://code.visualstudio.com/updates/v1_49)). The editor's find widget has the
   Aa / ab / `.*` toggles (Alt+C / Alt+W / Alt+R) and shows every match in the overview ruler.
3. **Other.** Compound logs: several output channels in one view.

### Dozzle

1. **Severity.** A 10 px gutter carries a level **dot** for a single line (info green, warn
   orange, error red, debug purple) and a 3 px **rail** for an error or a multi-line entry, "where
   its length is the thing it says (these lines are one entry)"
   ([`LogLevel.vue`](https://github.com/amir20/dozzle/blob/master/assets/components/logs/entries/LogLevel.vue)).
   Error and fatal rows get a 5 % red wash, and **warnings get none**: "Severity primarily rides on
   the level rail … so this is a hint rather than the signal, and warn does not get one at all: an
   orange wash on a routine retry line was the noisiest thing in the stream. Off by choice for
   anyone who wants the field completely flat"
   ([`LogList.vue`](https://github.com/amir20/dozzle/blob/master/assets/components/logs/LogList.vue)).
   Rows have a 2.5 % zebra stripe; timestamps are "unboxed and quiet"
   ([v11 notes](https://dozzle.dev/guide/whats-new)). Text is never recoloured by level.
2. **Search.** Regex search ([dozzle#225](https://github.com/amir20/dozzle/issues/225), shipped),
   level filter ([dozzle#2432](https://github.com/amir20/dozzle/issues/2432), 10, shipped), and an
   in-browser SQL engine ([README](https://github.com/amir20/dozzle)).
3. **Navigation.** A scroll-position readout floats over the stream while scrolling and says
   where in the container's lifetime you are.
4. **Structured.** JSON lines are rendered **inline as `key=value`**: keys light and dimmed,
   values bold, strings quoted, arrays bracketed; clicking the line opens a details drawer
   ([`ComplexLogItem.vue`](https://github.com/amir20/dozzle/blob/master/assets/components/logs/entries/ComplexLogItem.vue)).
   The level is read from `level`, OpenTelemetry's `severityText`/`severityNumber`, or Pino's
   numeric levels. Multi-line stack traces are grouped into one entry
   ([Dash0 guide](https://www.dash0.com/guides/dozzle-docker)).
5. **Asks.** When an update stopped rendering the application's own ANSI colours, users asked for
   them back as an option ([dozzle#2639](https://github.com/amir20/dozzle/issues/2639), 8,
   shipped): "they helped separate the various levels of logging for me when quickly glancing at
   them."

### k9s

1. **Severity.** None of its own; it renders the application's ANSI colours.
2. **Search.** `/` filters; the query is a case-insensitive regex (`(?i)` prefixed), `!` inverts
   it and `-f` makes it fuzzy; matched text is highlighted
   ([`log_items.go`](https://github.com/derailed/k9s/blob/master/internal/dao/log_items.go)).
   Invert was asked for twice ([k9s#564](https://github.com/derailed/k9s/issues/564),
   [k9s#848](https://github.com/derailed/k9s/issues/848), 11 together, both shipped). Keeping
   unmatched lines visible while filtering was asked for in
   [k9s#2056](https://github.com/derailed/k9s/issues/2056).
3. **Navigation.** `m` draws a marker line; `0`–`6` pick a time range; `s` autoscroll, `w` wrap,
   `t` timestamps ([DeepWiki summary](https://deepwiki.com/derailed/k9s/5.4-log-viewer)).
4. **Structured.** None. [k9s#364](https://github.com/derailed/k9s/issues/364) "View JSON logs"
   has 147 reactions and was closed as not planned.
5. **Asks.** Piping the log through `grep`/`jq`/`awk` from the viewer
   ([k9s#785](https://github.com/derailed/k9s/issues/785), 50, not planned). The body says the
   alternative is "saving the log state to a file" and using other tools on it, which is the
   Notepad++ workflow this note is about. Faster horizontal scrolling "like lnav"
   ([k9s#1705](https://github.com/derailed/k9s/issues/1705), 27).

### Lens and FreeLens

1. **Severity.** None of their own; FreeLens renders ANSI colours through `ansi_up`
   ([`list.tsx`](https://github.com/freelensapp/freelens/blob/main/packages/core/src/renderer/components/dock/logs/list.tsx)).
2. **Search.** A debounced regex scan of all lines with highlighted overlays and next/previous,
   plus Ctrl/Cmd+F pre-filled from the selection
   ([`search.tsx`](https://github.com/freelensapp/freelens/blob/main/packages/core/src/renderer/components/dock/logs/search.tsx)).
   "Filter log lines that either match or don't match a certain string" is still open
   ([freelens#1701](https://github.com/freelensapp/freelens/issues/1701)).
3. **Asks.** [lens#3045](https://github.com/lensapp/lens/issues/3045), "json prettify and custom
   keyword coloring", 44 reactions, open since 2021: "have a section where user can input keywords
   correlated to a color". [lens#4320](https://github.com/lensapp/lens/issues/4320), JSON parsing,
   24, open.

### Aptakube

1. **Severity.** A "Coloring" setting exists; what it colours is not documented. Pod and
   container names are colourised ([aggregated logs page](https://aptakube.com/aggregated-logs)).
2. **Search.** "Filter or Find logs using keywords. Aptakube will highlight the keywords in the
   logs and the option to navigate to the next or previous occurrence." Search-rather-than-filter
   was its most-reacted log issue ([aptakube#32](https://github.com/aptakube/aptakube/issues/32),
   15).
3. **Asks, all small but specific:** context lines when filtering, "as described in [grep's
   context line control]" ([aptakube#363](https://github.com/aptakube/aptakube/issues/363),
   open); "when i filter any log message i want see log before/after so option Go to would be
   perfect" ([aptakube#375](https://github.com/aptakube/aptakube/issues/375)); a marker line "to
   track the last entries seen", requested by a customer
   ([aptakube#30](https://github.com/aptakube/aptakube/issues/30), shipped); integrate
   [hl](https://github.com/pamburus/hl) for JSON/logfmt
   ([aptakube#379](https://github.com/aptakube/aptakube/issues/379)); JSON-pointer field selection
   ([aptakube#432](https://github.com/aptakube/aptakube/issues/432)). A "Clear Logs" button
   shipped in 1.20.2 ([changelog](https://aptakube.com/changelog)).

### Headlamp

1. **Severity.** No colouring of its own; the viewer is an xterm.js terminal, so ANSI colours
   pass through. Detecting and highlighting errors was proposed and closed as not planned
   ([headlamp#2057](https://github.com/kubernetes-sigs/headlamp/issues/2057)). A severity filter
   dropdown shipped in v0.42.0, and lines with no detectable severity are always shown
   ([PR #5338](https://github.com/kubernetes-sigs/headlamp/pull/5338)).
2. **Search.** xterm's search addon with **case-sensitive, whole-word and regex toggles**, match
   and active-match backgrounds, and **overview-ruler marks** for matches
   ([`LogViewer.tsx`](https://github.com/kubernetes-sigs/headlamp/blob/main/frontend/src/components/common/LogViewer.tsx)).
3. **Structured.** "Prettify" for JSON, persisted since
   [PR #5152](https://github.com/kubernetes-sigs/headlamp/pull/5152), and still being fixed for
   losing lines ([#4700](https://github.com/kubernetes-sigs/headlamp/pull/4700),
   [#7149](https://github.com/kubernetes-sigs/headlamp/pull/7149)).

### stern (and hl)

- stern colours pod and container names; `--include`/`--exclude`/`--highlight` all take
  **regular expressions and can be repeated**; its templates offer `levelColor` (colour the
  level word), `prettyJSON`, `tryParseJSON` and `extractJSONParts`
  ([README](https://github.com/stern/stern)).
- hl turns JSON/logfmt into `time |LVL| message key=value`, filters by level (`-l`), by field
  (`-f component=tsdb`, `!=`, `~=`) and by query, and hides or reveals fields
  ([README](https://github.com/pamburus/hl)).

### Kibana Discover

- The logs profile gives rows **severity indicators** and a Summary column with log-level badges
  ([kibana PR #288514](https://github.com/elastic/kibana/pull/288514)); users missed the automatic
  level colouring when it disappeared in some versions
  ([discuss thread](https://discuss.elastic.co/t/log-level-highlighting-in-discover/376732)).
- **View surrounding documents**: "By default, five documents are added with each click"; "the
  anchor document is highlighted in blue"; pinned filters stay active and normal ones are copied
  disabled ([Kibana 7.17 docs](https://www.elastic.co/guide/en/kibana/7.17/discover-view-document.html)).

## Across the field

### Severity presentation

| Product | Text of the line | Level token | Row mark | Info line looks like |
|---|---|---|---|---|
| Grafana 12.3 | default | bold uppercase, coloured | none (older panel: left bar) | blue `INFO`, plain text |
| VS Code log grammar | default | coloured | none | green-ish `INFO`, plain text |
| stern / hl | default | coloured (`levelColor`) | none | coloured level word |
| Dozzle | default | not shown separately | gutter dot; 3 px rail for error and multi-line; 5 % red wash on error only | green dot, plain text |
| Chrome DevTools | default | icon | wash on error and warning | plain |
| Kibana (logs profile) | default | badge in Summary | row indicator | badge |
| lnav | **red / yellow for error / warning** | — | scrollbar marks | plain |
| k9s, Lens, FreeLens, Headlamp | the application's own ANSI colours | — | — | whatever the app printed |
| kubeNimbus `main` | **whole line coloured, info included** | — | — | **blue line** |

The tools that recolour anything by level do it for a minority of lines. None of them colours
info text. Where a row gets a background, the wash is faint (Dozzle's is 5 %) and applies to
errors; Chrome applies it to warnings too, and Dozzle has written down why it stopped doing that.

### Search and navigation

| Capability | Who has it |
|---|---|
| Find (keep lines) *and* filter | Aptakube, Chrome, Notepad++, klogg (two panes), lnav, kubeNimbus |
| Regex | k9s, FreeLens, Headlamp (toggle), Chrome (`/…/`), Dozzle, klogg, lnav, Notepad++, stern, Seq (`/…/`) |
| Case toggle | Headlamp, Chrome search, VS Code, Notepad++ |
| Exclude / negate | k9s `!`, VS Code `!`, Chrome `-url:`, Datadog `-`, klogg `not()`, lnav filter-out, stern `--exclude`, hl `!=` |
| Several highlight terms, each coloured | lnav named searches, klogg colour labels, Notepad++ mark styles, stern `--highlight` (repeatable) |
| Context around a match | Grafana Show context, Kibana surrounding docs, Datadog View in context; asked of Aptakube (#363, #375) |
| Filtered line leads back to the full log | klogg (click in lower pane), Notepad++ Find All (double-click), Datadog View in context, Kibana anchor |
| Next/previous error or warning | lnav `e`/`w`; VS Code next problem |
| Scrollbar / overview marks | lnav (errors, warnings, bookmarks), klogg (matches red, marks blue), VS Code, Headlamp (matches) |
| Marks / bookmarks | lnav, klogg, Notepad++, k9s `m`, Aptakube marker line |
| Histogram | Grafana, Kibana, Datadog, lnav |
| Collapse repeats | Chrome "group similar", Grafana dedup, Datadog patterns |
| Stack traces as one entry | Dozzle, lnav |

## Demand, ranked by strength of evidence

1. **Structured / JSON logs.** k9s #364 (147), Lens #3045 (44, open, also asks for keyword
   colouring), Lens #4320 (24, open), k9s #2973, #1778 and #2148 (from the
   [2026-08-17 logs report](2026-08-17-logs.md)), Aptakube #337 (shipped) and #432, Dozzle #920.
   This is the strongest demand in the niche, and kubeNimbus has none of it. The in-progress work
   reads the level from JSON keys, but nothing *renders* JSON yet.
2. **"Let me work on it like a file": pipe, grep, jq.** k9s #785 (50) is the Notepad++ workflow
   stated outright. It argues for the pieces people leave for: regex (in progress), exclusion,
   several highlights, context, and JSON.
3. **Search that keeps context.** Aptakube #32 (15, shipped), #351 (7, shipped), #363, #375; k9s
   #2056, #73 (9). kubeNimbus has had find since FEAT-33; filter-with-context is in progress.
4. **Custom keyword colouring.** Lens #3045 (44, shared with JSON), klogg #270, and klogg's,
   lnav's and Notepad++'s whole feature sets. Evidence from K8s clients is one issue, but a
   popular one.
5. **Exclude.** k9s #564 and #848 (11 together, shipped), FreeLens #1701 (open). Modest numbers,
   but every mature tool has it.
6. **Severity filter.** Dozzle #2432 (10, shipped), Headlamp #5338 (maintainer PR). kubeNimbus
   has shipped this.
7. **ANSI colours kept rather than stripped.** Dozzle #2639 (8, restored as an option), klogg
   #338 (8, open). This is a counter-signal to kubeNimbus's decision to strip them. It is small,
   and it is about seeing levels at a glance, which a coloured level token also gives.
8. **Marker line / bookmarks.** Aptakube #30 (2, customer request, shipped), k9s `m`. Weak.

**No evidence found** in the Kubernetes client trackers (k9s, Lens, FreeLens, Aptakube, Headlamp,
Dozzle) for demand for a histogram, collapsing repeated lines, or stack-trace grouping; each
of those is only a feature some tools ship.

## Recommendations, ranked by value to effort

Every item is deterministic, runs over the lines already buffered, needs no server change, and
fits NativeAOT: `System.Text.Json`'s `JsonDocument` and the interpreted `Regex` are both AOT-safe,
and `LogLineText`'s text-layout style overrides (already used for the level token and the prefix
in the working tree) can colour any number of spans without `Inlines`.

1. **Colour the level token, never the info line; mark errors on the row.** *Demand: the owner's
   complaint; convergence: Grafana, VS Code, stern, Dozzle, Chrome.* Copy Grafana's token (bold,
   coloured, only the keyword) and dim timestamp, plus Dozzle's gutter bar. **In progress** and
   matching the evidence. One change the evidence argues for: Dozzle deliberately gives warnings
   a bar but no wash, because the orange wash made routine retry lines the noisiest thing on
   screen. Consider a wash for errors only and the bar alone for warnings. Size S (remaining).
2. **Exclude terms with a leading `!`.** *Demand: k9s #564/#848, FreeLens #1701; universal in
   mature tools.* Copy k9s and VS Code: `!healthz` in the same box hides matching lines. Several
   `!` terms, each ANDed, cover the probe and health-check noise that fills a pane. The counter
   says how many lines are hidden (lnav). It works with the existing find/filter and the in-progress
   `LogQuery`. Size S.
3. **JSON lines rendered, not just parsed.** *Demand: the strongest in the niche.* Copy Dozzle's
   inline form: level and message first, then `key=value` with dimmed keys and normal-weight
   values, all on one line so search, wrap and the row model are unchanged. Space or a click
   expands one line into indented JSON beneath it (Grafana inline details, Seq's expand, lnav
   `Shift+P`). Copy and Save keep the raw line. Parse once per line with `JsonDocument` into a
   display string plus spans; fall back to the raw text on any parse failure. Headlamp's
   prettify bugs came from reflowing the buffer, so leave the line model alone. Size M.
4. **Pin a search as a coloured highlight.** *Demand: Lens #3045's keyword colouring; convergence:
   lnav named searches, klogg colour labels, Notepad++ mark styles, stern `--highlight`.* Copy
   klogg: select text and press a key (klogg uses Ctrl+D) or use a pin button in the box, and every
   occurrence gets one of a small fixed palette as a background. The pins sit as removable chips
   beside the box, and the current search stays the yellow find highlight. Session-only, like the
   search itself. The painter already exists (`LogLineText` fills match rectangles). Size S–M.
5. **Overview ruler.** *Convergence: lnav, klogg, VS Code, Headlamp.* A thin strip beside the
   scrollbar with red and amber ticks for error and warning lines, accent ticks for find matches,
   and pins in their own colours; a click scrolls there. This completes the in-progress error
   counts and Alt+↑/↓, which say how many and step through them but not where they are. It is a
   drawn control in the `Sparkline`/`TimelineStrip` mould. Size M.
6. **From a filtered line to its place in the full log.** *Demand: Aptakube #375, #363;
   convergence: klogg's two panes, Notepad++ Find All, Datadog View in context, Kibana's anchor.*
   With the filter on (with or without the in-progress context), double-click or Enter on a line
   switches the box to find mode with that line as the current match, scrolled into view and drawn
   as Kibana draws its anchor. It uses machinery that exists (find's current match, scroll-to-match).
   Size S.
7. **A stack trace belongs to the line that threw it.** *Convergence only: Dozzle's grouped rail,
   lnav's multi-line messages.* A line with no level of its own that looks like a continuation (it
   starts with whitespace, `at `, `---`, `Caused by:`, or `... N more`) and follows a levelled line
   from the same source takes that line's level. The rail runs down the group, Errors only and
   Levels keep the whole trace, and the error jump lands on its first line. This matters most for
   .NET and Java pods, which is the population the owner's example comes from. Size S.
8. **Mark a line, and "new since here".** *Weak demand: Aptakube #30, k9s `m`; convergence: lnav,
   klogg, Notepad++.* `M` toggles a gutter dot on a line, and marks get ticks on the ruler (item 5)
   and a `[`/`]` step. A "divider here" command drops a rule at the end of the stream, so after a
   deploy you can see what arrived since. Size S. It is last because the demand is thin.

Not recommended now: a per-pane histogram (only marketing emphasis, and the hosted tools' version
depends on a time-indexed store); collapsing repeated lines (Chrome, Grafana and Datadog ship it,
but nobody in these trackers asked); SQL over logs (Dozzle, lnav). Rendering ANSI colours again
has a small counter-signal (Dozzle #2639, klogg #338) but conflicts with item 1: the application's
colours and the pane's own severity marks would compete, which is the reason
[log-pane-reading](../engineering/log-pane-reading.md) gives for stripping them.
