// EdgeRunners — режиссёр героя для DeepSeek Harness.
//
// Порт оригинального мода «edgerunners» для Claude Code (hooks/register.tsx, MIT, (c) Alexanderadon;
// см. LICENSE в корне пакета). Здесь только решения: что герой делает, что говорит и что получает
// десктоп-питомец. Никакого ввода-вывода, таймеров и Node-модулей — index.js переводит события
// Harness в вызовы этих методов и сам пишет assets/pet/state.json, запускает питомца, читает
// choice.json/settings.json и показывает входящий звонок.
//
//   const director = createDirector({
//     now: () => Date.now(),              // часы, мс эпохи (обязательный по смыслу)
//     random: () => Math.random(),        // для тестов; по умолчанию Math.random
//     log: (msg) => console.log(msg),     // необязательная диагностика
//     emit: (state) => writeState(state), // полный объект state.json (см. CONTRACT.md §1)
//     // необязательные побочные эффекты, решение о них принимает режиссёр:
//     launch: ({ hero, id }) => {},       // запустить/перезапустить pet.ps1
//     call: ({ hero, id, name, text }) => {}, // показать «входящий вызов» (popup/call.ps1)
//     notify: ({ title, text }) => {},    // герой без кадров: обычное уведомление
//     toast: (text) => {},                // предупреждение о контексте
//   })
//
//   director.setSession('session-id')     // какую сессию показывает питомец
//   director.setLang('ru')                // 'ru' | 'en' (из settings.json)
//   director.start()                      // session.start: приветствие и запуск питомца
//   director.promptSubmit({ text, machine })        // просьба человека (machine=false) или
//                                                   // отчёт фоновой задачи (machine=true)
//   director.turnStart()                  // turn.start без prompt.submit (продолжение)
//   director.toolCall({ name, input })    // инструмент сейчас побежит
//   director.toolResult({ name, input, isError, background })
//   director.turnComplete({ answer, durationMs, reason, turnId? })  // 'answer'|'aborted'|'error';
//                                                   // turnId (если есть) защищает от двойного конца хода
//   director.tick()                       // раз в секунду: alive, отдых, мелочи, отложенная сцена
//   director.chooseHero(id)               // /persona: { ok, id, name, text } — текст ответа в чат
//   director.report()                     // текст /report для сессии
//   director.current()                    // последний отданный state.json (для тестов)
//
// По желанию: createDirector({ hero: 'talia', choiceAt: 1700000000000 }) — пережить перезапуск:
// восстановить героя и метку последнего выбора из настроек профиля.
//
// Интеграция с файлами питомца (index.js):
//   • alive — режиссёр сам ставит now() в каждом emit, отдельно считать не нужно;
//   • choice.json — читать раз в секунду и отдавать текстом: director.applyChoice(text)
//     (BOM/битый JSON/повтор одного и того же at игнорируются — выбор применится ровно один раз);
//   • settings.json — director.setLang(lang) (или applySettings(text) — разбор BOM и смена языка);
//   • расход контекста — director.measure({ percent, tokens, window, usd }): предупреждение
//     при CONTEXT_WARN и строка расхода в /report;
//   • director.tick({ settings, choice, context }) умеет то же самое, если так удобнее
//     (каждое значение — текст файла или уже разобранный объект);
//   • /persona без аргумента — director.heroNow() (кто сейчас) + director.heroList() (список),
//     heroList(lang) есть и отдельным экспортом; heroReport({ journal, usage, now, heroId, lang })
//     собирает отчёт из чужого журнала, если он понадобится index.js.
//   • launch зовётся на старте и при смене героя командой; call — только после долгого хода;
//     повторный launch в пределах 20 с режиссёр не просит.

import {
  CLIPS,
  COSTARS,
  DEFAULT_HERO,
  FX,
  HEROES,
  TRANSITIONS,
  WORK,
  commandOf,
  cutAt,
  deedLine,
  deedOf,
  failDeed,
  field,
  findHero,
  flatChain,
  heroById,
  inLang,
  isBackground,
  isDanger,
  onceLength,
  pickLine,
  promptDeed,
  report,
  route,
  skipSteps,
  stuntReel,
  summary,
  targetOf,
  usageLine,
} from './mod-data.js'

/** @typedef {{ clip: string, ms?: number }} Part */
/** @typedef {{ kind: string, target: string }} Deed */

// ---- поведенческие константы оригинала (не менять без причины) ----

// Рабочая сцена держится не меньше WORK_HOLD: инструменты меняются чаще, чем глаз успевает понять сцену.
export const WORK_HOLD = 6_000
// Реакция на ошибку (и испуг) — не чаще раза в OOPS_GAP: параллельные инструменты падают пачкой.
export const OOPS_GAP = 4_000
// Позы отдыха: из них герой сам не уходит, пока его не позовут.
const REST = new Set(['idle', 'sit', 'lie', 'sleep'])
// Сколько ждать без дела, прежде чем сесть, лечь и уснуть.
export const SIT_AFTER = 90_000
export const LIE_AFTER = 160_000
export const SLEEP_AFTER = 260_000
// Мелочи без дела: только без хода, после FIDGET_QUIET тишины и не чаще раза в 45–90 секунд.
export const FIDGET_QUIET = 25_000
export const FIDGET_MIN = 45_000
export const FIDGET_MAX = 90_000
// Не в позе отдыха и без хода дольше этого (ход оборвался без конца) — вернуться в стойку.
const STRAY_AFTER = 20_000
// Ход, о конце которого не сообщили, дольше этого без событий не держит героя «занятым».
const BUSY_MAX = 300_000
// Ход короче SHORT_TURN — короткий, дольше LONG_TURN — долгий.
const SHORT_TURN = 15_000
const LONG_TURN = 60_000
// Звать человека, если работа шла дольше CALL_AFTER (он мог уйти в другое окно), но не чаще раза в CALL_GAP.
const CALL_AFTER = 15_000
const CALL_GAP = 8_000
// Предупредить, когда контекст заполнен на CONTEXT_WARN процентов.
export const CONTEXT_WARN = 80
// Отметка «жив» для питомца: без неё он уходит через ~60 с.
const PET_PULSE = 15_000
// Слэш-команда и ход модели сразу за ней: ход человека, а не продолжение.
const COMMAND_GAP = 5_000
// Привет длится ровно до опущенной руки (кадр hello_01), чтобы стойка не начиналась рывком.
const HELLO_MS = 2380
// Повторный запуск питомца сразу за первым (старт сессии и тут же смена героя) только плодит процессы.
const LAUNCH_GAP_MIN = 20_000

