# Read-only oracle for the selection colors in docs/parity/oracle/regedit-selection-colors.json.
#
# Launches classic C:\Windows\regedit.exe with __COMPAT_LAYER=RunAsInvoker on the child only, holds
# Local\BwcGuiTestLock, navigates with the keyboard to HKEY_CURRENT_USER\AppEvents\Console, and
# captures the main window with PrintWindow. It samples the tree and list selection colors from the
# captured pixels, saves three PNG captures, and composes each one side by side with the matching
# BWC capture written by ThemeTests (artifacts/e2e/visual/theme/main-light-light-*.png).
# No clicks on items, no edits, no registry writes. The window is closed with Alt+F4.
#
# Usage: pwsh -NoProfile -File tests/Bwc.E2E/Theme/classic-selection-oracle.ps1

param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\..\artifacts\e2e\visual\theme')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class OracleWin32 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr c, string cls, string t);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
public static class OraclePixels {
  // Pixels are 0xAARRGGBB ints, row-major, width w. Rectangle is x0,y0 inclusive to x1,y1 exclusive.
  public static int[] Hist(int[] px, int w, int x0, int y0, int x1, int y1) {
    var h = new Dictionary<int, int>();
    for (int y = y0; y < y1; y++)
      for (int x = x0; x < x1; x++) {
        int c = px[y * w + x] & 0xFFFFFF;
        int n; h.TryGetValue(c, out n); h[c] = n + 1;
      }
    var outp = new int[h.Count * 2];
    int i = 0;
    foreach (var kv in h) { outp[i++] = kv.Key; outp[i++] = kv.Value; }
    return outp;
  }
  // Bounding box of pixels equal to rgb. Returns x0,y0,x1,y1 (x1,y1 exclusive); x1 < 0 when empty.
  public static int[] Bounds(int[] px, int w, int x0, int y0, int x1, int y1, int rgb) {
    int l = int.MaxValue, t = int.MaxValue, rr = -1, bb = -1;
    for (int y = y0; y < y1; y++)
      for (int x = x0; x < x1; x++) {
        if ((px[y * w + x] & 0xFFFFFF) == rgb) { if (x < l) l = x; if (x > rr) rr = x; if (y < t) t = y; if (y > bb) bb = y; }
      }
    if (rr < 0) return new int[] { 0, 0, -1, -1 };
    return new int[] { l, t, rr + 1, bb + 1 };
  }
  public static int Darkest(int[] px, int w, int x0, int y0, int x1, int y1) {
    int best = 0, bv = int.MaxValue;
    for (int y = y0; y < y1; y++)
      for (int x = x0; x < x1; x++) {
        int c = px[y * w + x] & 0xFFFFFF;
        int v = (c >> 16) + ((c >> 8) & 255) + (c & 255);
        if (v < bv) { bv = v; best = c; }
      }
    return best;
  }
}
"@

[void][OracleWin32]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Hx([int]$rgb) { return ('#{0:X6}' -f $rgb) }

function Get-RegEditWindow {
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        foreach ($p in (Get-Process regedit -ErrorAction SilentlyContinue)) {
            if ($p.MainWindowHandle -ne [IntPtr]::Zero) { return $p.MainWindowHandle }
        }
        Start-Sleep -Milliseconds 200
    }
    throw 'regedit window not found'
}

function Get-WinRect([IntPtr]$hwnd) {
    $r = New-Object OracleWin32+RECT
    [void][OracleWin32]::GetWindowRect($hwnd, [ref]$r)
    return [System.Drawing.Rectangle]::FromLTRB($r.L, $r.T, $r.R, $r.B)
}

function Get-ChildRect([IntPtr]$parent, [string]$cls) {
    $h = [OracleWin32]::FindWindowEx($parent, [IntPtr]::Zero, $cls, $null)
    if ($h -eq [IntPtr]::Zero) { throw "child $cls not found" }
    return Get-WinRect $h
}

