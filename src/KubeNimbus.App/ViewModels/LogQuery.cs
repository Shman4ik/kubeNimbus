using System.Text.RegularExpressions;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// A log pane's search, compiled once per change of the box or its two toggles: plain text
/// or a regular expression, ignoring case or not, and any number of <c>!term</c>
/// exclusions. It is the one rule the find, the filter, the highlight and the overview
/// ruler all ask, so they cannot disagree about what matches.
/// </summary>
/// <remarks>
/// <para>
/// Regular expressions are what someone who copies a log into Notepad++ goes there for
/// (<c>timeout|refused</c>, <c>status=5\d\d</c>), and the two toggles sit in the search box
/// the way they do in VS Code's find widget, Alt+R and Alt+C included.
/// </para>
/// <para>
/// A word starting with <c>!</c> hides every line it matches, in either mode:
/// <c>!healthz !readyz</c> takes the probe noise out, and <c>timeout !retrying</c> finds the
/// timeouts that were not retried. k9s's <c>/!</c>, VS Code's output filter and Chrome's
/// <c>-</c> all read it that way. Only when the box holds such a word is it split on
/// spaces; otherwise the text is one phrase, spaces included, as it always was. An
/// exclusion is a term in the same mode as the rest — a regular expression while the
/// toggle is on.
/// </para>
/// <para>
/// A pattern runs on <see cref="RegexOptions.NonBacktracking"/>, which is linear in the
/// line's length whatever was typed: the search runs on the UI thread over a buffer of
/// thousands of lines on every keystroke, and a backtracking engine given <c>(a+)+b</c>
/// would freeze the window — a per-line timeout only bounds each line, and four thousand
/// bounded lines are still a hang. The price is that backreferences and lookarounds are
/// refused, which the box says in words. It needs no code generation, so it is the same
/// engine under NativeAOT. A pattern that does not parse is not a search at all — the pane
/// shows why and nothing is filtered, rather than hiding every line.
/// </para>
/// </remarks>
public sealed class LogQuery
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    private readonly Term? _include;
    private readonly Term[] _excludes;

    private LogQuery(string? includeText, Term? include, Term[] excludes)
    {
        IncludeText = includeText;
        _include = include;
        _excludes = excludes;
    }

    /// <summary>What is searched for, without the <c>!term</c> exclusions — what a pin takes.</summary>
    public string? IncludeText { get; }

    /// <summary>
    /// The query for the box's text, or null when there is nothing to search for or a
    /// pattern does not parse; <paramref name="error"/> then says which (null for empty).
    /// </summary>
    public static LogQuery? Create(string text, bool regex, bool matchCase, out string? error)
    {
        error = null;
        if (text.Length == 0)
        {
            return null;
        }

        var include = text;
        var excludes = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(IsExclusion))
        {
            include = string.Join(' ', words.Where(w => !IsExclusion(w)));
            excludes.AddRange(words.Where(IsExclusion).Select(w => w[1..]));
        }

        try
        {
            var includeTerm = include.Length == 0 ? null : new Term(include, regex, matchCase);
            var excludeTerms = excludes.Select(e => new Term(e, regex, matchCase)).ToArray();
            return includeTerm is null && excludeTerms.Length == 0 ? null : new LogQuery(includeTerm is null ? null : include, includeTerm, excludeTerms);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
        catch (NotSupportedException)
        {
            error = "Backreferences and lookarounds are not supported: the log search uses a linear-time engine so that no pattern can freeze the window.";
            return null;
        }
    }

    private static bool IsExclusion(string word) => word.Length > 1 && word[0] == '!';

    /// <summary>Whether the query has something to find, rather than only lines to hide.</summary>
    public bool HasInclude => _include is not null;

    /// <summary>Whether the query hides any line (a <c>!term</c>).</summary>
    public bool HasExcludes => _excludes.Length > 0;

    /// <summary>Whether <paramref name="text"/> survives the exclusions. True for every line when there are none.</summary>
    public bool Admits(string text)
    {
        foreach (var exclude in _excludes)
        {
            if (exclude.IsMatch(text))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="text"/> matches what is searched for. False when only exclusions were typed.</summary>
    public bool IsMatch(string text) => _include is { } include && include.IsMatch(text);

    /// <summary>
    /// Every [start, length) that matches in <paramref name="text"/> at or after
    /// <paramref name="from"/>, without overlaps. Empty regex matches are skipped: a
    /// highlight nobody can see is not a match anyone can read.
    /// </summary>
    public IReadOnlyList<(int Start, int Length)> Matches(string? text, int from) =>
        _include is { } include && !string.IsNullOrEmpty(text) ? include.Matches(text, from) : [];

    private sealed class Term
    {
        private readonly string _text;
        private readonly Regex? _regex;
        private readonly StringComparison _comparison;

        public Term(string text, bool regex, bool matchCase)
        {
            _text = text;
            _comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (regex)
            {
                var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                _regex = new Regex(text, options, Timeout);
            }
        }

        public bool IsMatch(string text)
        {
            if (_regex is null)
            {
                return text.Contains(_text, _comparison);
            }

            try
            {
                return _regex.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        public IReadOnlyList<(int Start, int Length)> Matches(string text, int from)
        {
            var ranges = new List<(int, int)>();
            var index = Math.Clamp(from, 0, text.Length);
            if (_regex is null)
            {
                while (index <= text.Length - _text.Length)
                {
                    var found = text.IndexOf(_text, index, _comparison);
                    if (found < 0)
                    {
                        break;
                    }

                    ranges.Add((found, _text.Length));
                    index = found + _text.Length;
                }

                return ranges;
            }

            // Matched against the message alone, never from an offset into the displayed
            // text: `^ERROR` means "the message starts with ERROR", and a start offset would
            // leave `^` anchored to the timestamp the pane printed in front of it.
            var message = index == 0 ? text : text[index..];
            try
            {
                for (var match = _regex.Match(message); match.Success; match = match.NextMatch())
                {
                    if (match.Length > 0)
                    {
                        ranges.Add((index + match.Index, match.Length));
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // What was found before the timeout is still drawn.
            }

            return ranges;
        }
    }
}
