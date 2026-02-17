const express = require('express');
const fs = require('fs');
const path = require('path');
const Database = require('better-sqlite3');
const { DateTime } = require('luxon');

const fetch = (...args) => import('node-fetch').then((m) => m.default(...args));

const app = express();
app.use(express.json());

const DAY_KEYS = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'];
const WEEKDAY_TO_KEY = {
  1: 'mon',
  2: 'tue',
  3: 'wed',
  4: 'thu',
  5: 'fri',
  6: 'sat',
  7: 'sun'
};
const DAY_LABELS_RU = {
  mon: 'Понедельник',
  tue: 'Вторник',
  wed: 'Среда',
  thu: 'Четверг',
  fri: 'Пятница',
  sat: 'Суббота',
  sun: 'Воскресенье'
};

const PORT = Number(process.env.PORT || 4000);
const DB_PATH = process.env.DB_PATH || '/data/kid-control-state.db';
const CONFIG_PATH = process.env.LIMITS_CONFIG_PATH || path.join(__dirname, 'config', 'kid-access-config.json');
const CONFIG_CACHE_TTL_MS = 5000;
const SWEEP_INTERVAL_MS = 10000;

const API_URL = resolveKidControlApiUrl();
const AUTH_HEADER = resolveAuthHeader();

const db = initDb(DB_PATH);
const statements = prepareStatements(db);
let configCache = { loadedAt: 0, mtimeMs: 0, value: null };

function resolveKidControlApiUrl() {
  if (process.env.MIKROTIK_KID_CONTROL_URL) {
    return process.env.MIKROTIK_KID_CONTROL_URL;
  }

  if (process.env.TARGET) {
    return process.env.TARGET;
  }

  const base = process.env.MIKROTIK_BASE_URL || 'http://192.168.88.254';
  return `${base.replace(/\/$/, '')}/rest/ip/kid-control`;
}

function resolveAuthHeader() {
  if (process.env.BASIC_AUTH && process.env.BASIC_AUTH.trim()) {
    return process.env.BASIC_AUTH.trim();
  }

  const user = process.env.MIKROTIK_USER;
  const password = process.env.MIKROTIK_PASSWORD;
  if (user && password) {
    return `Basic ${Buffer.from(`${user}:${password}`).toString('base64')}`;
  }

  return '';
}

function initDb(dbPath) {
  fs.mkdirSync(path.dirname(dbPath), { recursive: true });
  const sqlite = new Database(dbPath);
  sqlite.pragma('journal_mode = WAL');
  sqlite.exec(`
    CREATE TABLE IF NOT EXISTS sessions (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      user_name TEXT NOT NULL,
      started_at_ms INTEGER NOT NULL,
      expires_at_ms INTEGER NOT NULL,
      ended_at_ms INTEGER,
      ended_reason TEXT,
      created_at_ms INTEGER NOT NULL,
      updated_at_ms INTEGER NOT NULL
    );

    CREATE UNIQUE INDEX IF NOT EXISTS idx_sessions_active_user
    ON sessions (user_name)
    WHERE ended_at_ms IS NULL;

    CREATE INDEX IF NOT EXISTS idx_sessions_user_start
    ON sessions (user_name, started_at_ms);
  `);
  return sqlite;
}

function prepareStatements(sqlite) {
  return {
    getActiveSession: sqlite.prepare(`
      SELECT id, user_name, started_at_ms, expires_at_ms
      FROM sessions
      WHERE user_name = ? AND ended_at_ms IS NULL
      LIMIT 1
    `),
    listExpiredActiveSessions: sqlite.prepare(`
      SELECT id, user_name, started_at_ms, expires_at_ms
      FROM sessions
      WHERE ended_at_ms IS NULL AND expires_at_ms <= ?
      ORDER BY expires_at_ms ASC
    `),
    insertSession: sqlite.prepare(`
      INSERT INTO sessions (user_name, started_at_ms, expires_at_ms, ended_at_ms, ended_reason, created_at_ms, updated_at_ms)
      VALUES (@userName, @startedAtMs, @expiresAtMs, NULL, NULL, @nowMs, @nowMs)
    `),
    extendSession: sqlite.prepare(`
      UPDATE sessions
      SET expires_at_ms = @expiresAtMs, updated_at_ms = @nowMs
      WHERE id = @id AND ended_at_ms IS NULL
    `),
    endSession: sqlite.prepare(`
      UPDATE sessions
      SET ended_at_ms = @endedAtMs, ended_reason = @reason, updated_at_ms = @nowMs
      WHERE id = @id AND ended_at_ms IS NULL
    `),
    sumCompletedUsageMs: sqlite.prepare(`
      SELECT COALESCE(SUM(ended_at_ms - started_at_ms), 0) AS used_ms
      FROM sessions
      WHERE user_name = ?
        AND ended_at_ms IS NOT NULL
        AND started_at_ms >= ?
        AND started_at_ms < ?
    `)
  };
}