function Grab-Window([IntPtr]$hwnd) {
    $wr = Get-WinRect $hwnd
    $bmp = New-Object System.Drawing.Bitmap($wr.Width, $wr.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][OracleWin32]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $rect = [System.Drawing.Rectangle]::new(0, 0, $bmp.Width, $bmp.Height)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $px = New-Object int[] ($bmp.Width * $bmp.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $px, 0, $px.Length)
    $bmp.UnlockBits($data)
    return [pscustomobject]@{ Bmp = $bmp; Px = $px; W = $bmp.Width; O = $wr.Location }
}

function Top-Colors($g, [System.Drawing.Rectangle]$screen, [int]$min = 30, [int]$take = 6) {
    $hist = [OraclePixels]::Hist($g.Px, $g.W, $screen.Left - $g.O.X, $screen.Top - $g.O.Y, $screen.Right - $g.O.X, $screen.Bottom - $g.O.Y)
    $rows = for ($i = 0; $i -lt $hist.Length; $i += 2) { [pscustomobject]@{ c = $hist[$i]; n = $hist[$i + 1] } }
    return @($rows | Where-Object { $_.n -ge $min } | Sort-Object n -Descending | Select-Object -First $take | ForEach-Object { "$(Hx $_.c):$($_.n)" })
}

function Box-Of($g, [System.Drawing.Rectangle]$pane, [int]$rgb) {
    $b = [OraclePixels]::Bounds($g.Px, $g.W, $pane.Left - $g.O.X, $pane.Top - $g.O.Y, $pane.Right - $g.O.X, $pane.Bottom - $g.O.Y, $rgb)
    if ($b[2] -lt 0) { return $null }
    return [System.Drawing.Rectangle]::FromLTRB($b[0] + $g.O.X, $b[1] + $g.O.Y, $b[2] + $g.O.X, $b[3] + $g.O.Y)
}

function Edge-Colors($g, [System.Drawing.Rectangle]$box) {
    $bx = $box.Left - $g.O.X; $by = $box.Top - $g.O.Y; $ex = $box.Right - $g.O.X; $ey = $box.Bottom - $g.O.Y
    $cx = [int](($bx + $ex) / 2); $cy = [int](($by + $ey) / 2)
    $px = $g.Px; $w = $g.W
    return [ordered]@{
        outerTop = Hx ([OraclePixels]::Darkest($px, $w, $cx, $by - 1, $cx + 1, $by))
        innerTop = Hx ([OraclePixels]::Darkest($px, $w, $cx, $by, $cx + 1, $by + 1))
        outerLeft = Hx ([OraclePixels]::Darkest($px, $w, $bx - 1, $cy, $bx, $cy + 1))
        outerBottom = Hx ([OraclePixels]::Darkest($px, $w, $cx, $ey, $cx + 1, $ey + 1))
        outerRight = Hx ([OraclePixels]::Darkest($px, $w, $ex, $cy, $ex + 1, $cy + 1))
    }
}

function Fmt($r) { if ($r) { "$($r.Left),$($r.Top),$($r.Right),$($r.Bottom)" } else { 'none' } }

# Keystrokes go to the foreground window, so they are sent only after classic regedit is confirmed foreground.
# Another application covering it would otherwise receive them, including Alt+F4.
function Assert-Foreground($h) {
    for ($i = 0; $i -lt 6 -and [OracleWin32]::GetForegroundWindow().ToInt64() -ne $h.ToInt64(); $i++) {
        [void][OracleWin32]::SetForegroundWindow($h); Start-Sleep -Milliseconds 200
    }
    if ([OracleWin32]::GetForegroundWindow().ToInt64() -ne $h.ToInt64()) {
        throw 'classic regedit is not the foreground window; no keys were sent'
    }
}

