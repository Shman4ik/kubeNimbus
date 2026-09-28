using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The errors and warnings among a pane's shown lines: how many there are, and a cursor
/// that jumps between them without hiding anything — the counterpart of Errors only and
/// Levels, which narrow the pane and so take away the lines that explain the error.
/// </summary>
/// <remarks>
/// <para>
/// The first jump lands on the <b>latest</b> problem and each further one walks back in
/// time, wrapping; the reverse gesture walks forward. Same order as the search's find and
/// for the same reason: in an incident the first question is "when did it last happen".
/// This is lnav's <c>e</c>/<c>E</c> and VS Code's next/previous problem, in the pane's own
/// bar where the counts also answer "is anything wrong in here at all".
/// </para>
/// <para>
/// The line the cursor is on carries <see cref="LogLineViewModel.IsProblemCursor"/>, which
/// the view draws as a row highlight and scrolls to (<see cref="Current"/>). A new line
/// arriving does not move it, so a following stream never drags the reader off the error
/// they are reading.
/// </para>
/// </remarks>
public sealed partial class LogProblems : ObservableObject
{
    private readonly List<LogLineViewModel> _errors = [];
    private readonly List<LogLineViewModel> _warnings = [];
    private IList<LogLineViewModel> _shown = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrors), nameof(ErrorTip))]
    private int _errorCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings), nameof(WarningTip))]
    private int _warningCount;

    /// <summary>The line the cursor is on; the view scrolls it into sight when it changes.</summary>
    [ObservableProperty]
    private LogLineViewModel? _current;

    public bool HasErrors => ErrorCount > 0;

    public bool HasWarnings => WarningCount > 0;

    public string ErrorTip => ErrorCount == 0
        ? "No error lines shown"
        : $"{ErrorCount:N0} error line{(ErrorCount == 1 ? "" : "s")} — click for the latest, again for the one before (Alt+↑); Shift+click or Alt+↓ goes forward";

    public string WarningTip => WarningCount == 0
        ? "No warning lines shown"
        : $"{WarningCount:N0} warning line{(WarningCount == 1 ? "" : "s")} — click for the latest, again for the one before; Shift+click goes forward";

    /// <summary>Re-reads the problems from the lines on screen.</summary>
    public void Update(IList<LogLineViewModel> shown)
    {
        _shown = shown;
        _errors.Clear();
        _warnings.Clear();
        foreach (var line in shown)
        {
            // A stack trace's frames carry its error for the row's colour and Errors only,
            // but the jump stops once, on the line that threw.
            if (line.IsInheritedSeverity)
            {
                continue;
            }

            if (line.Severity == LogSeverity.Error)
            {
                _errors.Add(line);
            }
            else if (line.Severity == LogSeverity.Warn)
            {
                _warnings.Add(line);
            }
        }

        ErrorCount = _errors.Count;
        WarningCount = _warnings.Count;
        if (Current is { } current && !shown.Contains(current))
        {
            MoveTo(null);
        }

        PreviousErrorCommand.NotifyCanExecuteChanged();
        NextErrorCommand.NotifyCanExecuteChanged();
        PreviousWarningCommand.NotifyCanExecuteChanged();
        NextWarningCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The error before the cursor (the latest one first) — a click on the error count, Alt+↑.</summary>
    [RelayCommand(CanExecute = nameof(HasErrors))]
    private void PreviousError() => Step(_errors, -1);

    /// <summary>The error after the cursor — Shift+click on the error count, Alt+↓.</summary>
    [RelayCommand(CanExecute = nameof(HasErrors))]
    private void NextError() => Step(_errors, +1);

    [RelayCommand(CanExecute = nameof(HasWarnings))]
    private void PreviousWarning() => Step(_warnings, -1);

    [RelayCommand(CanExecute = nameof(HasWarnings))]
    private void NextWarning() => Step(_warnings, +1);

    private void Step(List<LogLineViewModel> lines, int direction)
    {
        if (lines.Count == 0)
        {
            return;
        }

        int next;
        var index = Current is null ? -1 : lines.IndexOf(Current);
        if (index >= 0)
        {
            next = ((index + direction) % lines.Count + lines.Count) % lines.Count;
        }
        else if (Current is not null && _shown.IndexOf(Current) is var at and >= 0)
        {
            // On a line of the other kind: the neighbour of this kind in the chosen direction.
            next = direction < 0
                ? lines.FindLastIndex(l => _shown.IndexOf(l) < at)
                : lines.FindIndex(l => _shown.IndexOf(l) > at);
            if (next < 0)
            {
                next = direction < 0 ? lines.Count - 1 : 0;
            }
        }
        else
        {
            // No cursor: back from the end lands on the latest, forward from the start on the first.
            next = direction < 0 ? lines.Count - 1 : 0;
        }

        MoveTo(lines[next]);
    }

    /// <summary>
    /// Puts the cursor on any shown line — a filtered context line double-clicked to be seen
    /// in place — so it is highlighted and the next jump starts from there.
    /// </summary>
    public void Point(LogLineViewModel line) => MoveTo(line);

    private void MoveTo(LogLineViewModel? line)
    {
        if (ReferenceEquals(line, Current))
        {
            return;
        }

        if (Current is not null)
        {
            Current.IsProblemCursor = false;
        }

        Current = line;
        if (line is not null)
        {
            line.IsProblemCursor = true;
        }
    }
}
