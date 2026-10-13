using Avalonia.Input;
using KubeNimbus.App.Views;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The chords the exec pane encodes itself because the terminal control gets them wrong
/// (<see cref="ExecView.EncodeKey"/>). The rendered half — real key events on a real pane, the
/// bytes read off the terminal model and the palette left shut — is the harness's
/// <c>ux-exec-keys</c> check.
/// </summary>
public class ExecKeyEncodingTests
{
    [Test]
    public async Task Alt_B_and_Alt_F_are_escape_and_the_lower_case_letter()
    {
        // #315: Windows hands the control "B" as the symbol of Alt+B, and the shell got ESC B.
        await Assert.That(ExecView.EncodeKey(Key.B, KeyModifiers.Alt, altIsMeta: true)).IsEqualTo("\u001bb");
        await Assert.That(ExecView.EncodeKey(Key.F, KeyModifiers.Alt, altIsMeta: true)).IsEqualTo("\u001bf");
    }

    [Test]
    public async Task Alt_Shift_letter_keeps_the_upper_case()
    {
        await Assert.That(ExecView.EncodeKey(Key.B, KeyModifiers.Alt | KeyModifiers.Shift, altIsMeta: true))
            .IsEqualTo("\u001bB");
    }

    [Test]
    public async Task Option_on_macOS_and_AltGr_are_left_to_the_control()
    {
        await Assert.That(ExecView.EncodeKey(Key.B, KeyModifiers.Alt, altIsMeta: false)).IsNull();
        await Assert.That(ExecView.EncodeKey(Key.Q, KeyModifiers.Control | KeyModifiers.Alt, altIsMeta: true)).IsNull();
    }

    [Test]
    public async Task Ctrl_Shift_L_is_form_feed_for_the_shell()
    {
        // #222: the owner's decision is that the terminal keeps the chord, so it is ^L here and
        // the window's logs palette does not open.
        await Assert.That(ExecView.EncodeKey(Key.L, KeyModifiers.Control | KeyModifiers.Shift, altIsMeta: true))
            .IsEqualTo("\u000c");
        await Assert.That(ExecView.EncodeKey(Key.A, KeyModifiers.Control | KeyModifiers.Shift, altIsMeta: true))
            .IsEqualTo("\u0001");
    }

    [Test]
    public async Task Keys_the_control_already_encodes_are_left_to_it()
    {
        await Assert.That(ExecView.EncodeKey(Key.C, KeyModifiers.Control, altIsMeta: true)).IsNull();
        await Assert.That(ExecView.EncodeKey(Key.B, KeyModifiers.None, altIsMeta: true)).IsNull();
        await Assert.That(ExecView.EncodeKey(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift, altIsMeta: true)).IsNull();
        await Assert.That(ExecView.EncodeKey(Key.D1, KeyModifiers.Alt, altIsMeta: true)).IsNull();
        await Assert.That(ExecView.EncodeKey(Key.F1, KeyModifiers.None, altIsMeta: true)).IsNull();
        await Assert.That(ExecView.EncodeKey(Key.F10, KeyModifiers.None, altIsMeta: true)).IsNull();
    }

    [Test]
    public async Task F11_and_F12_are_xterm_s_sequences()
    {
        // #337: the control sends nothing for these two.
        await Assert.That(ExecView.EncodeKey(Key.F11, KeyModifiers.None, altIsMeta: true)).IsEqualTo("\u001b[23~");
        await Assert.That(ExecView.EncodeKey(Key.F12, KeyModifiers.None, altIsMeta: true)).IsEqualTo("\u001b[24~");
        await Assert.That(ExecView.EncodeKey(Key.F12, KeyModifiers.Shift, altIsMeta: true)).IsEqualTo("\u001b[24;2~");
        await Assert.That(ExecView.EncodeKey(Key.F11, KeyModifiers.Control | KeyModifiers.Alt, altIsMeta: true)).IsEqualTo("\u001b[23;7~");
    }

    [Test]
    public async Task Cmd_chords_stay_the_window_s()
    {
        // On macOS Cmd+Shift+L is the palette's gesture and must still reach the window.
        await Assert.That(ExecView.EncodeKey(Key.L, KeyModifiers.Meta | KeyModifiers.Shift, altIsMeta: false)).IsNull();
    }
}
