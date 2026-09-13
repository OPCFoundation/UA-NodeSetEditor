# Running the NodeSet Editor in a container

The image contains the application only. A PostgreSQL DB has to be provided by the user. In addition a SMTP mail
server that delivers sign-in codes. Backups, upgrades and high availability of the database are
outside the image entirely.

The design behind these choices is in [DESIGN.md](DESIGN.md).

## Build

From the repository root, with the `UANodeSetSerializer` submodule checked out:

```bash
docker build -f docker/Dockerfile -t nodeset-editor:dev .
```

The build downloads the Core OPC UA NodeSet from the `latest` branch of
[OPCFoundation/UA-Nodeset](https://github.com/OPCFoundation/UA-Nodeset) — the same source
`db/initialize_db.ps1` uses — and bakes it in, so first-run initialization needs no internet
access. Which version you get is therefore fixed when the image is built; the build log prints
it. To pin a specific release instead of tracking `latest`:

```bash
docker build -f docker/Dockerfile -t nodeset-editor:1.05.07 \
  --build-arg UA_CORE_NODESET_URL=https://raw.githubusercontent.com/OPCFoundation/UA-Nodeset/refs/tags/<tag>/Schema/Opc.Ua.NodeSet2.xml .
```

It is deliberately *not* vendored as a pinned copy: such a copy is pinned to
whatever commit the tool was last built against, and trails the published release.

## 1. Create the database

The image does **not** create the database — on managed PostgreSQL that is a portal or IaC
operation. Create an empty database, then let the container populate it:

```bash
docker run --rm \
  -e ConnectionStrings__Postgres="Host=db.example.com;Database=nodeseteditor;Username=nodeset-editor-app;Password=…" \
  nodeset-editor:dev initialize
```

That creates the schema and imports the Core OPC UA NodeSet bundled in the image (no internet
access needed). It is safe to re-run: the schema step only adds missing tables.

> **It only adds.** The schema step cannot alter an existing table, so an image whose model has
> changed cannot upgrade a database in place yet. Until migrations exist, treat a schema change
> as "export your models, recreate the database". See DESIGN.md §3.

The role in the connection string needs DDL rights for `initialize`. For everyday running, use a
least-privilege role instead — `db/grant_webapp.sql` (also at `/app/seed/grant_webapp.sql` in the
image) defines the grants.

### Which hostname to use

`localhost` and `127.0.0.1` inside a container mean *the container*, so a connection string
pointing there fails with `Connection refused` even though PostgreSQL is running fine on your
machine. Use one of these instead:

| Where PostgreSQL runs | `Host=` |
| --- | --- |
| Installed on this machine, Docker Desktop (Windows/macOS) | `host.docker.internal` |
| Installed on this machine, Docker Engine on Linux | `host.docker.internal`, with `--add-host=host.docker.internal:host-gateway` |
| Another container on the same user-defined network | the container's name, e.g. `nse-pg` |
| A managed service | its own hostname |

The same applies to `Smtp__Host` when the mail server is on your machine.

A local PostgreSQL install usually needs two changes before it will accept a connection from a
container, because it listens on loopback only:

* `postgresql.conf`: `listen_addresses = '*'`
* `pg_hba.conf`: a line for Docker's subnet, typically
  `host all all 192.168.65.0/24 scram-sha-256` — if it is rejected, the PostgreSQL log names the
  address that actually arrived, which is the one to allow.

Then restart the service (`Restart-Service postgresql-x64-18` on Windows).

`Connection refused` means nothing is listening on that address; a connection that *hangs* is a
firewall dropping packets — on Windows, allow inbound TCP 5432 on the Docker/WSL adapter. To tell
them apart from inside a container:

```bash
docker run --rm nodeset-editor:dev sh -c "curl -sv telnet://host.docker.internal:5432 --max-time 5 2>&1 | tail -3"
```

Running PostgreSQL as a container on a shared network sidesteps all of this:

```bash
docker network create nse
docker run -d --name nse-pg --network nse -e POSTGRES_PASSWORD=… -e POSTGRES_DB=nodeseteditor postgres:18
docker run --rm --network nse \
  -e ConnectionStrings__Postgres="Host=nse-pg;Database=nodeseteditor;Username=postgres;Password=…" \
  nodeset-editor:dev initialize
```

## 2. Run

```bash
docker run -d -p 8080:8080 \
  -e ConnectionStrings__Postgres="Host=db.example.com;Database=nodeseteditor;Username=nodeset-editor-app;Password=…" \
  -e EmailAuth__CookieSecret="$(openssl rand -base64 32)" \
  -e Smtp__Host=smtp.example.com \
  -e Smtp__From=noreply@example.com \
  -e Smtp__User=apikey -e Smtp__Password=… \
  -e Auth__AllowedEmailDomains=example.com \
  nodeset-editor:dev
```

The app serves plain HTTP on 8080 and expects a reverse proxy or ingress to terminate TLS.

Two settings will bite if you skip them:

* **`EmailAuth__CookieSecret`** signs session cookies. Restarting with a different value signs
  everyone out, so set it explicitly rather than letting each deployment invent one.
* **`Auth__AllowedEmailDomains`** limits who may request a sign-in code. Leave it unset and
  anyone who can reach the port can create an account — on the hosted deployment the Azure AD
  tenant is the perimeter, and a self-hosted instance has no equivalent.

## Evaluating it without a mail server

Sign-in codes arrive by email, so a deployment with no SMTP has no way for anyone to sign in.
The container refuses to start in that state rather than presenting a login screen that cannot
work. To try the application anyway:

```bash
docker run -d -p 8080:8080 \
  -e ConnectionStrings__Postgres="…" \
  -e EmailAuth__CookieSecret="anything-for-a-throwaway-instance" \
  -e Auth__RequireSecureCookie=false \
  -e TestMode__Enabled=true \
  nodeset-editor:dev
```

The login screen then offers **Continue in Test Mode**: one shared account named "Test Mode",
with every feature switched on, including the beta download formats. Everyone who signs in this
way is the same user and sees the same models. Point it at a throwaway database, and do not
expose it to a network you do not control.

## Configuration

Standard ASP.NET Core environment-variable binding — `__` is the nesting separator.

| Variable | Required | Notes |
| --- | --- | --- |
| `ConnectionStrings__Postgres` | Yes | Least-privilege role for running; DDL rights for `initialize` |
| `EmailAuth__CookieSecret` | Yes | At least 16 characters. Changing it invalidates every session |
| `Smtp__Host` | Yes, unless test mode | Unset means no mail provider |
| `Smtp__Port` | | Default 587 |
| `Smtp__Security` | | `StartTls` (default), `SslOnConnect` (port 465), `None`, `Auto` |
| `Smtp__User` / `Smtp__Password` | | Omit for an unauthenticated relay |
| `Smtp__From` | Yes, with `Smtp__Host` | Envelope sender |
| `Smtp__FromName` | | Defaults to "NodeSet Editor" |
| `Smtp__AcceptInvalidCertificate` | | `true` for a relay with a self-signed certificate |
| `TestMode__Enabled` | | `true` enables the shared "Test Mode" account |
| `Auth__AllowedEmailDomains` | | Comma-separated domains/addresses. Empty admits everyone |
| `Auth__RequireSecureCookie` | | Default `true`; see below |
| `CloudLibraryUrl` | | Unset hides the Cloud Library feature |
| `CloudLibraryClientId` / `CloudLibraryClientSecret` | | Basic auth for the Cloud Library |
| `CloudLibraryModelFilters` | | Comma-separated namespaces to exclude from results |
| `BetaTesterDomains` | | Who may use the JSON / JSON-LD / archive download formats |
| `AzureAd__ClientId` | | Off in this image; see below |
| `ASPNETCORE_URLS` | | Default `http://+:8080` |

### `Auth__RequireSecureCookie`

The session cookie is marked `Secure` by default, and browsers will not send a `Secure` cookie
over plain HTTP to anything but `localhost`. Behind a TLS-terminating proxy that is exactly
right. Reaching the container directly over `http://192.168.x.x:8080`, it means sign-in appears
to succeed and the next request is anonymous again. Set it to `false` for that case only.

### Azure AD

This image ships with Azure AD sign-in off: `AzureAd__ClientId` is emptied in the Dockerfile,
because `appsettings.json` carries the OPC Foundation tenant's client id, which a self-hosted
deployment has no claim on. The server then reports email-code sign-in only, and the SPA hides
the Microsoft button.

To use your own tenant, build with the SPA's MSAL values and set the matching server settings:

```bash
docker build -f docker/Dockerfile -t nodeset-editor:aad \
  --build-arg VITE_MSAL_CLIENT_ID=<client-id> \
  --build-arg VITE_MSAL_AUTHORITY=https://login.microsoftonline.com/<tenant-id> \
  --build-arg VITE_MSAL_SCOPE=api://<client-id>/<scope> \
  --build-arg VITE_REDIRECT_URL=https://your-host/login/success .

docker run … -e AzureAd__ClientId=<client-id> -e AzureAd__TenantId=<tenant-id> \
             -e AzureAd__Audience=<client-id> nodeset-editor:aad
```

## Other verbs

```bash
docker run --rm -e ConnectionStrings__Postgres="…" nodeset-editor:dev dbtool status
docker run --rm -e ConnectionStrings__Postgres="…" nodeset-editor:dev dbtool import-file /path/to.xml
docker run --rm -it nodeset-editor:dev sh
```

`serve` is the default and needs no verb.

## Health

`GET /health` is anonymous. It returns 200 when the database answers and 503 when it does not, so
an instance that cannot serve is not marked healthy. `HEALTHCHECK` in the image already uses it.

## What the SPA learns at runtime

`GET /api/config` reports which sign-in providers exist, whether test mode is on, and whether a
Cloud Library is configured. The SPA reads it before rendering, which is why one image can serve
a deployment with Azure AD and one without. It exposes no credentials — only which features are
switched on.
