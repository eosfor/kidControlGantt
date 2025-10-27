# MikroTik Kid Control Gantt

Static web UI backed by a Node/Express proxy for visualising MikroTik Kid Control schedules as a Gantt chart.

## Repository contents

- `index.html` — Vega-Lite frontend that fetches `/api/kid-control`.
- `Dockerfile` — Nginx image serving the frontend and proxying `/api/` requests to the proxy service.
- `proxy/` — Node/Express proxy that calls the MikroTik REST endpoint and returns JSON.
- `docker-compose.yml` — compose definition for the `web` (nginx) and `proxy` services.
- `.env.example` — sample environment file for the proxy.

## Architecture

Nginx (exposed on port 3030) serves the static files and forwards `/api/*` requests to the internal `proxy` service. The proxy performs HTTP requests to the MikroTik router and returns the result as JSON. Because the browser only talks to Nginx, all traffic stays same-origin and CORS is avoided.

## Quick start (Raspberry Pi)

1. Copy the sample environment file and populate the values:

   ```bash
   cp .env.example .env
   ```

2. Set the `TARGET` URL (MikroTik REST endpoint) and `BASIC_AUTH` header value in `.env` (see the section below on generating it).

3. Build and start the containers (Docker Engine and the docker compose plugin must be installed):

   ```bash
   docker compose up -d --build
   ```

4. Open the UI in your browser at `http://<your-pi-ip>:3030`.

The frontend always talks to the local proxy at `/api/kid-control`. All sensitive values are provided via `.env` and handled on the server side so no credentials ever reach the browser or local storage.

## Generate `BASIC_AUTH`

HTTP Basic authentication expects a header of the form:

```
Authorization: Basic <base64(username:password)>
```

Examples for the string `kidreader:[SOME-PWD]`:

- macOS/Linux (`bash`/`zsh`):

  ```bash
  B64=$(printf '%s' 'kidreader:[SOME-PWD]' | base64 | tr -d '\n')
  echo "BASIC_AUTH=Basic $B64" >> .env
  ```

  Or, if `.env` already exists:

  ```bash
  printf "BASIC_AUTH=Basic %s\n" "$(printf '%s' 'kidreader:[SOME-PWD]' | base64 | tr -d '\n')" >> .env
  ```

- Windows PowerShell:

  ```powershell
  $bytes = [System.Text.Encoding]::UTF8.GetBytes('kidreader:[SOME-PWD]')
  $b64 = [Convert]::ToBase64String($bytes)
  Add-Content -Path .env -Value "BASIC_AUTH=Basic $b64"
  ```

Afterwards `.env` should contain a line similar to:

```
BASIC_AUTH=Basic a2lkcmVhZGVyOjhCaE5YZ3NuQmlNVytjSk0=
```

> Important: Do not commit `.env` to source control. Add it to `.gitignore` and lock file permissions to the current user:
>
> ```bash
> echo ".env" >> .gitignore
> chmod 600 .env
> ```

## Frontend and API usage

- The frontend is hardcoded to call `/api/kid-control`.
- Inside the Docker network, Nginx proxies `/api` to `proxy:4000`, so the browser never makes cross-origin requests.

If you need to access the proxy directly for debugging, expose port 4000 or run it locally and call `http://localhost:4000/api/kid-control`. The default compose file only exposes Nginx.

## Useful docker compose commands

```bash
# Build images and start containers in the background
docker compose up -d --build

# Restart only the proxy service
docker compose restart proxy

# Show container status
docker compose ps

# Tail logs
docker compose logs -f web
docker compose logs -f proxy

# Stop and remove containers
docker compose down
```

## Security recommendations

- Keep secrets out of the repository; consider Docker secrets or an external vault for production.
- Restrict access to the Pi/ports with a firewall (ufw, iptables) if the UI is for your local network only.
- If the service is exposed to the internet, enforce HTTPS and protect the UI (e.g., additional auth, rate limiting).

## Troubleshooting

- `502 Bad Gateway` from Nginx: confirm the MikroTik REST endpoint is reachable from the Pi:

  ```bash
  curl -v http://192.168.88.254/rest/ip/kid-control
  ```

- Check proxy logs for errors:

  ```bash
  docker compose logs -f proxy
  ```

## Potential improvements

- Add caching in the proxy to reduce load on the MikroTik API.
- Implement rate limiting and authentication for the UI.
- Automate TLS (e.g., Let's Encrypt) and redirect HTTP to HTTPS.
