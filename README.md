# Kid Control Gantt (.NET)

This branch rewrites the backend functionality to ASP.NET Core.

## Implemented

- MikroTik Kid Control API on .NET:
  - `GET /api/kid-control`
  - `GET /api/state`
  - `POST /api/users/{name}/request`
  - `POST /api/users/{name}/disable`
  - `GET /api/debug`
  - `GET /health`
- Day-based user limits from JSON config.
- UI shows only users listed in config.
- Access window grant/extend with `limit + grace` cap.
- Manual disable support.
- SQLite state storage and timer persistence.
- Background sweep for expired sessions.

## Main files

- `app/Program.cs` - API and business logic.
- `app/wwwroot/index.html` - management page.
- `config/kid-access-config.json` - active limits config.
- `Dockerfile` / `docker-compose.yml` - container runtime.

## Run

```bash
cp .env.example .env
# set MikroTik credentials
docker compose up -d --build
```

Open: `http://<host>:3030`
