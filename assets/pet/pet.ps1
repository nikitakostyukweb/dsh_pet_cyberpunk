# Десктоп-питомец: запускает движок pet.cs (окно с настоящей прозрачностью, 60 кадров в секунду) и держит его,
# пока плагин шлёт состояние (assets/pet/state.json). Всё остальное делает движок: в фоне читает состояние мода,
# заранее грузит кадры героя; живёт в правом нижнем углу ЭКРАНА (рабочая область, с учётом панели задач) и всегда
# поверх остальных окон; ПКМ — меню выбора напарника (выбор уходит моду в assets/pet/choice.json).
# От окон DeepSeek Harness позиция не зависит: питомец виден и при свёрнутом/закрытом Harness.
# Несколько сессий пишут в один state.json — питомец держится за одну (поля session и busy).
# Один на компьютер (мьютекс). Уходит, если мод молчит дольше минуты.
# Heartbeat для плагина: assets/pet/pet.beat {"pid":…,"at":<ms>} — при старте и раз в 5 с, удаляется при выходе.
# Сеть, обновления и звезда GitHub в порте выключены (см. NET_OFF в pet.cs). Ошибки UI/кликов/меню — в
# assets/pet/pet-crash.log (окно при этом не падает).
# Проверка без экрана: assets/pet/snap.request — снимок холста в preview/pet-live.png и строка о скорости
# отрисовки в stderr; assets/pet/menu.request — открыть меню (номер в файле — какую карточку подсветить);
# assets/pet/pick.request — выбрать героя по номеру в списке (как клик по карточке).
# Запуск: powershell -File pet.ps1 -Root <папка мода>
param([string]$Root)

$mutex = New-Object System.Threading.Mutex($false, 'Local\edgerunners-pet')
if (-not $mutex.WaitOne(0)) { exit 0 }
$crashLog = Join-Path $Root 'assets\pet\pet-crash.log'
try {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing, System.Management
    # Ловим исключения UI-потока (без диалога «PowerShell error») и пишем их в pet-crash.log.
    # Режим надо выставить ДО создания формы: после создания контролов WinForms его менять не даёт.
    [System.Windows.Forms.Application]::SetUnhandledExceptionMode([System.Windows.Forms.UnhandledExceptionMode]::CatchException)
    Add-Type -Path (Join-Path $PSScriptRoot 'pet.cs') -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Management
    $pet = New-Object PetForm
    $pet.Start($Root)
    try {
        [System.Windows.Forms.Application]::Run($pet)
    } catch {
        # Не даём процессу умереть молча: необработанное исключение UI-потока — в журнал.
        try { Add-Content -LiteralPath $crashLog -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff') + ' [run] ' + $_.Exception.ToString()) -Encoding UTF8 } catch { }
        throw
    }
} catch {
    try { Add-Content -LiteralPath $crashLog -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff') + ' [pet.ps1] ' + $_.Exception.ToString()) -Encoding UTF8 } catch { }
    throw
} finally {
    $mutex.ReleaseMutex()
}
