# Installing AgentSplice

AgentSplice is an experimental alpha. It forwards requests from an OpenAI-compatible client to a
local model runtime such as LM Studio, unchanged, and records where the time went — structural
metadata only, never prompts, model output, or tool arguments. No compatibility claim is made for any
client, model, or runtime (`docs/CONFORMANCE.md`).

Whichever way it is installed, the gateway listens on `http://127.0.0.1:5280` only, serves its
dashboard at `http://127.0.0.1:5280/dashboard/`, and forwards to the runtime it is configured with —
LM Studio's `http://127.0.0.1:1234/v1` by default. Point a client at `http://127.0.0.1:5280/v1`;
`docs/CLIENTS.md` shows how for OpenCode, Cline, and any OpenAI-compatible client.

## Choosing a path

| Path | Needs | Choose it when |
| --- | --- | --- |
| [Docker](#docker) | Docker; Compose v2 to build from a clone | Docker is already there: the published image needs nothing from the repository. The store lives in a volume, and an administrative token is required. |
| [Windows package](#windows-package) | Windows 10 or 11, x64 | You want no Docker and no .NET installation: a folder you extract and run. |
| [Linux package](#linux-package) | An x64 distribution based on glibc 2.34 or later, with OpenSSL 1.1.1 or later | The same on Linux: no Docker and no .NET installation. |
| [From source](#from-source) | .NET 10 SDK; Node 22 and pnpm 10 for the dashboard | You are developing AgentSplice, or running it directly on macOS or another platform without a package. |

## Docker

Docker Desktop, or Docker Engine 25 or later. There are two ways in:

- **the published image**, which needs nothing from the repository: a release's image is pushed to
  GitHub Container Registry when the release is published (ADR 0022);
- **Compose from a clone**, which builds the image from the source you have and can add the optional
  OpenTelemetry collector.

Either way an administrative token is required: the container binds every interface inside itself and
cannot tell that the port is published on loopback only, so the gateway refuses to start without one
(`docs/SECURITY.md`).

### Published image

The image is `ghcr.io/murillojndem/agent-splice`, tagged with each release's version — never `latest` —
and built for `linux/amd64` only; on an Apple silicon Mac, Docker Desktop runs it under emulation. Until
the first release is published there is no package at all, so a pull fails — `denied`, or `403 Forbidden`
while fetching a token — and signing in to the registry does not help: use Compose from a clone instead.
After that, `manifest unknown` means that the version you named has no image.

Choose a token, then start it:

```bash
export AGENTSPLICE_ADMIN_API_KEY="$(openssl rand -hex 32)"
echo "$AGENTSPLICE_ADMIN_API_KEY"   # paste this into the dashboard's token field
docker run --detach --name agentsplice \
  --publish 127.0.0.1:5280:5280 \
  --volume agentsplice-data:/data \
  --add-host host.docker.internal:host-gateway \
  --env 'AGENTSPLICE__PERSISTENCE__CONNECTIONSTRING=Data Source=/data/agentsplice.db' \
  --env AGENTSPLICE__RUNTIMES__0__BASEURL=http://host.docker.internal:1234/v1 \
  --env AGENTSPLICE__ADMINISTRATION__APIKEYENVIRONMENTVARIABLE=AGENTSPLICE_ADMIN_API_KEY \
  --env AGENTSPLICE_ADMIN_API_KEY \
  ghcr.io/murillojndem/agent-splice:0.2.0-alpha
```

In PowerShell:

```powershell
$bytes = [byte[]]::new(32); [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$env:AGENTSPLICE_ADMIN_API_KEY = ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
$env:AGENTSPLICE_ADMIN_API_KEY   # paste this into the dashboard's token field
docker run --detach --name agentsplice `
  --publish 127.0.0.1:5280:5280 `
  --volume agentsplice-data:/data `
  --add-host host.docker.internal:host-gateway `
  --env 'AGENTSPLICE__PERSISTENCE__CONNECTIONSTRING=Data Source=/data/agentsplice.db' `
  --env AGENTSPLICE__RUNTIMES__0__BASEURL=http://host.docker.internal:1234/v1 `
  --env AGENTSPLICE__ADMINISTRATION__APIKEYENVIRONMENTVARIABLE=AGENTSPLICE_ADMIN_API_KEY `
  --env AGENTSPLICE_ADMIN_API_KEY `
  ghcr.io/murillojndem/agent-splice:0.2.0-alpha
```

Each flag matters, and CI runs this command, flag for flag, against the image a release carries:

- `--publish 127.0.0.1:5280:5280` publishes the port on the host's loopback only;
- the volume and the connection string keep the store at `/data/agentsplice.db` on a volume that a new
  container finds again — without them it lives inside the container and goes with it;
- `--add-host` and the runtime address reach LM Studio on the host, as the Compose file does;
- `--env AGENTSPLICE_ADMIN_API_KEY`, with no value, passes the token from your shell rather than writing
  it into the command line. Docker copies it only from the environment of the shell that runs
  `docker run` — the `export` above in bash, `$env:` in PowerShell — and otherwise sets nothing, without a
  warning, so the gateway exits at startup. `sudo` clears that environment: use
  `sudo --preserve-env=AGENTSPLICE_ADMIN_API_KEY docker run …`, or run Docker as a member of the `docker`
  group.

`docker ps` shows the image's own health check once the gateway answers (ADR 0016); open
`http://127.0.0.1:5280/` for the dashboard. Do not start it with `--env-file .env.example`: that file is
for Compose and development, and it would turn on development settings.

**Pinning the image.** A version tag can be moved by whoever can push to the package; a digest cannot.
Once the image is pushed, the release notes end with its reference by digest,
`ghcr.io/murillojndem/agent-splice@sha256:<digest>`. Put that reference in place of the tagged one at the
end of the `docker run` above, and Docker runs exactly that image, pulling it if it must. A
`docker pull` by digest on its own leaves no tag behind, so a `docker run` that names the tag looks the
tag up again. The notes stay editable after publication, so their digest is a convenience; what cannot
change is the release's own copy of the image, `agentsplice-<version>-image-linux-amd64.tar.gz`, which is
also the way in for a machine without registry access. Check it with
`gh release verify-asset v<version> agentsplice-<version>-image-linux-amd64.tar.gz`, or against its line
in `SHA256SUMS` as for the packages, then `docker load --input` it.

**Data.** As with Compose below, but with `docker stop agentsplice`, `docker cp
agentsplice:/data/agentsplice.db ./agentsplice-backup.db`, and `docker start agentsplice`.

**Upgrading and removing.** `docker rm --force agentsplice`, then the same `docker run` with the new
version; the volume is kept. Nothing keeps the token for you, so in a new shell export
`AGENTSPLICE_ADMIN_API_KEY` again first: the value you used before, or a new one to paste into the
dashboard. `docker volume rm agentsplice-data` deletes the data too.

### Compose from a clone

Docker with the Compose plugin. Clone the repository, then from its root choose an administrative token
and start the stack:

```bash
cp .env.example .env
export AGENTSPLICE_ADMIN_API_KEY="$(openssl rand -hex 32)"
echo "AGENTSPLICE_ADMIN_API_KEY=$AGENTSPLICE_ADMIN_API_KEY" >> .env
docker compose up --build --wait
```

In PowerShell:

```powershell
Copy-Item .env.example .env
$bytes = [byte[]]::new(32); [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$env:AGENTSPLICE_ADMIN_API_KEY = ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
Add-Content .env "AGENTSPLICE_ADMIN_API_KEY=$env:AGENTSPLICE_ADMIN_API_KEY"
docker compose up --build --wait
```

`--wait` returns once the image's own health check reports the gateway healthy (ADR 0016), and
`docker compose ps` shows the status afterwards. The token is in `.env`, where Compose reads it on every
start, and in this shell, for calls to `/api/v1`. Open `http://127.0.0.1:5280/` and paste it into the
dashboard's token field; the page keeps it in memory only. Compose builds the image as
`agentsplice:dev` and never pulls it.

**Reaching LM Studio.** The container reaches the host as `host.docker.internal`, and the Compose file
points the runtime at `http://host.docker.internal:1234/v1`. Docker Desktop forwards that to the host
for you. On a Linux engine, `host.docker.internal` is the host's bridge address, so the runtime must
listen on an address the container can reach rather than on `127.0.0.1` alone. Override the URL in
`.env` with `AGENTSPLICE__RUNTIMES__0__BASEURL`. The same holds for the published image.

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
page. Every release so far is a pre-release, built by CI from its tag and published only after a
maintainer reviewed it (ADR 0018). Until the first one is published, the `agentsplice-win-x64` artifact
of a CI run for a push to `main` holds the same two files, which a signed-in GitHub user can download —
only such a run: a run for a pull request builds code nobody has reviewed yet, and its artifacts pass
the same digest check.

```powershell
gh run list --repo murillojndem/agent-splice --workflow CI --branch main --event push --limit 1
gh run download <run-id> --repo murillojndem/agent-splice --name agentsplice-win-x64
```

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
`AgentSplice.Api.exe`, `e_sqlite3.dll`, `appsettings.json`, the `dashboard` folder, `README.txt`, and the
license: `LICENSE`, `NOTICE`, `THIRD-PARTY-NOTICES.md`, and, in `licenses`, the licenses and notices of
the .NET runtime and ASP.NET Core it bundles.
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

Open `http://127.0.0.1:5280/` for the dashboard and point clients at `http://127.0.0.1:5280/v1`
(`docs/CLIENTS.md`).

### Administrative token

With no token configured, the gateway serves its administrative API and dashboard data to any caller
on this machine — any program, any account signed in to it — and refuses to start on any binding
another machine could reach. A web page you visit cannot use it: the gateway answers only requests
addressed to `localhost`, `127.0.0.1`, or `[::1]`, which closes DNS rebinding
(`docs/THREAT_MODEL.md`). Set a token when other accounts share the machine, or to close the stored
evidence to every program that does not hold it:

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
$env:AGENTSPLICE__PERSISTENCE__CONNECTIONSTRING = "Data Source=$env:LOCALAPPDATA\AgentSplice\agentsplice.db"
[Environment]::SetEnvironmentVariable('AGENTSPLICE__PERSISTENCE__CONNECTIONSTRING', $env:AGENTSPLICE__PERSISTENCE__CONNECTIONSTRING, 'User')
```

A variable set for your account reaches windows opened after it, not the one that set it, so the first
line sets it for this window too. The startup log names the store the gateway opened, as event `1305`.

A relative path in that setting is resolved against the package folder. To back the store up, stop the
gateway first, then copy `agentsplice.db`, and `agentsplice.db-wal` and `agentsplice.db-shm` if they
are still there. `agentsplice:persistence:mode` set to `None` keeps nothing at all.

### Upgrade and uninstall

To upgrade, stop the gateway and extract the new version beside the old one. If the store is in the
old folder, copy it across; settings kept in environment variables carry over by themselves. Then
delete the old folder.

The package carries its own .NET runtime, so Windows Update and .NET installers never patch it:
upgrading AgentSplice is how it receives .NET security fixes. `README.txt` in each package names the
runtime version it was built with. Microsoft supports .NET 10 until November 14, 2028; after that date no
.NET 10 build, this package included, receives runtime fixes.

To uninstall, stop the gateway, delete its folder and `%LOCALAPPDATA%\AgentSplice` if you used it, and
remove any `AGENTSPLICE__*` variables, and `AGENTSPLICE_ADMIN_API_KEY`, that you set for your account.

## Linux package

A self-contained build for 64-bit Linux distributions based on glibc 2.34 or later — Ubuntu 22.04,
Debian 12, RHEL 9, current Fedora, and their successors — but not Alpine, which uses musl (ADR 0021), and
not Ubuntu 20.04, Debian 11, or RHEL 8. It carries its own .NET runtime and the dashboard, so it needs
nothing installed beyond what such a distribution already has, OpenSSL 1.1.1 or later included. The glibc
floor is the SQLite library the package bundles, which Entity Framework Core 10 requires: the .NET 10
runtime itself needs only glibc 2.27, and the .NET 8 package needed glibc 2.28 (ADR 0024).

### Download and verify

Download `agentsplice-<version>-linux-x64.tar.gz` and `SHA256SUMS` from the repository's GitHub Releases
page into one folder. As for Windows, every release is a pre-release that a maintainer reviewed, and
until the first one is published the `agentsplice-linux-x64` artifact of a CI run for a push to `main` —
never one for a pull request — holds the same two files (`gh run download <run-id> --name
agentsplice-linux-x64`, as shown for Windows). Check the archive against its own line in `SHA256SUMS`,
which lists every asset of the release:

```bash
archive=agentsplice-0.2.0-alpha-linux-x64.tar.gz   # the file you downloaded
grep -F "  $archive" SHA256SUMS | sha256sum --check --strict -
```

It must print `agentsplice-0.2.0-alpha-linux-x64.tar.gz: OK`; if the line is missing, `sha256sum` finds
nothing to check and fails. GitHub also shows each asset's digest on the release page. The package is
not signed (`docs/SECURITY.md`).

### Extract

Into a folder only your account can write. By default the gateway keeps its store beside the
executable, so the folder must be writable by you, and whoever can write it can replace the executable
and the dashboard's script:

```bash
mkdir -p ~/.local/opt/agentsplice && chmod 700 ~/.local/opt/agentsplice
tar -xzf agentsplice-0.2.0-alpha-linux-x64.tar.gz -C ~/.local/opt/agentsplice
```

`chmod` rather than `mkdir -m`, which leaves a folder that already exists as it was.

That gives `~/.local/opt/agentsplice/agentsplice-<version>-linux-x64/`, which holds `AgentSplice.Api`,
`libe_sqlite3.so`, `appsettings.json`, the `dashboard` folder, `README.txt`, and the license: `LICENSE`,
`NOTICE`, `THIRD-PARTY-NOTICES.md`, and, in `licenses`, the licenses and notices of the .NET runtime and
ASP.NET Core it bundles. A folder under `/opt` owned by root is not writable by you, and the gateway
would refuse to start for want of a store; extract there only if you also point the store elsewhere (see
Token, configuration, and data below).

### Run

```bash
~/.local/opt/agentsplice/agentsplice-0.2.0-alpha-linux-x64/AgentSplice.Api
```

As on Windows, it can be started from any folder, reads its settings and serves its dashboard from
beside the executable, and writes nothing where it was started. Stop it with Ctrl+C.

### Token, configuration, and data

These work as for the Windows package, with the shell's syntax:

```bash
# An administrative token, in the shell that then starts the gateway
export AGENTSPLICE__ADMINISTRATION__APIKEYENVIRONMENTVARIABLE=AGENTSPLICE_ADMIN_API_KEY
export AGENTSPLICE_ADMIN_API_KEY="$(openssl rand -hex 32)"
echo "$AGENTSPLICE_ADMIN_API_KEY"   # paste this into the dashboard's token field

# A different runtime address, from the package folder
./AgentSplice.Api --agentsplice:runtimes:0:baseUrl=http://127.0.0.1:8080/v1

# The store outside the package folder, so an upgrade does not touch it
mkdir -p ~/.local/share/agentsplice && chmod 700 ~/.local/share/agentsplice
export AGENTSPLICE__PERSISTENCE__CONNECTIONSTRING="Data Source=$HOME/.local/share/agentsplice/agentsplice.db"
```

`chmod 700` keeps both folders to your account: SQLite creates the store readable by everyone your
umask allows, usually the whole machine, so the folder it is in is what protects the file. It does not
protect what the gateway serves: with no token, every account on the machine can read the same evidence
from `/api/v1` while the gateway runs, because loopback callers are served. On a machine with other
accounts, set the token. Two differences from Windows matter:

- **Environment variable names are case-sensitive.** The gateway looks a secret up by exactly the name
  the setting gives. With a name exported in another case, it finds no token: it refuses to start on a
  binding another machine could reach, and on loopback every `/api/v1` request is refused.
- **`~` is not expanded inside a setting.** Write the path in full, or let the shell expand `$HOME`
  before the gateway sees it, as above.

### Upgrade and uninstall

To upgrade, stop the gateway, extract the new version beside the old one, copy the store across if it
is in the old folder, and delete the old folder. The package carries its own .NET runtime, so the
distribution's updates never patch it: upgrading AgentSplice is how it receives .NET security fixes, and
`README.txt` names the runtime version it was built with. Microsoft supports .NET 10 until November 14,
2028; after that date no .NET 10 build, this package included, receives runtime fixes.

To uninstall, stop the gateway and delete its folder, `~/.local/share/agentsplice` if you used it, and
any `AGENTSPLICE__*` variables, and `AGENTSPLICE_ADMIN_API_KEY`, that you exported in your shell's
startup files.

## From source

With the .NET 10 SDK installed (`docs/DEVELOPMENT.md` lists the prerequisites), from the repository
root on Windows, Linux, or macOS, in any shell:

```text
dotnet run --project src/AgentSplice.Api
```

The working directory is the project directory, so the store is `src/AgentSplice.Api/agentsplice.db`
and settings are read from `src/AgentSplice.Api/appsettings.json`. A fresh clone has no dashboard build;
`docs/DEVELOPMENT.md` shows how to build one and point the gateway at it.

A framework-dependent build needs only the ASP.NET Core 10 runtime where it runs:

```powershell
dotnet publish src/AgentSplice.Api -c Release -o out
pnpm --dir web/AgentSplice.Dashboard install --frozen-lockfile
pnpm --dir web/AgentSplice.Dashboard build
Copy-Item -Recurse web/AgentSplice.Dashboard/dist out/dashboard
Set-Location out; dotnet AgentSplice.Api.dll
```

On Linux or macOS:

```bash
dotnet publish src/AgentSplice.Api -c Release -o out
pnpm --dir web/AgentSplice.Dashboard install --frozen-lockfile
pnpm --dir web/AgentSplice.Dashboard build
cp -R web/AgentSplice.Dashboard/dist out/dashboard
cd out && dotnet AgentSplice.Api.dll
```

Start it from inside `out`: unlike the packages, a framework-dependent build takes its content root from
the working directory, as every ASP.NET Core application does.

## Host names

The gateway answers only requests addressed to `localhost`, `127.0.0.1`, or `[::1]`; any other name
gets a `400` with no body, before anything answers (ADR 0027). That is what keeps a web page from
pointing a domain of its own at `127.0.0.1` and reading the gateway from your browser. A client that
reaches the gateway by another name — a Compose service name, `host.docker.internal` from another
container, a LAN address, a reverse proxy's upstream name — needs that name in `AllowedHosts`,
ASP.NET Core's own setting, as a semicolon-separated list without ports:

```text
--AllowedHosts="localhost;127.0.0.1;[::1];agentsplice"
```

or the environment variable `AllowedHosts` with the same value, passed to the container with
`--env` under Docker. Naming hosts replaces the default list, so keep the loopback names you still use;
`*` turns the check off and gives the rebinding read back. Each refusal is logged as
`The host '…' does not match an allowed host.` (`NoAllowedHostMatched`), which names the host to add.

A host name is not a network boundary: a binding beyond loopback still needs the administrative
token, and `/v1` takes no credential on any binding (`docs/SECURITY.md`).

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
| `1204 DashboardBuildMissing` at startup; nothing at `/dashboard/` | No build where `agentsplice:dashboard:rootPath` points. The image and the packages carry one; from source, build it first (`docs/DEVELOPMENT.md`). The APIs work regardless. |
| `The metadata store '…' could not be opened for writing` | The store's directory does not exist or is not writable. Create it, choose another file, or set `agentsplice:persistence:mode` to `None`. On Linux, a package extracted into a folder you cannot write, such as one under `/opt`, meets this at its default store. |
| `The metadata store '…' was not brought up to date within 30 seconds` | A gateway killed while it was starting — ended from Task Manager, `kill -9`, `docker kill`, a power cut — left Entity Framework Core's migration lock in the store, and a later start waits for it. If no other gateway uses that store, delete the lock with the SQLite command-line tool, `sqlite3 <store> "DELETE FROM __EFMigrationsLock;"`, and start again; the error names the store. For the image, stop the container and run that against the volume from a container that has `sqlite3`. |
| Startup fails with `agentsplice:… is null` | A settings file gives a switch, a number, a duration, or a mode as `null`, which would be read as `false`, zero, or the first of the mode's values rather than as its default. Remove the setting to use the default, or give it a value. |
| Building from source fails with `NU1901` to `NU1904` | A security advisory was published, after the commit you are building, against a package it references. Build a newer commit, or use a release's package or image, which need no build. |
| On Linux, startup fails with ``version `GLIBC_2.33' not found (required by …/libe_sqlite3.so)`` | The distribution's glibc is older than the SQLite library the package bundles needs. Use a distribution based on glibc 2.34 or later, or the Docker image. |
| `Permission denied` starting `AgentSplice.Api` on Linux | The archive was extracted with a tool that dropped the execute bit. Extract it with `tar -xzf`, which keeps it, or run `chmod +x AgentSplice.Api`. |
| On Linux, `/api/v1` answers `401` with the token you set | The gateway did not see the variable: its name differs in case from the one `agentsplice:administration:apiKeyEnvironmentVariable` names, which on Linux must match exactly, or it was not exported in the shell that started the gateway. Export it, then start the gateway from that shell. |
| `Failed to bind to address http://127.0.0.1:5280` | Something else is using the port. Stop it, or pass `--urls http://127.0.0.1:<another port>`. |
| Startup refuses a binding beyond loopback | Serving stored evidence to a network requires an administrative token (`docs/SECURITY.md`). A `Kestrel:Endpoints` entry that is not loopback, a socket or pipe endpoint, and `FORWARDEDHEADERS_ENABLED` count as beyond loopback too. |
| `1205 InferenceReachableBeyondLoopback` at startup | A warning, not a failure: the gateway listens beyond loopback, or trusts forwarded headers, and `/v1` authenticates no one, so anyone who can reach it can run completions on your runtime. The token protects `/api/v1` only. Bind to loopback, or put `/v1` behind an authenticating proxy. |
| Every request answers `400` with no body | The client used a host name the gateway does not serve. See [Host names](#host-names); the log names the refused host. |
| A completion answers `415` | The request did not say `Content-Type: application/json`. With `curl`, pass `-H "Content-Type: application/json"`; `-d` alone sends a form. |
| Exchanges are missing from the dashboard and `/api/v1/exchanges` | The store is refusing writes, or the metadata queue overflowed. The overview says how many exchanges were lost since the gateway started (`persistenceFailures` in `/api/v1/system`), and the log has an event `1302` for each failed write and `1301` for each overflow. Client responses are not affected. |
| Startup fails with `agentsplice:diagnostics:storeHeaders must be None` or `storeBodies must be false` | A configuration carried over from an earlier build turns on a capture this build does not have. Remove the setting (ADR 0027). |
| The container exits at once, and `docker logs agentsplice` says the variable `AGENTSPLICE_ADMIN_API_KEY` is unset or empty | Docker set nothing, because the variable was not exported in the shell that ran `docker run`, or `sudo` cleared it. Export it, `docker rm agentsplice`, and run the same command again ([Published image](#published-image)). |
| A setting seems to be ignored | Environment variables use double underscores (`AGENTSPLICE__PERSISTENCE__MODE`), and a command-line argument wins over both a variable and `appsettings.json`. |
| Requests fail with `agentsplice_runtime_unavailable` | The runtime is not listening where the gateway was told. Check that its server is started and the URL, including `/v1`. |
