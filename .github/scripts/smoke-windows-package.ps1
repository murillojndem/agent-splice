#Requires -Version 7.2
<#
.SYNOPSIS
Smoke test for the Windows package (ADR 0017): it runs with no .NET SDK assumed, from any folder.

.DESCRIPTION
Verifies the archive against SHA256SUMS the way the installation guide tells a user to, extracts it,
and checks what it contains. Then it starts the executable twice, each time from an empty folder
elsewhere, so that a package which only works from its own directory fails here:

1. With the shipped defaults — loopback, no administrative token — as a user who just extracted it
   gets it: liveness, readiness, the dashboard, /api/v1 served to a local caller, an exchange
   recorded in the SQLite store and read back, the store created beside the executable and nothing
   written to the working directory, and a listener on 127.0.0.1 only.
2. With a token, running the shared gateway smoke test (smoke-gateway.sh) through Git Bash, so the
   dashboard's serving rules and the traversal checks run under Kestrel on Windows too.

Every check runs and reports; the script exits non-zero if any failed. Port 5280 must be free.

.PARAMETER Archive
The agentsplice-<version>-win-x64.zip that package-windows.ps1 wrote; SHA256SUMS beside it.

.PARAMETER Bash
Git Bash, for the shared smoke test. On a runner a bare `bash` can resolve to the WSL launcher.

.EXAMPLE
pwsh -File .github/scripts/smoke-windows-package.ps1 -Archive artifacts/package/agentsplice-0.2.0-alpha-win-x64.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Archive,
    [string] $Bash = (Join-Path $env:ProgramFiles 'Git' 'bin' 'bash.exe')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$base = 'http://127.0.0.1:5280'
$script:failures = 0

function Pass([string] $What) { Write-Host "ok    $What" }
function Fail([string] $What) { Write-Host "FAIL  $What"; $script:failures++ }
function Check([bool] $Condition, [string] $What) { if ($Condition) { Pass $What } else { Fail $What } }

$archivePath = (Resolve-Path $Archive).Path
$archiveName = Split-Path -Leaf $archivePath

if ($archiveName -notmatch '^agentsplice-(?<version>.+)-win-x64\.zip$') {
    throw "'$archiveName' is not named like a Windows package."
}

$version = $Matches.version

# The procedure the installation guide gives a user: Get-FileHash against SHA256SUMS.
$sums = Join-Path (Split-Path -Parent $archivePath) 'SHA256SUMS'
$expected = Get-Content $sums |
    ForEach-Object { if ($_ -match "^(?<hash>[0-9a-f]{64})  $([regex]::Escape($archiveName))$") { $Matches.hash } } |
    Select-Object -First 1
$actual = (Get-FileHash -Algorithm SHA256 -Path $archivePath).Hash.ToLowerInvariant()
Check ($null -ne $expected -and $actual -eq $expected) "the archive matches its line in SHA256SUMS"

# Under the repository's ignored artifacts directory rather than the temporary directory, whose path
# on a hosted runner can be an 8.3 short name (RUNNER~1) that the gateway would report in its long
# form, failing the path comparisons below for no reason to do with the package.
$work = Join-Path $repository 'artifacts' ("smoke-windows-" + [guid]::NewGuid().ToString('N'))
$extracted = Join-Path $work 'extracted'
$elsewhere = Join-Path $work 'elsewhere'
New-Item -ItemType Directory -Force -Path $extracted, $elsewhere | Out-Null

$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.UseProxy = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(10)

$started = [Collections.Generic.List[Diagnostics.Process]]::new()
$logs = [Collections.Generic.List[string]]::new()

function Send([string] $Method, [string] $Path, [string] $Body = $null) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), "$base$Path")

    if ($null -ne $Body) {
        $request.Content = [Net.Http.StringContent]::new($Body, [Text.Encoding]::UTF8, 'application/json')
    }

    $response = $client.SendAsync($request).GetAwaiter().GetResult()

    [pscustomobject]@{
        Status = [int]$response.StatusCode
        Location = if ($response.Headers.Location) { $response.Headers.Location.OriginalString } else { $null }
        ContentType = if ($response.Content.Headers.ContentType) { $response.Content.Headers.ContentType.MediaType } else { $null }
        Body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
}