function loadAccessConfig() {
  const now = Date.now();
  let mtimeMs = 0;

  try {
    mtimeMs = fs.statSync(CONFIG_PATH).mtimeMs;
  } catch (err) {
    throw new Error(`Не найден файл конфигурации: ${CONFIG_PATH}`);
  }

  const isFresh = configCache.value && configCache.mtimeMs === mtimeMs && (now - configCache.loadedAt) < CONFIG_CACHE_TTL_MS;
  if (isFresh) {
    return configCache.value;
  }

  let parsed;
  try {
    const raw = fs.readFileSync(CONFIG_PATH, 'utf-8');
    parsed = JSON.parse(raw);
  } catch (err) {
    throw new Error(`Ошибка чтения конфигурации ${CONFIG_PATH}: ${err.message}`);
  }

  const normalized = normalizeConfig(parsed);
  configCache = { loadedAt: now, mtimeMs, value: normalized };
  return normalized;
}

function normalizeConfig(raw) {
  if (!raw || typeof raw !== 'object') {
    throw new Error('Некорректный формат конфигурации: ожидается JSON-объект');
  }

  const timezone = normalizeTimezone(raw.timezone || process.env.TZ || 'UTC');
  const defaultWindowMinutes = toNonNegativeInt(raw.defaultWindowMinutes, 120);
  const graceMinutes = toNonNegativeInt(raw.graceMinutes, 15);

  if (!Array.isArray(raw.users)) {
    throw new Error('Конфигурация должна содержать массив users');
  }

  const users = raw.users.map((user) => normalizeUser(user, defaultWindowMinutes));

  return {
    timezone,
    defaultWindowMinutes,
    graceMinutes,
    users
  };
}

function normalizeUser(user, globalDefaultWindowMinutes) {
  if (!user || typeof user !== 'object') {
    throw new Error('Некорректный элемент users: ожидается объект');
  }

  const name = String(user.name || '').trim();
  if (!name) {
    throw new Error('У каждого пользователя в конфиге должен быть name');
  }

  const displayName = String(user.displayName || name).trim();
  const limitsSource = user.limitsMinutes || {};
  const limitsMinutes = {};

  for (const dayKey of DAY_KEYS) {
    limitsMinutes[dayKey] = toNonNegativeInt(limitsSource[dayKey], 0);
  }

  return {
    name,
    displayName,
    limitsMinutes,
    defaultWindowMinutes: toNonNegativeInt(user.defaultWindowMinutes, globalDefaultWindowMinutes)
  };
}

function normalizeTimezone(zone) {
  const candidate = String(zone || '').trim();
  if (!candidate) {
    return 'UTC';
  }

  const check = DateTime.now().setZone(candidate);
  return check.isValid ? candidate : 'UTC';
}

