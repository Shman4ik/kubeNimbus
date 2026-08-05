namespace KubeNimbus.Core.Commands;

/// <summary>
/// The single source of truth for every command and documented keyboard gesture in
/// kubeNimbus. The window's key bindings, the Ctrl/Cmd+K palette, the F1 cheat sheet
/// and the published shortcut reference are all projections of this list — adding a
/// shortcut is one entry here, not four hand-kept copies.
///
/// <para>
/// The bar for an entry is unchanged from the hand-written list this replaced: every
/// row is a real gesture or interaction handled somewhere in the app, never
/// aspirational documentation. A cheat sheet that lists a key which does nothing is
/// worse than one that omits it.
/// </para>
///
/// <para>
/// The palette is a partial projection, deliberately. Its context-dependent rows —
/// the selected pod's logs/exec/port-forward, the open cluster tabs, the fleet toggle
/// — are built in the App layer because they only exist when they apply, and a palette
/// entry that matches a search and then refuses to run is worse than no match. What
/// comes from here is their identity, title and shortcut text, so those cannot drift
/// from the sheet.
/// </para>
/// </summary>
public static class CommandCatalog
{
    private const ChordModifiers Cmd = ChordModifiers.Command;
    private const ChordModifiers LiteralCtrl = ChordModifiers.Control;

    private const CommandSurface Everywhere =
        CommandSurface.WindowBinding | CommandSurface.Palette | CommandSurface.CheatSheet;

    /// <summary>
    /// Handled by a pane's own key handler (or by a mouse gesture), so no window
    /// binding is emitted — but still a documented shortcut.
    /// </summary>
    private const CommandSurface SheetOnly = CommandSurface.CheatSheet;

    private const CommandSurface PaletteAndSheet = CommandSurface.Palette | CommandSurface.CheatSheet;

    /// <summary>
    /// Reachable by name from the palette, and nowhere in the cheat sheet. This is the
    /// right home for an action with no gesture at all: F1 is a *keyboard* reference,
    /// and a row reading "Edit YAML — —" tells the reader nothing while pushing the
    /// rows that do carry a key further down the page. The palette's own hint (and the
    /// sheet's Ctrl/Cmd+K row) is how these are found.
    /// </summary>
    private const CommandSurface PaletteOnly = CommandSurface.Palette;

