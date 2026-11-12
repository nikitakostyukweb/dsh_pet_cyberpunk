# Live check: pet process has TWO visible windows (pet + SYS.MON panel), plus a screen capture around both.
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class W7 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public static List<string> List(uint pid) { var res = new List<string>(); EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h,out p); if (p==pid && IsWindowVisible(h)) { RECT r; GetWindowRect(h,out r); int ex=GetWindowLong(h,-20); res.Add(h+"|"+(r.R-r.L)+"x"+(r.B-r.T)+"|"+r.L+","+r.T+"|0x"+ex.ToString("X")); } return true; }, IntPtr.Zero); return res; }
}
'@
$root = Split-Path $PSScriptRoot -Parent
$proc = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'assets\\pet\\pet\.ps1' } | Select-Object -First 1
if (-not $proc) { 'FAIL: pet is not running'; exit 1 }
$wins = [W7]::List([uint32]$proc.ProcessId)
"pid=$($proc.ProcessId) visible windows=$($wins.Count)"
$wins | ForEach-Object { '  ' + $_ }
$rects = @()
foreach ($w in $wins) { $parts = $w.Split('|'); $size = $parts[1]; $pos = $parts[2].Split(','); $wh = $size.Split('x'); $rects += [pscustomobject]@{ L=[int]$pos[0]; T=[int]$pos[1]; W=[int]$wh[0]; H=[int]$wh[1] } }
$left = [Math]::Max(0, ($rects | Measure-Object L -Minimum).Minimum - 60)
$top = [Math]::Max(0, ($rects | Measure-Object T -Minimum).Minimum - 60)
$right = [Math]::Min([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, (($rects | ForEach-Object { $_.L + $_.W } | Measure-Object -Maximum).Maximum) + 60)
$bottom = [Math]::Min([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height, (($rects | ForEach-Object { $_.T + $_.H } | Measure-Object -Maximum).Maximum) + 60)
$w = $right - $left; $h = $bottom - $top
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size $w, $h))
$out = Join-Path $root 'preview\monitor-separate-live.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"saved: $out ($w x $h)"
# Settings + state snapshot (ASCII-safe output)
$s = Get-Content (Join-Path $root 'assets\pet\settings.json') -Raw | ConvertFrom-Json
"settings: posX=$($s.posX) posY=$($s.posY) mini=$($s.mini) monitor=$($s.monitor) monX=$($s.monX) monY=$($s.monY)"
$st = Get-Content (Join-Path $root 'assets\pet\state.json') -Raw | ConvertFrom-Json
$age = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - [int64]$st.alive
"state: build=$($st.build) chats=$($st.chats)/$($st.busyChats) aliveAge=${age}ms"
