# MikroTik Kid Control Access Manager

Веб-интерфейс + backend-прокси для управления доступом пользователей Kid Control через окно времени с дневными лимитами.

## Что делает приложение

- Загружает пользователей из MikroTik `/rest/ip/kid-control`.
- Показывает на странице только тех пользователей, которые перечислены в конфиге лимитов.
- Для каждого пользователя отображает:
  - дневной лимит на текущий день,
  - остаток до лимита,
  - таймер активного окна,
  - настраиваемый размер окна,
  - кнопки `Запросить/продлить доступ` и `Отключить`.
- По кнопке `Запросить/продлить` выдает/продлевает доступ с текущего момента.
- По кнопке `Отключить` немедленно завершает активную сессию и отключает пользователя в MikroTik.
- Хранит состояние сессий в SQLite (persist через docker volume).

## Структура

- `index.html` — UI с таблицей и таймерами.
- `proxy/index.js` — Express backend, логика лимитов/окон, интеграция с MikroTik REST API.
- `proxy/config/kid-access-config.json` — лимиты по дням и список отображаемых пользователей.
- `docker-compose.yml` — сервисы `web` и `proxy` + volume для SQLite.

## Конфиг лимитов

Файл: `proxy/config/kid-access-config.json`

```json
{
  "timezone": "Europe/Moscow",
  "defaultWindowMinutes": 120,
  "graceMinutes": 15,
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

Если пользователь есть в MikroTik, но отсутствует в `users`, он не отображается в UI.

## Логика лимита и grace

- `dayLimit` — лимит на день.
- Кнопка `Запросить/продлить` активна, пока `used < dayLimit`.
- Максимальный суммарный доступ за день ограничен `dayLimit + graceMinutes`.
- Если до лимита остается мало времени, запрос на длинное окно автоматически обрезается до доступного остатка с учетом grace.

## Быстрый старт

1. Создайте `.env`:

```bash
cp .env.example .env
```

2. Заполните переменные (`MIKROTIK_USER`, `MIKROTIK_PASSWORD`, URL).

3. Запустите контейнеры:

```bash
docker compose up -d --build
```

4. Откройте: `http://<host>:3030`

## Переменные окружения (`.env.example`)

- `MIKROTIK_KID_CONTROL_URL` — полный URL `/rest/ip/kid-control`.
- `MIKROTIK_BASE_URL` — базовый URL роутера (fallback).
- `MIKROTIK_USER`, `MIKROTIK_PASSWORD` — креды REST API.
- `DB_PATH` — путь к SQLite в контейнере (по умолчанию `/data/kid-control-state.db`).
- `LIMITS_CONFIG_PATH` — путь к JSON-конфигу лимитов.
- `TZ` — таймзона контейнера.

## Безопасность

- Не коммитьте `.env`.
- Не храните реальные пароли в репозитории.
- Для production лучше использовать Docker secrets.
