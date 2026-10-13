<#
.SYNOPSIS
    Drives the kubeNimbus instance started by qa-app.ps1 through Windows UI Automation.

.DESCRIPTION
    The kn-qa agent's hands and eyes, as plain text, so a cheap model can use them
    reliably. Avalonia publishes its control tree to UI Automation on Windows, which gives
    every element's type, name, AutomationId, enabled state and bounds. The Avalonia
    DevTools MCP would do the same job but is not available on every subscription; this
    needs nothing beyond Windows and PowerShell 7.

    Two kinds of action, deliberately separate:
    - `invoke`, `select`, `toggle`, `collapse`, `set-text` use UIA patterns. They do not move the
      mouse or need focus, so they are safe while someone is using the machine.
    - `click`, `double-click`, `right-click` and `keys` are REAL OS input: the window is
      brought to the front and the pointer and keyboard really move. Use them only when a
      check is about real input (a DataGrid double-click, a context menu, a key gesture),
      which is exactly what the pattern actions cannot prove.
    A pattern action reads its state back: `select` fails when the item is not selected
    afterwards, or when it sits in a popup list that is still open (the namespace picker's
    list only moves its highlight on a selection), `toggle` when the state did not change,
    `collapse` when the element is not collapsed, `set-text` when the box does not read the
    text back, and `invoke` says which pattern it used and, for InvokePattern (which has no
    state), that the effect is for the caller to check. A list that only acts on a pointer
    press then fails loudly instead of printing success; use `click` there.

    `dump` prints each element's pattern states when it has the pattern, false ones
    included: `selected` / `not selected`, `toggle=On|Off`, `expand=Expanded|Collapsed`,
    and the item status (`status='…'`) when the app sets one.

    Runs only inside the owner's Hyper-V QA VM (KUBENIMBUS_QA_VM=1), never on the owner's
    own desktop; see qa-app.ps1.

    `resize -Width <px> -Height <px>` restores the window and sets its outer size in screen
    pixels (what Windows reports, so at 150% scaling a 960-DIP window is 1440 wide). It
    prints the bounds UI Automation reads back afterwards, which is what to check: the app
    clamps to its own MinWidth.

    Elements in a popup (the log pane's ⋯ menu, a combo's drop-down, a context menu) are
    their own native window and may hang past the main window's edge. UI Automation does
    not give them a node of their own (their parent is the main window), so a real click is
    judged by asking Windows which window is at the point it would land on: it must be one
    of this app's own windows. That also refuses a click that another application's window
    would take. A scrolling ancestor clips the point only when it is drawn in the same
    window, so a combo's drop-down items (the Range list) are not judged by the closed box.

    `screenshot -Id DockContent` crops the open dock tab's pane (below the dock's tab strip).

    More real input, for checks that need it and have no element to target:
    - `click`/`double-click`/`right-click` take `-Modifiers ctrl,shift,alt` (held for the
      click: Shift+click and Ctrl+click on a list) and `-OffsetX/-OffsetY` (a point
      measured from the element's top-left instead of its centre: the terminal and the
      YAML editor have no children to aim at, so name the pane that holds them).
    - `drag -DX <px> -DY <px>` presses on the element (or its offset point), moves in
      steps and releases: a column edge, the DockSplitter, a text selection.
    - `wheel -Delta <notches>` turns the mouse wheel over the element (positive up);
      `-Horizontal` for the horizontal wheel.
    - `move -X <px> -Y <px>` puts the window's top-left there without resizing it.
    Real input needs an unlocked interactive desktop. When the VM's console is on the lock
    screen the OS drops the input and screenshots fail without a word, so those commands
    check first and say so.

    UI Automation reads sometimes throw "Unexpected HRESULT" while the tree changes under
    them (a streaming log); every read is retried once on that error.

    `screenshot` captures one element, cropped to its bounds, so a check reads the region
    it is about and not a whole window. -WholeWindow captures the app's window instead;
    it never captures the desktop.

    Element selection (every command that targets one element):
      -Id <AutomationId>   exact x:Name / AutomationId
      -Name <text>         exact accessible name
      -Match <text>        name contains text (case-insensitive)
      -Type <ControlType>  Button, Text, ListItem, Edit, DataItem, ... (narrows the above)
      -Index <n>           the n-th match (0-based), default 0
      -Within <AutomationId>  search only inside that container (ResourceGrid, PaletteList,
                           InspectorTabStrip, …): much faster than a whole-window scan
      -Dialog              search the open Windows file dialog instead of the main window
                           (its file name box is -Id 1148, Open is -Id 1, Cancel -Id 2).
                           On Windows 11 1148 and 1 are Panes: `set-text` writes to the Edit
                           inside 1148, `invoke` presses a Button inside 1 and, when there is
                           none, says to commit with `keys -Dialog -Id 1148 -Keys '{ENTER}'`;
                           real input (`keys`, `click`) brings the dialog forward, not the app

