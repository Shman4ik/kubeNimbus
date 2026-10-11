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
    - `invoke`, `select`, `toggle`, `set-text` use UIA patterns. They do not move the
      mouse or need focus, so they are safe while someone is using the machine.
    - `click`, `double-click`, `right-click` and `keys` are REAL OS input: the window is
      brought to the front and the pointer and keyboard really move. Use them only when a
      check is about real input (a DataGrid double-click, a context menu, a key gesture),
      which is exactly what the pattern actions cannot prove.

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
    would take.

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

.EXAMPLE
    ./scripts/qa-ui.ps1 dump -Match crashloop
    ./scripts/qa-ui.ps1 invoke -Id ResourcesModeItem
    ./scripts/qa-ui.ps1 set-text -Id FilterBox -Text payments
    ./scripts/qa-ui.ps1 wait -Match "Crash-looping" -Timeout 20
    ./scripts/qa-ui.ps1 double-click -Match "crashloop-" -Type DataItem
    ./scripts/qa-ui.ps1 keys -Keys "{ESC}"
    ./scripts/qa-ui.ps1 resize -Width 960 -Height 720
    ./scripts/qa-ui.ps1 screenshot -Id RowFilterBox -Out $env:TEMP/qa/step1.png
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [ValidateSet('dump', 'find', 'invoke', 'select', 'toggle', 'set-text', 'click', 'double-click',
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
    [switch]$WholeWindow
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
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
// POINT is passed by value as one 64-bit register on x64: y in the high half, x in the low.
[DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(long point);
[DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
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
    $window = Invoke-UiaRead { $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond) }
    if (-not $window) { throw "kubeNimbus (pid $appPid) has no window. Is it still running?" }
    $window
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
    try {
        $sel = $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($sel.Current.IsSelected) { $flags += 'selected' }
    } catch { }
    try {
        $tog = $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $flags += "toggle=$($tog.Current.ToggleState)"
    } catch { }
    $id = if ($c.AutomationId) { " [$($c.AutomationId)]" } else { '' }
    if ($c.HelpText) { $id += " help='$($c.HelpText)'" }
    $f = if ($flags) { " ($($flags -join ', '))" } else { '' }
    $bounds = if ($r.IsEmpty) { '' } else { " @{0:0},{1:0} {2:0}x{3:0}" -f $r.X, $r.Y, $r.Width, $r.Height }
    "#{0} {1} '{2}'{3}{4}{5}" -f $number, $kind, (Format-Name $c.Name), $id, $f, $bounds
}

function Get-Matches {
    $window = Get-Window
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

# Whether a real click at a screen point would land on one of this app's own windows.
# A popup (the log pane's ⋯ menu, a combo's drop-down, a context menu) is a native window
# of its own that can hang past the main window's edge, but UI Automation gives it no node
# of its own: its items are children of the main window (found out in the 0.7.0 QA run,
# where the earlier walk up to "the popup's HWND" never found one and refused a menu item
# that was plainly on screen). So the main window's bounds cannot judge them, and Windows
# is asked who is at the point instead. A point over the desktop or another application's
# window is refused, which also catches an app that something else has covered.
function Test-LandsOnApp([double]$x, [double]$y) {
    $low = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$x), 0)
    $point = ([long][int]$y -shl 32) -bor [long]$low
    $hwnd = [QaNative.Win32]::WindowFromPoint($point)
    if ($hwnd -eq [IntPtr]::Zero) { return $false }
    $top = [QaNative.Win32]::GetAncestor($hwnd, 2)   # GA_ROOT
    if ($top -eq [IntPtr]::Zero) { $top = $hwnd }
    $owner = [uint32]0
    [QaNative.Win32]::GetWindowThreadProcessId($top, [ref]$owner) | Out-Null
    $owner -eq $appPid
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
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $node = $walker.GetParent($element)
    while ($node -and -not [System.Windows.Automation.Automation]::Compare($node, $window)) {
        $scrolls = $false
        try { $null = $node.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern); $scrolls = $true } catch { }
        if ($scrolls) {
            $v = $node.Current.BoundingRectangle
            if (-not $v.IsEmpty -and ($x -lt $v.Left -or $x -gt $v.Right -or $y -lt $v.Top -or $y -gt $v.Bottom)) { return $false }
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

function Show-Window {
    Assert-InputDesktop
    $hwnd = [IntPtr](Get-Process -Id $appPid).MainWindowHandle
    [QaNative.Win32]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
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
            switch ($pattern.GetType().Name) {
                'InvokePattern' { $pattern.Invoke() }
                'SelectionItemPattern' { $pattern.Select() }
                'TogglePattern' { $pattern.Toggle() }
                'ExpandCollapsePattern' { $pattern.Expand() }
            }
            "invoked ($($pattern.GetType().Name)): $(Format-Element $e 0)"
            return
        }
        throw "No invoke/select/toggle/expand pattern on: $(Format-Element $e 0). Use click for real input."
    }
    'select' {
        $e = Get-Target
        $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        "selected: $(Format-Element $e 0)"
    }
    'toggle' {
        $e = Get-Target
        $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        "toggled: $(Format-Element $e 0)"
    }
    'set-text' {
        $e = Get-Target
        $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
        "text set: $(Format-Element $e 0)"
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
        $hwnd = [IntPtr](Get-Process -Id $appPid).MainWindowHandle
        [QaNative.Win32]::ShowWindow($hwnd, 9) | Out-Null
        # SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
        if (-not [QaNative.Win32]::SetWindowPos($hwnd, [IntPtr]::Zero, $X, $Y, 0, 0, 0x0001 -bor 0x0004 -bor 0x0010)) { throw 'SetWindowPos failed.' }
        Start-Sleep -Milliseconds 300
        "moved: $(Format-Element (Get-Window) 0)"
    }
    'resize' {
        if ($Width -le 0 -or $Height -le 0) { throw 'Pass -Width and -Height in screen pixels.' }
        $hwnd = [IntPtr](Get-Process -Id $appPid).MainWindowHandle
        [QaNative.Win32]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE: a maximized window keeps its size
        # SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE
        if (-not [QaNative.Win32]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, $Width, $Height, 0x0002 -bor 0x0004 -bor 0x0010)) {
            throw 'SetWindowPos failed.'
        }
        Start-Sleep -Milliseconds 300
        "resized: $(Format-Element (Get-Window) 0)"
    }
    'keys' {
        if (-not $Keys) { throw 'Pass -Keys in SendKeys syntax: {ENTER}, {ESC}, ^k, +l, l.' }
        Show-Window
        if ($Id -or $Name -or $Match) { (Get-Target).SetFocus() }
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