// Мелочи без дела: любая, кроме прошлой.
export const FIDGETS = [
  [{ clip: 'stretch' }], [{ clip: 'scratch' }], [{ clip: 'drink' }], [{ clip: 'phone', ms: 5000 }], [{ clip: 'music', ms: 6000 }],
  [{ clip: 'bored', ms: 4000 }], [{ clip: 'sneeze' }], [{ clip: 'fix' }], [{ clip: 'selfie' }], [{ clip: 'laugh' }], [{ clip: 'look' }],
]

// Выбор героя: поклон, привет, стойка (питомец играет то же сам, когда героя выбрали в его меню).
const ENTER = [{ clip: 'bow' }, { clip: 'hello', ms: HELLO_MS }, { clip: 'idle' }]
const AFTER_JOY = [{ clip: 'hello', ms: HELLO_MS }, { clip: 'idle' }]
// Звонок: показать на человека, помахать.
const RING = [{ clip: 'point' }, { clip: 'wave' }, { clip: 'idle' }]
// Реакции на ошибку: по счёту за ход; та же, что только что была, заменяется соседней.
const OOPS = {
  surprised: [{ clip: 'surprised' }, { clip: 'facepalm' }],
  facepalm: [{ clip: 'facepalm' }, { clip: 'sad', ms: 2500 }],
  angry: [{ clip: 'angry' }, { clip: 'sad', ms: 2500 }],
}
const INSTEAD = { surprised: 'facepalm', facepalm: 'angry', angry: 'facepalm' }

// Что герой делает с каким инструментом; прочее — снимает (Талия — взламывает).
const TOOL_CLIPS = {
  Read: 'read', NotebookRead: 'read',
  Grep: 'scan', Glob: 'scan', LS: 'scan', ToolSearch: 'scan',
  WebFetch: 'walk', WebSearch: 'walk',
  Edit: 'type', Write: 'type', MultiEdit: 'type', NotebookEdit: 'type',
  Bash: 'film', PowerShell: 'film',
  Agent: 'phone', Task: 'phone',
}

// Инструменты DSH → словарь мода. Неизвестный инструмент остаётся собой: герой просто «снимает» (film).
const MOD_TOOL = {
  read: 'Read', notebook_read: 'NotebookRead',
  write: 'Write', edit: 'Edit', apply_patch: 'Edit', multi_edit: 'MultiEdit',
  grep: 'Grep', glob: 'Glob', ls: 'LS', tool_search: 'ToolSearch',
  web_fetch: 'WebFetch', web_search: 'WebSearch',
  pwsh: 'PowerShell', powershell: 'PowerShell', bash: 'Bash', shell: 'Bash',
  subagent: 'Agent', subagent_fork: 'Agent', send_message: 'Agent', task: 'Task',
  ask_user_question: 'AskUserQuestion', todo_write: 'TodoWrite', skill: 'Skill',
}
const modTool = (name) => MOD_TOOL[name] ?? name

// Вход инструмента DSH → ключи, которых ждут deedOf/targetOf/isBackground оригинала.
const modInput = (mod, input) => {
  const src = input && typeof input === 'object' ? input : {}
  const path = () => field(src, 'file_path') || field(src, 'path') || field(src, 'notebook_path')
  const firstQuery = () => field(src, 'query') || (Array.isArray(src.queries) ? String(src.queries[0] ?? '') : '')
  switch (mod) {
    case 'Read':
    case 'Write':
    case 'Edit':
    case 'MultiEdit':
    case 'NotebookEdit':
      return { ...src, file_path: path() }
    case 'Grep':
    case 'Glob':
      return { ...src, pattern: field(src, 'pattern') || firstQuery() }
    case 'Bash':
    case 'PowerShell':
      return { ...src, command: field(src, 'command') || field(src, 'cmd') }
    case 'WebSearch':
      return { ...src, query: firstQuery() }
    case 'WebFetch':
      return { ...src, url: field(src, 'url') }
    case 'Agent':
    case 'Task':
      return { ...src, description: field(src, 'description') || field(src, 'subagent_type') || field(src, 'target') }
    default:
      return src
  }
}

// Меню питомца: герои с кадрами, их роль, строка о себе и шкалы.
const roster = (lang) => HEROES.filter(one => one.sprite).map(hero => inLang(hero, lang)).map(one => ({
  id: one.id, sprite: one.sprite, name: one.name, role: one.role ?? '', about: one.about ?? '', stats: one.stats ?? [],
}))
// Реакции на тычок мышью: короткие, стоя, с эффектами.
const POKES = ['laugh', 'wave', 'surprised', 'shrug', 'celebrate'].map(clip => flatChain([{ clip }]).timeline)
const ENTER_STEPS = flatChain(ENTER).timeline

// Первое настоящее действие цепочки — по нему реплика и статус.
const actionOf = (parts) => parts.find(part => !TRANSITIONS.has(part.clip))?.clip ?? 'idle'

const pickOne = (items, random) => items[Math.floor(random() * items.length)]
const between = (min, max, random) => min + random() * (max - min)

const freshDirector = (at = 0) => ({
  clip: 'idle', since: 0, until: 0, lastActivity: at, fidgetAt: 0, fidget: -1,
  parts: [{ clip: 'idle' }], line: '', reaction: '', reactedAt: -OOPS_GAP,
})

// choice.json: строка файла (BOM/битый JSON) или уже разобранный объект { hero, at }.
const parseChoice = (value) => {
  if (value && typeof value === 'object') {
    const at = Number(value.at)

    return typeof value.hero === 'string' && Number.isFinite(at) && at > 0 ? { hero: value.hero, at } : undefined
  }
  if (typeof value !== 'string') {
    return undefined
  }
  try {
    const parsed = JSON.parse(value.replace(/^\uFEFF/, ''))
    const at = Number(parsed?.at)

    return typeof parsed?.hero === 'string' && Number.isFinite(at) && at > 0 ? { hero: parsed.hero, at } : undefined
  } catch {
    return undefined
  }
}

