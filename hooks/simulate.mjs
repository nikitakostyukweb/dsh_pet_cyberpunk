// Headless-проверка режиссёра EdgeRunners: гоняет сессию на фейковых часах и проверяет, что
// состояние питомца (state.json) валидно, кадры существуют, а поведение совпадает с оригиналом.
//
//   node edgerunners-dsh/hooks/simulate.mjs        (обычный node, без TS и зависимостей)
//
// Проверяется: старт сессии, просьба человека, busy между submit и turnComplete, смены сцены по
// инструментам (read/edit/pwsh), испуг на опасной команде, реакции на ошибки, конец хода, покой
// (sit → lie → sleep), отметка «жив», выбор героя из choice.json (ровно один раз), звонок после
// долгого хода, /report и смена языка. Кадры сверяются с _research/edgerunners/assets/pet/<hero>/.

import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import { createDirector, DEFAULT_HERO, FIDGET_QUIET, SIT_AFTER, LIE_AFTER, SLEEP_AFTER } from './director.js'
import { CLIPS, COSTARS, FX, HEROES, STUNTS, WORK, heroFrames, heroById } from './mod-data.js'

const here = dirname(fileURLToPath(import.meta.url))
const root = resolve(here, '..', '..')
// Кадры и трюки проверяем по read-only копии оригинала: assets/ в пакете копирует другой тест.
const PET = join(root, '_research', 'edgerunners', 'assets', 'pet')

let passed = 0
const failures = []
const notes = []

const check = (name, fn) => {
  try {
    const detail = fn()
    passed += 1
    notes.push(`PASS  ${name}${detail === undefined ? '' : ` — ${detail}`}`)
  } catch (error) {
    failures.push(`${name}: ${error?.message ?? error}`)
    notes.push(`FAIL  ${name} — ${error?.message ?? error}`)
  }
}

const assert = (condition, message) => {
  if (!condition) {
    throw new Error(message)
  }

  return true
}
const eq = (actual, expected, message) => {
  const a = JSON.stringify(actual)
  const b = JSON.stringify(expected)
  assert(a === b, `${message}: получено ${a}, ждали ${b}`)
}
const includes = (haystack, needle, message) => assert(String(haystack).includes(needle), `${message}: «${haystack}» не содержит «${needle}»`)
const oneOf = (value, list, message) => assert(list.includes(value), `${message}: ${JSON.stringify(value)} не из ${JSON.stringify(list)}`)

// ---- фейковые часы и детерминированный random ----

let clock = Date.UTC(2026, 0, 1, 12, 0, 0)
const now = () => clock
// mulberry32: стабильная последовательность, чтобы PASS был воспроизводим.
const seeded = (seed) => () => {
  seed = (seed + 0x6d2b79f5) | 0
  let x = Math.imul(seed ^ (seed >>> 15), 1 | seed)
  x = (x + Math.imul(x ^ (x >>> 7), 61 | x)) ^ x

  return ((x ^ (x >>> 14)) >>> 0) / 4294967296
}
const random = seeded(20261111)

const states = []
const launches = []
const calls = []
const toasts = []
const logs = []
const director = createDirector({
  now,
  random,
  emit: state => states.push(state),
  launch: info => launches.push(info),
  call: decision => calls.push(decision),
  toast: message => toasts.push(message),
  log: message => logs.push(message),
})

// Раз в секунду — director.tick(), как в index.js. poll отдаём текстом, как файлы.
const advance = (ms, poll) => {
  let left = ms
  while (left > 0) {
    const step = Math.min(1000, left)
    clock += step
    director.tick(poll)
    left -= step
  }
}

const last = () => states[states.length - 1]
const lastClip = () => last()?.loop?.[0]?.[0]?.replace(/_\d+$/, '') ?? ''
// Кадры без переходов (камера, подбородок, сесть-лечь-уснуть): с чего начинается само действие.
const actionFrames = (state) => [...(state?.timeline ?? []), ...(state?.loop ?? [])]
  .map(s => s[0])
  .filter(f => !/^(rc|tc|sd|ld|fa)_/.test(f))

// ---- проверка кадров ----

