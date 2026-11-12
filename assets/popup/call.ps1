# Входящий вызов от героя, когда DeepSeek Harness закончил, а человек в другом окне: карточка слева на экране,
# тихий рингтон по кругу (assets/sounds: call.m4a/call.mp3 владельца или встроенный ring.wav),
# кнопка «ПРИНЯТЬ» — открыть DeepSeek Harness и закрыть карточку (вторая кнопка убрана).
# Вид карточки (-Kind): call — готовность (зелёный), question — ВОПРОС (жёлтый), approval — ПОДТВЕРЖДЕНИЕ (красный).
# Если DeepSeek Harness и так на переднем плане — ничего не делает.
# Запуск: powershell -File call.ps1 -Caller KADR -Text "что сделано" -Portrait <кадр героя.png> [-Kind call|question|approval] [-Seconds 15] [-Force] [-Snapshot <png>]
param([string]$Caller = 'KADR', [string]$Text = 'Готово!', [string]$Portrait = '', [string]$Kind = 'call', [int]$Seconds = 15, [switch]$Force, [string]$Snapshot = '', [switch]$Test)
if ($Kind -ne 'question' -and $Kind -ne 'approval') { $Kind = 'call' }

# Настройки из меню питомца: звонок вкл/выкл, громкость 0–10 (по умолчанию 4), язык.
$settings = $null
try { $settings = Get-Content (Join-Path (Split-Path $PSScriptRoot) 'pet\settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
$level = 4; if ($settings -and $null -ne $settings.volume) { $level = [Math]::Max(0, [Math]::Min(10, [int]$settings.volume)) }
$VOLUME = 0.055 * $level
if (-not $Force -and -not $Snapshot -and -not $Test -and $settings -and $settings.call -eq $false) { exit 0 }
$sounds = Join-Path (Split-Path $PSScriptRoot) 'sounds'
$sound = 'call.m4a', 'call.mp3', 'ring.wav' | ForEach-Object { Join-Path $sounds $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, PresentationCore

# Проба звука из настроек: две с половиной секунды рингтона без карточки.
if ($Test) {
    if ($level -le 0 -or -not $sound) { exit 0 }
    $player = New-Object System.Windows.Media.MediaPlayer
    $player.Volume = $VOLUME
    $player.Open([uri]$sound)
    $player.Play()
    $stop = New-Object System.Windows.Forms.Timer
    $stop.Interval = 2500
    $stop.Add_Tick({ $player.Stop(); [System.Windows.Forms.Application]::Exit() })
    $stop.Start()
    [System.Windows.Forms.Application]::Run()
    exit 0
}
Add-Type -Path (Join-Path $PSScriptRoot 'call.cs') -ReferencedAssemblies System.Windows.Forms, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CallWin {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
'@

# Главное окно DeepSeek Harness: у Electron процессов с этим именем несколько, но MainWindowHandle есть только
# у главного. Тот же принцип, что FindClaude() в pet.cs.
function Get-AppWindow {
    foreach ($p in @(Get-Process -Name 'DeepSeek Harness' -ErrorAction SilentlyContinue)) {
        try {
            $h = $p.MainWindowHandle
            if ($h -ne 0 -and [CallWin]::IsWindowVisible($h)) { return $p }
        } catch { }
    }
    return $null
}

# Приложение на виду: активное окно принадлежит процессу DeepSeek Harness.
function Test-AppOnView($app) {
    if (-not $app) { return $false }
    $fgPid = 0; [void][CallWin]::GetWindowThreadProcessId([CallWin]::GetForegroundWindow(), [ref]$fgPid)
    return $fgPid -eq $app.Id
}

$app = Get-AppWindow
if (-not $Force -and -not $Snapshot -and (Test-AppOnView $app)) { exit 0 }
# «Не беспокоить»: в файле snooze — до какого времени (unix-мс) молчать. Так питомец не звонит, пока работа не доделана.
$snooze = Join-Path $PSScriptRoot 'snooze'
if (-not $Force -and -not $Snapshot -and (Test-Path $snooze)) {
    $until = 0L; [void][long]::TryParse((Get-Content $snooze -Raw).Trim(), [ref]$until)
    if ([DateTimeOffset]::Now.ToUnixTimeMilliseconds() -lt $until) { exit 0 }
}

$call = New-Object CallForm
$call.En = $settings -and $settings.lang -eq 'en'
$call.Setup($Kind, $Caller, $Text, $Portrait)
$area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$call.Left0 = $area.Left + 24
$call.Top0 = $area.Top + [int]($area.Height * 0.30)

# Рингтон по кругу, пока вызов на экране: личный call.m4a (лучше качеством) или call.mp3, если есть,
# иначе встроенный ring.wav. Тихо и с плавным нарастанием за FADE секунд — не пугает.
$FADE = 1.2
$player = $null
if (-not $Snapshot -and $sound -and $level -gt 0) {
    $player = New-Object System.Windows.Media.MediaPlayer
    $player.Volume = 0
    $player.Open([uri]$sound)
    $player.Add_MediaEnded({ $player.Position = [TimeSpan]::Zero; $player.Play() })
    $player.Play()
}

$finish = { if ($player) { $player.Stop(); $player.Close() }; $call.Close() }
$call.add_Answered({
    $target = Get-AppWindow
    if ($target) {
        if ([CallWin]::IsIconic($target.MainWindowHandle)) { [void][CallWin]::ShowWindow($target.MainWindowHandle, 9) }
        [void][CallWin]::SetForegroundWindow($target.MainWindowHandle)
    }
    & $finish
})

$started = [DateTime]::Now
$tick = New-Object System.Windows.Forms.Timer
$tick.Interval = 16
$tick.Add_Tick({
    $call.Tick()
    $age = ([DateTime]::Now - $started).TotalSeconds
    # Нарастание громкости по квадрату — на слух ровнее, чем по прямой.
    if ($player -and $age -le $FADE + 0.1) { $k = [Math]::Min(1, $age / $FADE); $player.Volume = $VOLUME * $k * $k }
    if ($Snapshot -and $age -gt 1.2) { $call.Snapshot($Snapshot); & $finish }
    elseif ($age -gt $Seconds) { & $finish }
})
$call.Add_Shown({ $tick.Start() })
[System.Windows.Forms.Application]::Run($call)