function toNonNegativeInt(value, fallback) {
  const num = Number(value);
  if (!Number.isFinite(num)) {
    return fallback;
  }
  return Math.max(0, Math.floor(num));
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function getDayContext(timezone, nowMs = Date.now()) {
  const now = DateTime.fromMillis(nowMs, { zone: timezone });
  const dayKey = WEEKDAY_TO_KEY[now.weekday];
  const startOfDay = now.startOf('day');
  const nextDay = startOfDay.plus({ days: 1 });

  return {
    now,
    nowMs,
    dayKey,
    dayLabel: DAY_LABELS_RU[dayKey],
    startOfDayMs: startOfDay.toMillis(),
    nextDayMs: nextDay.toMillis()
  };
}

function formatMikrotikTime(timeInZone) {
  const hour = Math.floor(timeInZone.hour);
  const minute = Math.floor(timeInZone.minute);
  if (minute === 0) {
    return `${hour}h`;
  }
  return `${hour}h${minute}m`;
}

function buildTodayWindowValue(startMs, endMs, timezone) {
  const start = DateTime.fromMillis(startMs, { zone: timezone });
  const end = DateTime.fromMillis(endMs, { zone: timezone });
  return `${formatMikrotikTime(start)}-${formatMikrotikTime(end)}`;
}

function getConfiguredUser(config, name) {
  return config.users.find((user) => user.name === name);
}

function getUsageState(userName, dayContext) {
  const completed = statements.sumCompletedUsageMs.get(userName, dayContext.startOfDayMs, dayContext.nextDayMs);
  const activeSession = statements.getActiveSession.get(userName) || null;

  let activeUsedMs = 0;
  if (activeSession) {
    const activeEndBoundary = Math.min(dayContext.nowMs, activeSession.expires_at_ms);
    activeUsedMs = Math.max(0, activeEndBoundary - activeSession.started_at_ms);
  }

  const usedMs = (completed ? completed.used_ms : 0) + activeUsedMs;
  return {
    usedSeconds: Math.floor(usedMs / 1000),
    activeSession
  };
}

function buildUserStateRow(config, userConfig, mikrotikEntry, dayContext) {
  const dayLimitMinutes = userConfig.limitsMinutes[dayContext.dayKey] || 0;
  const dayLimitSeconds = dayLimitMinutes * 60;
  const maxPerDaySeconds = dayLimitSeconds + (config.graceMinutes * 60);

  const usage = getUsageState(userConfig.name, dayContext);
  const usedSeconds = usage.usedSeconds;

  const remainingSeconds = Math.max(0, dayLimitSeconds - usedSeconds);
  const remainingCapSeconds = Math.max(0, maxPerDaySeconds - usedSeconds);
  const secondsUntilEndOfDay = Math.max(0, Math.floor((dayContext.nextDayMs - dayContext.nowMs) / 1000));
  const maxGrantSecondsNow = Math.max(0, Math.min(remainingCapSeconds, secondsUntilEndOfDay));
  const canRequest = Boolean(mikrotikEntry) && usedSeconds < dayLimitSeconds && maxGrantSecondsNow > 0;

  const activeSession = usage.activeSession
    ? {
        id: usage.activeSession.id,
        startedAtMs: usage.activeSession.started_at_ms,
        endsAtMs: usage.activeSession.expires_at_ms,
        remainingSeconds: Math.max(0, Math.floor((usage.activeSession.expires_at_ms - dayContext.nowMs) / 1000)),
        totalSeconds: Math.max(0, Math.floor((usage.activeSession.expires_at_ms - usage.activeSession.started_at_ms) / 1000))
      }
    : null;

  return {
    name: userConfig.name,
    displayName: userConfig.displayName,
    existsInMikrotik: Boolean(mikrotikEntry),
    mikrotikId: mikrotikEntry ? mikrotikEntry['.id'] : null,
    mikrotikDisabled: mikrotikEntry ? String(mikrotikEntry.disabled || 'false') === 'true' : true,
    dayLimitMinutes,
    usedSeconds,
    remainingSeconds,
    remainingCapSeconds,
    maxGrantSecondsNow,
    canRequest,
    suggestedWindowMinutes: clamp(userConfig.defaultWindowMinutes, 0, dayLimitMinutes),
    activeSession
  };
}

async function mikrotikRequest(method, url, body) {
  const response = await fetch(url, {
    method,
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...(AUTH_HEADER ? { Authorization: AUTH_HEADER } : {})
    },
    body: body ? JSON.stringify(body) : undefined
  });

  if (!response.ok) {
    const errorBody = await response.text().catch(() => '');
    throw new Error(`MikroTik ${method} ${url} => ${response.status}. ${errorBody}`.trim());
  }

  const text = await response.text();
  if (!text) {
    return null;
  }

  try {
    return JSON.parse(text);
  } catch (err) {
    return text;
  }
}

async function fetchKidControlList() {
  const list = await mikrotikRequest('GET', API_URL);
  if (!Array.isArray(list)) {
    throw new Error('MikroTik вернул неожиданный формат данных для /ip/kid-control');
  }
  return list;
}

function buildKidControlItemUrl(itemId) {
  return `${API_URL.replace(/\/$/, '')}/${encodeURIComponent(itemId)}`;
}