const frameCache = new Map()
const framesOfHero = (sprite) => {
  if (!frameCache.has(sprite)) {
    const dir = join(PET, sprite)
    const set = new Set(existsSync(dir) ? readdirSync(dir).filter(file => file.endsWith('.png')).map(file => file.slice(0, -4)) : [])
    frameCache.set(sprite, set)
  }

  return frameCache.get(sprite)
}
const missingFrames = (sprite, frames) => [...new Set(frames)].filter(frame => !framesOfHero(sprite).has(frame))

const isStep = (step) => Array.isArray(step)
  && step.length >= 2 && step.length <= 3
  && typeof step[0] === 'string' && step[0].length > 0
  && Number.isFinite(step[1]) && step[1] > 0
  && (step.length === 2 || FX.includes(step[2]))

// Проверка одного отданного state.json по CONTRACT.md §1 + существование кадров.
const stepFrames = (state) => [
  ...state.timeline.flatMap(step => [step[0]]),
  ...state.loop.flatMap(step => [step[0]]),
  ...state.enter.flatMap(step => [step[0]]),
  ...state.pokes.flatMap(reel => reel.flatMap(step => [step[0]])),
]

const validateState = (state) => {
  assert(state && typeof state === 'object', 'state не объект')
  assert(typeof state.seq === 'string' && state.seq.length > 0, 'seq пустой')
  assert(typeof state.session === 'string' && state.session.length > 0, 'session пустой')
  assert(typeof state.busy === 'boolean', 'busy не boolean')
  assert(state.hero === null || typeof state.hero === 'string', 'hero не строка/null')
  assert(typeof state.name === 'string' && state.name.length > 0, 'name пустой')
  assert(typeof state.role === 'string', 'role не строка')
  assert(typeof state.line === 'string', 'line не строка')
  assert(Number.isFinite(state.alive) && state.alive <= now() && state.alive > 0, 'alive не число/из будущего')
  assert(Array.isArray(state.timeline) && state.timeline.every(isStep), 'timeline: неверные шаги')
  assert(Array.isArray(state.loop) && state.loop.length > 0 && state.loop.every(isStep), 'loop: неверные шаги')
  assert(Array.isArray(state.roster) && state.roster.length === 4, 'roster: не четыре героя с кадрами')
  eq(state.roster.map(one => one.id), ['kadr', 'talia', 'di', 'wick'], 'roster.id')
  for (const one of state.roster) {
    assert(typeof one.sprite === 'string' && one.sprite.length > 0, 'roster.sprite')
    assert(typeof one.name === 'string' && typeof one.role === 'string' && typeof one.about === 'string', 'roster: поля')
    assert(Array.isArray(one.stats) && one.stats.length === 3 && one.stats.every(([, v]) => v >= 0 && v <= 1), 'roster.stats')
  }
  assert(Array.isArray(state.pokes) && state.pokes.length >= 3 && state.pokes.length <= 5, 'pokes: 3–5 реакций')
  assert(state.pokes.every(reel => Array.isArray(reel) && reel.length > 0 && reel.every(isStep)), 'pokes: шаги')
  assert(Array.isArray(state.pokeLines) && (state.pokeLines.length > 0 || state.hero === null), 'pokeLines пустой')
  assert(Array.isArray(state.pokeMad) && (state.pokeMad.length > 0 || state.hero === null), 'pokeMad пустой')
  assert(Array.isArray(state.enter) && state.enter.length > 0 && state.enter[0][0] === 'bow_01', 'enter не с поклона')
  assert(state.enter.some(step => step[0].startsWith('hello_')), 'enter без приветствия')
  if (state.costar !== undefined) {
    assert(typeof state.costar.sprite === 'string' && typeof state.costar.name === 'string', 'costar: поля')
    assert(Array.isArray(state.costar.stunts) && state.costar.stunts.length > 0, 'costar.stunts пустые')
  }
  // JSON-ready: сериализуется без потерь.
  eq(JSON.parse(JSON.stringify(state)), state, 'state не JSON-ready')
  // Кадры: у героя с кадрами все кадры должны быть нарисованы; трюки напарника — у напарника.
  if (state.hero) {
    const missing = missingFrames(state.hero, stepFrames(state))
    assert(missing.length === 0, `нет кадров у ${state.hero}: ${missing.join(' ')}`)
  }
  if (state.costar) {
    const stuntFrames = state.costar.stunts.flatMap(reel => reel.flatMap(step => [step[0]]))
    const missing = missingFrames(state.costar.sprite, stuntFrames)
    assert(missing.length === 0, `нет трюков у напарника ${state.costar.sprite}: ${missing.join(' ')}`)
  }
}