.EXAMPLE
    ./scripts/qa-ui.ps1 dump -Match crashloop
    ./scripts/qa-ui.ps1 invoke -Id ResourcesModeItem
    ./scripts/qa-ui.ps1 set-text -Id FilterBox -Text payments
    ./scripts/qa-ui.ps1 wait -Match "Crash-looping" -Timeout 20
    ./scripts/qa-ui.ps1 double-click -Match "crashloop-" -Type DataItem
    ./scripts/qa-ui.ps1 keys -Keys "{ESC}"
    ./scripts/qa-ui.ps1 resize -Width 960 -Height 720
    ./scripts/qa-ui.ps1 set-text -Dialog -Id 1148 -Text C:\qa\extra.kubeconfig
    ./scripts/qa-ui.ps1 invoke -Dialog -Id 1
    ./scripts/qa-ui.ps1 keys -Dialog -Id 1148 -Keys "{ENTER}"
    ./scripts/qa-ui.ps1 collapse -Name Config -Within SidebarItems
    ./scripts/qa-ui.ps1 screenshot -Id RowFilterBox -Out $env:TEMP/qa/step1.png
    ./scripts/qa-ui.ps1 screenshot -Id DockContent -Out $env:TEMP/qa/dock.png
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [ValidateSet('dump', 'find', 'invoke', 'select', 'toggle', 'collapse', 'set-text', 'click', 'double-click',
        'right-click', 'drag', 'wheel', 'keys', 'wait', 'wait-gone', 'screenshot', 'window', 'resize', 'move')]
    [string]$Command,
    [string]$Id,
    [string]$Name,
    [string]$Match,
    [string]$Within,
    [string]$Type,
    [int]$Index = 0,
    [string]$Text,
    [string]$Keys,
    [string]$Out,
    [int]$Timeout = 10,
    [int]$Max = 400,
    [int]$Width,
    [int]$Height,
    [int]$X,
    [int]$Y,
    [int]$DX,
    [int]$DY,
    [int]$Delta,
    [int]$OffsetX = [int]::MinValue,
    [int]$OffsetY = [int]::MinValue,
    [string]$Modifiers,
    [switch]$Horizontal,
    [switch]$WholeWindow,
    [switch]$Dialog
)

$ErrorActionPreference = 'Stop'
if ($env:KUBENIMBUS_QA_VM -ne '1') {
    throw "Refusing: this machine is not marked as the QA VM (KUBENIMBUS_QA_VM is not '1'). See qa-app.ps1."
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type -Namespace QaNative -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
[DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
// POINT is passed by value as one 64-bit register on x64: y in the high half, x in the low.
[DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(long point);
[DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
'@

$A = [System.Windows.Automation.AutomationElement]
$pidFile = Join-Path ([IO.Path]::GetTempPath()) 'kubenimbus-qa/app.pid'
if (-not (Test-Path $pidFile)) { throw 'No QA instance. Start one: ./scripts/qa-app.ps1' }
$appPid = [int](Get-Content $pidFile -Raw | ConvertFrom-Json).Pid

# A UIA read can throw "Unexpected HRESULT" (a COMException, or an
# ElementNotAvailableException) while the tree changes under it, typically while a log
# streams. One retry after a short pause has been enough; a second failure is real.
function Invoke-UiaRead([scriptblock]$read) {
    try { & $read }
    catch {
        $ex = $_.Exception
        $transient = $ex -is [System.Runtime.InteropServices.COMException] -or
            $ex -is [System.Windows.Automation.ElementNotAvailableException] -or
            $ex.Message -like '*Unexpected HRESULT*' -or
            ($ex.InnerException -and $ex.InnerException.Message -like '*Unexpected HRESULT*')
        if (-not $transient) { throw }
        Start-Sleep -Milliseconds 250
        & $read
    }
}

function Get-Window {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $appPid)
    $windows = Invoke-UiaRead { , @($A::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) }
    # The open-file dialog is a top-level window of the same process: never mistake it for
    # the app's own window (#314).
    $window = $windows | Where-Object { $_.Current.ClassName -ne $DialogClass } | Select-Object -First 1
    if (-not $window) { throw "kubeNimbus (pid $appPid) has no window. Is it still running?" }
    $window
}

# The Windows common file dialog (Preferences → General → Add file…, Open kubeconfig file):
# class #32770, in this process. UI Automation lists it beside the main window or, as an owned
# window, inside it; both are searched. Its controls have Win32 ids as AutomationIds: the
# file name box is 1148 and Open is 1, so `set-text -Dialog -Id 1148 -Text <path>` and
# `invoke -Dialog -Id 1` drive it (#314). Cancel is 2. In the QA VM's Windows 11 both are
# Panes with no pattern (#370), so those commands look inside them for the Edit and the
# Button.
$DialogClass = '#32770'
function Get-Dialog {
    $byClass = New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, $DialogClass)
    $byPid = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $appPid)
    $both = New-Object System.Windows.Automation.AndCondition($byClass, $byPid)
    $dialog = Invoke-UiaRead { $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $both) }
    if (-not $dialog) {
        $dialog = Invoke-UiaRead { (Get-Window).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byClass) }
    }
    if (-not $dialog) { throw 'No file dialog is open in kubeNimbus (no #32770 window in its process).' }
    $dialog
}

