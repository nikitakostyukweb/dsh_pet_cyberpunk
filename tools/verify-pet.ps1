# Round 2 checks: pet in screen corner, TOPMOST, heartbeat, live state (ASCII only for PS 5.1).
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W2 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public static IntPtr Find(uint pid) { IntPtr f = IntPtr.Zero; EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { f = h; return false; } return true; }, IntPtr.Zero); return f; }
}
'@
$root = Split-Path $PSScriptRoot -Parent
$proc = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'assets\\pet\\pet\.ps1' } | Select-Object -First 1
if (-not $proc) { 'FAIL: pet is not running'; exit 1 }
$h = [W2]::Find([uint32]$proc.ProcessId)
if ($h -eq [IntPtr]::Zero) { 'FAIL: pet has no visible window'; exit 1 }
$r = New-Object W2+RECT; [void][W2]::GetWindowRect($h, [ref]$r)
$ex = [W2]::GetWindowLong($h, -20)
$scr = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$topmost = ($ex -band 0x8) -ne 0
"pet pid=$($proc.ProcessId) hwnd=$h rect=$($r.L),$($r.T)-$($r.R),$($r.B)"
"screen workarea=$($scr.Left),$($scr.Top)-$($scr.Right),$($scr.Bottom)"
"topmost=$topmost exstyle=0x$('{0:X}' -f $ex)"
"window right edge past screen by $([Math]::Max(0, $r.R - $scr.Right)) px (transparent canvas part)"
$feetOk = [Math]::Abs(($r.B) - ($scr.Bottom - 32)) -le 10
"feet at screen bottom - 32 = $feetOk"
$beatPath = Join-Path $root 'assets\pet\pet.beat'
if (Test-Path $beatPath) {
  $beat = Get-Content $beatPath -Raw | ConvertFrom-Json
  $age = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - [int64]$beat.at
  "beat pid=$($beat.pid) age=${age}ms"
} else { 'beat: no file' }
$statePath = Join-Path $root 'assets\pet\state.json'
$s = Get-Content $statePath -Raw | ConvertFrom-Json
$age2 = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - [int64]$s.alive
"state build=$($s.build) busy=$($s.busy) session=$($s.session) aliveAge=${age2}ms"