async function applyWindowToMikrotik(entry, dayKey, startMs, endMs, timezone) {
  const value = buildTodayWindowValue(startMs, endMs, timezone);
  const patch = {
    [dayKey]: value,
    disabled: 'false'
  };
  await mikrotikRequest('PATCH', buildKidControlItemUrl(entry['.id']), patch);
  return value;
}

async function disableUserInMikrotik(entry, dayKey) {
  const patch = {
    [dayKey]: '',
    disabled: 'true'
  };
  await mikrotikRequest('PATCH', buildKidControlItemUrl(entry['.id']), patch);
}

function sweepExpiredSessions() {
  const nowMs = Date.now();
  const expired = statements.listExpiredActiveSessions.all(nowMs);
  if (expired.length === 0) {
    return 0;
  }

  const tx = db.transaction((rows) => {
    for (const row of rows) {
      statements.endSession.run({
        id: row.id,
        endedAtMs: row.expires_at_ms,
        reason: 'expired',
        nowMs
      });
    }
  });

  tx(expired);
  return expired.length;
}

function parseRequestedWindowMinutes(rawValue, fallback, maxLimit) {
  const parsed = Number(rawValue);
  const value = Number.isFinite(parsed) ? parsed : fallback;
  const integerValue = Math.floor(value);
  return clamp(integerValue, 0, maxLimit);
}

async function buildStateResponse(config) {
  const dayContext = getDayContext(config.timezone);
  const mikrotikUsers = await fetchKidControlList();
  const mikrotikMap = new Map(mikrotikUsers.map((item) => [item.name, item]));

  const users = config.users.map((userCfg) => {
    const entry = mikrotikMap.get(userCfg.name);
    return buildUserStateRow(config, userCfg, entry, dayContext);
  });

  return {
    serverTimeMs: dayContext.nowMs,
    timezone: config.timezone,
    dayKey: dayContext.dayKey,
    dayLabel: dayContext.dayLabel,
    defaultWindowMinutes: config.defaultWindowMinutes,
    graceMinutes: config.graceMinutes,
    users
  };
}

function findMikrotikEntryByName(entries, name) {
  return entries.find((entry) => entry.name === name);
}

app.get('/api/kid-control', async (req, res) => {
  try {
    const data = await fetchKidControlList();
    res.json(data);
  } catch (err) {
    console.error('GET /api/kid-control failed:', err.message);
    res.status(502).json({ error: err.message });
  }
});

app.get('/api/state', async (req, res) => {
  try {
    sweepExpiredSessions();
    const config = loadAccessConfig();
    const state = await buildStateResponse(config);
    res.json(state);
  } catch (err) {
    console.error('GET /api/state failed:', err.message);
    res.status(500).json({ error: err.message });
  }
});