# Avalonia falls back to the content's type name when a control has no accessible name;
# that is a missing AutomationProperties.Name, and it is reported as such.
function Format-Name([string]$value) {
    if ($value -match '^(Avalonia|KubeNimbus)\.[\w.]+$') { return '<unnamed>' }
    $value
}

function Format-Element($element, [int]$number) {
    $c = $element.Current
    $kind = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
    $r = $c.BoundingRectangle
    $flags = @()
    if (-not $c.IsEnabled) { $flags += 'disabled' }
    if ($c.IsOffscreen) { $flags += 'offscreen' }
    if ($c.HasKeyboardFocus) { $flags += 'focused' }
    # Each pattern's state is printed whenever the element has the pattern, false included:
    # a row that is not selected or a group that is collapsed is a reading a check can need
    # (C-13, C-16 in the 2026-10-13 run could not tell "not selected" from "no pattern").
    try {
        $sel = $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $flags += if ($sel.Current.IsSelected) { 'selected' } else { 'not selected' }
    } catch { }
    try {
        $tog = $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $flags += "toggle=$($tog.Current.ToggleState)"
    } catch { }
    try {
        $exp = $element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $flags += "expand=$($exp.Current.ExpandCollapseState)"
    } catch { }
    $id = if ($c.AutomationId) { " [$($c.AutomationId)]" } else { '' }
    if ($c.HelpText) { $id += " help='$($c.HelpText)'" }
    if ($c.ItemStatus) { $id += " status='$($c.ItemStatus)'" }
    $f = if ($flags) { " ($($flags -join ', '))" } else { '' }
    $bounds = if ($r.IsEmpty) { '' } else { " @{0:0},{1:0} {2:0}x{3:0}" -f $r.X, $r.Y, $r.Width, $r.Height }
    "#{0} {1} '{2}'{3}{4}{5}" -f $number, $kind, (Format-Name $c.Name), $id, $f, $bounds
}

function Get-Matches {
    $window = if ($Dialog) { Get-Dialog } else { Get-Window }
    $scope = $window
    if ($Within) {
        # A scan of the whole window walks every row of every grid and the sidebar's hundred
        # kinds: 2.8 s for a `dump -Type ListItem` in the QA VM against 0.7 s inside
        # SidebarItems (of which about half a second is starting pwsh). The container is
        # found by the provider's own search, not by walking the tree here.
        $byId = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $Within)
        $scope = Invoke-UiaRead { $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byId) }
        if (-not $scope) { return }
    }
    $all = Invoke-UiaRead {
        # Read every element's name inside the retry too: the throw can come from either.
        $found = $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($f in $found) { $null = $f.Current.Name }
        , @($found)
    }
    foreach ($e in $all) {
        $c = $e.Current
        if ($Id -and $c.AutomationId -ne $Id) { continue }
        if ($Name -and $c.Name -ne $Name) { continue }
        if ($Match -and $c.Name -notlike "*$Match*" -and $c.AutomationId -notlike "*$Match*" -and $c.HelpText -notlike "*$Match*") { continue }
        if ($Type -and ($c.ControlType.ProgrammaticName -replace '^ControlType\.', '') -ne $Type) { continue }
        $e
    }
}

function Get-Target {
    if (-not ($Id -or $Name -or $Match)) { throw 'Name a target with -Id, -Name or -Match.' }
    $found = @(Get-Matches)
    if ($found.Count -le $Index) { throw "No element matches (found $($found.Count))." }
    Show-Element $found[$Index]
}