const parseLang = (value, fallback) => {
  if (value && typeof value === 'object') {
    return value.lang === 'en' ? 'en' : 'ru'
  }
  if (typeof value !== 'string') {
    return fallback
  }
  try {
    return JSON.parse(value.replace(/^\uFEFF/, '')).lang === 'en' ? 'en' : 'ru'
  } catch {
    return fallback
  }
}

const EMPTY_USAGE = { percent: 0, tokens: 0, window: 0, usd: 0 }

// Список героев для /persona — на языке настроек питомца.
export const heroList = (lang = 'ru') =>
  HEROES.map(hero => inLang(hero, lang))
    .map(one => `${one.icon} ${one.name}${one.role ? ` (${one.role.toLowerCase()})` : ''} — /persona ${one.aliases[0]}`)
    .join('\n')

// Готовый текст отчёта (то же, что отдаёт director.report()). Для тех, кто хочет собрать отчёт сам.
export const heroReport = ({ journal, usage = EMPTY_USAGE, now = Date.now(), heroId = DEFAULT_HERO, lang = 'ru' } = {}) =>
  report(journal ?? { startedAt: now, turns: 0, entries: [] }, usage, now, inLang(heroById(heroId), lang), lang)

/**
 * Режиссёр одной сессии. Все методы без ожиданий: index.js зовёт их из обработчиков событий.
 * opts — см. шапку файла. Возвращает объект из CONTRACT.md §2.
 */
