# MikroTik Kid Control Gantt

Статический веб-интерфейс и прокси на Node/Express для визуализации расписаний MikroTik Kid Control в виде диаграммы Ганта.

## Содержимое репозитория

- `index.html` — фронтенд на Vega-Lite, который запрашивает `/api/kid-control`.
- `Dockerfile` — образ Nginx, отдаёт фронтенд и проксирует `/api/` на сервис `proxy`.
- `proxy/` — Node/Express-прокси, обращается к REST-эндпоинту MikroTik и возвращает JSON.
- `docker-compose.yml` — конфигурация docker compose для сервисов `web` (nginx) и `proxy`.
- `.env.example` — пример файла с переменными окружения для прокси.

## Архитектура

Nginx (порт 3030) отдаёт статические файлы и перенаправляет запросы к `/api/*` на внутренний сервис `proxy`. Прокси выполняет HTTP-запросы к роутеру MikroTik и возвращает JSON. Браузер общается только с Nginx, поэтому всё происходит в рамках одного origin и проблем с CORS нет.

## Быстрый старт (Raspberry Pi)

1. Скопируйте шаблон переменных окружения и заполните значения:

   ```bash
   cp .env.example .env
   ```

2. Укажите в `.env` URL в переменной `TARGET` (REST-эндпоинт MikroTik) и значение `BASIC_AUTH` (см. раздел ниже о генерации).

3. Соберите и запустите контейнеры (должны быть установлены Docker и плагин docker compose):

   ```bash
   docker compose up -d --build
   ```

4. Откройте интерфейс в браузере по адресу `http://<ip-адрес-pi>:3030`.

Фронтенд всегда обращается к локальному прокси по пути `/api/kid-control`. Все чувствительные данные задаются через `.env` и обрабатываются на серверной стороне, поэтому учётные данные не попадают в браузер или localStorage.

## Генерация `BASIC_AUTH`

HTTP Basic Authentication использует заголовок вида:

```
Authorization: Basic <base64(username:password)>
```

Примеры для строки `kidreader:[SOME-PWD]`:

- macOS/Linux (`bash`/`zsh`):

  ```bash
  B64=$(printf '%s' 'kidreader:[SOME-PWD]' | base64 | tr -d '\n')
  echo "BASIC_AUTH=Basic $B64" >> .env
  ```

  Или, если `.env` уже создан:

  ```bash
  printf "BASIC_AUTH=Basic %s\n" "$(printf '%s' 'kidreader:[SOME-PWD]' | base64 | tr -d '\n')" >> .env
  ```

- Windows PowerShell:

  ```powershell
  $bytes = [System.Text.Encoding]::UTF8.GetBytes('kidreader:[SOME-PWD]')
  $b64 = [Convert]::ToBase64String($bytes)
  Add-Content -Path .env -Value "BASIC_AUTH=Basic $b64"
  ```

После этого в `.env` должна появиться строка наподобие:

```
BASIC_AUTH=Basic a2lkcmVhZGVyOjhCaE5YZ3NuQmlNVytjSk0=
```

> Важно: не коммитьте `.env` в репозиторий. Добавьте его в `.gitignore` и ограничьте права доступа:
>
> ```bash
> echo ".env" >> .gitignore
> chmod 600 .env
> ```

## Фронтенд и API

- Фронтенд жёстко привязан к эндпоинту `/api/kid-control`.
- Внутри docker-сети Nginx проксирует `/api` на `proxy:4000`, поэтому браузер не делает кросс-доменные запросы.

Для отладки можно открыть сам прокси (например, `http://localhost:4000/api/kid-control`), но по умолчанию docker-compose публикует только порт Nginx.

## Полезные команды docker compose

```bash
# Собрать образы и запустить контейнеры в фоне
docker compose up -d --build

# Перезапустить только сервис proxy
docker compose restart proxy

# Проверить состояние контейнеров
docker compose ps

# Смотреть логи
docker compose logs -f web
docker compose logs -f proxy

# Остановить и удалить контейнеры
docker compose down
```

## Рекомендации по безопасности

- Не храните секреты в репозитории; для production используйте Docker secrets или внешний vault.
- Ограничьте доступ к Pi и портам с помощью firewall (ufw, iptables), если сервис нужен только в локальной сети.
- При публикации в интернет включайте HTTPS и защиту интерфейса (дополнительная аутентификация, rate limiting).

## Отладка

- Если Nginx отдаёт `502 Bad Gateway`, убедитесь, что MikroTik доступен с устройства, где работает прокси:

  ```bash
  curl -v http://192.168.88.254/rest/ip/kid-control
  ```

- Проверьте логи прокси:

  ```bash
  docker compose logs -f proxy
  ```

## Возможные улучшения

- Добавить кеширование в прокси, чтобы уменьшить количество обращений к MikroTik.
- Реализовать rate limiting и дополнительную аутентификацию UI.
- Настроить автоматический выпуск TLS (Let's Encrypt) и принудительный редирект на HTTPS.