// ---- 0. статические требования к модулям ----
check('director.js: без claude-code, Node-модулей, JSX и таймеров', () => {
  const source = readFileSync(join(here, 'director.js'), 'utf8')
  for (const banned of [/from\s+['"]claude-code['"]/, /from\s+['"]node:/, /require\s*\(/, /setTimeout\s*\(/, /setInterval\s*\(/, /<[A-Z][A-Za-z]*[\s/>]/]) {
    assert(!banned.test(source), `director.js содержит ${banned}`)
  }
  const imports = [...source.matchAll(/from\s+['"]([^'"]+)['"]/g)].map(match => match[1])
  eq(imports, ['./mod-data.js'], 'director.js импортирует только mod-data.js')

  return `${imports.length} импорт(ов), только данные`
})
check('mod-data.js: self-contained ESM без claude-code, Node-модулей и TS', () => {
  const source = readFileSync(join(here, 'mod-data.js'), 'utf8')
  for (const banned of [/from\s+['"]claude-code['"]/, /from\s+['"]node:/, /require\s*\(/, /\.tsx?['"]/]) {
    assert(!banned.test(source), `mod-data.js содержит ${banned}`)
  }
  assert(!/\bexport\s+(type|interface)\b/.test(source), 'mod-data.js: TypeScript-типы в рантайме')
  assert(!/\.(ts|tsx)\b/.test(source.replace(/\/\/[^\n]*/g, '')), 'mod-data.js: ссылки на .ts в коде')

  return `${(source.length / 1024) | 0} KiB`
})
check('hooks/: не осталось .ts/.tsx файлов', () => {
  const files = readdirSync(here)
  const bad = files.filter(file => /\.tsx?$/.test(file))
  eq(bad, [], 'в hooks лежат TS-файлы')
})

// ---- 1. кадры: все клипы всех четырёх героев нарисованы ----
check('кадры всех клипов у четырёх героев существуют', () => {
  const report = []
  for (const one of HEROES.filter(hero => hero.sprite)) {
    const frames = heroFrames(one.sprite)
    const missing = missingFrames(one.sprite, frames)
    assert(missing.length === 0, `${one.sprite}: нет ${missing.length}/${frames.length} кадров (${missing.slice(0, 6).join(' ')})`)
    report.push(`${one.sprite}:${frames.length}`)
  }
  // Напарник в кадре тоже существует и умеет трюки из CONTRACT.
  const partner = heroById(COSTARS.kadr.partner)
  assert(partner.sprite === 'talia', 'напарник Романа — Талия')
  const stuntFrames = COSTARS.kadr.stunts.flatMap(name => STUNTS[name].steps.map(step => step[0]))
  assert(missingFrames('talia', stuntFrames).length === 0, 'трюки напарника нарисованы')
  assert(WORK.has('film') && CLIPS.film.loop === true, 'film — рабочая петля')

  return report.join(' ')
})

// ---- 2. старт сессии ----
director.setSession('sim-session')
// Первое чтение настроек — как в session.start оригинала: язык до первой сцены.
director.applySettings('{"lang":"ru"}')
director.start()

check('старт: Роман, приветствие, питомец запущен', () => {
  const state = last()
  validateState(state)
  eq(state.hero, DEFAULT_HERO, 'герой по умолчанию')
  eq(state.name, 'Роман', 'имя')
  eq(state.role, 'ОПЕРАТОР', 'роль')
  eq(state.session, 'sim-session', 'session')
  eq(state.busy, false, 'busy на старте')
  assert(state.timeline.every(step => step[0].startsWith('wave')), 'старт: машет')
  eq(lastClip(), 'idle', 'после приветствия — стойка')
  eq(launches.length, 1, 'питомец запускается один раз')

  return `${states.length} state.json`
})

// ---- 3. просьба человека: busy, кивок, реплика о деле ----
check('promptSubmit: busy, кивок и сцена think', () => {
  advance(2_000)
  director.promptSubmit({ text: 'почини\nклик   по герою', machine: false })
  const state = last()
  validateState(state)
  eq(state.busy, true, 'busy между submit и complete')
  eq(state.timeline[0][0], 'nod_01', 'сперва кивает')
  eq(lastClip(), 'think', 'думает')
  includes(state.line, 'почини клик по герою', 'цитата просьбы')

  return state.line
})

check('жизнь: отметка «жив» каждые 15 с без новой сцены', () => {
  const before = last()
  const count = states.length
  advance(16_000)
  assert(states.length > count, 'за 16 с не было ни одной записи')
  eq(last().seq, before.seq, 'seq не меняется на отметке «жив»')
  eq(last().busy, true, 'ход всё ещё идёт')
  assert(last().alive > before.alive, 'alive не обновился')

  return `seq ${last().seq}, alive +${last().alive - before.alive} мс`
})

// ---- 4. сцены по инструментам ----
const tool = (name, input, result = {}) => {
  director.toolCall({ name, input })
  director.toolResult({ name, input, result, isError: false, background: false })
}

check('инструмент read → сцена read, реплика о файле', () => {
  advance(7_000)
  tool('read', { file_path: 'C:/proj/src/app.ts' })
  const state = last()
  validateState(state)
  eq(lastClip(), 'read', 'клип read')
  includes(state.line, 'app.ts', 'файл в реплике')
  assert(!state.line.includes('C:/proj'), 'полный путь в реплику не идёт')

  return state.line
})

check('инструмент edit → сцена type', () => {
  advance(7_000)
  tool('edit', { file_path: 'C:/proj/src/app.ts', old_string: 'a', new_string: 'b' })
  validateState(last())
  eq(lastClip(), 'type', 'клип type')
  includes(last().line, 'app.ts', 'файл в реплике')

  return last().line
})

check('инструмент pwsh → сцена film', () => {
  advance(7_000)
  tool('pwsh', { command: 'npm test' })
  validateState(last())
  eq(lastClip(), 'film', 'клип film')
  includes(last().line, 'npm test', 'команда в реплике')

  return last().line
})

check('опасная команда пугает, потом снимает; Роман снимает Талию', () => {
  advance(7_000)
  tool('pwsh', { command: 'Remove-Item -Recurse -Force C:\\build' })
  const state = last()
  validateState(state)
  eq(actionFrames(state)[0], 'scared_01', 'испуг первым кадром')
  eq(lastClip(), 'film', 'потом снимает')
  eq(state.costar?.sprite, 'talia', 'напарник в кадре')
  eq(state.costar?.name, 'Талия', 'имя напарника')
  assert(state.costar.stunts.length >= 4 && state.costar.stunts.length <= 6, 'серия трюков 4–6')
  for (const reel of state.costar.stunts) {
    eq(reel.length, 8, 'трюк из 8 кадров')
    assert(reel.every(step => /^(lp|pk)_0[1-8]$/.test(step[0])), 'кадры трюка')
  }

  return state.line
})

// ---- 5. ошибки инструментов: удивился, фейспалм, злость ----
const reactions = []
const failTool = () => {
  advance(4_500)
  director.toolCall({ name: 'read', input: { file_path: 'C:/proj/src/broken.ts' } })
  director.toolResult({ name: 'read', input: { file_path: 'C:/proj/src/broken.ts' }, result: { error: 'ENOENT' }, isError: true })
  const state = last()
  validateState(state)
  const frame = actionFrames(state).find(f => /^(surprised|facepalm|angry)_/.test(f)) ?? ''

  return frame
}

check('ошибки: удивился → фейспалм → злость, и не та же подряд', () => {
  for (let i = 0; i < 3; i += 1) {
    reactions.push(failTool())
  }
  eq(reactions, ['surprised_01', 'facepalm_01', 'angry_01'], 'счёт реакций за ход')
  includes(last().line, 'broken.ts', 'реплика о упавшем деле')

  return reactions.join(' → ')
})

// ---- 6. конец хода: радость по длине работы ----
check('turnComplete: busy=false, радость и приветствие, реплика = итог', () => {
  director.turnComplete({ answer: '## Итог\n**Починил** клик по герою.', durationMs: 9_000, reason: 'answer', turnId: 't1' })
  const state = last()
  validateState(state)
  eq(state.busy, false, 'busy снят')
  // Начатый жест ошибки доигрывается, дальше — радость по длине работы (ход шёл дольше LONG_TURN).
  const frames = actionFrames(state)
  assert(frames.includes('happy_01'), 'долгий ход: прыжок радости')
  assert(frames.includes('celebrate_01'), 'победа')
  assert(frames.indexOf('point_01') < frames.indexOf('wave_01'), 'звонок: показал на человека и махнул')
  eq(state.line, 'Починил клик по герою.', 'реплика — первая строка ответа')

  return state.line
})

// ---- 7. покой: sit → lie → sleep, мелочи и пробуждение ----
check(`покой: после ${SIT_AFTER / 1000} с садится, ${LIE_AFTER / 1000} с — ложится, ${SLEEP_AFTER / 1000} с — спит`, () => {
  const seen = new Map()
  const watch = () => {
    seen.set(lastClip(), seen.get(lastClip()) ?? 0)
  }
  const chase = (target) => {
    for (let i = 0; i < 400; i += 1) {
      watch()
      if (lastClip() === target) {
        return true
      }
      advance(1_000)
    }

    return false
  }
  advance(30_000)
  // Мелочь без дела: что-то из набора после 25 с тишины.
  assert(states.some(s => s.timeline.some(step => /^(iv|stretch|drink|phone|music|bored|sneeze|fix|selfie|laugh|look)_/.test(step[0]))), 'мелочь без дела не сыграла')
  assert(chase('sit'), `не сел за ${SIT_AFTER} с`)
  assert(chase('lie'), `не лёг за ${LIE_AFTER} с`)
  assert(chase('sleep'), `не уснул за ${SLEEP_AFTER} с`)
  validateState(last())
  eq(lastClip(), 'sleep', 'спит')
  // Пусть переход в сон (fallAsleep) доиграется: просыпается уже из позы сна.
  advance(2_000)

  // Проснулся рывком, встал и потянулся — на новую просьбу.
  director.promptSubmit({ text: 'привет', machine: false })
  const woke = last()
  validateState(woke)
  eq(woke.timeline[0], ['fa_04', 120, 'shake'], 'пробуждение — вздрогнул')
  const frames = [...woke.timeline.map(step => step[0])]
  assert(frames.indexOf('sd_01') > frames.indexOf('ld_01'), 'встаёт после того, как встал с лежанки')
  assert(frames.indexOf('stretch_01') < frames.indexOf('nod_01'), 'потягивается до кивка')
  eq(lastClip(), 'think', 'думает над просьбой')
  director.turnComplete({ answer: 'Готово.', durationMs: 3_000, reason: 'answer', turnId: 't2' })

  return `мелочей: ${seen.size} поз, кадров в пробуждении: ${frames.length}`
})

// ---- 8. выбор героя из choice.json: ровно один раз ----
check('choice.json: герой меняется ровно один раз, вход доигрывается с середины', () => {
  advance(2_000)
  const before = last()
  const seqBefore = before.seq
  const choice = `\uFEFF${JSON.stringify({ hero: 'talia', at: clock - 500 })}`
  director.tick({ choice })
  const switched = last()
  validateState(switched)
  eq(switched.hero, 'talia', 'герой из меню питомца')
  eq(switched.name, 'Талия', 'имя героя')
  eq(switched.role, 'НЕТРАННЕР', 'роль героя')
  eq(switched.pokeLines, heroById('talia').pokeLines, 'тычки нового героя')
  assert(switched.seq !== seqBefore, 'seq сменился на нового героя')
  // Питомец уже сыграл поклон сам: кадр bow_01 (240 мс) пропущен, а приветствие осталось.
  assert(!(switched.timeline[0][0] === 'bow_01' && switched.timeline[0][1] === 240), 'вход не начинается с начала поклона')
  assert(switched.timeline.some(step => step[0].startsWith('hello_')), 'приветствие в входе')

  // Тот же выбор вторым тиком — ничего: ни сцены, ни seq.
  const count = states.length
  const seq = last().seq
  director.tick({ choice })
  eq(states.length, count, 'повторный выбор не пишет state.json')
  eq(last().seq, seq, 'повторный выбор не меняет seq')

  // Битый файл и выбор героя без кадров — молча ничего.
  director.tick({ choice: '{не json' })
  director.tick({ choice: JSON.stringify({ hero: 'robot', at: clock + 1_000 }) })
  eq(last().hero, 'talia', 'плохие выборы проигнорированы')

  return `${before.name} → ${last().name}`
})

check('после смены героя инструменты идут его голосом и с его кадрами', () => {
  director.promptSubmit({ text: 'проверь main', machine: false })
  advance(7_000)
  tool('read', { file_path: 'C:/proj/src/main.ts' })
  const state = last()
  validateState(state)
  eq(state.hero, 'talia', 'герой Талия')
  eq(lastClip(), 'read', 'сцена read')
  includes(state.line, 'main.ts', 'файл в реплике')

  return state.line
})

// ---- 9. звонок после долгого хода ----
check('долгий ход — входящий вызов один раз, с честным текстом', () => {
  calls.length = 0
  director.promptSubmit({ text: 'подними сервер', machine: false })
  advance(2_000)
  director.turnComplete({ answer: '## Итог\n**Починил** ходьбу и добавил звонок.', durationMs: 30_000, reason: 'answer', turnId: 't3' })
  const state = last()
  validateState(state)
  assert(actionFrames(state).some(f => f.startsWith('happy_')), 'радость за долгую работу')
  eq(calls.length, 1, 'звонок ровно один')
  eq(calls[0].name, 'Талия', 'звонит текущий герой')
  eq(calls[0].text, 'Починил ходьбу и добавил звонок.', 'текст звонка — строка ответа')
  assert(actionFrames(state).includes('point_01'), 'сперва показывает на человека')
  assert(actionFrames(state).indexOf('point_01') < actionFrames(state).indexOf('wave_01'), 'потом машет')
  // Повторный конец того же хода — не второй звонок.
  director.turnComplete({ answer: 'Починил ходьбу и добавил звонок.', durationMs: 30_000, reason: 'answer', turnId: 't3' })
  eq(calls.length, 1, 'повторный turnComplete не звонит второй раз')

  return `${calls[0].hero}: ${calls[0].text}`
})

// ---- 10. прерванный ход ----
check('aborted: пожал плечами и вернулся в стойку', () => {
  director.promptSubmit({ text: 'останови', machine: false })
  advance(1_000)
  director.turnComplete({ answer: '', durationMs: 2_000, reason: 'aborted', turnId: 't4' })
  const state = last()
  validateState(state)
  // Начатый кивок доигрывается, дальше — жест «ну, стоп так стоп».
  assert(actionFrames(state).includes('shrug_01'), 'пожал плечами')
  eq(lastClip(), 'idle', 'вернулся в стойку')
  eq(state.busy, false, 'ход кончился')
  assert(state.line.length > 0, 'есть реплика об остановке')

  return state.line
})

// ---- 11. /report ----
check('/report: сессия, файлы, команды, ошибки, расход', () => {
  director.measure({ percent: 85, tokens: 170_000, window: 200_000, usd: 0.42 })
  assert(toasts.some(message => message.includes('85%')), 'предупреждение о контексте не показалось')
  const text = director.report()
  assert(text.length > 80, 'отчёт слишком короткий')
  includes(text, 'Отчёт: Талия', 'герой в отчёте')
  includes(text, 'app.ts', 'правленый файл в отчёте')
  includes(text, 'npm test', 'запущенная команда в отчёте')
  includes(text, 'Ошибок: 3', 'счёт ошибок')
  includes(text, 'контекст 85%', 'расход контекста')
  includes(text, 'ходов:', 'счёт ходов')

  return text.split('\n')[0]
})

// ---- 12. язык питомца ----
check('settings.json: en — имя, реплика и меню на английском; битый файл не ломает', () => {
  director.tick({ settings: '\uFEFF{"lang":"en"}' })
  const state = last()
  validateState(state)
  eq(state.name, 'Talia', 'имя на английском')
  eq(state.role, 'NETRUNNER', 'роль на английском')
  eq(state.roster.map(one => one.name), ['Roman', 'Talia', 'Dee', 'Wick'], 'меню на английском')
  assert(!/[а-яё]/i.test(state.line), `реплика на английском: «${state.line}»`)
  director.tick({ settings: '{битый' })
  eq(last().name, 'Talia', 'битый файл ничего не меняет')
  director.tick({ settings: '{"lang":"ru"}' })
  eq(last().name, 'Талия', 'русский вернулся')

  return last().name
})

// ---- 13. словарь инструментов DSH → сцены ----
check('словарь DSH → сцены: grep/glob → scan, subagent → phone, web_search → walk, незнакомый → film', () => {
  director.promptSubmit({ text: 'проверь всё', machine: false })
  advance(1_000)
  const seen = []
  const runTool = (name, input) => {
    advance(7_000)
    tool(name, input)
    seen.push(lastClip())
  }
  runTool('grep', { pattern: 'todo' })
  runTool('glob', { pattern: '**/*.ts' })
  runTool('subagent', { description: 'проверка' })
  runTool('web_search', { queries: ['typescript'] })
  runTool('present', { path: 'C:/x.png' })
  eq(seen, ['scan', 'scan', 'phone', 'walk', 'film'], 'сцены по инструментам')
  validateState(last())
  director.turnComplete({ answer: 'Проверил.', durationMs: 30_000, reason: 'answer', turnId: 'tools2' })

  return seen.join(' → ')
})

// ---- 14. удержание рабочей сцены ----
check('WORK_HOLD: рабочая сцена не дёргается чаще 6 с, отложенная ставится после', () => {
  director.promptSubmit({ text: 'быстро правь', machine: false })
  advance(1_000)
  tool('read', { file_path: 'C:/a.ts' })
  eq(lastClip(), 'read', 'первая сцена — read')
  const readSeq = last().seq
  for (let i = 0; i < 4; i += 1) {
    advance(1_000)
    tool('edit', { file_path: 'C:/a.ts', old_string: 'a', new_string: 'b' })
  }
  eq(lastClip(), 'read', 'сцена read ещё держится')
  includes(last().line, 'a.ts', 'реплика о новом деле без новой сцены')
  const changed = states.filter(state => state.seq !== readSeq && state.timeline[0]?.[0] === 'read_01')
  eq(changed.length <= 1, true, 'лишних сцен read не появилось')
  advance(3_000)
  eq(lastClip(), 'type', 'отложенная сцена type встала после удержания')
  director.turnComplete({ answer: 'Готово.', durationMs: 8_000, reason: 'answer', turnId: 'hold' })

  return `${readSeq} → ${last().seq}`
})

// ---- 15. фоновые задачи: «готово» ждёт их отчёта ----
check('фон: без радости и звонка, пока фоновая задача не отчиталась; потом радость один раз', () => {
  // Дай доиграть радости прошлого хода: считаем только новые сцены.
  advance(10_000)
  calls.length = 0
  const from = states.length
  const cheers = () => states.slice(from).filter(state => state.timeline.some(step => /^(happy|celebrate|clap|dance)_/.test(step[0]))).length
  const agentInput = { description: 'проверка', prompt: 'проверь', run_in_background: true }
  director.promptSubmit({ text: 'подними сервер и проверь', machine: false })
  advance(1_000)
  director.toolCall({ name: 'subagent', input: agentInput })
  director.toolResult({ name: 'subagent', input: agentInput, result: { status: 'async_launched' }, isError: false, background: true })
  director.turnComplete({ answer: 'Сервер запущен в фоне, жду.', durationMs: 30_000, reason: 'answer', turnId: 'bg1' })
  advance(2_000)
  eq([calls.length, cheers()], [0, 0], 'фон ещё идёт: ни радости, ни звонка')
  // Отчёт фоновой задачи — ход с уведомлением.
  director.promptSubmit({ text: '<task-notification>done</task-notification>', machine: true })
  advance(1_000)
  director.turnComplete({ answer: 'Сервер поднялся, тесты прошли.', durationMs: 20_000, reason: 'answer', turnId: 'bg2' })
  advance(100)
  assert(cheers() >= 1, 'отчёт фона: радость')
  eq(calls.length, 1, 'радость со звонком — один раз')
  const stateCount = states.length
  const callCount = calls.length
  director.turnComplete({ answer: 'Сервер поднялся, тесты прошли.', durationMs: 20_000, reason: 'answer', turnId: 'bg2' })
  advance(100)
  eq([calls.length, states.length], [callCount, stateCount], 'повторный конец хода ничего не меняет')

  return `${calls[0].name}: ${calls[0].text}`
})

// ---- 16. chooseHero: команда и герой без кадров ----
check('chooseHero: вход с поклоном, ответ в чат, герой без кадров и неизвестное имя', () => {
  const picked = director.chooseHero('ди')
  eq([picked.ok, picked.name], [true, 'Ди'], 'герой выбран')
  includes(picked.text, 'на связи', 'ответ в чат')
  const state = last()
  validateState(state)
  eq(state.hero, 'di', 'hero di')
  assert(state.timeline.some(step => step[0].startsWith('bow_')), 'вход с поклона')
  assert(state.timeline.some(step => step[0].startsWith('hello_')), 'приветствие в входе')
  // Герой без кадров: state.json с hero null, но валидный.
  const off = director.chooseHero('выкл')
  eq([off.ok, off.name], [true, 'Без персонажа'], 'Без персонажа')
  const plain = last()
  eq(plain.hero, null, 'hero null у героя без кадров')
  validateState(plain)
  eq(director.current(), plain, 'current() — последний отданный state')
  // Неизвестный герой: не выбран, в ответе список.
  const bad = director.chooseHero('дракон')
  eq(bad.ok, false, 'неизвестный герой не выбран')
  includes(bad.text, 'Не знаю персонажа', 'подсказка со списком')
  // Выбор можно отдать и разобранным объектом, а не только текстом файла.
  const viaObject = director.applyChoice({ hero: 'wick', at: clock - 1_000 })
  eq([viaObject.applied, last().hero], [true, 'wick'], 'выбор объектом тоже применяется')
  director.chooseHero('роман')
  eq(last().hero, 'kadr', 'Роман вернулся')

  return `${state.name} → ${plain.name} → ${last().name}`
})

// ---- 17. контекст: предупреждение повторяется после падения ниже порога ----
check('контекст: предупреждение один раз на порог и снова после падения', () => {
  const before = toasts.length
  director.measure({ percent: 55, tokens: 110_000, window: 200_000, usd: 0 })
  director.measure({ percent: 90, tokens: 180_000, window: 200_000, usd: 0 })
  eq(toasts.length, before + 1, 'второе предупреждение после падения')
  includes(toasts[toasts.length - 1], '90%', 'процент в предупреждении')

  return toasts[toasts.length - 1]
})

// ---- 18. вся история state.json: seq/сцена согласованы, кадры на месте ----
check('история state.json: все записи валидны, seq меняется только со сценой', () => {
  for (const state of states) {
    validateState(state)
  }
  let previous
  let previousAlive
  for (const state of states) {
    if (previous && state.seq === previous.seq) {
      eq([state.hero, state.timeline, state.loop], [previous.hero, previous.timeline, previous.loop], 'тот же seq — та же сцена')
    }
    if (previous && state.hero && previous.hero && (state.hero !== previous.hero || JSON.stringify(state.loop) !== JSON.stringify(previous.loop))) {
      assert(state.seq !== previous.seq, 'новая сцена — новый seq')
    }
    // Отметка «жив» не реже 15 с: иначе питомец уйдёт, не дождавшись следующего события.
    if (previousAlive !== undefined) {
      assert(state.alive - previousAlive <= 15_000, `alive не обновлялся ${state.alive - previousAlive} мс`)
    }
    previous = state
    previousAlive = state.alive
  }

  return `${states.length} состояний, ${new Set(states.map(s => s.seq)).size} сцен`
})

// ---- итог ----

console.log(notes.join('\n'))
console.log('')
if (failures.length === 0) {
  console.log(`PASS — ${passed} проверок, ${states.length} state.json, ${new Set(states.map(s => s.seq)).size} сцен, кадры на месте`)
  process.exit(0)
}
console.error(`FAIL — ${failures.length} из ${passed + failures.length} проверок провалились:`)
for (const failure of failures) {
  console.error(`  • ${failure}`)
}
process.exit(1)
