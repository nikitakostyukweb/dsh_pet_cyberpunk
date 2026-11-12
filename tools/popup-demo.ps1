# Capture what a real incoming-call card looks like for each -Kind (call/question/approval).
# ASCII only (PS 5.1 reads BOM-less .ps1 as ANSI).
param([string]$Kind = 'question', [int]$Seconds = 4, [string]$Out = 'preview\popup-question.png')
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Cap {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public static IntPtr Find(uint pid) { IntPtr f = IntPtr.Zero; EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { f = h; return false; } return true; }, IntPtr.Zero); return f; }
}
'@
$root = Split-Path $PSScriptRoot -Parent
$hero = 'talia'
$portrait = Join-Path $root 'assets\pet\talia\idle_01.png'
$call = Join-Path $root 'assets\popup\call.ps1'
$outPath = Join-Path $root $Out
$startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
$text = switch ($Kind) {
  'question' { 'Какой ракурс выбрать?' }
  'approval' { 'Запускать опасную команду?' }
  default { 'Готово: сборка починена.' }
}
$caller = switch ($Kind) { 'approval' { 'Уик' } default { 'Талия' } }
$heroImg = switch ($Kind) { 'approval' { 'wick' } default { 'talia' } }
$portrait = Join-Path $root ("assets\pet\" + $heroImg + "\idle_01.png")
$cmd = 'cmd.exe /c powershell -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $call + '" -Caller "' + $caller + '" -Text "' + $text + '" -Portrait "' + $portrait + '" -Seconds ' + $Seconds + ' -Kind ' + $Kind
$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd; ProcessStartupInformation = $startup }
if ($r.ReturnValue -ne 0) { "FAIL: Create returned $($r.ReturnValue)"; exit 1 }
$procId = [uint32]$r.ProcessId
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $h -eq [IntPtr]::Zero; $i++) {
  Start-Sleep -Milliseconds 150
  # call.ps1 runs powershell; its window belongs to a child powershell process - search all visible windows for the caller text is overkill,
  # so look through powershell processes started in the last 20 seconds.
  foreach ($p in Get-Process -Name powershell -ErrorAction SilentlyContinue) {
    if (((Get-Date) - $p.StartTime).TotalSeconds -gt 25) { continue }
    $cand = [Cap]::Find([uint32]$p.Id)
    if ($cand -ne [IntPtr]::Zero) {
      $rr = New-Object Cap+RECT; [void][Cap]::GetWindowRect($cand, [ref]$rr)
      if (($rr.R - $rr.L) -gt 300 -and ($rr.R - $rr.L) -lt 900 -and ($rr.B - $rr.T) -gt 100 -and ($rr.B - $rr.T) -lt 400) { $h = $cand; break }
    }
  }
}
if ($h -eq [IntPtr]::Zero) { "FAIL: call window not found"; exit 1 }
$rr = New-Object Cap+RECT; [void][Cap]::GetWindowRect($h, [ref]$rr)
$pad = 24
$left = [Math]::Max(0, $rr.L - $pad); $top = [Math]::Max(0, $rr.T - $pad)
$w = [Math]::Min(($rr.R - $left) + $pad, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width - $left)
$hh = [Math]::Min(($rr.B - $top) + $pad, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height - $top)
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size $w, $hh))
$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"saved: $outPath ($w x $hh) kind=$Kind"
