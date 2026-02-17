# MikroTik Kid Control Access Manager

Web UI + backend proxy for managing MikroTik Kid Control access windows with daily limits.

## Features

- Reads users from MikroTik `/rest/ip/kid-control`.
- Displays only users listed in `proxy/config/kid-access-config.json`.
- Shows per-user daily limit, remaining time, active window timer, window size selector, and action buttons.
- `Request/Extend` grants or extends access from now.
- `Disable` stops active session and disables user access immediately.
- Stores runtime session state in SQLite (persisted in Docker volume).

## Main files

- `index.html` - table-based UI.
- `proxy/index.js` - API/backend logic and MikroTik REST integration.
- `proxy/config/kid-access-config.json` - day-based limits and user allowlist.
- `docker-compose.yml` - `web` + `proxy` services with persistent DB volume.

## Run

```bash
cp .env.example .env
# fill credentials and URL
docker compose up -d --build
```

Open: `http://<host>:3030`

## Security notes

- Keep real credentials out of git.
- Do not commit `.env`.
- Prefer Docker secrets for production deployments.
