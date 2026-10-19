#Requires -Version 7.4
<#
.SYNOPSIS
Smoke test for a self-contained package (ADR 0017, ADR 0021, ADR 0029): it runs with no .NET SDK
assumed, from any folder, on the platform it was built for.

.DESCRIPTION
Verifies the archive against SHA256SUMS the way the installation guide tells a user to, extracts it,
and checks what it contains. Then it starts the executable twice, each time from an empty folder
elsewhere, so that a package which only works from its own directory fails here:

1. With the shipped defaults — loopback, no administrative token — as a user who just extracted it
   gets it: liveness, readiness, the dashboard, /api/v1 served to a local caller, an exchange
   recorded in the SQLite store and read back, the store created beside the executable and nothing
   written to the working directory, and a listener on 127.0.0.1 only.
2. With a token, running the shared gateway smoke test (smoke-gateway.sh), so the dashboard's serving
   rules and the traversal checks run under Kestrel on the package's own file system too.

The package's name says which platform it is for, and the checks that differ between platforms follow
it: the archive format, the installation guide's digest command, the executable's and SQLite's file
names, the executable's permission bits outside Windows, the oldest glibc on Linux, and how the listener
is found (Get-NetTCPConnection on Windows, ss on Linux, lsof on macOS). On macOS it also checks that
every native file is arm64, signed ad hoc, and asks for no macOS later than the floor the documents
state, and that the guide's command clears the quarantine mark a browser download carries, before it
starts anything — the order the guide gives.

Every check runs and reports; the script exits non-zero if any failed. Port 5280 must be free.

.PARAMETER Archive
The agentsplice-<version>-win-x64.zip, agentsplice-<version>-linux-x64.tar.gz, or
agentsplice-<version>-osx-arm64.tar.gz that package.ps1 wrote; SHA256SUMS beside it.

.PARAMETER Bash
Bash, for the shared smoke test. On Windows, Git Bash: on a runner a bare `bash` can resolve to the WSL
launcher. On macOS, /bin/bash, which is Bash 3.2.

.EXAMPLE
pwsh -File .github/scripts/smoke-package.ps1 -Archive artifacts/package/agentsplice-0.2.0-alpha-linux-x64.tar.gz
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Archive,
    [string] $Bash = $(if ($IsWindows) { Join-Path $env:ProgramFiles 'Git' 'bin' 'bash.exe' } else { 'bash' })
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

if ($archiveName -notmatch '^agentsplice-(?<version>.+)-(?<rid>win-x64|linux-x64|osx-arm64)\.(?<format>zip|tar\.gz)$' -or
    ($Matches.rid -eq 'win-x64') -ne ($Matches.format -eq 'zip')) {
    throw "'$archiveName' is not named like a Windows, a Linux, or a macOS package."
}

$version = $Matches.version
$rid = $Matches.rid
$windows = $rid -eq 'win-x64'
$linux = $rid -eq 'linux-x64'
$macos = $rid -eq 'osx-arm64'

