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

    `screenshot` captures one element, cropped to its bounds, so a check reads the region
    it is about and not a whole window. -WholeWindow captures the app's window instead;
    it never captures the desktop.

    Element selection (every command that targets one element):
      -Id <AutomationId>   exact x:Name / AutomationId
      -Name <text>         exact accessible name
      -Match <text>        name contains text (case-insensitive)
      -Type <ControlType>  Button, Text, ListItem, Edit, DataItem, ... (narrows the above)
      -Index <n>           the n-th match (0-based), default 0

.EXAMPLE
    ./scripts/qa-ui.ps1 dump -Match crashloop
    ./scripts/qa-ui.ps1 invoke -Id ResourcesModeItem
    ./scripts/qa-ui.ps1 set-text -Id FilterBox -Text payments
    ./scripts/qa-ui.ps1 wait -Match "Crash-looping" -Timeout 20
    ./scripts/qa-ui.ps1 double-click -Match "crashloop-" -Type DataItem
    ./scripts/qa-ui.ps1 keys -Keys "{ESC}"
    ./scripts/qa-ui.ps1 screenshot -Id RowFilterBox -Out $env:TEMP/qa/step1.png
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [ValidateSet('dump', 'find', 'invoke', 'select', 'toggle', 'set-text', 'click', 'double-click',
        'right-click', 'keys', 'wait', 'wait-gone', 'screenshot', 'window')]
    [string]$Command,
    [string]$Id,
    [string]$Name,
    [string]$Match,
    [string]$Type,
    [int]$Index = 0,
    [string]$Text,
    [string]$Keys,
    [string]$Out,
    [int]$Timeout = 10,
    [int]$Max = 400,
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
'@

$A = [System.Windows.Automation.AutomationElement]
$pidFile = Join-Path ([IO.Path]::GetTempPath()) 'kubenimbus-qa/app.pid'
if (-not (Test-Path $pidFile)) { throw 'No QA instance. Start one: ./scripts/qa-app.ps1' }
$appPid = [int](Get-Content $pidFile -Raw | ConvertFrom-Json).Pid

function Get-Window {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $appPid)
    $window = $A::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
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
    $all = (Get-Window).FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
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

# An element scrolled out of its list is in the tree but has no usable bounds and cannot
# be clicked. Scroll it into view first when its container supports that. (Items inside a
# collapsed sidebar section are not realized at all — use a filter box to reach those.)
function Test-InWindow($element) {
    $r = $element.Current.BoundingRectangle
    if ($r.IsEmpty) { return $false }
    $x = $r.X + $r.Width / 2; $y = $r.Y + $r.Height / 2
    $window = Get-Window
    $w = $window.Current.BoundingRectangle
    if ($x -lt $w.Left -or $x -gt $w.Right -or $y -lt $w.Top -or $y -gt $w.Bottom) { return $false }

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

function Show-Window {
    $hwnd = [IntPtr](Get-Process -Id $appPid).MainWindowHandle
    [QaNative.Win32]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
    [QaNative.Win32]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 200
}

function Send-Click($element, [int]$count, [bool]$right) {
    Show-Window
    $r = $element.Current.BoundingRectangle
    if (-not (Test-InWindow $element)) {
        throw "The element is outside the window ($($r.X),$($r.Y)) — a real click would land elsewhere. Filter the list to bring it into view."
    }
    [QaNative.Win32]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
    $down, $up = if ($right) { 0x0008, 0x0010 } else { 0x0002, 0x0004 }
    for ($i = 0; $i -lt $count; $i++) {
        [QaNative.Win32]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero)
        [QaNative.Win32]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
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
    'click' { $e = Get-Target; Send-Click $e 1 $false; "clicked (real mouse): $(Format-Element $e 0)" }
    'double-click' { $e = Get-Target; Send-Click $e 2 $false; "double-clicked (real mouse): $(Format-Element $e 0)" }
    'right-click' { $e = Get-Target; Send-Click $e 1 $true; "right-clicked (real mouse): $(Format-Element $e 0)" }
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
