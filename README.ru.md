# Kid Control Gantt (.NET)

Веб-приложение на ASP.NET Core для управления `MikroTik Kid Control` с дневными лимитами и окнами доступа.

## Что делает приложение

- Загружает пользователей из `MikroTik /rest/ip/kid-control`.
- Показывает в UI только пользователей, перечисленных в конфиге лимитов.
- Для каждого пользователя отображает:
  - лимит на текущий день,
  - остаток до лимита,
  - таймер активного окна,
  - размер окна,
  - действия `Запросить/продлить доступ` и `Отключить`.
- По `Запросить/продлить доступ` backend изменяет запись в MikroTik и открывает/продлевает доступ.
- По `Отключить` активная сессия завершается, доступ в MikroTik закрывается.
- Состояние сессий хранится в SQLite.

## Правила лимитов

- Лимит на день задается в минутах по дням недели (`mon..sun`).
- Для каждого дня задается окно `dayWindows.<day>.start/end` (например `06:00-23:30`).
- Кнопка `Запросить/продлить доступ` активна, пока `used < dayLimit`.
- Максимум выдачи в день ограничен `dayLimit + graceMinutes`.
- Если запрошенное окно больше доступного остатка, окно автоматически обрезается.
- Размер окна в UI ограничен диапазоном `0..dayLimit`.
- Запрос доступа невозможен раньше `start` и позже `end`, даже если лимит не исчерпан.
- В `end + 10 минут` доступ автоматически обрывается (жесткий cutoff).

## Архитектура

- `app/Program.cs` — API, бизнес-логика лимитов/окон, интеграция с MikroTik, фоновые задачи.
- `app/wwwroot/index.html` — UI таблицы и таймеров.
- `config/kid-access-config.json` — рабочий конфиг лимитов.
- `config/kid-access-config.example.json` — пример конфига.
- SQLite файл — путь из `DB_PATH` (по умолчанию `/data/kid-control-state.db`).

## Конфиг лимитов

Файл: `config/kid-access-config.json`

```json
{
  "timezone": "America/Los_Angeles",
  "defaultWindowMinutes": 120,
  "graceMinutes": 15,
  "dayWindows": {
    "mon": { "start": "06:00", "end": "23:30" },
    "tue": { "start": "06:00", "end": "23:30" },
    "wed": { "start": "06:00", "end": "23:30" },
    "thu": { "start": "06:00", "end": "23:30" },
    "fri": { "start": "06:00", "end": "23:30" },
    "sat": { "start": "06:00", "end": "23:30" },
    "sun": { "start": "06:00", "end": "23:30" }
  },
  "users": [
    {
      "name": "test",
      "displayName": "test",
      "defaultWindowMinutes": 120,
      "limitsMinutes": {
        "mon": 120,
        "tue": 120,
        "wed": 120,
        "thu": 120,
        "fri": 120,
        "sat": 120,
        "sun": 120
      }
    }
  ]
}
```

Если пользователь есть в MikroTik, но отсутствует в `users`, он не появится на странице.

## Переменные окружения

См. `.env.example`.

- `MIKROTIK_KID_CONTROL_URL` — полный URL REST endpoint.
- `MIKROTIK_BASE_URL` — fallback, если полный URL не задан.
- `MIKROTIK_USER`, `MIKROTIK_PASSWORD` — креды для MikroTik REST.
- `BASIC_AUTH` — опциональный override заголовка авторизации.
- `DB_PATH` — путь к SQLite.
- `LIMITS_CONFIG_PATH` — путь к JSON-конфигу лимитов.
- `APP_TIMEZONE` — опциональный override таймзоны (например `America/Los_Angeles`), имеет приоритет над `timezone` в JSON.
- `CONFIG_CACHE_TTL_MS` — кеш конфига.
- `SWEEP_INTERVAL_SECONDS` — частота cleanup просроченных сессий.

## API

- `GET /health` — healthcheck.
- `GET /api/kid-control` — сырой список MikroTik kid-control.
- `GET /api/state` — состояние для UI.
- `POST /api/users/{name}/request` — запрос/продление окна.
  - body: `{ "windowMinutes": 120 }`
- `POST /api/users/{name}/disable` — принудительное отключение.
- `GET /api/debug` — отладочная сводка.

## Запуск локально

```bash
dotnet restore app/KidControlGantt.App.csproj
dotnet run --project app/KidControlGantt.App.csproj
```

По умолчанию приложение поднимается на портах из `launchSettings`.

## Запуск в Docker

```bash
cp .env.example .env
# заполните MIKROTIK_USER / MIKROTIK_PASSWORD

docker compose up -d --build
```

UI/API: `http://<host>:3030`

## Запуск в Docker Desktop (пошагово)

1. Запустите Docker Desktop и дождитесь статуса `Engine running`.
2. Откройте терминал и перейдите в проект:
   ```bash
   cd /Users/andrei/repo/kidControlGantt
   ```
3. Проверьте `.env` в корне проекта (`/Users/andrei/repo/kidControlGantt/.env`):
   - `MIKROTIK_KID_CONTROL_URL`
   - `MIKROTIK_USER`
   - `MIKROTIK_PASSWORD`
4. Проверьте лимиты в `config/kid-access-config.json`.
5. Поднимите контейнер:
   ```bash
   docker compose up -d --build
   ```
6. Проверьте доступ:
   - UI: `http://localhost:3030`
   - health: `http://localhost:3030/health`

Полезные команды:

```bash
docker compose logs -f app
docker compose ps
docker compose down
```

## Безопасность

- Не коммитьте `.env`.
- Не храните реальные пароли в git.
- Для production используйте secrets/vault.