function Start-Gateway([string] $Name) {
    $out = Join-Path $work "$Name.out.log"
    $err = Join-Path $work "$Name.err.log"
    $logs.Add($out)
    $logs.Add($err)

    # From an empty folder elsewhere: the package must find its settings and dashboard beside the
    # executable, and must write nothing where it was started from.
    $process = Start-Process -FilePath $exe -WorkingDirectory $elsewhere -NoNewWindow -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError $err
    $started.Add($process)

    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($process.HasExited) {
            throw "The gateway exited with code $($process.ExitCode) before it answered. See $out."
        }

        try {
            if ((Send GET '/health/live').Status -eq 204) {
                return $process
            }
        }
        catch [Net.Http.HttpRequestException] {
            # Not listening yet.
        }

        Start-Sleep -Milliseconds 500
    }

    throw "The gateway did not answer /health/live within 30 seconds. See $out."
}

function Stop-Gateway([Diagnostics.Process] $Process) {
    Stop-Process -Id $Process.Id -Force
    Check ($Process.WaitForExit(10000)) "the gateway stopped"
}

# What a log file says once it says it: the console logger writes on a background thread.
function Wait-ForLog([string] $Path, [string] $Pattern) {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if ((Test-Path $Path) -and ((Get-Content -Raw $Path) -match $Pattern)) {
            return $true
        }

        Start-Sleep -Milliseconds 250
    }

    return $false
}