$CE8 = 0xCCE8FF
$BLUE = 0x0078D4
$out = [ordered]@{ generator = 'tests/Bwc.E2E/Theme/classic-selection-oracle.ps1'; read_only = $true }
$hwnd = [IntPtr]::Zero
$mutex = [System.Threading.Mutex]::new($false, 'Local\BwcGuiTestLock')
if (-not $mutex.WaitOne([TimeSpan]::FromMinutes(15))) { throw 'Timed out waiting for Local\BwcGuiTestLock' }
try {
    if (Get-Process regedit -ErrorAction SilentlyContinue) {
        $older = Get-Process regedit | Where-Object { ((Get-Date) - $_.StartTime).TotalMinutes -gt 20 }
        if ($older) { throw 'an older regedit is running; not touching it' }
    }
    if (-not (Get-Process regedit -ErrorAction SilentlyContinue)) {
        $env:__COMPAT_LAYER = 'RunAsInvoker'
        $null = Start-Process -FilePath 'C:\Windows\regedit.exe' -PassThru
        Remove-Item Env:\__COMPAT_LAYER
    }
    $hwnd = Get-RegEditWindow
    # Kept above other applications until the finally block clears it (flags NOMOVE|NOSIZE|NOACTIVATE).
    [void][OracleWin32]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0013)
    Start-Sleep -Milliseconds 1500
    [void][OracleWin32]::SetForegroundWindow($hwnd); Start-Sleep -Milliseconds 400
    $tree = Get-ChildRect $hwnd 'SysTreeView32'
    $list = Get-ChildRect $hwnd 'SysListView32'

    # Navigate: Computer -> HKEY_CURRENT_USER (expand) -> AppEvents -> Console.
    Assert-Foreground $hwnd
    foreach ($k in '{HOME}', '{DOWN}', '{DOWN}', '{RIGHT}', '{DOWN}', '{DOWN}') {
        [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 180
    }
    Start-Sleep -Milliseconds 400
    [void][OracleWin32]::SetCursorPos(5, 1500); Start-Sleep -Milliseconds 500

    # Tree focused, Console selected.
    $focus = Grab-Window $hwnd
    $consoleBox = Box-Of $focus $tree $CE8
    if (-not $consoleBox) { throw 'focused tree selection (#CCE8FF) not found' }
    $out.treeFocused = [ordered]@{ box = (Fmt $consoleBox); fill = Hx $CE8; border = Hx $BLUE; edges = (Edge-Colors $focus $consoleBox) }
    $focus.Bmp.Save((Join-Path $OutDir 'classic-main-tree-focus.png'), [System.Drawing.Imaging.ImageFormat]::Png)

    # Tab to the list and select the first value.
    Assert-Foreground $hwnd
    [System.Windows.Forms.SendKeys]::SendWait('{TAB}'); Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('{DOWN}'); Start-Sleep -Milliseconds 500
    $listFocus = Grab-Window $hwnd
    $listBox = Box-Of $listFocus $list $BLUE
    if (-not $listBox) { throw 'focused list selection (#0078D4) not found' }
    $out.listFocused = [ordered]@{ box = (Fmt $listBox); fill = Hx $BLUE; text = (Hx (0xFFFFFF)); insideTopColors = (Top-Colors $listFocus ([System.Drawing.Rectangle]::FromLTRB($listBox.Left + 2, $listBox.Top + 2, $listBox.Right - 2, $listBox.Bottom - 2)) 30 4); edges = (Edge-Colors $listFocus $listBox) }
    $out.treeUnfocused = [ordered]@{ fill = Hx 0xD9D9D9; border = Hx 0x949494; topColors = (Top-Colors $listFocus $consoleBox 20 4) }
    $listFocus.Bmp.Save((Join-Path $OutDir 'classic-main-list-focus.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $listFocus.Bmp.Dispose()

    # Tab back to the tree. The list keeps its selection but draws none.
    for ($i = 0; $i -lt 3; $i++) {
        Assert-Foreground $hwnd
        [System.Windows.Forms.SendKeys]::SendWait('{TAB}'); Start-Sleep -Milliseconds 350
        if (Box-Of (Grab-Window $hwnd) $tree $CE8) { break }
    }
    $refocus = Grab-Window $hwnd
    $out.listUnfocused = [ordered]@{ box = (Fmt $listBox); topColors = (Top-Colors $refocus $listBox 20 4) }
    $out.treeRefocused = [ordered]@{ box = (Fmt (Box-Of $refocus $tree $CE8)); fill = Hx $CE8 }
    $refocus.Bmp.Save((Join-Path $OutDir 'classic-main-tree-refocus.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $refocus.Bmp.Dispose()
    $focus.Bmp.Dispose()

    # Hover: the tree draws no hot highlight. Move over an unselected row and count changed pixels.
    $pitch = ($consoleBox.Bottom - $consoleBox.Top) + 1
    $hoverRect = [System.Drawing.Rectangle]::FromLTRB($consoleBox.Left - 2, $consoleBox.Bottom, $consoleBox.Right + 60, $consoleBox.Bottom + 2 * $pitch)
    $before = Grab-Window $hwnd
    [void][OracleWin32]::SetCursorPos($consoleBox.Left + 60, $consoleBox.Top + $pitch + [int]($pitch / 2)); Start-Sleep -Milliseconds 1000
    $hover = Grab-Window $hwnd
    $changed = 0
    for ($yy = $hoverRect.Top; $yy -lt $hoverRect.Bottom; $yy++) {
        for ($xx = $hoverRect.Left; $xx -lt $hoverRect.Right; $xx++) {
            $ih = ($yy - $hover.O.Y) * $hover.W + ($xx - $hover.O.X)
            $ib = ($yy - $before.O.Y) * $before.W + ($xx - $before.O.X)
            if (($hover.Px[$ih] -band 0xFFFFFF) -ne ($before.Px[$ib] -band 0xFFFFFF)) { $changed++ }
        }
    }
    $out.treeHover = [ordered]@{ changedPixelsInRowBelowSelection = $changed; hotHighlightDrawn = ($changed -gt 0) }
    $before.Bmp.Dispose(); $hover.Bmp.Dispose()
    [void][OracleWin32]::SetCursorPos(5, 1500)
}
finally {
    if ($hwnd -ne [IntPtr]::Zero) {
        try {
            [void][OracleWin32]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0013)
            # Alt+F4 is sent only to the confirmed foreground window, so it cannot close another application.
            Assert-Foreground $hwnd
            [System.Windows.Forms.SendKeys]::SendWait('%{F4}'); Start-Sleep -Milliseconds 800
        } catch { Write-Host "close failed: $($_.Exception.Message)" }
    }
    $mutex.ReleaseMutex(); $mutex.Dispose()
}

# Side by side: classic on the left, BWC (ThemeTests, light case) on the right, for each state.
$pairs = @(
    @{ classic = 'classic-main-tree-focus.png'; bwc = 'main-light-light-tree-focus.png'; name = 'compare-tree-focus.png' },
    @{ classic = 'classic-main-list-focus.png'; bwc = 'main-light-light-list-focus.png'; name = 'compare-list-focus.png' },
    @{ classic = 'classic-main-tree-refocus.png'; bwc = 'main-light-light-tree-refocus.png'; name = 'compare-tree-refocus.png' }
)
foreach ($pair in $pairs) {
    $leftPath = Join-Path $OutDir $pair.classic
    $rightPath = Join-Path $OutDir $pair.bwc
    if (-not ((Test-Path $leftPath) -and (Test-Path $rightPath))) { Write-Host "skip $($pair.name): missing capture"; continue }
    $left = [System.Drawing.Image]::FromFile($leftPath)
    $right = [System.Drawing.Image]::FromFile($rightPath)
    $gap = 40
    $canvas = New-Object System.Drawing.Bitmap(($left.Width + $gap + $right.Width), [Math]::Max($left.Height, $right.Height))
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 128, 128, 128))
    $g.DrawImage($left, 0, 0, $left.Width, $left.Height)
    $g.DrawImage($right, $left.Width + $gap, 0, $right.Width, $right.Height)
    $g.Dispose()
    $canvas.Save((Join-Path $OutDir $pair.name), [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose(); $left.Dispose(); $right.Dispose()
}

$samplesPath = Join-Path $OutDir 'classic-samples.json'
$out | ConvertTo-Json -Depth 8 | Set-Content -Path $samplesPath -Encoding UTF8
"written: $samplesPath"