function Test-SameElement($a, $b) { [System.Windows.Automation.Automation]::Compare($a, $b) }

# The element itself when it has the pattern, else the first descendant of the given control
# type that has it, else any descendant that has it; $null when none does.
function Find-PatternInside($element, $pattern, [string]$preferredType) {
    $has = { param($el) try { $null = $el.GetCurrentPattern($pattern); $true } catch { $false } }
    if (& $has $element) { return $element }
    $inside = Invoke-UiaRead {
        , @($element.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition))
    }
    $typed = $inside | Where-Object { ($_.Current.ControlType.ProgrammaticName -replace '^ControlType\.', '') -eq $preferredType -and (& $has $_) } | Select-Object -First 1
    if ($typed) { return $typed }
    $inside | Where-Object { & $has $_ } | Select-Object -First 1
}

# Whether a real click at a screen point would land on one of this app's own windows.
# A popup (the log pane's ⋯ menu, a combo's drop-down, a context menu) is a native window
# of its own that can hang past the main window's edge, but UI Automation gives it no node
# of its own: its items are children of the main window (found out in the 0.7.0 QA run,
# where the earlier walk up to "the popup's HWND" never found one and refused a menu item
# that was plainly on screen). So the main window's bounds cannot judge them, and Windows
# is asked who is at the point instead. A point over the desktop or another application's
# window is refused, which also catches an app that something else has covered.
function Get-RootAt([double]$x, [double]$y) {
    $low = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$x), 0)
    $point = ([long][int]$y -shl 32) -bor [long]$low
    $hwnd = [QaNative.Win32]::WindowFromPoint($point)
    if ($hwnd -eq [IntPtr]::Zero) { return [IntPtr]::Zero }
    $top = [QaNative.Win32]::GetAncestor($hwnd, 2)   # GA_ROOT
    if ($top -eq [IntPtr]::Zero) { $top = $hwnd }
    $top
}

function Test-LandsOnApp([double]$x, [double]$y) {
    $top = Get-RootAt $x $y
    if ($top -eq [IntPtr]::Zero) { return $false }
    $owner = [uint32]0
    [QaNative.Win32]::GetWindowThreadProcessId($top, [ref]$owner) | Out-Null
    $owner -eq $appPid
}

# The popup window an element is drawn in, or zero when it is in the main window (or not on
# screen). A flyout, a combo's drop-down and a context menu are native windows of their own
# in this process; UI Automation still lists their items under the main window.
function Get-PopupWindow($element) {
    $r = $element.Current.BoundingRectangle
    if ($r.IsEmpty) { return [IntPtr]::Zero }
    $x = $r.X + $r.Width / 2
    $y = $r.Y + $r.Height / 2
    if (-not (Test-LandsOnApp $x $y)) { return [IntPtr]::Zero }
    $root = Get-RootAt $x $y
    if ($root -eq (Get-MainHandle)) { return [IntPtr]::Zero }
    $root
}

# An element scrolled out of its list is in the tree but has no usable bounds and cannot
# be clicked. Scroll it into view first when its container supports that. (Items inside a
# collapsed sidebar section are not realized at all — use a filter box to reach those.)
function Test-InWindow($element, [double]$px = [double]::NaN, [double]$py = [double]::NaN) {
    $r = $element.Current.BoundingRectangle
    if ($r.IsEmpty) { return $false }
    $x = if ([double]::IsNaN($px)) { $r.X + $r.Width / 2 } else { $px }
    $y = if ([double]::IsNaN($py)) { $r.Y + $r.Height / 2 } else { $py }
    $window = Get-Window
    if (-not (Test-LandsOnApp $x $y)) { return $false }

    # Also inside the viewport of every scrolling ancestor: an item can be within the
    # window yet clipped by the sidebar's or a list's scroll viewer. AutomationElements
    # are compared with Automation.Compare — two wrappers of one element are not -eq.
    # Only an ancestor drawn in the same native window as the point can clip it: a ComboBox
    # has the Scroll pattern and its bounds are the closed box in the main window, while its
    # items are in the drop-down's popup window, so judging them by the box refused every
    # Range item in the 2026-10-13 run (#370).
    $pointRoot = Get-RootAt $x $y
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $node = $walker.GetParent($element)
    while ($node -and -not [System.Windows.Automation.Automation]::Compare($node, $window)) {
        $scrolls = $false
        try { $null = $node.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern); $scrolls = $true } catch { }
        if ($scrolls) {
            $v = $node.Current.BoundingRectangle
            $sameWindow = -not $v.IsEmpty -and (Get-RootAt ($v.X + $v.Width / 2) ($v.Y + $v.Height / 2)) -eq $pointRoot
            # An open drop-down is never clipped by its own closed box, whichever window is
            # at the box's centre.
            try {
                $dropDown = $node.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                if ($dropDown.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded) { $sameWindow = $false }
            } catch { }
            if ($sameWindow -and ($x -lt $v.Left -or $x -gt $v.Right -or $y -lt $v.Top -or $y -gt $v.Bottom)) { return $false }
        }
        $node = $walker.GetParent($node)
    }
    $true
}

