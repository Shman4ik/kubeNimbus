using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #267: a copy from the exec pane drops the blank cells at the end of each line and the blank
/// lines at the end, as every terminal emulator does. The rendered half (Ctrl+Shift+C and the
/// menu's Copy go through this) is the harness's <c>ux-exec-copy</c> check.
/// </summary>
public class ExecCopyTests
{
    [Test]
    public async Task Each_line_loses_its_padding_and_the_blank_rows_under_it()
    {
        var screen = "/ # ls" + new string(' ', 131) + "\n"
            + "run.sh   report.log" + new string(' ', 118) + "\n"
            + "/ # " + new string(' ', 133) + "\n"
            + new string(' ', 137) + "\n"
            + new string(' ', 137);

        await Assert.That(ExecCopy.Trim(screen)).IsEqualTo("/ # ls\nrun.sh   report.log\n/ #");
    }

    [Test]
    public async Task Spaces_inside_and_before_the_text_are_kept()
    {
        await Assert.That(ExecCopy.Trim("  PID  USER   \n    1  root  "))
            .IsEqualTo("  PID  USER\n    1  root");
    }

    [Test]
    public async Task A_blank_line_between_lines_is_kept()
    {
        await Assert.That(ExecCopy.Trim("one   \n      \ntwo  ")).IsEqualTo("one\n\ntwo");
    }

    [Test]
    public async Task Windows_line_endings_stay_windows_line_endings()
    {
        await Assert.That(ExecCopy.Trim("one  \r\ntwo \r\n   \r\n")).IsEqualTo("one\r\ntwo");
    }

    [Test]
    public async Task Unwritten_cells_that_come_back_as_nul_are_trimmed_too()
    {
        await Assert.That(ExecCopy.Trim("prompt\0\0\0 \0")).IsEqualTo("prompt");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("    \n   \n")]
    public async Task A_selection_of_blank_cells_copies_nothing(string? selected)
    {
        await Assert.That(ExecCopy.Trim(selected)).IsEqualTo("");
    }
}
