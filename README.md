# MikroTik Kid Control Gantt

Simple static frontend + Node/Express proxy to visualize MikroTik Kid Control schedules as a Gantt chart.

This repository contains:

- `index.html` - frontend (Vega-Lite) that fetches `/api/kid-control` by default. The UI no longer has fields for API URL/username/password — these values are supplied to the server via the `.env` file used by the `proxy` service.
- `Dockerfile` - nginx image to serve frontend and proxy `/api/` requests to the proxy service
- `proxy/` - Node/Express proxy that fetches data from MikroTik and returns JSON
- `docker-compose.yml` - compose file to run both services
- `.env.example` - example environment file for proxy

## Quickstart (Raspberry Pi)

1. Copy `.env.example` to `.env` and edit `TARGET` and `BASIC_AUTH` to point to your MikroTik REST endpoint and credentials. The frontend will always call the local proxy at `/api/kid-control` (no client-side credential inputs).

2. Build and run (ensure Docker and docker compose plugin are installed):

```bash
docker compose up -d --build
```

3. Open the frontend in your browser:

```
# MikroTik Kid Control Gantt

Статический фронтенд (Vega-Lite) + Node/Express proxy для визуализации расписаний MikroTik Kid Control в виде диаграммы Ганта.

## Что в репозитории

- `index.html` — фронтенд. По умолчанию запрашивает `/api/kid-control`.
- `Dockerfile` — образ Nginx для отдачи фронтенда и проксирования `/api/` на сервис `proxy`.
- `proxy/` — Node/Express proxy, делает запрос к MikroTik и возвращает JSON.
- `docker-compose.yml` — запускает `web` (nginx) и `proxy` сервисы.
- `.env.example` — пример файла с переменными окружения для `proxy`.

## Коротко как это работает

Nginx (порт 3030) отдаёт фронтенд и проксирует `/api/*` на `proxy` (внутренний сервис). `proxy` выполняет HTTP-запрос к MikroTik и возвращает JSON. Браузер общается same-origin с nginx — проблем с CORS нет.

## Быстрый старт (Raspberry Pi)

1) Скопируйте шаблон `.env.example` в `.env` и заполните значения.

```bash
cp .env.example .env
```

2) Сгенерируйте значение для `BASIC_AUTH` в формате `Basic <base64(username:password)>` (см. раздел ниже).

3) Соберите и запустите контейнеры (на Pi должны быть Docker и docker compose plugin):

```bash
docker compose up -d --build
```

4) Откройте в браузере: `http://<IP_вашего_pi>:3030`.

Фронтенд использует локальный прокси `/api/kid-control`. Все параметры (TARGET, BASIC_AUTH) задаются в `.env` и обрабатываются сервером (proxy). Это предотвращает утечку учётных данных в браузер или localStorage.

## Генерация `BASIC_AUTH` (подробно)

HTTP Basic Authorization требует заголовка в виде:

```
Authorization: Basic <base64(username:password)>
```

Примеры генерации base64 для строки `username:password`.

macOS / Linux (bash / zsh):

```bash
# безопасно (не интерпретируя спецсимволов оболочкой):
B64=$(printf '%s' 'kidreader:[SOME-PWD]' | base64 | tr -d '\n')
echo "BASIC_AUTH=Basic $B64" >> .env
```

или (однострочно, если вы уже скопировали .env):

```bash
printf "BASIC_AUTH=Basic %s\n" "$(printf '%s' 'kidreader:[SOME-PWD]' | base64 | tr -d '\n')" >> .env
```

Windows PowerShell:

```powershell
$bytes = [System.Text.Encoding]::UTF8.GetBytes('kidreader:[SOME-PWD]')
$b64 = [Convert]::ToBase64String($bytes)
Add-Content -Path .env -Value "BASIC_AUTH=Basic $b64"
```

После этого в файле `.env` должна быть строка вида:

```
BASIC_AUTH=Basic a2lkcmVhZGVyOjhCaE5YZ3NuQmlNVytjSk0=
```

> ВАЖНО: не коммитите `.env` в репозиторий. Добавьте `.env` в `.gitignore` и ограничьте права на файл:

```bash
echo ".env" >> .gitignore
chmod 600 .env
```

## Работа с фронтендом и API

- По умолчанию `index.html` содержит в поле API адрес `/api/kid-control`.
- Nginx внутри контейнерной сети проксирует `/api` на `proxy:4000`, поэтому браузер обращается same-origin — CORS не возникает.

Если вы хотите обратиться к proxy напрямую (например, для отладки), можно использовать `http://<pi>:4000/api/kid-control`, но текущая конфигурация не выставляет порт 4000 наружу по умолчанию.

## Полезные команды

```bash
# Запустить сборку и фоновые контейнеры
docker compose up -d --build

# Перезапустить только proxy
docker compose restart proxy

# Проверить статус
docker compose ps

# Смотреть логи
docker compose logs -f web
docker compose logs -f proxy

# Остановить
docker compose down
```

## Безопасность и рекомендации

- Не храните секреты в репозитории. Для production используйте Docker secrets или внешний vault.
- Ограничьте доступ к Pi/портам через firewall (ufw, iptables) если нужен доступ только из локальной сети.
- Если открываете доступ из интернета — обязательно настройте HTTPS и защиту доступа (firewall, basic auth для UI и т.д.).

## Отладка

- Если proxy возвращает ошибку 502, проверьте, доступен ли MikroTik с Pi. Выполните на Pi:

```bash
curl -v http://192.168.88.254/rest/ip/kid-control
```

- Проверьте логи proxy: `docker compose logs -f proxy`.

## Возможные улучшения

- Добавить кеширование в proxy (TTL) чтобы снизить частоту обращений к MikroTik.
- Добавить rate-limiting и аутентификацию для UI.
- Настроить автоматический выпуск TLS (Let's Encrypt) и редиректы на HTTPS.

---

Если хотите, могу автоматически сгенерировать значение `BASIC_AUTH` и положить его в `.env` (только если вы подтвердите, что это тестовый пароль и не против хранения его в репозитории). Также могу добавить `.env` в `.gitignore` автоматически.