function Show-Element($element) {
    # Avalonia reports an item scrolled below its list's viewport with IsOffscreen = false
    # and bounds past the window's edge, so "offscreen" is decided against the window.
    if ($element.Current.IsOffscreen -or -not (Test-InWindow $element)) {
        try {
            $element.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
            Start-Sleep -Milliseconds 150
        } catch { }
    }
    $element
}

# While the VM's console sits on the lock screen the OS accepts real input and screen
# captures and does nothing with them: SendKeys throws "The operation completed
# successfully", CopyFromScreen "The handle is invalid", and a click reports success. The
# 0.7.0 run lost most of a batch to that. Say so instead.
function Assert-InputDesktop {
    try {
        $bitmap = New-Object System.Drawing.Bitmap 1, 1
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $g.CopyFromScreen(0, 0, 0, 0, $bitmap.Size)
        $g.Dispose(); $bitmap.Dispose()
    } catch {
        throw 'The interactive desktop is not available (the console is locked or disconnected): real input and screenshots would silently do nothing. Ask the owner to unlock the VM, and turn off its lock screen timeout.'
    }
}

# The main window's handle, from the same UI Automation node every other command uses.
# `Process.MainWindowHandle` is a guess Windows makes per call (the first visible unowned
# window it finds), and right after start it can be 0 or another window, which is the likeliest
# reason the 0.7.0 run's first `resize` threw "SetWindowPos failed." (#314; not reproduced
# outside the QA VM).
function Get-MainHandle {
    $handle = [IntPtr](Get-Window).Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) { $handle = [IntPtr](Get-Process -Id $appPid).MainWindowHandle }
    if ($handle -eq [IntPtr]::Zero) { throw "kubeNimbus (pid $appPid) has no window handle yet. Wait for its window and retry." }
    $handle
}

# SetWindowPos, retried once after a pause (a window still restoring from maximized), and
# failing with the Win32 error rather than a bare "failed".
function Set-WindowPos([IntPtr]$hwnd, [int]$x, [int]$y, [int]$cx, [int]$cy, [uint32]$flags) {
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        if ([QaNative.Win32]::SetWindowPos($hwnd, [IntPtr]::Zero, $x, $y, $cx, $cy, $flags)) { return }
        $code = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        if ($attempt -eq 2) {
            throw "SetWindowPos failed on window 0x$($hwnd.ToString('X')): $([System.ComponentModel.Win32Exception]::new($code).Message) (Win32 error $code)."
        }
        Start-Sleep -Milliseconds 300
    }
}

function Show-Window {
    Assert-InputDesktop
    # The file dialog takes real input itself: bringing its disabled owner forward instead
    # would send the keys nowhere.
    $hwnd = if ($Dialog) { [IntPtr](Get-Dialog).Current.NativeWindowHandle } else { Get-MainHandle }
    # SW_RESTORE only when minimized: on a maximized window it restores the old size, and the
    # move closes any popup (a drop-down, a flyout) the next click was meant for.
    if ([QaNative.Win32]::IsIconic($hwnd)) { [QaNative.Win32]::ShowWindow($hwnd, 9) | Out-Null }
    [QaNative.Win32]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 200
}

# Where a real click lands: the element's centre, or -OffsetX/-OffsetY from its top-left
# (either one alone keeps the other at the centre).
function Get-ClickPoint($element) {
    $r = $element.Current.BoundingRectangle
    $ox = if ($OffsetX -ne [int]::MinValue) { $OffsetX } else { $r.Width / 2 }
    $oy = if ($OffsetY -ne [int]::MinValue) { $OffsetY } else { $r.Height / 2 }
    [int]($r.X + $ox), [int]($r.Y + $oy)
}

