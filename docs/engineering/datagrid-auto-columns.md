# An Auto DataGrid column ratchets, and only one grid can afford it

> Part of the kubeNimbus engineering contract, moved out of `CLAUDE.md` so it loads only when relevant. Same discipline applies: keep it current in the PR that changes what it describes.


No column in the **resource list** is `Width="Auto"`, and the reason is measured rather
than argued. Reported from a real cluster as "the text in Details jumps randomly"; the
probe that reproduced it drove real pointer-free layout passes over the real
`ClusterTabView` inside a real `MainWindow` and printed every column's `ActualWidth`.

Two facts came out of it, and the second is the one nobody expects:

1. **An Auto column takes its width off the top, so widening one narrows every other
   column at once.** Making a single row's namespace longer took Namespace from **113px
   to 294px** and, in the same pass, Name 240 → 136, Ready 81 → 78, Restarts 93 → 78,
   CPU 117 → 98, Memory 138 → 106 and Age 68 → 60. One cell's text moved eight columns.
2. **It never shrinks back.** Setting that namespace to `"x"` left the column at 294.
   Avalonia's DataGrid sizes an Auto column to the widest cell it has *ever realized*, so
   on a virtualized list every long value that scrolls into view permanently widens that
   column and permanently narrows the rest.

Which makes the resource list the worst possible host for one: it virtualizes over
thousands of rows, a live watch rewrites cells under the reader, and `RefreshTimes` — a
wall-clock timer with no watch event and no user action behind it — rewrites Age and
Restarts on a tick. The third experiment measured that directly: `RestartsText` gaining
`"(43m ago)"` moved Restarts 93 → 108, Memory 138 → 131 and Age 68 → 60. Text shifting
sideways for no reason the reader can see is exactly the report.

So the list's columns are one of two kinds. **Bounded, monospace cells get a fixed
width** — Ready 84, Restarts 112, Age 72, CPU 118, Memory 132, Namespace 130, Cluster 150
— sized from what the Auto pass had measured for the same content, headers included (72
was tried for Ready first and clipped its own header to `Read`, which only the rendered
screenshot showed). **Everything whose content wants the window's spare width is a star
column** — Name, Status, Details, the CRD printer slots. Both are stable under any cell
change; fixed also means a wider window feeds the columns that need it instead of
padding a Namespace column full of the same word, which is the same argument the 224px
sidebar settled.

**The Helm and Argo grids keep `Auto`, deliberately, and that is a scope statement rather
than an oversight** — this file already records that a column rule has to be applied to
both grids or stated as applying to one. They are short, one-shot lists over a small
fixed vocabulary of states, so their Auto columns reach their widest on the first render
and effectively stop; there is no virtualized tail to ratchet through and no timer
rewriting a cell. Fixing their widths was tried and measurably rendered *worse* — the
Helm list clipped its `Rev` header, its status pill and the right-hand end of `Updated`,
none of which it does today. The cost of leaving them is bounded to a few pixels in one
column on a state transition.
