# Снимок рабочего стола вокруг питомца: показывает, что герой стоит у нижнего края окна Harness.
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Winfo {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public static IntPtr Find(uint pid) { IntPtr f = IntPtr.Zero; EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { f = h; return false; } return true; }, IntPtr.Zero); return f; }
}
'@
$proc = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'assets\\pet\\pet\.ps1' } | Select-Object -First 1
if (-not $proc) { 'pet is not running'; exit 1 }
$h = [Winfo]::Find([uint32]$proc.ProcessId)
if ($h -eq [IntPtr]::Zero) { "pet $($proc.ProcessId) has no visible window"; exit 1 }
$r = New-Object Winfo+RECT; [void][Winfo]::GetWindowRect($h, [ref]$r)
$left = [Math]::Max(0, $r.L - 700)
$top = [Math]::Max(0, $r.T - 260)
$width = [Math]::Min(([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width - $left), ($r.R - $left) + 40)
$height = [Math]::Min(([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height - $top), ($r.B - $top) + 40)
Add-Type -AssemblyName System.Windows.Forms
$bmp = New-Object System.Drawing.Bitmap $width, $height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size $width, $height))
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'preview\desktop-pet.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"desktop snapshot: $out ($width x $height, pet at $($r.L),$($r.T))"