# Holds or releases -Modifiers (ctrl, shift, alt) with real key events; scan codes are sent
# because a console or terminal reads those and ignores a bare virtual key.
function Set-Modifiers([bool]$down) {
    if (-not $Modifiers) { return }
    $map = @{ ctrl = 0x11; shift = 0x10; alt = 0x12 }
    foreach ($m in ($Modifiers -split ',' | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })) {
        if (-not $map.ContainsKey($m)) { throw "Unknown modifier '$m'; use ctrl, shift or alt." }
        $vk = [byte]$map[$m]
        $scan = [byte][QaNative.Win32]::MapVirtualKey([uint32]$vk, 0)
        [QaNative.Win32]::keybd_event($vk, $scan, $(if ($down) { 0 } else { 2 }), [UIntPtr]::Zero)
    }
}

function Move-PointerTo($element) {
    Show-Window
    $px, $py = Get-ClickPoint $element
    if (-not (Test-InWindow $element $px $py)) {
        $r = $element.Current.BoundingRectangle
        if (Test-LandsOnApp $px $py) {
            throw "The point ($px,$py) is on the app but outside the viewport of a list that scrolls (element at $($r.X),$($r.Y)) — a real click would hit whatever covers it. Filter the list or scroll it into view."
        }
        throw "The point ($px,$py) is not on one of the app's windows (element at $($r.X),$($r.Y)) — a real click would land elsewhere. Filter the list to bring it into view."
    }
    [QaNative.Win32]::SetCursorPos($px, $py) | Out-Null
    $px, $py
}

function Send-Click($element, [int]$count, [bool]$right) {
    $null = Move-PointerTo $element
    $down, $up = if ($right) { 0x0008, 0x0010 } else { 0x0002, 0x0004 }
    Set-Modifiers $true
    try {
        for ($i = 0; $i -lt $count; $i++) {
            [QaNative.Win32]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero)
            [QaNative.Win32]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 60
        }
    } finally { Set-Modifiers $false }
}

# Press, move in ten steps, release: enough for a column edge, a splitter or a selection.
function Send-Drag($element, [int]$dx, [int]$dy) {
    $px, $py = Move-PointerTo $element
    Set-Modifiers $true
    try {
        [QaNative.Win32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        for ($i = 1; $i -le 10; $i++) {
            [QaNative.Win32]::SetCursorPos([int]($px + $dx * $i / 10), [int]($py + $dy * $i / 10)) | Out-Null
            Start-Sleep -Milliseconds 30
        }
        [QaNative.Win32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    } finally { Set-Modifiers $false }
}

function Send-Wheel($element, [int]$notches, [bool]$horizontal) {
    $null = Move-PointerTo $element
    $flag = if ($horizontal) { 0x01000 } else { 0x0800 }   # MOUSEEVENTF_HWHEEL / _WHEEL
    $data = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]($notches * 120)), 0)
    [QaNative.Win32]::mouse_event($flag, 0, 0, $data, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
}

# A pattern action is read back after the app has had a moment to react, and one that left
# the state where it was fails (#314). In the 0.7.0 run `select` on a namespace-picker item and
# `select`/`invoke` on sidebar items printed success while nothing changed, so checks were
# recorded on a state that never happened. A list whose selection the app re-derives (the
# sidebar marks the selected kind itself) or that acts only on a pointer press puts the old
# selection back, and that shows here as a failure: use `click` for those.
$SettleMs = 400

function Invoke-Select($element, $pattern) {
    # A list in a popup that is still open afterwards has only moved its highlight: the
    # namespace picker's list selects a candidate and chooses on a press or Enter, so a
    # Select read back "selected" in the 2026-10-13 run while the namespace stayed where it
    # was (Q-2, #370). A popup that closes, as a combo's drop-down does when its choice is
    # taken, passes.
    $popup = Get-PopupWindow $element
    $pattern.Select()
    Start-Sleep -Milliseconds $SettleMs
    if (-not (Invoke-UiaRead { $pattern.Current.IsSelected })) {
        throw "Select changed nothing: $(Format-Element $element 0) is not selected afterwards (the app put its own selection back, or ignores a selection it did not see a press for). Use click for real input."
    }
    if ($popup -ne [IntPtr]::Zero -and [QaNative.Win32]::IsWindowVisible($popup)) {
        throw "Select changed nothing the app acts on: $(Format-Element $element 0) is selected in a popup list that is still open, so only its highlight moved (the namespace picker chooses on a press or Enter). Use click for real input."
    }
}

function Invoke-Toggle($element, $pattern) {
    $before = $pattern.Current.ToggleState
    $pattern.Toggle()
    Start-Sleep -Milliseconds $SettleMs
    if ((Invoke-UiaRead { $pattern.Current.ToggleState }) -eq $before) {
        throw "Toggle changed nothing: $(Format-Element $element 0) is still $before. Use click for real input."
    }
}

