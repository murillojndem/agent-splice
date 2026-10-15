# Installing AgentSplice

AgentSplice is an experimental alpha. It forwards requests from an OpenAI-compatible client to a
local model runtime such as LM Studio, unchanged, and records where the time went — structural
metadata only, never prompts, model output, or tool arguments. No compatibility claim is made for any
client, model, or runtime (`docs/CONFORMANCE.md`).

Whichever way it is installed, the gateway listens on `http://127.0.0.1:5280` only, serves its
dashboard at `http://127.0.0.1:5280/dashboard/`, and forwards to the runtime it is configured with —
LM Studio's `http://127.0.0.1:1234/v1` by default. Point a client at `http://127.0.0.1:5280/v1`.

## Choosing a path

| Path | Needs | Choose it when |
| --- | --- | --- |
| [Docker](#docker) | Docker with Compose v2 | Docker is already there. The store lives in a volume, and an administrative token is required. |
| [Windows package](#windows-package) | Windows 10 or 11, x64 | You want no Docker and no .NET installation: a folder you extract and run. |
| [From source](#from-source) | .NET 8 SDK; Node 22 and pnpm 10 for the dashboard | You are developing AgentSplice, or running it directly on Linux or macOS. |

## Docker

Docker Desktop, or Docker Engine 25 or later with the Compose plugin. Clone the repository, then from
its root choose an administrative token and start the stack. The token is required: the container
binds every interface inside itself and cannot tell that Compose publishes the port on loopback only,
so the gateway refuses to serve stored evidence without one (`docs/SECURITY.md`).

```bash
cp .env.example .env
echo "AGENTSPLICE_ADMIN_API_KEY=$(openssl rand -hex 32)" >> .env
docker compose up --build --wait
```

In PowerShell:

```powershell
Copy-Item .env.example .env
$bytes = [byte[]]::new(32); [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
Add-Content .env ("AGENTSPLICE_ADMIN_API_KEY=" + (($bytes | ForEach-Object { $_.ToString('x2') }) -join ''))
docker compose up --build --wait
```

`--wait` returns once the image's own health check reports the gateway healthy (ADR 0016), and
`docker compose ps` shows the status afterwards. Open `http://127.0.0.1:5280/` and paste the token from
`.env` into the dashboard's token field; the page keeps it in memory only.

**Reaching LM Studio.** The container reaches the host as `host.docker.internal`, and the Compose file
points the runtime at `http://host.docker.internal:1234/v1`. Docker Desktop forwards that to the host
for you. On a Linux engine, `host.docker.internal` is the host's bridge address, so the runtime must
listen on an address the container can reach rather than on `127.0.0.1` alone. Override the URL in
`.env` with `AGENTSPLICE__RUNTIMES__0__BASEURL`.

**Data.** Trace metadata is kept in `/data/agentsplice.db` on the `agentsplice-data` volume; the
startup log names it as event `1305`. To back it up, stop the gateway first so SQLite's write-ahead log
is folded into the file, then copy it out:

```bash
docker compose stop agentsplice
docker compose cp agentsplice:/data/agentsplice.db ./agentsplice-backup.db
docker compose start agentsplice
```

**Upgrading and removing.** Pull the new revision and run `docker compose up --build --wait` again;
the volume is kept. `docker compose down` stops and removes the container and keeps the data;
`docker compose down --volumes` deletes the data too.

## Windows package

A self-contained build for 64-bit Windows 10 or 11 (ADR 0017). It carries its own .NET runtime and the
dashboard, so nothing needs to be installed first.

### Download and verify

Download `agentsplice-<version>-win-x64.zip` and `SHA256SUMS` from the repository's GitHub Releases
page. Every release so far is a pre-release. Until the first one is published, each CI run's
`agentsplice-win-x64` artifact holds the same two files, which a signed-in GitHub user can download.

Check the archive against its published digest before opening it:

```powershell
$zip = 'agentsplice-0.2.0-alpha-win-x64.zip'   # the file you downloaded
$expected = ((Select-String -Path SHA256SUMS -SimpleMatch $zip).Line -split '\s+')[0]
(Get-FileHash -Algorithm SHA256 $zip).Hash.ToLower() -eq $expected   # must print True
```

GitHub also shows each release asset's SHA-256 digest on the release page. The package is not code
signed (`docs/SECURITY.md`); the digest is what tells you the file is the one the release built.

### Extract

Remove the downloaded-from-the-internet mark before extracting, because Windows copies it onto every
extracted file, then extract into a folder only your account can write:

```powershell
Unblock-File $zip
Expand-Archive $zip -DestinationPath "$env:LOCALAPPDATA\Programs\AgentSplice"
```

That gives `%LOCALAPPDATA%\Programs\AgentSplice\agentsplice-<version>-win-x64\`, which holds
`AgentSplice.Api.exe`, `e_sqlite3.dll`, `appsettings.json`, the `dashboard` folder, and `README.txt`.
A per-user folder matters for two reasons. By default the gateway keeps its store beside the
executable, so the folder must be writable by you. And whoever can write the folder can replace the
executable and the dashboard's script, so it should be writable by nobody else; a folder created
directly under `C:\` is writable by every signed-in user (`docs/THREAT_MODEL.md`).

### Run

```powershell
& "$env:LOCALAPPDATA\Programs\AgentSplice\agentsplice-0.2.0-alpha-win-x64\AgentSplice.Api.exe"
```

It can be started from any folder: it reads its settings and serves its dashboard from beside the
executable, and writes nothing to the folder it was started from. The console shows the startup log,
including where the dashboard (`1203`) and the store (`1305`) are. Stop it with Ctrl+C.

Windows may stop an unsigned program it has not seen before. If **Windows protected your PC** appears,
choose **More info**, then **Run anyway**. If Smart App Control is on, it can block unsigned programs
with no way to allow one; use Docker or run from source instead. The gateway listens on loopback only,
so Windows Firewall has nothing to ask about.

Open `http://127.0.0.1:5280/` for the dashboard and point clients at `http://127.0.0.1:5280/v1`.

### Administrative token

With no token configured, the gateway serves its administrative API and dashboard data to any caller
on this machine, and refuses to start on any binding another machine could reach. That is the
default, and it leaves one documented gap: a web page you visit could use DNS rebinding to read the
administrative API from your browser (`docs/THREAT_MODEL.md`). Setting a token closes it:

```powershell
$env:AGENTSPLICE__ADMINISTRATION__APIKEYENVIRONMENTVARIABLE = 'AGENTSPLICE_ADMIN_API_KEY'
$env:AGENTSPLICE_ADMIN_API_KEY = 'a long random value you choose'
& "$env:LOCALAPPDATA\Programs\AgentSplice\agentsplice-0.2.0-alpha-win-x64\AgentSplice.Api.exe"
```

The gateway is told the variable's name and never stores its value. Paste the same value into the
dashboard's token field.

### Configure

Settings are the `agentsplice` section of `appsettings.json`. Rather than editing that file, which an
upgrade replaces, pass a setting on the command line or as an environment variable, which the gateway
reads in that order of precedence:

```powershell
# A different runtime address, on the command line
.\AgentSplice.Api.exe --agentsplice:runtimes:0:baseUrl=http://127.0.0.1:8080/v1

# The same, as an environment variable for this session, or permanently for your account
$env:AGENTSPLICE__RUNTIMES__0__BASEURL = 'http://127.0.0.1:8080/v1'
[Environment]::SetEnvironmentVariable('AGENTSPLICE__RUNTIMES__0__BASEURL', 'http://127.0.0.1:8080/v1', 'User')

# Another port, still loopback
.\AgentSplice.Api.exe --urls http://127.0.0.1:5290
```

`docs/DEVELOPMENT.md` lists how setting names map to environment variables, and
`docs/SPECIFICATION.md` section 12 describes the settings. A runtime's API key is configured the same
way the token is: the setting names an environment variable, never the key itself.

### Data

Trace metadata is kept in `agentsplice.db` beside the executable unless you choose another file. To
keep it somewhere an upgrade will not touch, create the folder first — the gateway does not create
one — and point the setting at it:

```powershell
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\AgentSplice" | Out-Null
[Environment]::SetEnvironmentVariable('AGENTSPLICE__PERSISTENCE__CONNECTIONSTRING', "Data Source=$env:LOCALAPPDATA\AgentSplice\agentsplice.db", 'User')
```

A relative path in that setting is resolved against the package folder. To back the store up, stop the
gateway first, then copy `agentsplice.db`, and `agentsplice.db-wal` and `agentsplice.db-shm` if they
are still there. `agentsplice:persistence:mode` set to `None` keeps nothing at all.

### Upgrade and uninstall

To upgrade, stop the gateway and extract the new version beside the old one. If the store is in the
old folder, copy it across; settings kept in environment variables carry over by themselves. Then
delete the old folder.

The package carries its own .NET runtime, so Windows Update and .NET installers never patch it:
upgrading AgentSplice is how it receives .NET security fixes. `README.txt` in each package names the
runtime version it was built with.

To uninstall, stop the gateway, delete its folder and `%LOCALAPPDATA%\AgentSplice` if you used it, and
remove any `AGENTSPLICE__*` variables you set for your account.

## From source

With the .NET 8 SDK installed (`docs/DEVELOPMENT.md` lists the prerequisites), from the repository
root on Windows, Linux, or macOS:

```powershell
dotnet run --project src/AgentSplice.Api
```

The working directory is the project directory, so the store is `src/AgentSplice.Api/agentsplice.db`
and settings are read from `src/AgentSplice.Api/appsettings.json`. A fresh clone has no dashboard build;
`docs/DEVELOPMENT.md` shows how to build one and point the gateway at it.

A framework-dependent build needs only the ASP.NET Core 8 runtime where it runs:

```powershell
dotnet publish src/AgentSplice.Api -c Release -o out
pnpm --dir web/AgentSplice.Dashboard install --frozen-lockfile
pnpm --dir web/AgentSplice.Dashboard build
Copy-Item -Recurse web/AgentSplice.Dashboard/dist out/dashboard
Set-Location out; dotnet AgentSplice.Api.dll
```

Start it from inside `out`: unlike the Windows package, a framework-dependent build takes its content
root from the working directory, as every ASP.NET Core application does.

## Checking that it is up

`GET /health/live` answers `204` whenever the process is serving, and `GET /health/ready` answers
`200` — or `503` only when a deployment has asked readiness to require a reachable runtime. Neither
needs a token.

```powershell
curl.exe -i http://127.0.0.1:5280/health/live
```

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `1204 DashboardBuildMissing` at startup; nothing at `/dashboard/` | No build where `agentsplice:dashboard:rootPath` points. The image and the Windows package carry one; from source, build it first (`docs/DEVELOPMENT.md`). The APIs work regardless. |
| `The metadata store '…' could not be opened for writing` | The store's directory does not exist or is not writable. Create it, choose another file, or set `agentsplice:persistence:mode` to `None`. |
| `Failed to bind to address http://127.0.0.1:5280` | Something else is using the port. Stop it, or pass `--urls http://127.0.0.1:<another port>`. |
| Startup refuses a binding beyond loopback | Serving stored evidence to a network requires an administrative token (`docs/SECURITY.md`). |
| A setting seems to be ignored | Environment variables use double underscores (`AGENTSPLICE__PERSISTENCE__MODE`), and a command-line argument wins over both a variable and `appsettings.json`. |
| Requests fail with `agentsplice_runtime_unavailable` | The runtime is not listening where the gateway was told. Check that its server is started and the URL, including `/v1`. |
