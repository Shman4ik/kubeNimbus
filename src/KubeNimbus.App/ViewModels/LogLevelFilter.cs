using CommunityToolkit.Mvvm.ComponentModel;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The log panes' level filter: which of the three severities the lines are coloured by
/// are shown. A narrowing of the projection, never of the buffer — turning a level back on
/// brings its lines back in place.
/// </summary>
/// <remarks>
/// <para>
/// <b>A line of no known severity is always shown.</b> Severity is read off a keyword in
/// the text (<see cref="LogLineViewModel.Severity"/>), and most real output carries none:
/// nginx access logs, Go's <c>log.Print</c>, anything JSON. Hiding those because they are
/// "not Error" would turn "hide the INFO noise" into "hide most of the log", and that
/// population is the one the log pane has already made invisible once by accident (the
/// null-brush bug, <c>log-severity-classes.md</c>). Headlamp ships the same rule for the
/// same reason.
/// </para>
/// <para>
/// Not persisted, deliberately (<c>AppSettings.LogShowTimestamps</c> says why): a level
/// hidden in one pane and silently still hidden in the next is a pane that looks quiet.
/// And the three toggles are two-way <c>IsChecked</c> bindings with no command beside them
/// (UI rule 8b); the pane listens for <see cref="Changed"/>.
/// </para>
/// </remarks>
public sealed partial class LogLevelFilter : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(IsFiltering))]
    private bool _showError = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(IsFiltering))]
    private bool _showWarn = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(IsFiltering))]
    private bool _showInfo = true;

    /// <summary>Raised after any of the three changes.</summary>
    public event EventHandler? Changed;

    /// <summary>True while a level is hidden — the control is drawn in the accent so a filtered pane never looks unfiltered.</summary>
    public bool IsFiltering => !(ShowError && ShowWarn && ShowInfo);

    /// <summary>
    /// The button's own text, which is the filter's state: "Levels" when nothing is hidden,
    /// otherwise the levels still shown ("Error, Warn"), or "Unleveled only" when all three
    /// are off. A filter whose state lives only inside a closed flyout is a trap.
    /// </summary>
    public string Label
    {
        get
        {
            if (!IsFiltering)
            {
                return "Levels";
            }

            var shown = new List<string>(3);
            if (ShowError) shown.Add("Error");
            if (ShowWarn) shown.Add("Warn");
            if (ShowInfo) shown.Add("Info");
            return shown.Count == 0 ? "Unleveled only" : string.Join(", ", shown);
        }
    }

    /// <summary>Whether <paramref name="line"/> survives the filter. Unclassified lines always do.</summary>
    public bool Admits(LogLineViewModel line) => line.Severity switch
    {
        LogSeverity.Error => ShowError,
        LogSeverity.Warn => ShowWarn,
        LogSeverity.Info => ShowInfo,
        _ => true,
    };

    partial void OnShowErrorChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnShowWarnChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnShowInfoChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);
}