switch ($Command) {
    'window' {
        $w = Get-Window
        Format-Element $w 0
    }
    { $_ -in 'dump', 'find' } {
        $n = 0
        foreach ($e in Get-Matches) {
            if ($n -ge $Max) { "… truncated at $Max; narrow with -Match/-Type"; break }
            Format-Element $e $n
            $n++
        }
        if ($n -eq 0) { 'No elements match.' }
    }
    'invoke' {
        $e = Get-Target
        foreach ($p in [System.Windows.Automation.InvokePattern]::Pattern,
                        [System.Windows.Automation.SelectionItemPattern]::Pattern,
                        [System.Windows.Automation.TogglePattern]::Pattern,
                        [System.Windows.Automation.ExpandCollapsePattern]::Pattern) {
            try { $pattern = $e.GetCurrentPattern($p) } catch { continue }
            $before = Format-Element $e 0
            switch ($pattern.GetType().Name) {
                'InvokePattern' {
                    $pattern.Invoke()
                    # Invoke has no state to read back; say so rather than claim an effect.
                    "invoked (InvokePattern; the pattern reports no state, so check what it should have changed): $before"
                }
                'SelectionItemPattern' { Invoke-Select $e $pattern; "invoked (SelectionItemPattern): $(Format-Element $e 0)" }
                'TogglePattern' { Invoke-Toggle $e $pattern; "invoked (TogglePattern): $(Format-Element $e 0)" }
                'ExpandCollapsePattern' {
                    $pattern.Expand()
                    Start-Sleep -Milliseconds $SettleMs
                    if ($pattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
                        throw "Expand changed nothing: $(Format-Element $e 0) is still collapsed. Use click for real input."
                    }
                    "invoked (ExpandCollapsePattern): $(Format-Element $e 0)"
                }
            }
            return
        }
        # The file dialog's Open (id 1) is a Pane with no pattern on this Windows build; the
        # button, when there is one, is inside it (Q-3, #370).
        if ($Dialog) {
            $button = Find-PatternInside $e ([System.Windows.Automation.InvokePattern]::Pattern) 'Button'
            if ($button) {
                $before = Format-Element $button 0
                $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                "invoked (InvokePattern on a button inside $(Format-Element $e 0); the pattern reports no state, so check what it should have changed): $before"
                return
            }
            throw "No Invoke pattern on the dialog's $(Format-Element $e 0) or on a button inside it. Commit the file name with Enter instead: ./scripts/qa-ui.ps1 keys -Dialog -Id 1148 -Keys '{ENTER}'."
        }
        throw "No invoke/select/toggle/expand pattern on: $(Format-Element $e 0). Use click for real input."
    }
    'collapse' {
        $e = Get-Target
        $pattern = try { $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) } catch { $null }
        if (-not $pattern) { throw "No ExpandCollapse pattern on: $(Format-Element $e 0). Use click for real input." }
        $pattern.Collapse()
        Start-Sleep -Milliseconds $SettleMs
        if ((Invoke-UiaRead { $pattern.Current.ExpandCollapseState }) -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
            throw "Collapse changed nothing: $(Format-Element $e 0) is not collapsed afterwards. Use click for real input."
        }
        "collapsed: $(Format-Element $e 0)"
    }
    'select' {
        $e = Get-Target
        Invoke-Select $e $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        "selected: $(Format-Element $e 0)"
    }
    'toggle' {
        $e = Get-Target
        Invoke-Toggle $e $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        "toggled: $(Format-Element $e 0)"
    }
    'set-text' {
        if (-not $PSBoundParameters.ContainsKey('Text')) { throw 'Pass -Text, the text to put in the box.' }
        $e = Get-Target
        # The file dialog's name box (id 1148) is a Pane on this Windows build with the Edit
        # inside it (Q-3, #370); a number box's text box sits inside its control the same way.
        $box = Find-PatternInside $e ([System.Windows.Automation.ValuePattern]::Pattern) 'Edit'
        if (-not $box) { throw "No Value pattern on $(Format-Element $e 0) or on an edit box inside it. Use click and keys for real input." }
        $value = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $value.SetValue($Text)
        Start-Sleep -Milliseconds $SettleMs
        $now = Invoke-UiaRead { $value.Current.Value }
        if ($now -ne $Text) { throw "Set text changed nothing: $(Format-Element $box 0) reads '$now' afterwards, not '$Text'. Use click and keys for real input." }
        $where = if (Test-SameElement $box $e) { '' } else { " (the edit box inside $(Format-Element $e 0))" }
        "text set: $(Format-Element $box 0)$where"
    }
    # The element is described before the input is sent: the click usually closes or
    # rebuilds what it hit, and a description read afterwards shows a half-gone element
    # ("disabled, offscreen") that sent several 0.7.0 runs looking for a failure.
    'click' { $e = Get-Target; $before = Format-Element $e 0; Send-Click $e 1 $false; "clicked (real mouse): $before" }
    'double-click' { $e = Get-Target; $before = Format-Element $e 0; Send-Click $e 2 $false; "double-clicked (real mouse): $before" }
    'right-click' { $e = Get-Target; $before = Format-Element $e 0; Send-Click $e 1 $true; "right-clicked (real mouse): $before" }
    'drag' {
        if (-not ($PSBoundParameters.ContainsKey('DX') -or $PSBoundParameters.ContainsKey('DY'))) { throw 'Pass -DX and/or -DY, the distance to drag in screen pixels.' }
        $e = Get-Target; $before = Format-Element $e 0; Send-Drag $e $DX $DY; "dragged by $DX,$DY (real mouse): $before"
    }
    'wheel' {
        if ($Delta -eq 0) { throw 'Pass -Delta, the wheel notches (positive up/left, negative down/right).' }
        $e = Get-Target; $before = Format-Element $e 0; Send-Wheel $e $Delta $Horizontal.IsPresent; "wheel $Delta (real mouse): $before"
    }
    'move' {
        if (-not ($PSBoundParameters.ContainsKey('X') -and $PSBoundParameters.ContainsKey('Y'))) { throw 'Pass -X and -Y in screen pixels.' }
        $hwnd = Get-MainHandle
        [QaNative.Win32]::ShowWindow($hwnd, 9) | Out-Null
        # SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
        Set-WindowPos $hwnd $X $Y 0 0 (0x0001 -bor 0x0004 -bor 0x0010)
        Start-Sleep -Milliseconds 300
        "moved: $(Format-Element (Get-Window) 0)"
    }
    'resize' {
        if ($Width -le 0 -or $Height -le 0) { throw 'Pass -Width and -Height in screen pixels.' }
        $hwnd = Get-MainHandle
        [QaNative.Win32]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE: a maximized window keeps its size
        # SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE
        Set-WindowPos $hwnd 0 0 $Width $Height (0x0002 -bor 0x0004 -bor 0x0010)
        Start-Sleep -Milliseconds 300
        "resized: $(Format-Element (Get-Window) 0)"
    }
    'keys' {
        if (-not $Keys) { throw 'Pass -Keys in SendKeys syntax: {ENTER}, {ESC}, ^k, +l, l.' }
        Show-Window
        if ($Id -or $Name -or $Match) {
            $e = Get-Target
            # With -Dialog the name box is a Pane with the Edit inside it (#370): focus the Edit.
            if ($Dialog) { $e = (Find-PatternInside $e ([System.Windows.Automation.ValuePattern]::Pattern) 'Edit') ?? $e }
            $e.SetFocus()
        }
        [System.Windows.Forms.SendKeys]::SendWait($Keys)
        "sent (real keyboard): $Keys"
    }
    'wait' {
        $deadline = (Get-Date).AddSeconds($Timeout)
        while ((Get-Date) -lt $deadline) {
            $hit = @(Get-Matches) | Select-Object -First 1
            if ($hit) { "appeared: $(Format-Element $hit 0)"; return }
            Start-Sleep -Milliseconds 300
        }
        throw "Timed out after $Timeout s waiting for a match."
    }
    'wait-gone' {
        $deadline = (Get-Date).AddSeconds($Timeout)
        while ((Get-Date) -lt $deadline) {
            if (-not (@(Get-Matches) | Select-Object -First 1)) { 'gone'; return }
            Start-Sleep -Milliseconds 300
        }
        throw "Timed out after $Timeout s; still present."
    }
    'screenshot' {
        if (-not $Out) { throw 'Pass -Out <file.png>.' }
        if (-not $WholeWindow -and -not ($Id -or $Name -or $Match)) {
            throw 'Name the element to capture (-Id, -Name or -Match), or pass -WholeWindow.'
        }
        $target = if ($WholeWindow) { $null } else { Get-Target }
        Show-Window
        $r = if ($target) { $target.Current.BoundingRectangle } else { (Get-Window).Current.BoundingRectangle }
        if ($r.IsEmpty -or $r.Width -lt 1 -or $r.Height -lt 1) { throw 'The element has no bounds on screen.' }
        $bitmap = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bitmap.Size)
        New-Item -ItemType Directory -Force (Split-Path $Out -Parent) | Out-Null
        $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bitmap.Dispose()
        "saved $Out"
    }
}
