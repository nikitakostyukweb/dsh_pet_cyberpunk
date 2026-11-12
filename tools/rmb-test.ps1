# Right-click smoke test: hits the pet only (WindowFromPoint probe), opens the menu,
# screenshots the area around the pet, checks for external PowerShell/dialog spawns and crash log.
# ASCII only (PowerShell 5.1 reads BOM-less .ps1 as ANSI).
param([switch]$Close)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Rmb {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint data, UIntPtr extra);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public static IntPtr Find(uint pid) { IntPtr f = IntPtr.Zero; EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { f = h; return false; } return true; }, IntPtr.Zero); return f; }
}
'@
$root = Split-Path $PSScriptRoot -Parent
$proc = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'assets\\pet\\pet\.ps1' } | Select-Object -First 1
if (-not $proc) { 'FAIL: pet is not running'; exit 1 }
$h = [Rmb]::Find([uint32]$proc.ProcessId)
if ($h -eq [IntPtr]::Zero) { 'FAIL: pet has no visible window'; exit 1 }
$r = New-Object Rmb+RECT; [void][Rmb]::GetWindowRect($h, [ref]$r)
$x = [int](($r.L + $r.R) / 2)
$y = $r.B - 120
$old = New-Object Rmb+POINT; [void][Rmb]::GetCursorPos([ref]$old)
[void][Rmb]::SetCursorPos($x, $y)
Start-Sleep -Milliseconds 150
$pt = New-Object Rmb+POINT; $pt.X = $x; $pt.Y = $y
$under = [Rmb]::WindowFromPoint($pt)
"point=$x,$y under_hwnd=$under pet_hwnd=$h hit=$(($under -eq $h))"
$psBefore = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'").Count
# 0x0008 = MOUSEEVENTF_RIGHTDOWN, 0x0010 = RIGHTUP
[Rmb]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 80
[Rmb]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 1500
$psAfter = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'").Count
"powershell procs before=$psBefore after=$psAfter"
$alive = $null -ne (Get-Process -Id $proc.ProcessId -ErrorAction SilentlyContinue)
"pet alive after RMB=$alive"
$crash = Join-Path $root 'assets\pet\pet-crash.log'
if (Test-Path $crash) { "FAIL: crash log present"; Get-Content $crash -Tail 5 } else { 'crash log: none' }
# Screenshot around the pet (captures the topmost window as the user sees it).
$left = [Math]::Max(0, $r.L - 480)
$top = [Math]::Max(0, $r.T - 40)
$w = [Math]::Min(($r.R - $left), [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width - $left)
$hh = [Math]::Min(($r.B - $top), [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height - $top)
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size $w, $hh))
$out = Join-Path $root 'preview\pet-menu-live.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"screenshot: $out ($w x $hh)"
if ($Close) {
  [void][Rmb]::SetCursorPos($x, $y)
  Start-Sleep -Milliseconds 120
  [Rmb]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 80
  [Rmb]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 800
  "menu closed by second RMB"
}
[void][Rmb]::SetCursorPos($old.X, $old.Y)