app.post('/api/users/:name/request', async (req, res) => {
  try {
    sweepExpiredSessions();
    const config = loadAccessConfig();
    const userConfig = getConfiguredUser(config, req.params.name);

    if (!userConfig) {
      return res.status(404).json({ error: `Пользователь ${req.params.name} отсутствует в конфигурации` });
    }

    const dayContext = getDayContext(config.timezone);
    const dayLimitMinutes = userConfig.limitsMinutes[dayContext.dayKey] || 0;
    const requestedWindowMinutes = parseRequestedWindowMinutes(
      req.body ? req.body.windowMinutes : undefined,
      userConfig.defaultWindowMinutes,
      dayLimitMinutes
    );

    if (requestedWindowMinutes <= 0) {
      return res.status(400).json({ error: 'Размер окна должен быть больше 0 минут' });
    }

    const usage = getUsageState(userConfig.name, dayContext);
    const usedSeconds = usage.usedSeconds;
    const dayLimitSeconds = dayLimitMinutes * 60;

    if (usedSeconds >= dayLimitSeconds) {
      return res.status(409).json({ error: `Дневной лимит (${dayLimitMinutes} мин) исчерпан` });
    }

    const maxPerDaySeconds = dayLimitSeconds + (config.graceMinutes * 60);
    const remainingCapSeconds = Math.max(0, maxPerDaySeconds - usedSeconds);
    const secondsUntilEndOfDay = Math.max(0, Math.floor((dayContext.nextDayMs - dayContext.nowMs) / 1000));
    const maxGrantSecondsNow = Math.min(remainingCapSeconds, secondsUntilEndOfDay);

    if (maxGrantSecondsNow <= 0) {
      return res.status(409).json({ error: 'Невозможно выдать доступ: достигнут верхний лимит или завершился день' });
    }

    const mikrotikList = await fetchKidControlList();
    const entry = findMikrotikEntryByName(mikrotikList, userConfig.name);

    if (!entry) {
      return res.status(404).json({ error: `Пользователь ${userConfig.name} не найден в MikroTik Kid Control` });
    }

    const requestedWindowSeconds = requestedWindowMinutes * 60;
    const activeSession = usage.activeSession;
    const startMs = activeSession ? activeSession.started_at_ms : dayContext.nowMs;
    const extensionBaseMs = activeSession ? Math.max(activeSession.expires_at_ms, dayContext.nowMs) : dayContext.nowMs;

    let targetEndMs = extensionBaseMs + (requestedWindowSeconds * 1000);
    const maxAllowedEndMs = dayContext.nowMs + (maxGrantSecondsNow * 1000);
    if (targetEndMs > maxAllowedEndMs) {
      targetEndMs = maxAllowedEndMs;
    }

    if (targetEndMs <= dayContext.nowMs) {
      return res.status(409).json({ error: 'Недостаточно остатка времени для продления доступа' });
    }

    await applyWindowToMikrotik(entry, dayContext.dayKey, startMs, targetEndMs, config.timezone);

    const tx = db.transaction(() => {
      if (activeSession) {
        statements.extendSession.run({
          id: activeSession.id,
          expiresAtMs: targetEndMs,
          nowMs: dayContext.nowMs
        });
      } else {
        statements.insertSession.run({
          userName: userConfig.name,
          startedAtMs: dayContext.nowMs,
          expiresAtMs: targetEndMs,
          nowMs: dayContext.nowMs
        });
      }
    });

    tx();

    const state = await buildStateResponse(config);
    return res.json({
      ok: true,
      user: userConfig.name,
      requestedWindowMinutes,
      grantedUntilMs: targetEndMs,
      state
    });
  } catch (err) {
    console.error(`POST /api/users/${req.params.name}/request failed:`, err.message);
    return res.status(500).json({ error: err.message });
  }
});

app.post('/api/users/:name/disable', async (req, res) => {
  try {
    sweepExpiredSessions();
    const config = loadAccessConfig();
    const userConfig = getConfiguredUser(config, req.params.name);

    if (!userConfig) {
      return res.status(404).json({ error: `Пользователь ${req.params.name} отсутствует в конфигурации` });
    }

    const dayContext = getDayContext(config.timezone);
    const mikrotikList = await fetchKidControlList();
    const entry = findMikrotikEntryByName(mikrotikList, userConfig.name);

    if (!entry) {
      return res.status(404).json({ error: `Пользователь ${userConfig.name} не найден в MikroTik Kid Control` });
    }

    const activeSession = statements.getActiveSession.get(userConfig.name);
    if (activeSession) {
      statements.endSession.run({
        id: activeSession.id,
        endedAtMs: dayContext.nowMs,
        reason: 'manual',
        nowMs: dayContext.nowMs
      });
    }

    await disableUserInMikrotik(entry, dayContext.dayKey);

    const state = await buildStateResponse(config);
    return res.json({
      ok: true,
      user: userConfig.name,
      state
    });
  } catch (err) {
    console.error(`POST /api/users/${req.params.name}/disable failed:`, err.message);
    return res.status(500).json({ error: err.message });
  }
});

app.get('/api/debug', async (req, res) => {
  try {
    const config = loadAccessConfig();
    const debugResponse = await mikrotikRequest('GET', API_URL);

    res.json({
      upstreamUrl: API_URL,
      authConfigured: Boolean(AUTH_HEADER),
      timezone: config.timezone,
      usersInConfig: config.users.length,
      upstreamEntries: Array.isArray(debugResponse) ? debugResponse.length : 0
    });
  } catch (err) {
    console.error('GET /api/debug failed:', err.message);
    res.status(502).json({ error: err.message });
  }
});

app.get('/health', (req, res) => {
  res.send('OK');
});

setInterval(() => {
  try {
    sweepExpiredSessions();
  } catch (err) {
    console.error('session sweep failed:', err.message);
  }
}, SWEEP_INTERVAL_MS);

app.listen(PORT, () => {
  console.log(`Proxy listening on ${PORT}`);
  console.log(`MikroTik API: ${API_URL}`);
  console.log(`Config: ${CONFIG_PATH}`);
});
