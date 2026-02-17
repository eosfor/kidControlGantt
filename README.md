# Kid Control Gantt (.NET)

ASP.NET Core web app for managing `MikroTik Kid Control` with daily limits and access windows.

## What it does

- Reads users from `MikroTik /rest/ip/kid-control`.
- Shows only users listed in limits config.
- Per user UI shows:
  - current day limit,
  - remaining time to limit,
  - active window timer,
  - window size,
  - `Request/Extend` and `Disable` actions.
- `Request/Extend` updates MikroTik and opens/extends access.
- `Disable` stops active session and closes access in MikroTik.
- Session state is stored in SQLite.

## Limit rules

- Day limits are configured per weekday (`mon..sun`) in minutes.
- `Request/Extend` stays enabled while `used < dayLimit`.
- Hard daily ceiling is `dayLimit + graceMinutes`.
- If requested window is larger than available cap, it is truncated.
- UI window size is clamped to `0..dayLimit`.

## Architecture

- `app/Program.cs` - API + business logic + MikroTik integration + background sweep.
- `app/wwwroot/index.html` - management table UI and timers.
- `config/kid-access-config.json` - active limits config.
- `config/kid-access-config.example.json` - sample config.
- SQLite path comes from `DB_PATH` (default `/data/kid-control-state.db`).

## Limits config

File: `config/kid-access-config.json`

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

If a user exists in MikroTik but is missing in `users`, it is hidden from UI.

## Environment variables

See `.env.example`.

- `MIKROTIK_KID_CONTROL_URL` - full REST endpoint URL.
- `MIKROTIK_BASE_URL` - fallback base URL.
- `MIKROTIK_USER`, `MIKROTIK_PASSWORD` - MikroTik REST credentials.
- `BASIC_AUTH` - optional auth header override.
- `DB_PATH` - SQLite DB path.
- `LIMITS_CONFIG_PATH` - limits config path.
- `CONFIG_CACHE_TTL_MS` - config cache TTL.
- `SWEEP_INTERVAL_SECONDS` - expired session cleanup interval.

## API

- `GET /health` - healthcheck.
- `GET /api/kid-control` - raw MikroTik kid-control list.
- `GET /api/state` - UI state payload.
- `POST /api/users/{name}/request`
  - body: `{ "windowMinutes": 120 }`
- `POST /api/users/{name}/disable`
- `GET /api/debug`

## Run locally

```bash
dotnet restore app/KidControlGantt.App.csproj
dotnet run --project app/KidControlGantt.App.csproj
```

## Run with Docker

```bash
cp .env.example .env
# set MIKROTIK_USER / MIKROTIK_PASSWORD

docker compose up -d --build
```

Open: `http://<host>:3030`

## Run in Docker Desktop (step-by-step)

1. Start Docker Desktop and wait for `Engine running`.
2. Open terminal and go to the project:
   ```bash
   cd /Users/andrei/repo/kidControlGantt
   ```
3. Check root `.env` (`/Users/andrei/repo/kidControlGantt/.env`):
   - `MIKROTIK_KID_CONTROL_URL`
   - `MIKROTIK_USER`
   - `MIKROTIK_PASSWORD`
4. Check limits file `config/kid-access-config.json`.
5. Start container:
   ```bash
   docker compose up -d --build
   ```
6. Validate endpoints:
   - UI: `http://localhost:3030`
   - health: `http://localhost:3030/health`

Useful commands:

```bash
docker compose logs -f app
docker compose ps
docker compose down
```

## Security

- Do not commit `.env`.
- Do not store real passwords in git.
- Use secrets/vault for production.