# The macOS package is arm64 only; an Intel Mac would refuse it before any check could run.
$native = if ($windows) { [bool]$IsWindows }
elseif ($linux) { [bool]$IsLinux }
else { [bool]$IsMacOS -and [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64 }

if (-not $native) {
    throw "'$archiveName' is a $rid package; smoke-test it on the platform it runs on."
}

# The procedure the installation guide gives a user: the archive's own line in SHA256SUMS, by
# Get-FileHash on Windows, sha256sum on Linux, and shasum on macOS, which compute the same digest.
$sums = Join-Path (Split-Path -Parent $archivePath) 'SHA256SUMS'
$expected = Get-Content $sums |
    ForEach-Object { if ($_ -match "^(?<hash>[0-9a-f]{64})  $([regex]::Escape($archiveName))$") { $Matches.hash } } |
    Select-Object -First 1
$actual = (Get-FileHash -Algorithm SHA256 -Path $archivePath).Hash.ToLowerInvariant()
Check ($null -ne $expected -and $actual -eq $expected) "the archive matches its line in SHA256SUMS"

if ($linux) {
    # The very command the installation guide gives a Linux user, run where they would run it.
    $checked = @(& $Bash -c 'cd "$1" && grep -F "  $2" SHA256SUMS | sha256sum --check --strict -' bash (Split-Path -Parent $archivePath) $archiveName 2>&1)
    Check ($LASTEXITCODE -eq 0 -and ($checked -join "`n") -match "$([regex]::Escape($archiveName)): OK") "the installation guide's sha256sum check passes"
}

if ($macos) {
    # The very command the installation guide gives a macOS user, run where they would run it. The guide
    # also says it fails when SHA256SUMS has no line for the file, rather than checking nothing.
    $command = 'cd "$1" && grep -F "  $2" SHA256SUMS | shasum -a 256 --check --strict -'
    $checked = @(& $Bash -c $command bash (Split-Path -Parent $archivePath) $archiveName 2>&1)
    Check ($LASTEXITCODE -eq 0 -and ($checked -join "`n") -match "$([regex]::Escape($archiveName)): OK") "the installation guide's shasum check passes"
    & $Bash -c $command bash (Split-Path -Parent $archivePath) "agentsplice-$version-unlisted-osx-arm64.tar.gz" 2>&1 | Out-Null
    Check ($LASTEXITCODE -ne 0) "the installation guide's shasum check fails for a file SHA256SUMS does not list"
}

# Under the repository's ignored artifacts directory rather than the temporary directory, whose path
# on a hosted Windows runner can be an 8.3 short name (RUNNER~1) that the gateway would report in its
# long form, and on macOS lies under /var, a link to /private/var that the gateway would report
# resolved, failing the path comparisons below for no reason to do with the package.
$work = Join-Path $repository 'artifacts' ("smoke-$rid-" + [guid]::NewGuid().ToString('N'))
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

# Whether anything listens on port 5280, on any platform.
function Test-PortBusy {
    @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
            Where-Object { $_.Port -eq 5280 }).Count -gt 0
}