export const createDirector = (opts = {}) => {
  const {
    now = () => Date.now(),
    random = Math.random,
    log = () => {},
    emit = () => {},
    launch = undefined,
    call = undefined,
    notify = undefined,
    toast = undefined,
    hero: initialHero = undefined,
    choiceAt = 0,
  } = opts

  // ---- состояние сессии (переживает перезапуск хода, сбрасывается в start) ----
  let lang = 'ru'
  let session = ''
  let started = false
  let current = heroById(typeof initialHero === 'string' ? initialHero : DEFAULT_HERO)
  let feeling = 'idle'
  let director = freshDirector(now())
  // Дело, о котором герой говорит, пока идёт ход: настоящий файл, команда, задача.
  let deed
  // Что играет питомец: seq/timeline/loop и напарник в кадре.
  let pet = { seq: '', timeline: [], loop: [['idle_01', 1000]], wrote: 0, costar: undefined }
  let busy = false
  let turnKind = 'none'
  let announced = false
  let commandAt = -COMMAND_GAP
  let owed = false
  let cycleStart = 0
  let lastTurn
  let pending = 0
  let turnErrors = 0
  let journal = { startedAt: 0, turns: 0, entries: [] }
  let lastCall = -CALL_GAP
  let lastCallDecision
  let warnedContext = false
  let lastChoice = Number.isFinite(choiceAt) ? choiceAt : 0
  let settingsText
  let usage = { ...EMPTY_USAGE }
  let ticks = 0
  let launchedAt = -LAUNCH_GAP_MIN
  let lastState

  // ---- реплики ----

  // Реплики к сцене у героя с кадрами, иначе по настроению.
  const linesFor = (who, action, how) => {
    const said = inLang(who, lang)

    return (said.sprite ? said.scenes?.[action] : undefined) ?? said.lines[how]
  }

  const pickN = () => Math.floor(random() * 1000)
  const deedText = () => (deed ? deedLine(current.id, lang, deed, deed.n) : '')

  const tellDeed = (next) => {
    if (!next || (deed && deed.kind === next.kind && deed.target === next.target)) {
      return false
    }
    deed = { ...next, n: pickN() }

    return true
  }
  // Разовая реплика без цели: «жду фон», «остановили».
  const voiced = (kind) => deedLine(current.id, lang, { kind, target: '' }, pickN())
  const spoken = (staging, line) => staging && { ...staging, line }

  // ---- state.json ----

  const buildState = () => {
    const who = inLang(current, lang)

    return {
      seq: pet.seq, session, busy, hero: who.sprite ?? null, name: who.name, role: who.role ?? '',
      timeline: pet.timeline, loop: pet.loop, line: director.line, alive: now(),
      roster: roster(lang), pokes: POKES, pokeLines: who.pokeLines ?? [], pokeMad: who.pokeMad ?? [],
      enter: ENTER_STEPS,
      ...(pet.costar ? { costar: pet.costar } : {}),
    }
  }

  // Новая сцена/реплика: питомец получает полное состояние (index.js пишет state.json).
  const tellPet = () => {
    let state
    try {
      state = buildState()
    } catch (error) {
      log(`edgerunners: state не собрался: ${error?.message ?? error}`)

      return undefined
    }
    lastState = state
    try {
      emit(state)
    } catch (error) {
      log(`edgerunners: emit упал: ${error?.message ?? error}`)
    }

    return state
  }

  // ---- сцена ----

  /**
   * Ставит цепочку: доигрывает начатый жест, от него через переходы — к плану. Одна и та же петля второй раз
   * не ставится: новый seq заставил бы питомца начать её сначала. Возвращает, поставила ли.
   */
  const commit = (at, staging) => {
    const sprite = current.sprite
    const [first] = staging.plan
    const isSame = staging.plan.length === 1 && first !== undefined && first.ms === undefined && first.clip === director.clip
    if (!staging.force && isSame && at >= director.until) {
      return false
    }
    const cut = staging.keep === false
      ? { keep: [], offset: 0, from: staging.from ?? director.clip, held: WORK_HOLD }
      : cutAt(director.parts, at - director.since, sprite)
    const next = route(cut.from, staging.plan, cut.held < WORK_HOLD)
    const parts = [...cut.keep, ...next]
    const last = parts[parts.length - 1]
    const since = at - cut.offset
    director = {
      ...director,
      clip: last.clip,
      since,
      until: since + onceLength(parts, sprite),
      parts,
      line: staging.line
        || (staging.say === undefined && busy ? deedText() : '')
        || pickLine(linesFor(current, staging.say ?? actionOf(next), feeling), director.line),
    }
    const chain = flatChain(parts, sprite)
    // Напарник в кадре: Роман в сцене film снимает трюки Талии.
    const costar = sprite ? COSTARS[sprite] : undefined
    const partner = costar?.scene === last.clip ? HEROES.find(one => one.sprite === costar.partner) : undefined
    pet = {
      seq: `${at}-${Math.round(random() * 1e6)}`,
      timeline: skipSteps(chain.timeline, cut.offset + (staging.skip ?? 0)),
      loop: chain.loop,
      wrote: at,
      costar: costar && partner ? { sprite: costar.partner, name: inLang(partner, lang).name, stunts: stuntReel(costar.stunts, random) } : undefined,
    }

    return true
  }

  /**
   * Решает и ставит сцену. decide получает время и смотрит на режиссёра в тот же миг, когда сцена записывается.
   * touch — это событие от модели: отсчёт «без дела» начинается заново.
   */
  const stage = (decide, touch = false) => {
    const at = now()
    if (touch) {
      director.lastActivity = at
    }
    const staging = decide(at)
    if (!staging || !commit(at, staging)) {
      return false
    }
    tellPet()

    return true
  }

  // Новая реплика без новой сцены: seq прежний, цепочка питомца не начинается заново.
  const sayLine = (line) => {
    if (!line) {
      return false
    }
    director = { ...director, line }
    tellPet()

    return true
  }

  // Отдыхал — сперва потянется.
  const wake = (plan) => (director.clip === 'sleep' || director.clip === 'lie' ? [{ clip: 'stretch' }, ...plan] : [...plan])

  // Стойка без лишних жестов.
  const calm = () => ({ plan: [{ clip: 'idle' }] })

  /**
   * Рабочая сцена по инструменту. Та же, что идёт или к которой идём, — ничего. Цепочка идёт к рабочей сцене,
   * которая ещё не отдержалась WORK_HOLD (с момента, как началась её петля), — новая ждёт своей очереди.
   */
  const workPlan = (at, clip) => {
    if (director.clip === clip) {
      director.want = undefined

      return undefined
    }
    if (WORK.has(director.clip) && at - director.until < WORK_HOLD) {
      director.want = clip

      return undefined
    }
    director.want = undefined

    return { plan: [{ clip }] }
  }

  // Реакция на ошибку с хвостом tail: не чаще OOPS_GAP и не та же, что была только что.
  const oopsPlan = (at, tail) => {
    if (at - director.reactedAt < OOPS_GAP) {
      return undefined
    }
    const due = turnErrors <= 1 ? 'surprised' : turnErrors === 2 ? 'facepalm' : 'angry'
    const key = due === director.reaction ? INSTEAD[due] : due
    director.reaction = key
    director.reactedAt = at
    director.want = undefined

    return { plan: [...OOPS[key], ...tail] }
  }

  // Радость по длине работы; кивку реплики берутся от радости, а не от «понял».
  const joy = (ms) => {
    if (ms > LONG_TURN) {
      return { plan: [{ clip: 'happy' }, { clip: 'dance', ms: 5000 }] }
    }
    if (ms > SHORT_TURN) {
      return { plan: [{ clip: 'happy' }, { clip: 'celebrate' }] }
    }
    const clip = pickOne(['celebrate', 'clap', 'nod'], random)

    return { plan: [{ clip }], say: clip === 'nod' ? 'happy' : undefined }
  }

  // Мелочь без дела: любая, кроме прошлой.
  const fidget = () => {
    const options = FIDGETS.map((_, i) => i).filter(i => i !== director.fidget)
    const i = pickOne(options, random)
    director.fidget = i

    return [...FIDGETS[i], { clip: 'idle' }]
  }

  // Что делать без дела: сесть, лечь, уснуть, мелочь, а застрявшего — вернуть в стойку. Во время хода — ничего.
  const idlePlan = (at) => {
    if (busy || at < director.until) {
      return undefined
    }
    const idle = at - director.lastActivity
    const clip = director.clip
    if (clip === 'idle' && idle > SIT_AFTER) {
      return { plan: [{ clip: 'sit' }] }
    }
    if (clip === 'sit' && idle > LIE_AFTER) {
      return { plan: [{ clip: 'yawn' }, { clip: 'lie' }] }
    }
    if (clip === 'lie' && idle > SLEEP_AFTER) {
      return { plan: [{ clip: 'sleep' }] }
    }
    if (clip === 'idle' && idle >= FIDGET_QUIET && at >= director.fidgetAt) {
      director.fidgetAt = at + between(FIDGET_MIN, FIDGET_MAX, random)

      return { plan: fidget() }
    }
    if (!REST.has(clip) && idle > STRAY_AFTER) {
      return calm()
    }

    return undefined
  }

  // Та же сцена новым героем, сразу: без входа, без доигрыша и без ожидания удержания.
  const recast = () => ({ plan: [{ clip: director.clip }], keep: false, force: true })

  // ---- герой ----

  const setMood = (next) => {
    feeling = next
  }

  const setHero = (id) => {
    current = heroById(id)
  }

  // Запускает питомца отдельной программой: решает режиссёр, запускает index.js (opts.launch).
  const launchPet = () => {
    const at = now()
    if (at - launchedAt < LAUNCH_GAP_MIN) {
      return false
    }
    launchedAt = at
    if (typeof launch === 'function') {
      try {
        launch({ hero: current.sprite, id: current.id })
      } catch (error) {
        log(`edgerunners: запуск питомца не удался: ${error?.message ?? error}`)
      }
    }

    return true
  }

  /**
   * Смена героя. options.fromPet — сколько миллисекунд назад героя выбрали в меню питомца
   * (тот уже сам играет вход); options.at — метка выбора для дедупликации (choice.json).
   */
  const chooseHero = (word, options = {}) => {
    const settings = typeof options === 'number' ? { fromPet: options } : (options ?? {})
    const picked = findHero(String(word ?? ''))
    if (!picked) {
      return {
        ok: false, name: '', applied: false,
        text: lang === 'en' ? `No character "${word}". Available:\n${heroList(lang)}` : `Не знаю персонажа «${word}». Есть:\n${heroList(lang)}`,
      }
    }
    setHero(picked.id)
    if (busy) {
      // Ход идёт: герой меняется сразу и продолжает ту же работу; ход идёт дальше как шёл.
      stage(recast)
    } else if (settings.fromPet === undefined) {
      setMood('happy')
      stage(() => ({ plan: ENTER }), true)
    } else {
      // Питомец начал вход прямо с поклона, без переходов: продолжаем с того же места.
      setMood('happy')
      stage(() => ({ plan: ENTER, keep: false, from: 'idle', skip: settings.fromPet }), true)
    }
    if (settings.fromPet === undefined && current.sprite) {
      launchPet()
    }
    const named = inLang(picked, lang)

    return {
      ok: true, id: picked.id, name: named.name, applied: true,
      text: lang === 'en' ? `${picked.faces.happy} ${named.name} is online!` : `${picked.faces.happy} ${named.name} на связи!`,
    }
  }

  // ---- события ----

  // Ход начался: человеку — кивнуть, присмотреться, задуматься; уведомлению — задуматься без «понял»;
  // продолжению — не дёргаться (только встать, если отдыхал).
  const openTurn = (kind) => {
    const wasBusy = busy
    turnKind = kind
    busy = true
    lastTurn = undefined
    director.want = undefined
    let staged = false
    if (kind === 'user') {
      pending = 0
      turnErrors = 0
      director.reaction = ''
      owed = true
      cycleStart = now()
      journal.turns += 1
      setMood('think')
      staged = stage(() => ({ plan: wake([{ clip: 'nod' }, { clip: 'curious', ms: 1500 }, { clip: 'think' }]) }), true)
    } else if (kind === 'notice') {
      setMood('think')
      staged = stage(() => ({ plan: wake([{ clip: 'think' }]) }), true)
    } else {
      staged = stage(() => (REST.has(director.clip) && director.clip !== 'idle' ? { plan: wake([{ clip: 'think' }]) } : undefined), true)
    }
    if (!staged && deed) {
      sayLine(deedText())
    } else if (!staged && !wasBusy) {
      tellPet()
    }
  }

  const startTurn = (event) => {
    const text = typeof event?.text === 'string' ? event.text : ''
    const machine = event?.machine === true
    // Слэш-команда сама показывает своё (/persona — вход героя); если за ней пойдёт ход модели, его откроет turnStart.
    if (event?.command) {
      commandAt = now()

      return
    }
    if (machine) {
      pending = Math.max(0, pending - 1)
    }
    announced = true
    deed = undefined
    tellDeed(machine ? { kind: 'notice', target: '' } : promptDeed(text))
    openTurn(machine ? 'notice' : 'user')
  }

  // turn.start без prompt.submit: ход сразу за слэш-командой или продолжение.
  const beginTurn = () => {
    if (announced) {
      announced = false

      return
    }
    if (busy && turnKind !== 'none') {
      return
    }
    const at = now()
    const isCommand = at - commandAt < COMMAND_GAP
    commandAt = -COMMAND_GAP
    openTurn(isCommand ? 'user' : 'continuation')
  }

  // Короткая фраза из инструмента DSH, у которого нет «дела» в словаре мода: план, навык, файлы,
  // сценарий, фоновая задача, вопрос человеку. Возвращает готовую реплику или undefined.
  const cut = (value, limit = 64) => {
    const text = String(value ?? '').replace(/\s+/g, ' ').trim()
    if (!text) return ''
    return text.length > limit ? `${text.slice(0, limit - 1)}…` : text
  }
  const specialLine = (rawName, rawInput) => {
    const src = rawInput && typeof rawInput === 'object' ? rawInput : {}
    const ru = lang !== 'en'
    switch (rawName) {
      case 'todo_write': {
        const todos = Array.isArray(src.todos) ? src.todos : []
        if (todos.length === 0) return undefined
        const current = todos.find(item => item?.status === 'in_progress') ?? todos[0]
        const title = cut(current?.content, 56)
        return ru
          ? `План: ${todos.length} шаг(ов)${title ? `, сейчас «${title}»` : ''}`
          : `Plan: ${todos.length} step(s)${title ? `, now “${title}”` : ''}`
      }
      case 'skill': {
        const name = cut(field(src, 'name') || field(src, 'skill'), 40)
        return name ? (ru ? `Открываю навык: ${name}` : `Loading skill: ${name}`) : undefined
      }
      case 'present': {
        const files = Array.isArray(src.files) ? src.files.length : 0
        return files
          ? (ru ? `Отдаю файлы (${files})` : `Delivering files (${files})`)
          : (ru ? 'Отдаю результат' : 'Delivering the result')
      }
      case 'workflow': {
        const name = cut(src?.meta?.name || src?.description, 40)
        return ru ? `Запускаю сценарий${name ? `: ${name}` : ''}` : `Running a workflow${name ? `: ${name}` : ''}`
      }
      case 'job_output':
      case 'job_list':
      case 'job_kill':
        return ru ? 'Проверяю фоновые задачи' : 'Checking background jobs'
      case 'schedule_create': {
        const title = cut(field(src, 'title'), 48)
        return ru ? `Ставлю напоминание${title ? `: ${title}` : ''}` : `Scheduling a reminder${title ? `: ${title}` : ''}`
      }
      case 'schedule_list':
      case 'schedule_view':
        return ru ? 'Смотрю расписание' : 'Looking at the schedule'
      case 'schedule_edit':
        return ru ? 'Правлю расписание' : 'Editing the schedule'
      case 'exit_plan_mode':
        return ru ? 'Показываю план на проверку' : 'Presenting the plan for review'
      case 'working_directory': {
        const dir = cut(field(src, 'cd') || field(src, 'path'), 40)
        return dir ? (ru ? `Меняю папку: ${dir}` : `Changing directory: ${dir}`) : undefined
      }
      case 'create_worktree': {
        const name = cut(field(src, 'name'), 32)
        return ru ? `Создаю ветку${name ? `: ${name}` : ''}` : `Creating a worktree${name ? `: ${name}` : ''}`
      }
      case 'read_image':
        return ru ? 'Разглядываю картинку' : 'Looking at an image'
      case 'load_workspace_dependencies':
        return ru ? 'Проверяю окружение' : 'Checking the environment'
      case 'ask_user_question': {
        const questions = Array.isArray(src.questions) ? src.questions : []
        const text = cut(questions[0]?.question, 72)
        return ru ? `Спрашиваю тебя${text ? `: «${text}»` : ''}` : `Asking you${text ? `: “${text}”` : ''}`
      }
      default:
        return undefined
    }
  }

  // Тематические реплики о настоящей работе: гиты, PHP, Битрикс, Figma, вёрстка, CSS и консоль.
  // Возвращает готовую строку или undefined (тогда работает общий словарь дел).
  const techLine = (rawName, rawInput) => {
    const src = rawInput && typeof rawInput === 'object' ? rawInput : {}
    const ru = lang !== 'en'
    const name = String(rawName ?? '').toLowerCase()
    const filePath = String(field(src, 'file_path') || field(src, 'path') || field(src, 'notebook_path') || '')
    const file = filePath.split(/[\\/]/).pop() || filePath
    const lower = filePath.toLowerCase()
    const readish = name === 'read' || name === 'notebook_read'
    const editish = ['edit', 'write', 'apply_patch', 'multi_edit', 'notebook_edit'].includes(name)
    if ((readish || editish) && file) {
      const doing = ru ? (readish ? 'смотрю' : 'правлю') : (readish ? 'reading' : 'editing')
      if (/bitrix|component\.php|template\.php|\.bitrix|php_interface/i.test(lower)) {
        return ru ? `Битрикс: ${doing} ${cut(file, 42)}` : `Bitrix: ${doing} ${cut(file, 42)}`
      }
      if (/\.php$/i.test(file)) {
        return ru ? `PHP: ${doing} ${cut(file, 42)}` : `PHP: ${doing} ${cut(file, 42)}`
      }
      if (/\.(css|scss|sass|less)$/i.test(file)) {
        return ru ? `CSS: ${doing} ${cut(file, 42)}` : `CSS: ${doing} ${cut(file, 42)}`
      }
      if (/\.(html?|twig|hbs|pug|vue|svelte|astro)$/i.test(file)) {
        return ru ? `Вёрстка: ${doing} ${cut(file, 42)}` : `Markup: ${doing} ${cut(file, 42)}`
      }
    }
    if (name === 'pwsh' || name === 'powershell' || name === 'bash' || name === 'shell') {
      const cmd = String(field(src, 'command') || field(src, 'cmd') || '').trim()
      if (cmd) {
        const git = /(^|[;&|]\s*)git\s+([a-z-]+)/i.exec(cmd)
        if (git) {
          const map = {
            status: 'смотрю статус', diff: 'смотрю дифф', log: 'смотрю историю', add: 'индексирую',
            commit: 'коммичу', push: 'пушу', pull: 'подтягиваю', fetch: 'обновляю', checkout: 'переключаю ветку',
            switch: 'переключаю ветку', branch: 'смотрю ветки', merge: 'сливаю', rebase: 'переношу коммиты',
            stash: 'прячу изменения', clone: 'клонирую', reset: 'откатываю', tag: 'ставлю тег', remote: 'смотрю remote',
          }
          const verb = git[2].toLowerCase()
          return ru ? `Гит: ${map[verb] || `git ${verb}`}` : `Git: ${verb}`
        }
        const composer = /composer\s+([a-z-]+)/i.exec(cmd)
        if (composer) {
          const map = {
            install: 'ставлю зависимости', require: 'добавляю зависимость', update: 'обновляю зависимости',
            'dump-autoload': 'пересобираю autoload', 'dump-autoload-o': 'пересобираю autoload', test: 'запускаю тесты',
          }
          const verb = composer[1].toLowerCase()
          return ru ? `Composer: ${map[verb] || verb}` : `Composer: ${verb}`
        }
        if (/bitrix|1c-bitrix|bvt/i.test(cmd)) {
          const phpScript = /^php\b/i.test(cmd)
          return ru
            ? phpScript ? 'Битрикс: запускаю php-скрипт' : 'Битрикс: запускаю сборку/миграции'
            : phpScript ? 'Bitrix: running a PHP script' : 'Bitrix: running a build/migration'
        }
        const php = /^php\s+(?:-f\s+)?(\S+)/i.exec(cmd)
        if (php) {
          return ru ? `PHP: запускаю ${cut(php[1], 40)}` : `PHP: running ${cut(php[1], 40)}`
        }
        if (/^(npm|pnpm|yarn|bun)\b/i.test(cmd)) {
          return ru ? `Консоль: ${cut(cmd, 48)}` : `Console: ${cut(cmd, 48)}`
        }
        if (/^docker(-compose)?\b/i.test(cmd)) {
          const rest = cmd.replace(/^docker(-compose)?\s*/i, '') || cmd
          return ru ? `Докер: ${cut(rest, 44)}` : `Docker: ${cut(rest, 44)}`
        }
        if (/^(node|npx|python|py|go|cargo|dotnet|make|composer)\b/i.test(cmd)) {
          return ru ? `Консоль: ${cut(cmd, 48)}` : `Console: ${cut(cmd, 48)}`
        }

        return ru ? `Консоль: ${cut(cmd, 48)}` : `Console: ${cut(cmd, 48)}`
      }
    }
    if (name.includes('figma') || /figma\.com/i.test(JSON.stringify(src))) {
      if (/get|read|list|view|fetch|download|export/i.test(name)) {
        return ru ? 'Фигма: смотрю макет' : 'Figma: reading the design'
      }
      if (/create|update|edit|write|post|set|add|move|delete/i.test(name)) {
        return ru ? 'Фигма: правлю макет' : 'Figma: editing the design'
      }

      return ru ? 'Фигма: работаю с макетом' : 'Figma: working on the design'
    }

    return undefined
  }

  // Инструмент: герой занят им; опасная команда сперва пугает.
  const startTool = (rawName, rawInput) => {
    const mod = modTool(rawName)
    const input = modInput(mod, rawInput)
    setMood('work')
    const clip = TOOL_CLIPS[mod] ?? 'film'
    const isScary = isDanger(commandOf(mod, input))
    const isNew = tellDeed(deedOf(mod, input))
    const staged = stage(at => {
      if (isScary && director.reaction !== 'scared' && at - director.reactedAt >= OOPS_GAP) {
        director.reaction = 'scared'
        director.reactedAt = at
        director.want = undefined

        return { plan: [{ clip: 'scared' }, { clip }] }
      }

      return workPlan(at, clip)
    }, true)
    // Реплика осмысленнее общего «дела», когда у инструмента есть свой смысл (гит, PHP, Figma, план…).
    const special = techLine(rawName, rawInput) || specialLine(rawName, rawInput)
    if (special) {
      sayLine(special)
    } else if (isNew && !staged) {
      // Сцена держится (WORK_HOLD), а дело уже другое — реплика меняется и без новой сцены.
      sayLine(deedText())
    }
  }

  // Внимание: позвать человека (вопрос, подтверждение, звонок). Реплика ставится сразу, а если герой
  // отдыхал — он ещё и встаёт, показывает рукой и возвращается в стойку.
  const attention = (text) => {
    const line = typeof text === 'string' ? text.replace(/\s+/g, ' ').trim() : ''
    if (!line) return false
    const resting = director.clip === 'sleep' || director.clip === 'lie' || director.clip === 'sit'
    if (!busy && resting) {
      stage(() => ({ plan: wake([{ clip: 'point' }, { clip: 'idle' }]), line }), true)
    } else {
      director = { ...director, line }
      tellPet()
    }

    return true
  }

  const failTool = (rawName, rawInput) => {
    const mod = modTool(rawName)
    const input = modInput(mod, rawInput)
    turnErrors += 1
    setMood('sad')
    const isNew = tellDeed(failDeed(deedOf(mod, input)))
    const staged = stage(at => oopsPlan(at, [{ clip: 'think' }]), true)
    if (isNew && !staged) {
      sayLine(deedText())
    }
  }

  /**
   * Конец хода. Своя просьба человека (или ход без начала) — реакция: радость по длине работы, прерван —
   * пожать плечами, ошибка — как ошибка; долгая работа — звонок. Радость играет один раз за просьбу:
   * пока фоновые задачи не отчитались, она ждёт.
   */
  const finishTurn = (event) => {
    const turnId = event?.turnId
    if (turnId !== undefined && turnId === lastTurn) {
      return
    }
    if (turnId !== undefined) {
      lastTurn = turnId
    }
    const kind = turnKind
    const isOwn = kind === 'user' || kind === 'none'
    turnKind = 'none'
    announced = false
    busy = false
    deed = undefined
    director.want = undefined
    const reason = event?.reason ?? 'answer'
    const answer = typeof event?.answer === 'string' ? event.answer : ''
    let staged = false
    let said = ''
    if (reason === 'error') {
      owed = false
      turnErrors += 1
      setMood('sad')
      said = voiced('crash')
      staged = stage(at => spoken(oopsPlan(at, [{ clip: 'idle' }]) ?? calm(), said), true)
    } else if (reason !== 'answer') {
      owed = false
      setMood(reason === 'refusal' ? 'sad' : 'idle')
      said = isOwn ? voiced('stopped') : ''
      staged = stage(() => spoken(isOwn ? { plan: [{ clip: 'shrug' }, { clip: 'idle' }] } : calm(), said), true)
    } else if (!isOwn && !owed) {
      setMood('idle')
      staged = stage(calm, true)
    } else if (pending > 0) {
      // Агенты и фоновые команды ещё работают: ход кончился на «работа идёт» — кивнуть и ждать их отчёта.
      owed = true
      setMood('work')
      said = voiced('wait')
      staged = stage(() => spoken(isOwn ? { plan: [{ clip: 'nod' }, { clip: 'phone', ms: 3000 }, { clip: 'idle' }], say: 'phone' } : calm(), said), true)
    } else {
      owed = false
      setMood('happy')
      const at = now()
      const worked = Math.max(Number(event?.durationMs) || 0, cycleStart > 0 ? at - cycleStart : 0)
      cycleStart = 0
      const ringing = worked > CALL_AFTER && at - lastCall >= CALL_GAP
      const result = joy(worked)
      said = summary(answer, lang)
      staged = stage(() => ({
        plan: [...result.plan, ...(ringing ? RING : AFTER_JOY)],
        say: ringing ? 'point' : result.say,
        line: said,
      }), true)
      if (ringing) {
        callYou(summary(answer, lang))
      }
    }
    // Сцена та же, а ход кончился: питомец должен узнать, что сессия свободна.
    if (!staged && said) {
      sayLine(said)
    } else if (!staged) {
      tellPet()
    }
  }

  // Входящий вызов: решает режиссёр, показывает index.js (opts.call). Герои без кадров шлют уведомление.
  const callYou = (text) => {
    const at = now()
    if (at - lastCall < CALL_GAP) {
      return undefined
    }
    lastCall = at
    const named = inLang(current, lang)
    if (!current.sprite) {
      if (typeof notify === 'function') {
        try {
          notify({ title: `${current.icon} ${named.name}`, text })
        } catch (error) {
          log(`edgerunners: notify упал: ${error?.message ?? error}`)
        }
      }

      return undefined
    }
    const decision = { hero: current.sprite, id: current.id, name: named.name, icon: current.icon, text }
    lastCallDecision = decision
    if (typeof call === 'function') {
      try {
        call(decision)
      } catch (error) {
        log(`edgerunners: звонок не показался: ${error?.message ?? error}`)
      }
    } else {
      log(`edgerunners: звонок — ${decision.name}: ${text}`)
    }

    return decision
  }

  // ---- файлы питомца и расход контекста ----

  // settings.json: язык; сменился — та же сцена, но реплика и подписи уже на новом языке.
  const applySettings = (value) => {
    if (typeof value !== 'string' && (!value || typeof value !== 'object')) {
      return false
    }
    const key = typeof value === 'string' ? value : JSON.stringify(value)
    if (key === settingsText) {
      return false
    }
    const first = settingsText === undefined
    settingsText = key
    const next = parseLang(value, lang)
    if (next === lang) {
      return false
    }
    lang = next
    if (!first && started) {
      // Язык сменился: та же сцена, но реплика и подписи уже на новом языке.
      stage(recast)
    } else if (started) {
      // Первое чтение настроек: сцену не перезапускаем, но имя и меню обновляем сразу.
      tellPet()
    }

    return true
  }

  const setLang = (next) => {
    const value = next === 'en' ? 'en' : 'ru'
    if (value === lang) {
      return false
    }
    lang = value
    if (started) {
      stage(recast)
    }

    return true
  }

  // choice.json: новый выбор — новый at; тот же at дважды не применяется.
  const applyChoice = (value) => {
    const choice = parseChoice(value)
    if (!choice || choice.at <= lastChoice) {
      return { ok: false, applied: false }
    }
    lastChoice = choice.at
    // Герой без кадров из меню питомца не выбирается.
    const picked = HEROES.find(one => one.sprite && (one.id === choice.hero || one.sprite === choice.hero))
    if (!picked) {
      return { ok: false, applied: false }
    }

    return { ...chooseHero(picked.id, { fromPet: Math.max(0, now() - choice.at) }), applied: true }
  }

  const setChoiceAt = (at) => {
    if (Number.isFinite(at) && at > lastChoice) {
      lastChoice = at
    }
  }

  // session.measure: расход контекста; предупреждение — один раз на порог.
  const measure = (context) => {
    if (!context || typeof context !== 'object') {
      return usage
    }
    const percent = Number(context.percent) || 0
    usage = {
      percent,
      tokens: Number(context.tokens) || 0,
      window: Number(context.window) || 0,
      usd: Number(context.usd) || 0,
    }
    if (percent >= CONTEXT_WARN && !warnedContext) {
      warnedContext = true
      const message = lang === 'en'
        ? `Context is ${Math.round(percent)}% full — time to /compact`
        : `Контекст заполнен на ${Math.round(percent)}% — пора набрать /compact`
      if (typeof toast === 'function') {
        try {
          toast(message)
        } catch (error) {
          log(`edgerunners: toast упал: ${error?.message ?? error}`)
        }
      } else {
        log(message)
      }
    }
    if (percent < CONTEXT_WARN - 20) {
      warnedContext = false
    }

    return usage
  }

  // ---- тик ----

  const restOnce = (poll) => {
    ticks += 1
    if (poll && typeof poll === 'object') {
      // index.js может отдать как текст файла, так и уже разобранное значение.
      if (poll.settings !== undefined) {
        applySettings(poll.settings)
      }
      if (poll.choice !== undefined) {
        applyChoice(poll.choice)
      }
      if (poll.context) {
        measure(poll.context)
      }
    }
    const at = now()
    if (busy && at - director.lastActivity >= BUSY_MAX) {
      busy = false
      turnKind = 'none'
      pet.wrote = 0
    }
    if (at - pet.wrote >= PET_PULSE) {
      pet.wrote = at
      tellPet()
    }
    const want = director.want
    if (want) {
      stage(when => (director.want === want ? workPlan(when, want) : undefined))
    } else if (!busy && at >= director.until) {
      stage(idlePlan)
    }
  }

  // ---- старт и отчёт ----

  const start = () => {
    started = true
    const at = now()
    director = freshDirector(at)
    journal = { startedAt: at, turns: 0, entries: [] }
    pending = 0
    turnErrors = 0
    busy = false
    turnKind = 'none'
    announced = false
    owed = false
    cycleStart = 0
    deed = undefined
    lastTurn = undefined
    pet = { seq: '', timeline: [], loop: [['idle_01', 1000]], wrote: 0, costar: undefined }
    setMood('idle')
    stage(() => ({ plan: [{ clip: 'wave' }, { clip: 'idle' }] }))
    if (current.sprite) {
      launchPet()
    }

    return lastState
  }

  // /report: что модель сделала за сессию — от имени текущего героя.
  const answerReport = () => {
    try {
      return heroReport({ journal, usage, now: now(), heroId: current.id, lang })
    } catch (error) {
      log(`edgerunners: отчёт не собрался: ${error?.message ?? error}`)

      return 'Отчёт не собрался — попробуй ещё раз.'
    }
  }

  // Герой — украшение: его сбой не должен ломать ход. События заворачиваем в предохранитель.
  const guard = (fn) => (event) => {
    try {
      return fn(event)
    } catch (error) {
      log(`edgerunners: ${error?.stack ?? error}`)

      return undefined
    }
  }

  return {
    // --- контракт CONTRACT.md §2 ---
    setSession: (id) => {
      session = typeof id === 'string' ? id : String(id ?? '')
    },
    setLang: guard(setLang),
    start: guard(() => start()),
    promptSubmit: guard(startTurn),
    turnStart: guard(() => beginTurn()),
    toolCall: guard(event => startTool(event?.name, event?.input)),
    toolResult: guard(event => {
      const mod = modTool(event?.name)
      const input = modInput(mod, event?.input)
      const isError = event?.isError === true
      const background = event?.background === undefined
        ? isBackground(mod, input, event?.result)
        : event.background === true
      journal.entries.push({ tool: mod, target: targetOf(input), isError })
      if (!isError && background) {
        pending += 1
      }
      if (isError) {
        failTool(mod, input)
      }
    }),
    turnComplete: guard(finishTurn),
    tick: guard(restOnce),
    chooseHero: (id, options) => {
      try {
        return chooseHero(id, options)
      } catch (error) {
        log(`edgerunners: смена героя упала: ${error?.stack ?? error}`)

        return { ok: false, name: '', applied: false, text: 'Не получилось сменить героя.' }
      }
    },
    report: () => answerReport(),
    current: () => lastState,

    // --- дополнительные методы для index.js (см. шапку) ---
    measure,
    applyChoice,
    applySettings,
    setChoiceAt,
    // Позвать человека: вопрос модели или подтверждение операции. Ставит реплику герою и, если он
    // отдыхал, поднимает его с жестом. index.js дополнительно показывает «звонок».
    attention: (text) => {
      try {
        return attention(text)
      } catch (error) {
        log(`edgerunners: attention упал: ${error?.stack ?? error}`)

        return false
      }
    },
    heroList: () => heroList(lang),
    language: () => lang,
    // Последнее решение о звонке (для логов/тестов): { hero, id, name, icon, text }.
    lastCall: () => lastCallDecision,
    // Кто сейчас герой (копия справочных полей; style — для prompt.compose).
    heroNow: () => {
      const who = inLang(current, lang)

      return {
        id: who.id, name: who.name, icon: who.icon, role: who.role ?? '', sprite: who.sprite,
        face: who.faces?.[feeling] ?? '', style: who.style ?? '',
      }
    },
  }
}

// Наборы клипов и подпись о контексте — чтобы index.js мог их показать, а тесты проверить.
export { CLIPS, FX, TRANSITIONS, WORK, DEFAULT_HERO, usageLine }
