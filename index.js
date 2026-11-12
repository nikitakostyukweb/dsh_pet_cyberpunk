// EdgeRunners for DeepSeek Harness — Host plugin half.
//
// Ports the Claude Code fan mod «edgerunners» (MIT, (c) Alexanderadon) to the DSH desktop app:
// the anime pet engine (assets/pet/pet.cs) reads assets/pet/state.json ten times per second, and this
// plugin translates Harness session events into that file. All visual decisions live in
// hooks/director.js (a faithful port of the original hooks/register.tsx); this file only does I/O:
//
//   DeepSeek Harness events ──► one director per live main session ──► state.json ──► pet window
//                                                                  └─► choice.json / settings.json (pet menu)
//   /persona · /report  ◄── Harness command registry
//
// Everything here is best effort: a broken pet must never delay a prompt or a tool.

import { spawn } from 'node:child_process'
import { readFileSync, readdirSync, renameSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { createDirector } from './hooks/director.js'

const ROOT = dirname(fileURLToPath(import.meta.url))
const PET_DIR = join(ROOT, 'assets', 'pet')
const STATE_PATH = join(PET_DIR, 'state.json')
const CHOICE_PATH = join(PET_DIR, 'choice.json')
const SETTINGS_PATH = join(PET_DIR, 'settings.json')
const META_PATH = join(PET_DIR, 'dsh-meta.json')
// Дневной счётчик трат на токены (реальные деньги с баланса аккаунта DeepSeek, в рублях).
const SPEND_PATH = join(PET_DIR, 'spend.json')
// Сердцебиение питомца пишет сам движок (pet.cs) — по нему плагин понимает, что процесс жив.
const BEAT_PATH = join(PET_DIR, 'pet.beat')
const PET_ERR_PATH = join(tmpdir(), 'edgerunners-pet-err.txt')

const TICK_MS = 1000
// Метка сборки: по ней видно, какой код реально пишет state.json (полезно после перезапуска приложения).
const BUILD = '1.0.0'
// Повторный запуск питомца сразу за первым только плодит процессы (мьютекс в pet.ps1 их всё равно схлопнет).
const LAUNCH_GAP = 20_000

const log = (...args) => console.log('[edgerunners]', ...args)

// ---------------------------------------------------------------------------------------------
// уборка чужого мусора в %TEMP%
// ---------------------------------------------------------------------------------------------

// Некоторые агентские скрипты запускают headless Chrome с временным профилем в %TEMP%
// (`--user-data-dir=…\Temp\cdp-profile-XXXX`) и не удаляют его — по 20–30 МБ за запуск.
// Питомец тут ни при чём, но чтобы диск не забивался, раз в час подчищаем только СТАРЫЕ
// (старше 6 часов) профили, которые уже точно не используются; занятые/свежие не трогаем.
const STALE_TEMP_PROFILE = /^cdp-(profile|h|w)-/
const cleanupStaleTempProfiles = (maxAgeMs = 6 * 3600_000) => {
  let removed = 0
  try {
    for (const entry of readdirSync(tmpdir(), { withFileTypes: true })) {
      if (!entry.isDirectory() || !STALE_TEMP_PROFILE.test(entry.name)) continue
      const path = join(tmpdir(), entry.name)
      try {
        if (Date.now() - statSync(path).mtimeMs < maxAgeMs) continue
        rmSync(path, { recursive: true, force: true, maxRetries: 3, retryDelay: 150 })
        removed += 1
      } catch {
        // профиль занят живым Chrome или уже удалён — пропускаем
      }
    }
  } catch (error) {
    log('уборка temp:', error?.message ?? error)
  }
  if (removed > 0) log(`подчистил старых chrome-профилей в TEMP: ${removed}`)

  return removed
}

// ---------------------------------------------------------------------------------------------
// small file helpers
// ---------------------------------------------------------------------------------------------

const readText = (path) => {
  try {
    return readFileSync(path, 'utf8')
  } catch {
    return undefined
  }
}

const readJson = (path) => {
  const text = readText(path)
  if (text === undefined) return undefined
  try {
    return JSON.parse(text.replace(/^\uFEFF/, ''))
  } catch {
    return undefined
  }
}

/** Писать state.json атомарно: питомец читает файл в цикле и не должен увидеть половину. */
const writeState = (state) => {
  try {
    const tmp = `${STATE_PATH}.tmp`
    writeFileSync(tmp, JSON.stringify({ ...state, build: BUILD }), 'utf8')
    renameSync(tmp, STATE_PATH)
  } catch (error) {
    log('state.json не записался:', error?.message ?? error)
  }
}

/** Плоский текст сообщения: ContentBlock[] (text) либо готовое поле text. */
const textOf = (message) => {
  if (!message || typeof message !== 'object') return ''
  if (typeof message.text === 'string') return message.text
  const blocks = message.content
  if (!Array.isArray(blocks)) return ''
  return blocks
    .filter(block => block && block.type === 'text' && typeof block.text === 'string')
    .map(block => block.text)
    .join('\n')
}

const reasonOf = (end) => {
  const kind = end?.kind
  if (kind === 'completed') return 'answer'
  if (kind === 'error') return 'error'
  return 'aborted'
}

// Служебные строки, которые Harness подмешивает в пользовательское сообщение (контекст запуска,
// каталог навыков, напоминания). Герой не должен их цитировать: реплика «Цель: «Time sampled…»»
// выглядела как глюк. Оставляем только то, что написал человек.
const META_LINE = /^(?:time sampled while preparing|elapsed since the preceding|current runtime context\.|current working directory:|current DSH file policy:|approval prompts are enabled|you are an ai agent powered|you are the team lead|agent teams is available|the team lead and all teammates|use list_agents|to message another teammate|current deepseek harness implementation checkout|current deepseek harness web gui url|a skill is a reusable set|if the user names a skill|the following skills are available|use the skill tool|load all applicable skills|a user may also invoke a skill|browser time zone for this request|team message from|background subagent|image "|!\[|system reminder)/i
const META_ANYWHERE = /(runtime context\. This snapshot|available_skills|Time sampled while preparing|DSH file policy:|Approval prompts are enabled in this session|Current DeepSeek Harness implementation checkout)/i
const SKILL_BULLET = /^[-*•]\s+`[a-z0-9-]+`:/i

const cleanUserText = (raw) => {
  if (!raw || typeof raw !== 'string') return { text: '', human: false }
  let text = raw.replace(/<system-reminder>[\s\S]*?<\/system-reminder>/gi, '\n')
  text = text.replace(/<available_skills>[\s\S]*?<\/available_skills>/gi, '\n')
  const kept = []
  for (const line of text.split(/\r?\n/)) {
    const trimmed = line.trim()
    if (!trimmed) {
      kept.push('')
      continue
    }
    if (META_LINE.test(trimmed) || SKILL_BULLET.test(trimmed) || META_ANYWHERE.test(trimmed)) continue
    if (/^```/.test(trimmed)) continue
    kept.push(trimmed)
  }
  const merged = kept.join('\n').replace(/\n{2,}/g, '\n').trim()
  if (!merged) return { text: '', human: false }
  const flat = merged.replace(/\s+/g, ' ').trim()
  const text150 = flat.length > 150 ? `${flat.slice(0, 149)}…` : flat

  return { text: text150, human: true }
}

// ---------------------------------------------------------------------------------------------
// plugin
// ---------------------------------------------------------------------------------------------

export function apply(ctx, config = {}) {
  const launchEnabled = config.launchPet !== false
  // Траты на токены: реальный баланс аккаунта в CNY/USD пересчитывается в рубли.
  const spendEnabled = config.spendEnabled !== false
  const spendSampleMs = Math.max(60_000, Number(config.spendSampleMinutes ?? 5) * 60_000)
  const rubPerCny = Number(config.rubPerCny ?? 12.39)
  const rubPerUsd = Number(config.rubPerUsd ?? 82.66)
  // Курс ЦБ раз в день (cbr-xml-daily); false — брать только значения из конфига.
  const fetchCbrRate = config.fetchCbrRate !== false
  const dailyBudgetRub = Math.max(1, Number(config.dailyBudgetRub ?? 100))
  // Сколько активной сессии молчать, прежде чем питомец переедет к другой, где идёт работа.
  const activeQuietMs = Math.max(0, Number(config.activeSessionQuietSeconds ?? 30)) * 1000
  // С какого времени работы страховочный «звонок о готовности» (если режиссёр уже не позвал).
  const completionCallAfterMs = Math.max(0, Number(config.completionCallAfterSeconds ?? 15)) * 1000
  /** @type {Map<string, object>} */
  const sessions = new Map()
  // Питомец показывает ОДНУ сессию — ту, в которой человек сейчас работает. Иначе несколько
  // открытых сессий по очереди перезаписывают state.json, и герой говорит не о том, что происходит.
  let activeId = ''
  /** @type {{hero?: string, choiceAt?: number}} */
  let meta = { hero: undefined, choiceAt: 0 }
  const saved = readJson(META_PATH)
  if (saved && typeof saved === 'object') meta = { ...meta, ...saved }
  let lastLaunch = 0
  let stopped = false
  let timer
  let lastTempCleanup = 0

  // ---- дневной счётчик трат на токены ----
  // Аккаунт DeepSeek хранит баланс в CNY/USD; считаем, сколько убыло с баланса за сегодня,
  // и пересчитываем в рубли по курсу из конфига (по умолчанию 11.5 ₽/¥, 90 ₽/$).
  let accountService
  let spendBusy = false
  let lastSpendSample = 0
  const round2 = (value) => Math.round(Number(value) * 100) / 100
  const todayKey = () => new Date().toLocaleDateString('sv-SE')
  let spend = { date: '', currency: 'CNY', total: null, spent: 0, rub: 0, updatedAt: 0, balanceRub: null, balanceUpdatedAt: 0, balanceCurrency: 'CNY', rubPerCny: rubPerCny, rubPerUsd: rubPerUsd, rateDate: '' }
  const saveSpend = () => {
    try { writeFileSync(SPEND_PATH, JSON.stringify(spend), 'utf8') } catch (error) { log('spend.json не записался:', error?.message ?? error) }
  }
  const resetSpend = (keepCurrency) => {
    spend = { date: todayKey(), currency: keepCurrency || spend.currency || 'CNY', total: null, spent: 0, rub: 0, updatedAt: 0, balanceRub: null, balanceUpdatedAt: 0, balanceCurrency: keepCurrency || spend.currency || 'CNY', rubPerCny: spend.rubPerCny, rubPerUsd: spend.rubPerUsd, rateDate: spend.rateDate }
    saveSpend()
  }
  const loadSpend = () => {
    const saved = readJson(SPEND_PATH)
    const today = todayKey()
    if (saved && saved.date === today && Number.isFinite(Number(saved.spent)) && Number.isFinite(Number(saved.rub))) {
      spend = {
        date: today,
        currency: typeof saved.currency === 'string' ? saved.currency : 'CNY',
        total: saved.total === null || saved.total === undefined ? null : Number(saved.total),
        spent: Math.max(0, Number(saved.spent)),
        rub: Math.max(0, Number(saved.rub)),
        updatedAt: Number(saved.updatedAt) || 0,
        balanceRub: saved.balanceRub === null || saved.balanceRub === undefined ? null : Number(saved.balanceRub),
        balanceUpdatedAt: Number(saved.balanceUpdatedAt) || 0,
        balanceCurrency: typeof saved.balanceCurrency === 'string' ? saved.balanceCurrency : 'CNY',
        rubPerCny: Number(saved.rubPerCny) || rubPerCny,
        rubPerUsd: Number(saved.rubPerUsd) || rubPerUsd,
        rateDate: typeof saved.rateDate === 'string' ? saved.rateDate : '',
      }
    } else {
      resetSpend(saved?.currency)
    }
  }
  const rateFor = (currency) => {
    const cny = Number(spend.rubPerCny) || rubPerCny
    const usd = Number(spend.rubPerUsd) || rubPerUsd
    return currency === 'USD' ? usd : cny
  }
  // Раз в день — официальный курс ЦБ (cbr-xml-daily), чтобы рубли были честные.
  // Ошибка сети не ломает счётчик: остаются курсы из конфига или прошлого дня.
  const refreshRates = async () => {
    if (!fetchCbrRate || spend.rateDate === todayKey()) return
    try {
      const response = await fetch('https://www.cbr-xml-daily.ru/daily_json.js', { signal: AbortSignal.timeout(15000) })
      if (!response.ok) return
      const data = await response.json()
      const cny = Number(data?.Valute?.CNY?.Value)
      const usd = Number(data?.Valute?.USD?.Value)
      if (Number.isFinite(cny) && cny > 0) spend.rubPerCny = cny
      if (Number.isFinite(usd) && usd > 0) spend.rubPerUsd = usd
      spend.rateDate = todayKey()
      spend.rub = Math.max(0, spend.spent) * rateFor(spend.currency)
      saveSpend()
      log(`курс ЦБ: ¥1 = ${spend.rubPerCny} ₽, $1 = ${spend.rubPerUsd} ₽`)
    } catch (error) {
      log('курс ЦБ не получен:', error?.message ?? error)
    }
  }
  const sampleSpend = async () => {
    if (!spendEnabled || spendBusy || !accountService) {
      spend.lastSkip = !spendEnabled ? 'disabled' : spendBusy ? 'busy' : 'no-service'
      spend.lastSkipAt = Date.now()
      saveSpend()
      return
    }
    spendBusy = true
    spend.attempts = (Number(spend.attempts) || 0) + 1
    spend.lastAttemptAt = Date.now()
    spend.lastSkip = ''
    saveSpend()
    try {
      const client = {
        version: process.env.DSH_CLIENT_VERSION || '0.8.0',
        locale: 'ru',
        timezoneOffsetSeconds: -new Date().getTimezoneOffset() * 60,
      }
      await refreshRates()
      if (spend.total !== null) spend.rub = Math.max(0, spend.spent) * rateFor(spend.currency)
      const result = await accountService.getBalance(client)
      spend.lastBalanceStatus = result ? result.status : 'null'
      if (!result || result.status !== 'ready') { saveSpend(); return }
      const wallets = [...(result.value ?? []), ...(result.bonusWallets ?? [])]
      const sums = new Map()
      for (const wallet of wallets) {
        const amount = Number(wallet?.balance)
        if (!Number.isFinite(amount)) continue
        const currency = wallet?.currency === 'USD' ? 'USD' : 'CNY'
        sums.set(currency, (sums.get(currency) ?? 0) + amount)
      }
      if (sums.size === 0) return
      const [currency, total] = [...sums.entries()].sort((a, b) => b[1] - a[1])[0]
      if (spend.total === null || currency !== spend.currency) {
        spend.total = total
        spend.currency = currency
      } else {
        const delta = spend.total - total
        if (delta > 0) spend.spent += delta
        spend.total = total
      }
      spend.updatedAt = Date.now()
      spend.rub = Math.max(0, spend.spent) * rateFor(spend.currency)
      spend.balanceRub = round2(total * rateFor(currency))
      spend.balanceCurrency = currency
      spend.balanceUpdatedAt = spend.updatedAt
      saveSpend()
      log(`траты за сегодня: ${spend.rub.toFixed(2)} ₽ (${spend.spent.toFixed(2)} ${spend.currency})`)
      // Сразу показать в SYS.MON, не дожидаясь следующего пульса state.json.
      const entry = activeId ? sessions.get(activeId) : undefined
      const current = entry && typeof entry.director.current === 'function' ? entry.director.current() : undefined
      if (current) writeWithStats(current)
    } catch (error) {
      spend.lastError = String(error?.message ?? error)
      saveSpend()
      log('баланс не прочитался:', error?.message ?? error)
    } finally {
      spendBusy = false
      lastSpendSample = Date.now()
    }
  }
  loadSpend()
  // При старте плагина — разовая уборка старых chrome-профилей, дальше раз в час в тике.
  try { cleanupStaleTempProfiles() } catch { /* ignore */ }
  lastTempCleanup = Date.now()

  // settings.json/choice.json пишет меню питомца; читаем по mtime, чтобы не дёргать диск впустую.
  const cacheOf = (path) => ({ path, mtime: -1, text: undefined, value: undefined })
  const settingsCache = cacheOf(SETTINGS_PATH)
  const choiceCache = cacheOf(CHOICE_PATH)

  const poll = (cache) => {
    try {
      const info = statSync(cache.path)
      if (info.mtimeMs !== cache.mtime) {
        cache.mtime = info.mtimeMs
        // Файл мог быть недописан — читаем молча, следующий тик догонит.
        const text = readFileSync(cache.path, 'utf8')
        cache.text = text
        try {
          cache.value = JSON.parse(text.replace(/^\uFEFF/, ''))
        } catch {
          cache.value = undefined
        }
      }
    } catch {
      // Файла нет — это нормально, меню ещё не трогали.
    }

    return cache
  }

  const saveMeta = (patch) => {
    meta = { ...meta, ...patch }
    try {
      writeFileSync(META_PATH, JSON.stringify(meta), 'utf8')
    } catch (error) {
      log('meta не записалась:', error?.message ?? error)
    }
  }

  // ---- питомец: отдельная программа Windows, живёт своей жизнью ----

  // Запускаем не напрямую, а через невидимый WMI-вызов: так у программы нет ни консольного окна,
  // ни родительского job-объекта — питомец (и звонок) живут, пока мод шлёт им состояние.
  const psQuote = (text) => `'${String(text).replace(/'/g, "''")}'`
  const launchDetached = (commandLine, label) => {
    try {
      const script = [
        `$cmd = ${psQuote(commandLine)}`,
        '$startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }',
        '[void](Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd; ProcessStartupInformation = $startup })',
      ].join('; ')
      // Сам WMI-вызов короткий: детачить его не нужно (detached на Windows мешает PowerShell
      // выполнить -Command), окно скрыто windowsHide, а питомца создаёт уже служба WMI.
      const child = spawn('powershell.exe',
        ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-Command', script],
        { stdio: 'ignore', windowsHide: true })
      child.on('error', error => log(`${label}:`, error?.message ?? error))
      child.on('exit', () => { /* WMI-вызов завершился: питомец уже создан службой WMI */ })
      child.unref()
    } catch (error) {
      log(`${label}:`, error?.message ?? error)
    }
  }

  const launchPet = () => {
    if (!launchEnabled) return
    const at = Date.now()
    if (at - lastLaunch < LAUNCH_GAP) return
    lastLaunch = at
    log('запускаю питомца')
    launchDetached(
      `cmd.exe /c powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "${join(PET_DIR, 'pet.ps1')}" -Root "${ROOT}" 2> "${PET_ERR_PATH}"`,
      'питомец не запустился',
    )
  }

  // «Входящий вызов»: портрет героя, что случилось, кнопка ОТВЕТИТЬ (возвращает в приложение).
  // kind: call (ход готов) | question (модель спрашивает человека) | approval (нужно подтверждение).
  const dquote = (text) => `"${String(text ?? '').replace(/"/g, '""')}"`
  const shortText = (text, limit = 180) => {
    const flat = String(text ?? '').replace(/\s+/g, ' ').trim()
    return flat.length > limit ? `${flat.slice(0, limit - 1)}…` : flat
  }
  const callPopup = (decision) => {
    const kind = decision.kind === 'question' || decision.kind === 'approval' ? decision.kind : 'call'
    log(`звонок ${kind}: ${shortText(decision.text, 80)}`)
    const settings = settingsCache.value
    if (settings && settings.call === false) return
    launchDetached(
      `powershell -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "${join(ROOT, 'assets', 'popup', 'call.ps1')}"` +
        ` -Caller ${dquote(decision.name)} -Text ${dquote(shortText(decision.text))}` +
        ` -Portrait "${join(PET_DIR, String(decision.hero), 'idle_01.png')}" -Seconds 15 -Kind ${kind}`,
      'звонок не показался',
    )
  }

  // Один и тот же вопрос/подтверждение не должен открывать карточку несколько раз подряд.
  const lastPopup = new Map()
  const popupOnce = (key, decision) => {
    const at = Date.now()
    if (at - (lastPopup.get(key) ?? 0) < 4000) return false
    lastPopup.set(key, at)
    callPopup(decision)
    return true
  }

  // Сколько чатов сейчас открыто (живые главные сессии) и сколько из них занято работой —
  // это поле читает движок питомца для строки CHATS в мониторинге.
  const liveMainSessions = () => {
    try {
      return (ctx.get('agents')?.list?.() ?? []).filter(agent => {
        try {
          return Boolean(agent?.session) && agent.session.header?.origin !== 'subagent'
        } catch {
          return false
        }
      })
    } catch {
      return []
    }
  }
  const chatStats = () => {
    const live = liveMainSessions()
    let busyChats = 0
    for (const agent of live) {
      try {
        if (sessions.get(String(agent.id))?.turnActive) busyChats += 1
      } catch {
        // ignore
      }
    }

    return { chats: live.length, busyChats }
  }
  let lastChatSignature = ''
  const writeWithStats = (state) => {
    const stats = chatStats()
    lastChatSignature = `${stats.chats}:${stats.busyChats}`
    writeState({
      ...state,
      ...stats,
      spendRub: round2(spend.rub),
      spendBudgetRub: dailyBudgetRub,
      spendCurrency: spend.currency,
      spendUpdatedAt: spend.updatedAt || undefined,
      balanceRub: spend.balanceRub === null || spend.balanceRub === undefined ? undefined : round2(spend.balanceRub),
      balanceCurrency: spend.balanceCurrency || spend.currency,
      balanceUpdatedAt: spend.balanceUpdatedAt || undefined,
    })
  }

  // ---- режиссёр на каждую живую главную сессию; питомцу пишет только активная ----

  /**
   * Завести (или взять) режиссёра сессии. activate: сессия становится той, которую показывает питомец.
   * Неактивные сессии живут только для /report и не пишут state.json.
   */
  const ensureSession = (session, options = {}) => {
    const id = String(session?.id ?? '')
    if (!id) return undefined
    // Ход субагента — не ход сессии: своего питомца ему не положено.
    if (session?.header?.origin === 'subagent') return undefined
    const activate = options.activate === true
    const known = sessions.get(id)
    if (known) {
      if (activate && activeId !== id) {
        activeId = id
        try {
          const settings = poll(settingsCache)
          if (settings.text !== undefined) known.director.applySettings(settings.text)
          // Герой общий для всех сессий: догоняем выбор, сделанный в другой сессии.
          const want = meta.hero
          if (want && known.director.heroNow().id !== want) known.director.chooseHero(want)
        } catch (error) {
          log('переключение сессии упало:', error?.message ?? error)
        }
        log(`активная сессия → ${id}`)
      }

      return known
    }

    const entry = {
      id,
      director: undefined,
      turnStart: 0,
      turnActive: false,
      // Когда сессия реально начала текущий ход (для «работа шла долго» даже если плагин
      // подключился посреди хода и не видел prompt.submit).
      busySince: 0,
      attachedAt: Date.now(),
      answer: '',
      window: 0,
      tokens: 0,
      calls: new Map(),
      lastActivity: Date.now(),
    }
    const director = createDirector({
      now: () => Date.now(),
      log,
      // Пишем state.json только от имени активной сессии: иначе герой прыгает между чатами.
      emit: state => {
        if (id === activeId) writeWithStats(state)
      },
      launch: () => launchPet(),
      call: decision => callPopup(decision),
      notify: ({ title, text }) => log(`звонок (без кадров): ${title} — ${text}`),
      toast: text => log(`toast: ${text}`),
      hero: meta.hero,
      choiceAt: meta.choiceAt,
    })
    entry.director = director
    director.setSession(id)
    sessions.set(id, entry)
    // Настройки (язык) — из меню питомца, если оно уже писалось.
    const settings = poll(settingsCache)
    if (settings.text !== undefined) {
      try { director.applySettings(settings.text) } catch { /* ignore */ }
    }
    try { director.setChoiceAt(Number(meta.choiceAt) || 0) } catch { /* ignore */ }
    if (activate) {
      activeId = id
      director.start()
      log(`сессия ${id} под наблюдением (герой ${director.heroNow().name})`)
    }

    return entry
  }

  /** Тик: alive-пульс, отдых, мелочи, отложенная сцена; настройки, меню питомца и его здоровье. */
  const tick = () => {
    if (stopped) return
    const settings = poll(settingsCache)
    const choice = poll(choiceCache)
    const entry = activeId ? sessions.get(activeId) : undefined
    if (entry) {
      const director = entry.director
      try {
        // Выбор героя в меню питомца: применяется активной сессией ровно один раз.
        if (choice.text !== undefined) {
          const result = director.applyChoice(choice.text)
          const chosenAt = Number(choice.value?.at) || 0
          if (result?.applied && chosenAt > (Number(meta.choiceAt) || 0)) {
            saveMeta({ hero: result.id, choiceAt: chosenAt })
          }
        }
        const context = entry.window > 0
          ? { percent: (entry.tokens / entry.window) * 100, tokens: entry.tokens, window: entry.window, usd: 0 }
          : undefined
        director.tick({ settings: settings.text, context })
        // Число открытых/занятых чатов меняется и без смены сцены (открыли/закрыли чат) —
        // обновляем state.json, чтобы мониторинг в питомце показывал актуальное значение.
        const stats = chatStats()
        const signature = `${stats.chats}:${stats.busyChats}`
        if (signature !== lastChatSignature) {
          const current = typeof director.current === 'function' ? director.current() : undefined
          if (current) writeWithStats(current)
        }
      } catch (error) {
        log('тик упал:', error?.message ?? error)
      }
    }
    // Питомец мог упасть (меню/камера/что угодно): движок обновляет pet.beat раз в 5 с —
    // протухший beat значит, что процесса нет, и его надо поднять заново. Без активной сессии
    // питомца не поднимаем: мод ему ничего не пишет, и он сам уходит после минуты тишины.
    if (launchEnabled && entry) {
      const beatAt = Number(readJson(BEAT_PATH)?.at) || 0
      if (Date.now() - beatAt > 25_000) launchPet()
    }
    // Раз в час — подчистить старые временные профили Chrome (по 20–30 МБ каждый).
    if (Date.now() - lastTempCleanup > 3600_000) {
      lastTempCleanup = Date.now()
      try { cleanupStaleTempProfiles() } catch { /* ignore */ }
    }
    // Траты на токены: новый день — новый счётчик; баланс опрашиваем раз в несколько минут.
    if (spendEnabled) {
      if (spend.date !== todayKey()) {
        resetSpend()
        const entryNow = activeId ? sessions.get(activeId) : undefined
        const currentNow = entryNow && typeof entryNow.director.current === 'function' ? entryNow.director.current() : undefined
        if (currentNow) writeWithStats(currentNow)
      }
      if (Date.now() - lastSpendSample > spendSampleMs) void sampleSpend()
    }
  }

  // ---- события сессии ----

  const onSessionEvent = (session, event) => {
    if (stopped || !event || typeof event !== 'object') return
    if (session?.header?.origin === 'subagent') return
    const id = String(session?.id ?? '')
    if (!id) return

    // Питомец показывает ОДНУ сессию. Активной становится та, где человек написал просьбу;
    // если активная сессия давно молчит, а в другой идёт работа — питомец переезжает туда
    // (так он показывает ту работу, которая реально происходит, а не простаивающий чат).
    const isPrompt = event.type === 'user/message'
    const isWork = event.type === 'tool/call' || event.type === 'turn/start'
    const activeEntryNow = activeId ? sessions.get(activeId) : undefined
    const activeQuiet = !activeEntryNow
      || (!activeEntryNow.turnActive && Date.now() - activeEntryNow.lastActivity > activeQuietMs)
    let entry
    if (id === activeId) {
      entry = sessions.get(id) ?? ensureSession(session, { activate: true })
    } else if (isPrompt || !activeId || (isWork && activeQuiet)) {
      entry = ensureSession(session, { activate: true })
    } else {
      return
    }
    if (!entry) return
    const director = entry.director
    entry.lastActivity = Date.now()
    const data = event.data ?? {}

    switch (event.type) {
      case 'user/message': {
        const cleaned = cleanUserText(textOf(data))
        if (cleaned.human && cleaned.text) {
          if (!entry.turnActive) {
            entry.turnActive = true
            entry.busySince = Date.now()
          }
          director.promptSubmit({ text: cleaned.text, machine: false })
        }
        // Только служебный контекст (продолжение после лимита шагов и т.п.) — не цитируем его,
        // просто отмечаем, что ход идёт: сцену движения задаст turn/start.
        else if (entry.turnActive) {
          director.turnStart()
        }
        break
      }
      case 'turn/start': {
        entry.turnStart = Number(event.time) || Date.now()
        entry.turnActive = true
        if (!entry.busySince) entry.busySince = Date.now()
        entry.answer = ''
        director.turnStart()
        break
      }
      case 'tool/call': {
        // Плагин мог подключиться посреди хода: turn/start прошёл мимо, но работа идёт.
        // Без этой отметки герой считает себя «на отдыхе» и говорит общие реплики вместо настоящих.
        if (!entry.turnActive) {
          entry.turnActive = true
          entry.turnStart = Date.now()
          if (!entry.busySince) entry.busySince = Date.now()
          director.turnStart()
        }
        let input = {}
        try {
          input = typeof data.arguments === 'string' ? JSON.parse(data.arguments) : (data.arguments ?? {})
        } catch {
          input = {}
        }
        entry.calls.set(String(data.callId ?? ''), { name: String(data.name ?? ''), input })
        director.toolCall({ name: data.name, input })
        // Вопрос человеку и проверка плана видны уже в самом вызове инструмента — карточку показываем
        // отсюда, не дожидаясь события answerers (оно может уйти в UI раньше нас).
        if (data.name === 'ask_user_question') {
          const questions = Array.isArray(input?.questions) ? input.questions : []
          const first = questions[0]
          const lang = languageOf(entry)
          const question = shortText((first && first.question) || '', 110)
          if (question) {
            const line = askLine(lang, question, false)
            announce(entry, `question:${data.callId ?? question}`, 'question', line)
          }
        } else if (data.name === 'exit_plan_mode') {
          const lang = languageOf(entry)
          announce(entry, `approval:${data.callId ?? 'plan'}`, 'approval',
            lang === 'en' ? 'The plan is waiting for your approval' : 'План ждёт твоего подтверждения')
        }
        break
      }
      case 'tool/result': {
        const callId = String(data.message?.source?.callId ?? data.message?.toolCallId ?? '')
        const call = entry.calls.get(callId)
        entry.calls.delete(callId)
        const isError = data.message?.isError === true || data.error !== undefined
        const background = call?.input?.run_in_background === true
        director.toolResult({ name: call?.name, input: call?.input, isError, background })
        break
      }
      case 'assistant/message': {
        const text = textOf(data.message)
        if (text) entry.answer = text
        const usage = data.usage
        if (usage && entry.window > 0) {
          entry.tokens = Number(usage.inputTokens ?? 0) + Number(usage.cacheReadTokens ?? 0) + Number(usage.outputTokens ?? 0)
        }
        break
      }
      case 'request/context': {
        const window = Number(data.contextWindow ?? 0)
        if (window > 0) entry.window = window
        break
      }
      case 'turn/end': {
        const workedMs = entry.busySince > 0 ? Math.max(0, Date.now() - entry.busySince) : 0
        entry.turnActive = false
        entry.busySince = 0
        const at = Date.now()
        const durationMs = entry.turnStart > 0 ? Math.max(0, at - entry.turnStart) : 0
        entry.turnStart = 0
        // Решает ли режиссёр звонить — смотрим до и после: если он уже позвал, второй карточки не надо.
        const beforeCall = typeof director.lastCall === 'function' ? director.lastCall() : undefined
        director.turnComplete({
          answer: entry.answer,
          durationMs,
          reason: reasonOf(data.reason),
          turnId: `${id}:${String(data.turn ?? '')}`,
        })
        const afterCall = typeof director.lastCall === 'function' ? director.lastCall() : undefined
        const calledByDirector = afterCall !== undefined && afterCall !== beforeCall
        // Страховка: ход шёл долго и завершился ответом, а звонка ещё не было (например, плагин
        // подключился посреди хода — тогда режиссёр считает ход «продолжением» и молчит).
        const reason = reasonOf(data.reason)
        if (!calledByDirector && reason === 'answer' && Math.max(workedMs, durationMs) >= completionCallAfterMs) {
          const who = director.heroNow()
          const first = String(entry.answer ?? '').split(/\r?\n/).map(line => line.replace(/^[#>\-*\s`]+/, '').trim()).find(Boolean)
          const text = shortText(first || (languageOf(entry) === 'en' ? 'Done.' : 'Готово.'), 140)
          log(`звонок о готовности (${Math.round(Math.max(workedMs, durationMs) / 1000)} с): ${text}`)
          popupOnce(`call:${id}:${String(data.turn ?? '')}`, { kind: 'call', hero: who.sprite ?? 'kadr', id: who.id, name: who.name, text })
        }
        entry.answer = ''
        break
      }
      default:
        break
    }
  }

  const onSessionCreated = (session) => {
    if (stopped) return
    if (session?.header?.origin === 'subagent') return
    // Питомец выходит на первой сессии; переключать его на каждый новый чат не надо.
    if (!activeId) ensureSession(session, { activate: true })
  }

  // ---- сессии, которые уже живут на момент установки плагина ----

  const adoptLiveSessions = (agents) => {
    try {
      const candidates = []
      for (const agent of agents?.list?.() ?? []) {
        try {
          const session = agent?.session
          if (session && session.header?.origin !== 'subagent') candidates.push(session)
        } catch {
          // одна кривая сессия не мешает остальным
        }
      }
      if (activeId || candidates.length === 0) return
      // Активная — та, где работа шла последней (а не просто самая новая по createdAt:
      // открытая позже пустая вкладка не должна забирать питомца).
      const lastTime = (session) => {
        try {
          const seq = Number(session?.seq) || 0
          const last = seq > 0 ? session.eventAt(seq - 1) : undefined
          return Number(last?.time) || Number(session?.header?.createdAt) || 0
        } catch {
          return Number(session?.header?.createdAt) || 0
        }
      }
      candidates.sort((a, b) => lastTime(b) - lastTime(a))
      ensureSession(candidates[0], { activate: true })
    } catch (error) {
      log('живые сессии не прочитались:', error?.message ?? error)
    }
  }

  // ---- /persona и /report ----

  const personaReply = (entry) => {
    const director = entry.director
    const who = director.heroNow()
    return `${who.icon} Сейчас: ${who.name}\n\n${director.heroList()}`
  }

  const resolveEntry = (agent, agents, options = {}) => {
    const id = String(agent?.id ?? '')
    const known = sessions.get(id)
    if (known) {
      if (options.activate === true) ensureSession(agent?.session ?? { id }, { activate: true })
      return known
    }
    try {
      const live = agents?.get?.(agent?.id)
      if (live?.session) return ensureSession(live.session, options)
    } catch {
      // ignore
    }
    try {
      if (agent?.session) return ensureSession(agent.session, options)
    } catch {
      // ignore
    }
    return undefined
  }

  const registerCommands = (commands, agents) => {
    // Имя команды в Harness — строчная латиница: /persona, /report (кириллические имена не допускаются).
    const registered = []
    const safeRegister = (definition) => {
      try {
        ctx.effect(() => commands.register(definition))
        registered.push(definition.name)
      } catch (error) {
        log(`команда /${definition.name} не зарегистрировалась:`, error?.message ?? error)
      }
    }

    safeRegister({
      name: 'persona',
      description: 'Выбрать аниме-напарника: роман, талия, ди, уик, кот, бабушка… (без аргумента — текущий и список)',
      input: { hint: '[персонаж]' },
      handler: async (invocation) => {
        try {
          // /persona — явное действие человека в этой сессии: питомец переезжает сюда.
          const entry = resolveEntry(invocation.agent, agents, { activate: true })
          if (!entry) return { kind: 'error', text: 'Напарник ещё не подключился к этой сессии — попробуй ещё раз.' }
          const word = String(invocation.rawInput ?? '').trim()
          if (!word) return { kind: 'success', text: personaReply(entry) }
          const result = entry.director.chooseHero(word)
          if (result?.ok) saveMeta({ hero: result.id })
          return { kind: result?.ok ? 'success' : 'error', text: String(result?.text ?? 'Не получилось сменить героя.') }
        } catch (error) {
          log('/persona упал:', error?.message ?? error)
          return { kind: 'error', text: 'Смена героя не получилась — попробуй ещё раз.' }
        }
      },
    })

    const reportHandler = async (invocation) => {
      try {
        const entry = resolveEntry(invocation.agent, agents)
        if (!entry) return { kind: 'error', text: 'Отчёт не собрался: сессия не найдена.' }
        return { kind: 'success', text: String(entry.director.report()) }
      } catch (error) {
        log('/report упал:', error?.message ?? error)
        return { kind: 'error', text: 'Отчёт не собрался — попробуй ещё раз.' }
      }
    }

    safeRegister({
      name: 'report',
      description: 'Отчёт напарника: что агент сделал за сессию, ошибки, расход контекста',
      handler: reportHandler,
    })

    // Диагностика для проверки порта: какие команды реально принял реестр Harness.
    saveMeta({ commands: registered, build: BUILD })
  }

  // ---- вопросы модели и подтверждения операций: это тоже «входящий вызов» ----

  const activeEntry = () => (activeId ? sessions.get(activeId) : undefined)

  const languageOf = (entry) => {
    try {
      return entry?.director?.language?.() === 'en' ? 'en' : 'ru'
    } catch {
      return 'ru'
    }
  }

  const localized = (display, lang) => (display && (display[lang] || display.en)) || ''

  const entryForAgent = (agent) => {
    const id = String(agent?.id ?? '')
    if (id && sessions.has(id)) return sessions.get(id)
    try {
      const live = ctx.get('agents')?.get?.(agent?.id)
      if (live?.session) return ensureSession(live.session)
    } catch {
      // ignore
    }

    return activeEntry()
  }

  /** Реплика героя (если спрашивают активную сессию) + карточка «звонка» определённого вида. */
  const announce = (entry, key, kind, text) => {
    const speaker = entry && entry.id === activeId ? entry : activeEntry()
    if (entry && entry.id === activeId) {
      try {
        entry.director.attention(text)
      } catch (error) {
        log('attention:', error?.message ?? error)
      }
    }
    const who = speaker ? speaker.director.heroNow() : { id: 'kadr', name: 'Роман', sprite: 'kadr' }
    popupOnce(key, { kind, hero: who.sprite ?? 'kadr', id: who.id, name: who.name, text })
  }

  const askLine = (lang, question, approval) => {
    if (approval) {
      return lang === 'en' ? `Confirm, please: ${question}` : `Подтверди, пожалуйста: ${question}`
    }

    return lang === 'en' ? `They're asking you: “${question}”` : `Тебя спрашивают: «${question}»`
  }

  // Подтверждение операции: решаем после того, как политика разрешений составила вердикт.
  const onPreExecute = async (exec, next) => {
    const decision = await next()
    try {
      if (decision && decision.kind === 'ask') {
        const entry = entryForAgent(exec?.agent)
        const lang = languageOf(entry)
        const reason = shortText(localized(decision.displayReason, lang) || exec?.name || '', 110)
        const line = askLine(lang, reason, true)
        announce(entry, `approval:${exec?.callId ?? exec?.name ?? ''}`, 'approval', line)
        log(`подтверждение операции ${exec?.name ?? ''}: ${reason}`)
      }
    } catch (error) {
      log('подтверждение не показалось:', error?.message ?? error)
    }

    return decision
  }

  // Собственно запрос на подтверждение (иногда answerers забирают его до нас — тогда сработает
  // tools/pre-execute выше или подсказка из tool/call для exit_plan_mode).
  const onApprovalRequest = async (req, next) => {
    try {
      const entry = entryForAgent(req?.agent)
      const lang = languageOf(entry)
      const reason = shortText(localized(req?.displayReason, lang) || req?.reason || req?.toolName || '', 110)
      const line = askLine(lang, reason, true)
      announce(entry, `approval:${req?.callId ?? req?.toolName ?? ''}`, 'approval', line)
    } catch (error) {
      log('подтверждение не показалось:', error?.message ?? error)
    }

    return next()
  }

  // Структурированный вопрос человеку (ask_user_question / проверка плана).
  const onUserQuestion = async (request, next) => {
    try {
      const entry = entryForAgent(request?.agent)
      const lang = languageOf(entry)
      const first = Array.isArray(request?.questions) ? request.questions[0] : undefined
      const intent = first?.intent
      const approval = intent?.kind === 'approval' || intent?.kind === 'plan-review'
      const question = shortText(localized(intent?.display?.question, lang) || first?.question || intent?.approve || '', 110)
      const line = askLine(lang, question, approval)
      const callId = request?.wait?.callId ?? intent?.callId ?? first?.id ?? question
      announce(entry, `${approval ? 'approval' : 'question'}:${callId}`, approval ? 'approval' : 'question', line)
    } catch (error) {
      log('вопрос не показался:', error?.message ?? error)
    }

    return next()
  }

  // Подписки и таймер — сразу; сервисы Harness подключаются, как только доступны.
  ctx.effect(() => {
    const offCreated = ctx.on('session/created', onSessionCreated)
    const offEvent = ctx.on('session/event', onSessionEvent)
    // prepend: проходим по цепочке ПЕРВЫМИ. Иначе политика разрешений или композер забирают
    // решение раньше нас, и карточка о вопросе/подтверждении просто не показывается.
    const offPre = ctx.on('tools/pre-execute', onPreExecute, { prepend: true })
    const offApproval = ctx.on('approval/request', onApprovalRequest, { prepend: true })
    const offQuestion = ctx.on('user-questions/request', onUserQuestion, { prepend: true })
    timer = setInterval(tick, TICK_MS)
    if (timer && typeof timer.unref === 'function') timer.unref()

    return () => {
      stopped = true
      try { offCreated?.() } catch { /* ignore */ }
      try { offEvent?.() } catch { /* ignore */ }
      try { offPre?.() } catch { /* ignore */ }
      try { offApproval?.() } catch { /* ignore */ }
      try { offQuestion?.() } catch { /* ignore */ }
      try { if (timer) clearInterval(timer) } catch { /* ignore */ }
      sessions.clear()
    }
  })

  ctx.inject(['agents'], (agentCtx) => {
    adoptLiveSessions(agentCtx.agents)
  })

  // Баланс аккаунта DeepSeek для дневного счётчика трат (CNY/USD → ₽).
  ctx.inject(['deepseekAccount'], (accountCtx) => {
    accountService = accountCtx.deepseekAccount
    spend.hasAccountService = Boolean(accountService)
    spend.injectAt = Date.now()
    saveSpend()
    if (spendEnabled) void sampleSpend()
  })

  ctx.inject(['commands'], (commandCtx) => {
    const agents = commandCtx.get('agents')
    registerCommands(commandCtx.commands, agents)
  })

  log('на связи')
}