try {
    # Busy port: a gateway already running would answer every check below instead of the package.
    if (Get-NetTCPConnection -State Listen -LocalPort 5280 -ErrorAction SilentlyContinue) {
        throw 'Port 5280 is already in use; stop whatever is listening there first.'
    }

    Expand-Archive -Path $archivePath -DestinationPath $extracted
    $top = @(Get-ChildItem -Path $extracted)
    Check ($top.Count -eq 1 -and $top[0].PSIsContainer -and $top[0].Name -eq "agentsplice-$version-win-x64") "the archive has one top-level folder named for its version"

    $package = $top[0].FullName
    $exe = Join-Path $package 'AgentSplice.Api.exe'

    Check (Test-Path $exe) "the package holds AgentSplice.Api.exe"
    Check (Test-Path (Join-Path $package 'e_sqlite3.dll')) "SQLite's native library sits beside the executable"
    Check (Test-Path (Join-Path $package 'appsettings.json')) "appsettings.json sits beside the executable"
    Check (Test-Path (Join-Path $package 'dashboard' 'index.html')) "the dashboard's page is in dashboard/"
    Check (Test-Path (Join-Path $package 'dashboard' 'assets')) "the dashboard's assets are in dashboard/assets/"
    Check (Test-Path (Join-Path $package 'README.txt')) "the package carries README.txt"
    Check (-not (Get-ChildItem -Path $package -Recurse -Filter '*.pdb')) "no loose symbol files"
    Check (-not (Test-Path (Join-Path $package 'web.config'))) "no IIS web.config"
    Check (-not (Test-Path (Join-Path $package 'aspnetcorev2_inprocess.dll'))) "no IIS module"
    Check (-not (Test-Path (Join-Path $package 'appsettings.Development.json'))) "no development settings"
    Check (-not (Get-ChildItem -Path $package -Filter 'AgentSplice.*.dll')) "the gateway's assemblies are inside the executable"

    # The package must run on what it carries: nothing inherited from this machine's .NET or from a
    # developer's AgentSplice settings.
    Get-ChildItem env: |
        Where-Object { $_.Name -like 'ASPNETCORE_*' -or $_.Name -like 'DOTNET_*' -or $_.Name -like 'AGENTSPLICE*' } |
        ForEach-Object { Remove-Item -Path "env:$($_.Name)" }

    Write-Host '-- run 1: the shipped defaults, no administrative token'
    $gateway = Start-Gateway 'defaults'
    $log = Join-Path $work 'defaults.out.log'

    Check ((Send GET '/health/live').Status -eq 204) "GET /health/live -> 204"
    Check ((Send GET '/health/ready').Status -eq 200) "GET /health/ready -> 200"

    $root = Send GET '/'
    Check ($root.Status -eq 302 -and $root.Location -eq '/dashboard/') "GET / redirects to /dashboard/"

    $shell = Send GET '/dashboard/'
    Check ($shell.Status -eq 200 -and $shell.ContentType -eq 'text/html') "GET /dashboard/ -> the page"

    # No token is configured and the binding is loopback, so a local caller is served (docs/SECURITY.md).
    $system = Send GET '/api/v1/system'
    Check ($system.Status -eq 200) "GET /api/v1/system without a token -> 200"

    if ($system.Status -eq 200) {
        $reported = ($system.Body | ConvertFrom-Json).version
        Check ($reported -eq $version -or $reported.StartsWith("$version+")) "the gateway reports version $version (it says '$reported')"
        Check (($system.Body | ConvertFrom-Json).metadataRetentionEnabled) "metadata retention is on"
    }

    # A request the gateway refuses before reading an envelope is still recorded. Reading it back
    # proves the store works inside the single file: EF Core, its migrations, and SQLite's native
    # library together.
    $refused = Send POST '/v1/chat/completions' '{ not json'
    Check ($refused.Status -eq 400) "POST /v1/chat/completions with malformed JSON -> 400"

    $recorded = $false

    for ($attempt = 0; $attempt -lt 40 -and -not $recorded; $attempt++) {
        $exchanges = Send GET '/api/v1/exchanges'
        $recorded = $exchanges.Status -eq 200 -and @(($exchanges.Body | ConvertFrom-Json).items).Count -ge 1

        if (-not $recorded) {
            Start-Sleep -Milliseconds 250
        }
    }

    Check $recorded "the refused request is read back from the store"

    $store = Join-Path $package 'agentsplice.db'
    Check (Wait-ForLog $log ([regex]::Escape("Content root path: $package"))) "the content root is the package folder, not the working directory"
    Check (Wait-ForLog $log '\[1203\]') "the startup log announces the dashboard (1203)"
    Check (Wait-ForLog $log ([regex]::Escape((Join-Path $package 'dashboard')))) "the dashboard is served from the package's dashboard folder"
    Check (-not ((Get-Content -Raw $log) -match '\[1204\]')) "the startup log does not report a missing dashboard (1204)"
    Check (Wait-ForLog $log ('\[1305\][\s\S]*' + [regex]::Escape($store))) "the startup log names the store beside the executable (1305)"
    Check (Test-Path $store) "the store was created beside the executable"
    Check (-not (Get-ChildItem -Force -Path $elsewhere)) "nothing was written to the working directory"

    $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $gateway.Id -ErrorAction SilentlyContinue)
    Check ($listeners.Count -ge 1 -and -not ($listeners | Where-Object { $_.LocalAddress -ne '127.0.0.1' })) "the gateway listens on 127.0.0.1 only"

    Stop-Gateway $gateway

    Write-Host '-- run 2: with an administrative token, the shared gateway smoke test'
    $bytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $token = [Convert]::ToHexString($bytes).ToLowerInvariant()

    $env:AGENTSPLICE__ADMINISTRATION__APIKEYENVIRONMENTVARIABLE = 'AGENTSPLICE_ADMIN_API_KEY'
    $env:AGENTSPLICE_ADMIN_API_KEY = $token

    $gateway = Start-Gateway 'token'

    # Forward slashes: Git Bash opens a script by a Windows path written that way without surprises.
    & $Bash ((Join-Path $repository '.github' 'scripts' 'smoke-gateway.sh') -replace '\\', '/') $base $token
    Check ($LASTEXITCODE -eq 0) "the shared gateway smoke test passes against the package"

    Stop-Gateway $gateway
}
catch {
    # Reported like any other failed check, so the logs below are printed for it too.
    Fail "the smoke test could not continue: $_"
}
finally {
    foreach ($process in $started) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(10000) | Out-Null
        }
    }

    Remove-Item -Path env:AGENTSPLICE__ADMINISTRATION__APIKEYENVIRONMENTVARIABLE, env:AGENTSPLICE_ADMIN_API_KEY -ErrorAction SilentlyContinue

    if ($script:failures -ne 0) {
        foreach ($path in $logs) {
            if (Test-Path $path) {
                Write-Host "----- $path"
                Get-Content -Path $path | Write-Host
            }
        }
    }

    $client.Dispose()
    Remove-Item -Recurse -Force -Path $work -ErrorAction SilentlyContinue
}

if ($script:failures -ne 0) {
    Write-Host "$($script:failures) check(s) failed."
    exit 1
}

Write-Host 'All checks passed.'