    /// <summary>Every descriptor, in the order the cheat sheet and docs list them.</summary>
    public static IReadOnlyList<CommandDescriptor> All { get; } =
    [
        // ------------------------------------------------------------- Clusters
        new()
        {
            Id = CommandId.ClusterSwitcher,
            Title = "Switch cluster…",
            CheatTitle = "Switch or open a cluster",
            Category = CommandCategory.Clusters,
            IconKey = "SwapHorizontalIconGeometry",
            // Its own gesture rather than a palette entry: switching cluster is the
            // most frequent navigation in a multi-cluster session, and every
            // comparable tool gives it one (k9s `:ctx`, kubectx).
            Chord = new(CommandKey.P, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.GoToTabByNumber,
            Title = "Jump to cluster tab 1–9",
            Category = CommandCategory.Clusters,
            IconKey = "LayersIconGeometry",
            // A range, not a chord: nine bindings registered in a loop from the
            // resolved modifier (UI rule 4 — never nine XAML KeyBindings).
            GestureNote = "{cmd}+1 … {cmd}+9",
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.NewClusterTab,
            Title = "New cluster tab",
            Category = CommandCategory.Clusters,
            IconKey = "PlusIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.CloseClusterTab,
            Title = "Close cluster tab",
            Category = CommandCategory.Clusters,
            IconKey = "CloseIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.OpenDemoCluster,
            Title = "Explore the demo cluster",
            CheatTitle = "Open the demo cluster",
            Category = CommandCategory.Clusters,
            IconKey = "LayersIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.OpenKubeconfigFile,
            Title = "Open kubeconfig file…",
            Category = CommandCategory.Clusters,
            IconKey = "OpenInNewIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.RescanKubeconfig,
            Title = "Rescan kubeconfig",
            Category = CommandCategory.Clusters,
            IconKey = "RefreshIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.SetEnvironment,
            Title = "Set the cluster's environment",
            CheatTitle = "Correct a cluster's environment colour",
            Category = CommandCategory.Clusters,
            IconKey = "TagIconGeometry",
            GestureNote = "Right-click a cluster tab",
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ToggleFleetView,
            Title = "Fleet view: aggregate across all clusters",
            CheatTitle = "Aggregate one kind across every connected cluster",
            Category = CommandCategory.Clusters,
            IconKey = "LayersIconGeometry",
            Surfaces = PaletteOnly,
        },

        // ------------------------------------------------------------ Resources
        new()
        {
            Id = CommandId.FilterList,
            Title = "Search the resource list by name",
            Category = CommandCategory.Resources,
            IconKey = "MagnifyIconGeometry",
            // The find-in-list every application binds to this chord, and typing a pod
            // name is the fastest way through a 200-row namespace.
            Chord = new(CommandKey.F, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.FilterSidebar,
            Title = "Filter the sidebar's resource kinds",
            Category = CommandCategory.Resources,
            IconKey = "MagnifyIconGeometry",
            GestureNote = "Type in the filter box",
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.OpenResource,
            Title = "Open the selected resource",
            Category = CommandCategory.Resources,
            Scope = CommandScope.List,
            IconKey = "OpenInNewIconGeometry",
            Chord = new(CommandKey.Enter),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.QuickPeek,
            Title = "Quick-peek the selected resource",
            Category = CommandCategory.Resources,
            Scope = CommandScope.List,
            IconKey = "EyeIconGeometry",
            Chord = new(CommandKey.Space),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.DefaultAction,
            Title = "Default action (pod → logs, resource → YAML, …)",
            Category = CommandCategory.Resources,
            Scope = CommandScope.List,
            IconKey = "PlayIconGeometry",
            GestureNote = "Double-click",
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.RowContextMenu,
            Title = "Logs, exec, port-forward, YAML, delete",
            Category = CommandCategory.Resources,
            Scope = CommandScope.List,
            IconKey = "MenuIconGeometry",
            GestureNote = "Right-click a row",
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.EditYaml,
            Title = "Edit YAML",
            Category = CommandCategory.Resources,
            IconKey = "CodeBracesIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.DeleteResource,
            Title = "Delete resource",
            Category = CommandCategory.Resources,
            IconKey = "DeleteIconGeometry",
            Surfaces = PaletteOnly,
        },

        // ----------------------------------------------------------------- Pods
        new()
        {
            Id = CommandId.PodLogs,
            Title = "Logs",
            CheatTitle = "Open the selected pod's logs",
            Category = CommandCategory.Pods,
            IconKey = "ClockOutlineIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.PreviousLogs,
            Title = "Previous logs (crashed container)",
            Category = CommandCategory.Pods,
            IconKey = "ClockOutlineIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.Exec,
            Title = "Exec into a container",
            Category = CommandCategory.Pods,
            IconKey = "ConsoleIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.PortForward,
            Title = "Port-forward",
            Category = CommandCategory.Pods,
            IconKey = "SwapHorizontalIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.ExecInterrupt,
            Title = "Interrupt a command in the exec pane",
            Category = CommandCategory.Pods,
            Scope = CommandScope.Exec,
            IconKey = "ConsoleIconGeometry",
            // Literal Ctrl, NOT the primary modifier — the one place the platform rule
            // is deliberately not applied. ^C is a terminal control character, and an
            // interrupt is Control on macOS too; Cmd+C there is Copy, which is what the
            // exec pane leaves it as.
            Chord = new(CommandKey.C, LiteralCtrl),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ExecEndInput,
            Title = "End input / exit the shell",
            Category = CommandCategory.Pods,
            Scope = CommandScope.Exec,
            IconKey = "ConsoleIconGeometry",
            Chord = new(CommandKey.D, LiteralCtrl),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ExecComplete,
            Title = "Complete a command in the exec pane",
            Category = CommandCategory.Pods,
            Scope = CommandScope.Exec,
            IconKey = "ConsoleIconGeometry",
            Chord = new(CommandKey.Tab),
            Surfaces = SheetOnly,
        },

        // ---------------------------------------------------------------- Tools
        new()
        {
            Id = CommandId.HelmReleases,
            Title = "Helm releases",
            Category = CommandCategory.Tools,
            IconKey = "CubeOutlineIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.AccessReview,
            Title = "Access review — my permissions",
            Category = CommandCategory.Tools,
            IconKey = "AccountMultipleIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.WhoCan,
            Title = "Access review — who can do X?",
            Category = CommandCategory.Tools,
            IconKey = "AccountMultipleIconGeometry",
            Surfaces = PaletteOnly,
        },

        // ----------------------------------------------------------------- View
        new()
        {
            Id = CommandId.CommandPalette,
            Title = "Open the command palette",
            Category = CommandCategory.View,
            IconKey = "MagnifyIconGeometry",
            Chord = new(CommandKey.K, Cmd),
            Surfaces = CommandSurface.WindowBinding | CommandSurface.CheatSheet,
        },
        new()
        {
            Id = CommandId.ToggleAdvancedView,
            Title = "Advanced view",
            CheatTitle = "Show or hide the advanced controls",
            Category = CommandCategory.View,
            IconKey = "TuneIconGeometry",
            GestureNote = "The sliders icon, top of the sidebar",
            Surfaces = PaletteAndSheet,
        },
        new()
        {
            Id = CommandId.ToggleSidebar,
            Title = "Show or hide the resource sidebar",
            Category = CommandCategory.View,
            IconKey = "SidebarToggleIconGeometry",
            Chord = new(CommandKey.B, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.MaximizeInspector,
            Title = "Maximize the inspector over the list",
            Category = CommandCategory.View,
            IconKey = "FullscreenIconGeometry",
            GestureNote = "The inspector's expand icon",
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ToggleTheme,
            Title = "Toggle light/dark theme",
            Category = CommandCategory.View,
            IconKey = "WeatherNightIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.Preferences,
            Title = "Preferences…",
            Category = CommandCategory.View,
            IconKey = "CogIconGeometry",
            // The standard gesture on every platform, and the one macOS puts under
            // Cmd+, by convention.
            Chord = new(CommandKey.Comma, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ShortcutsWindow,
            Title = "Keyboard shortcuts",
            CheatTitle = "Show this cheat sheet",
            Category = CommandCategory.View,
            IconKey = "HelpCircleIconGeometry",
            Chord = new(CommandKey.F1),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.About,
            Title = "About kubeNimbus",
            Category = CommandCategory.View,
            IconKey = "HelpCircleIconGeometry",
            Surfaces = PaletteOnly,
        },
        new()
        {
            Id = CommandId.RefreshList,
            Title = "Refresh the list",
            Category = CommandCategory.Resources,
            IconKey = "RefreshIconGeometry",
            Chord = new(CommandKey.R, Cmd),
            Surfaces = Everywhere,
        },
        new()
        {
            Id = CommandId.ApplyYaml,
            Title = "Apply the edited YAML",
            Category = CommandCategory.Resources,
            Scope = CommandScope.Editor,
            IconKey = "ContentSaveIconGeometry",
            Chord = new(CommandKey.S, Cmd),
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.FollowLogs,
            Title = "Follow the log stream",
            Category = CommandCategory.Pods,
            IconKey = "PlayIconGeometry",
            GestureNote = "The Follow toggle, above the log pane",
            Surfaces = SheetOnly,
        },
        new()
        {
            Id = CommandId.ReorderTabs,
            Title = "Reorder cluster tabs",
            Category = CommandCategory.Clusters,
            IconKey = "SwapHorizontalIconGeometry",
            GestureNote = "Drag tab",
            Surfaces = SheetOnly,
        },
    ];

    /// <summary>Looks a descriptor up by id. Throws when it is missing — the id is a compile-time enum.</summary>
    public static CommandDescriptor Get(CommandId id) =>
        All.FirstOrDefault(d => d.Id == id)
        ?? throw new KeyNotFoundException($"No command descriptor for {id}.");

    /// <summary>Every descriptor that appears on a given surface, in catalog order.</summary>
    public static IEnumerable<CommandDescriptor> On(CommandSurface surface) =>
        All.Where(d => d.In(surface));

    /// <summary>A command's primary chord, or null when it has none (palette-only actions).</summary>
    public static Chord? ChordFor(CommandId id) => Get(id).Chord;

    /// <summary>A command's palette/menu title, so no call site retypes it.</summary>
    public static string TitleFor(CommandId id) => Get(id).Title;

    /// <summary>
    /// The cheat sheet's sections, in catalog order, each with the rows filed under it.
    /// Empty categories are dropped rather than rendered as a heading over nothing.
    /// </summary>
    public static IEnumerable<(CommandCategory Category, IReadOnlyList<CommandDescriptor> Rows)> CheatSheetSections()
    {
        foreach (var category in Enum.GetValues<CommandCategory>())
        {
            var rows = All
                .Where(d => d.Category == category && d.In(CommandSurface.CheatSheet))
                .ToList();

            if (rows.Count > 0)
            {
                yield return (category, rows);
            }
        }
    }

    /// <summary>The heading a category is printed under.</summary>
    public static string Label(CommandCategory category) => category switch
    {
        CommandCategory.Clusters => "CLUSTERS",
        CommandCategory.Resources => "RESOURCES",
        CommandCategory.Pods => "PODS",
        CommandCategory.Tools => "CLUSTER TOOLS",
        CommandCategory.View => "VIEW & APP",
        _ => category.ToString().ToUpperInvariant(),
    };
}
