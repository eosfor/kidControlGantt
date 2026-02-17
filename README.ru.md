# Kid Control Gantt (.NET)

Ветка `codex/dotnet-rewrite` полностью переносит backend-функционал на ASP.NET Core.

## Что реализовано

- API управления MikroTik Kid Control на .NET:
  - `GET /api/kid-control`
  - `GET /api/state`
  - `POST /api/users/{name}/request`
  - `POST /api/users/{name}/disable`
  - `GET /api/debug`
  - `GET /health`
- Дневные лимиты по дням из JSON-конфига.
- В UI отображаются только пользователи, перечисленные в конфиге.
- Выдача/продление окна доступа, обрезка по `limit + grace`.
- Отключение доступа кнопкой `Отключить`.
- Хранение состояния и таймеров в SQLite.
- Фоновый sweep просроченных сессий.

## Структура

- `app/Program.cs` — .NET API + логика лимитов/таймеров/MikroTik.
- `app/wwwroot/index.html` — страница управления.
- `config/kid-access-config.json` — рабочий конфиг лимитов.
- `config/kid-access-config.example.json` — шаблон.
- `Dockerfile` — сборка/рантайм .NET.
- `docker-compose.yml` — запуск контейнера.

## Конфиг лимитов

Файл: `config/kid-access-config.json`

- `timezone` — таймзона расчета дня.
- `defaultWindowMinutes` — окно по умолчанию.
- `graceMinutes` — дополнительный лимит сверх дневного.
- `users[]` — пользователи для UI и лимитов.
- `limitsMinutes.mon..sun` — лимит на день (в минутах).

Если пользователь не указан в `users`, на странице не отображается.

## Запуск

```bash
cp .env.example .env
# заполните MIKROTIK_USER / MIKROTIK_PASSWORD

docker compose up -d --build
```

UI: `http://<host>:3030`

## Безопасность

- Не коммитьте `.env`.
- Не храните реальные пароли в git.
- Для production используйте secrets/vault.