# The local addresses a process listens on.
function Get-Listeners([Diagnostics.Process] $Process) {
    if ($windows) {
        return @(Get-NetTCPConnection -State Listen -OwningProcess $Process.Id -ErrorAction SilentlyContinue |
                ForEach-Object { $_.LocalAddress })
    }

    if ($macos) {
        # lsof -Fn: an "n<local address:port>" line for each listening socket of the process, after its
        # "p" and "f" lines. It exits 1 when the process listens on nothing, which is an answer here.
        $lines = @(& lsof -nP -a -p $Process.Id -iTCP -sTCP:LISTEN -Fn 2>$null)

        return @($lines | Where-Object { $_.StartsWith('n') } | ForEach-Object { $_.Substring(1) -replace ':\d+$', '' })
    }

    # ss -H: no header. Each line is "LISTEN <recv-q> <send-q> <local address:port> <peer> users:((...))",
    # and -p names the owning process, which ss can see for this user's own processes.
    $lines = @(& ss -Hltnp | Where-Object { $_ -match "pid=$($Process.Id)," })

    return @($lines | ForEach-Object { (($_ -split '\s+')[3]) -replace ':\d+$', '' })
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
    if (Test-PortBusy) {
        throw 'Port 5280 is already in use; stop whatever is listening there first.'
    }

    if ($windows) {
        Expand-Archive -Path $archivePath -DestinationPath $extracted
    }
    else {
        # What the archive records, before anything is extracted: every entry owned by 0:0, so it names no
        # account of the machine that built it, and nothing writable by group or others. The listing's
        # first column is the mode, the second owner/group, in GNU tar's format, which is why macOS reads
        # it with gtar: its own bsdtar lists owner and group as two columns.
        $gnuTar = if ($macos) { 'gtar' } else { 'tar' }
        $listing = @(& $gnuTar --numeric-owner -tvzf $archivePath)

        if ($LASTEXITCODE -ne 0) {
            throw "$gnuTar could not list '$archiveName'."
        }

        Check (-not ($listing | Where-Object { ($_ -split '\s+')[1] -ne '0/0' })) "every entry in the archive is owned by 0:0"

        # No AppleDouble companions: bsdtar writes a ._ file for each file with extended attributes, which
        # would extract on Linux as stray files and on a Mac as attributes the package never had.
        Check (-not ($listing | Where-Object { ($_ -split '\s+')[-1] -match '(^|/)\._' })) "the archive holds no ._ (AppleDouble) entries"

        # Exactly the modes the package script sets: folders rwxr-xr-x, the executable rwxr-xr-x, and
        # every other file rw-r--r--, whatever modes the build machine left on them.
        $unexpected = @($listing | Where-Object {
                $entry = $_ -split '\s+'
                $expectedMode = if ($entry[-1].EndsWith('/')) { 'drwxr-xr-x' }
                elseif ($entry[-1] -match '/AgentSplice\.Api$') { '-rwxr-xr-x' }
                else { '-rw-r--r--' }
                $entry[0] -ne $expectedMode
            })
        Check ($unexpected.Count -eq 0) "folders and the executable are rwxr-xr-x and every other file rw-r--r-- ($($unexpected -join '; '))"
        Check ([bool]($listing | Where-Object { $_ -match '^-rwxr-xr-x\s.*/AgentSplice\.Api$' })) "the archive records AgentSplice.Api as executable (rwxr-xr-x)"

        # As the installation guide extracts it, with the system's tar — bsdtar on macOS; tar restores the
        # permission bits the archive records.
        & tar -xzf $archivePath -C $extracted

        if ($LASTEXITCODE -ne 0) {
            throw "tar could not extract '$archiveName'."
        }
    }

    $top = @(Get-ChildItem -Path $extracted)
    Check ($top.Count -eq 1 -and $top[0].PSIsContainer -and $top[0].Name -eq "agentsplice-$version-$rid") "the archive has one top-level folder named for its version"

    $package = $top[0].FullName
    $executableName = if ($windows) { 'AgentSplice.Api.exe' } else { 'AgentSplice.Api' }
    $sqlite = if ($windows) { 'e_sqlite3.dll' } elseif ($macos) { 'libe_sqlite3.dylib' } else { 'libe_sqlite3.so' }
    $exe = Join-Path $package $executableName

    Check (Test-Path $exe) "the package holds $executableName"

    if (-not $windows) {
        $mode = [IO.File]::GetUnixFileMode($exe)
        Check ($mode.HasFlag([IO.UnixFileMode]::UserExecute)) "$executableName is executable once extracted ($mode)"
    }

    if ($macos) {
        # The package is signed ad hoc and not notarized, so a browser's quarantine mark makes macOS refuse
        # to open it; the installation guide clears the mark after the digest check and before the first
        # start, with this very command (ADR 0029). The entries a folder holds, the folder included. A
        # function's output is unrolled, so no entry reaches the caller as $null and one as a string,
        # neither of which has a Count under strict mode: every call below is wrapped in @().
        function Get-Entries([string] $Path) { & find $Path }
        function Get-Quarantined([string] $Path) { & find $Path -xattrname com.apple.quarantine }
        $mark = '0083;{0:x8};Safari;' -f [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

        # A download made with curl, as the README's commands are, carries no mark: the command must
        # succeed on such a folder and say nothing.
        $said = @(& xattr -dr com.apple.quarantine $package 2>&1)
        $code = $LASTEXITCODE
        Check ($code -eq 0 -and $said.Count -eq 0) "the guide's xattr command succeeds, and says nothing, on a folder with no quarantine mark (exit $code; $($said -join '; '))"

        # Whether this macOS's tar carries an archive's mark onto what it extracts: recorded, not
        # required, because the guide clears the folder either way.
        $probe = Join-Path $work 'probe'
        New-Item -ItemType Directory -Force -Path $probe | Out-Null
        $marked = Join-Path $work $archiveName
        Copy-Item -Path $archivePath -Destination $marked
        & xattr -w com.apple.quarantine $mark $marked
        & tar -xzf $marked -C $probe
        Write-Host "note  tar extracting a quarantined archive marked $(@(Get-Quarantined $probe).Count) of $(@(Get-Entries $probe).Count) entries"

        # Finder's Archive Utility marks every file and folder it extracts from a quarantined archive.
        & xattr -r -w com.apple.quarantine $mark $package
        $entries = @(Get-Entries $package).Count
        Check (@(Get-Quarantined $package).Count -eq $entries) "every one of the package's $entries entries carries the quarantine mark, as Finder leaves a browser download"

        $said = @(& xattr -dr com.apple.quarantine $package 2>&1)
        $code = $LASTEXITCODE
        $left = @(Get-Quarantined $package)
        Check ($code -eq 0 -and $left.Count -eq 0) "the guide's xattr command clears the mark from every file and folder (exit $code; $((@($said) + @($left)) -join '; '))"

        # Apple silicon runs no unsigned code. Every native file — found by its Mach-O magic, as the Linux
        # check finds ELF — is arm64 only, signed ad hoc, which names nobody, and asks for no macOS later
        # than the floor the installation guide, the package's README, and the release notes state; a
        # contract test holds them, and the macOS runner CI uses, to it. Today the newest any file asks
        # for is the .NET runtime's 12.0, while Microsoft supports .NET 10 from macOS 15.
        $macosFloor = [version]'15.0'
        $machO = @(Get-ChildItem -Path $package -Recurse -File | Where-Object {
                (@(Get-Content -Path $_.FullName -AsByteStream -TotalCount 4) -join ',') -in @('207,250,237,254', '202,254,186,190')
            })
        Check ($machO.Count -ge 2) "the package's native files are Mach-O ($(($machO | ForEach-Object Name) -join ', '))"

        foreach ($file in $machO) {
            $archs = "$(& lipo -archs $file.FullName 2>&1)".Trim()
            Check ($archs -eq 'arm64') "$($file.Name) is arm64 only ($archs)"

            $verified = @(& codesign --verify --strict $file.FullName 2>&1)
            Check ($LASTEXITCODE -eq 0) "$($file.Name) has a valid signature ($($verified -join '; '))"

            $signature = (@(& codesign -dv $file.FullName 2>&1) -join "`n")
            Check ($signature -match '(?m)^Signature=adhoc$') "$($file.Name) is signed ad hoc"

            $minimums = @(& otool -l $file.FullName |
                    Select-String -Pattern '^\s*minos (\d+\.\d+(?:\.\d+)?)\s*$' |
                    ForEach-Object { [version]$_.Matches[0].Groups[1].Value })
            Check ($minimums.Count -ge 1 -and -not ($minimums | Where-Object { $_ -gt $macosFloor })) "$($file.Name) asks for macOS $($minimums -join ', '), no later than the $macosFloor its documents state"
        }
    }

    if ($linux) {
        # The oldest glibc the package runs on is the newest any of its native files asks for. Today
        # that is the bundled SQLite library's, not the .NET runtime's, and nothing else would notice
        # it rising: this runner's glibc is newer than either. The installation guide, the package's
        # README, and the release notes state this floor, and a contract test holds them to it (ADR 0024).
        $glibcFloor = [version]'2.34'
        $elf = @(Get-ChildItem -Path $package -Recurse -File | Where-Object {
                (@(Get-Content -Path $_.FullName -AsByteStream -TotalCount 4) -join ',') -eq '127,69,76,70'
            })
        $readelf = Get-Command readelf -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        Check ($null -ne $readelf) "readelf is there to read which glibc the package needs"

        if ($null -ne $readelf) {
            $needed = $elf |
                ForEach-Object { & $readelf.Source --version-info --wide $_.FullName } |
                Select-String -Pattern 'GLIBC_(\d+\.\d+(?:\.\d+)?)' -AllMatches |
                ForEach-Object { $_.Matches } |
                ForEach-Object { [version]$_.Groups[1].Value } |
                Sort-Object -Descending |
                Select-Object -First 1
            Check ($elf.Count -gt 0 -and $null -ne $needed -and $needed -le $glibcFloor) "the package's $($elf.Count) native files need glibc $needed, no later than the $glibcFloor its documents state"
        }
    }

    Check (Test-Path (Join-Path $package $sqlite)) "SQLite's native library sits beside the executable"
    Check (Test-Path (Join-Path $package 'appsettings.json')) "appsettings.json sits beside the executable"
    Check (Test-Path (Join-Path $package 'dashboard' 'index.html')) "the dashboard's page is in dashboard/"
    Check (Test-Path (Join-Path $package 'dashboard' 'assets')) "the dashboard's assets are in dashboard/assets/"
    Check (Test-Path (Join-Path $package 'README.txt')) "the package carries README.txt"

    if (Test-Path (Join-Path $package 'README.txt')) {
        $readme = [IO.File]::ReadAllText((Join-Path $package 'README.txt'))

        if ($windows) {
            Check ($readme.Contains("`r`n") -and -not ($readme -match "(?<!`r)`n")) "README.txt has Windows line endings, for Notepad"
        }
        else {
            Check (-not $readme.Contains("`r")) "README.txt has Unix line endings"
        }

        if ($macos) {
            Check ($readme.Contains('xattr -dr com.apple.quarantine .')) "README.txt says how to clear the quarantine mark"
        }
    }

    # What it may be passed on under, and the notices of what it bundles (ADR 0020).
    foreach ($notice in @('LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md')) {
        Check ((Test-Path (Join-Path $package $notice)) -and
            (Get-FileHash (Join-Path $package $notice)).Hash -eq (Get-FileHash (Join-Path $repository $notice)).Hash) "the package carries the repository's $notice"
    }

    foreach ($notice in @('dotnet-runtime-LICENSE.TXT', 'dotnet-runtime-THIRD-PARTY-NOTICES.TXT', 'aspnetcore-runtime-LICENSE.TXT', 'aspnetcore-runtime-THIRD-PARTY-NOTICES.TXT')) {
        Check (Test-Path (Join-Path $package 'licenses' $notice)) "the package carries the bundled runtime's licenses/$notice"
    }

    Check (-not (Get-ChildItem -Path $package -Recurse -Filter '*.pdb')) "no loose symbol files"
    Check (-not (Test-Path (Join-Path $package 'web.config'))) "no IIS web.config"
    Check (-not (Test-Path (Join-Path $package 'aspnetcorev2_inprocess.dll'))) "no IIS module"
    Check (-not (Test-Path (Join-Path $package 'appsettings.Development.json'))) "no development settings"
    Check (-not (Get-ChildItem -Path $package -Filter 'AgentSplice.*.dll')) "the gateway's assemblies are inside the executable"
    # The Web SDK publishes an empty manifest of static web assets beside the gateway unless the project
    # turns them off; the dashboard is a folder, not static web assets (ADR 0015, ADR 0024).
    Check (-not (Get-ChildItem -Path $package -Recurse -Filter '*.staticwebassets.*')) "no static web assets manifest"

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
    # The whole line, so a sibling folder whose name merely starts with the package's cannot pass for it.
    Check (Wait-ForLog $log ('(?m)^\s*Content root path: ' + [regex]::Escape($package) + '[\\/]?\r?$')) "the content root is the package folder, not the working directory"
    Check (Wait-ForLog $log '\[1203\]') "the startup log announces the dashboard (1203)"
    Check (Wait-ForLog $log ([regex]::Escape((Join-Path $package 'dashboard')))) "the dashboard is served from the package's dashboard folder"
    Check (-not ((Get-Content -Raw $log) -match '\[1204\]')) "the startup log does not report a missing dashboard (1204)"
    Check (Wait-ForLog $log ('\[1305\][\s\S]*' + [regex]::Escape($store))) "the startup log names the store beside the executable (1305)"
    Check (Test-Path $store) "the store was created beside the executable"
    Check (-not (Get-ChildItem -Force -Path $elsewhere)) "nothing was written to the working directory"

    $listeners = @(Get-Listeners $gateway)
    Check ($listeners.Count -ge 1 -and -not ($listeners | Where-Object { $_ -ne '127.0.0.1' })) "the gateway listens on 127.0.0.1 only (it listens on $($listeners -join ', '))"

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
