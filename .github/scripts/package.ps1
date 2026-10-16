#Requires -Version 7.4
<#
.SYNOPSIS
Builds a self-contained package: the gateway for one platform with the dashboard beside it (ADR 0017,
ADR 0021).

.DESCRIPTION
Builds the dashboard with pnpm, publishes the gateway with the publish profile named for the runtime
identifier, puts the dashboard where the shipped agentsplice:dashboard:rootPath looks for it, adds the
license and the notices of what the package bundles (ADR 0020), and writes one archive with one
top-level folder — agentsplice-<version>-win-x64.zip, or agentsplice-<version>-linux-x64.tar.gz — plus
SHA256SUMS in the format `sha256sum --check` reads.

The .NET build never invokes Node (ADR 0015), so this script is where the two builds meet, as the
Dockerfile is for the image. CI runs it on windows-latest for win-x64 and on ubuntu-latest for
linux-x64; it needs the .NET 8 SDK, Node 22, and pnpm 10 — the machine that runs the package needs
none of them. The Linux package is built on Linux, where its archive can record that the executable is
executable.

.PARAMETER RuntimeIdentifier
The platform: win-x64 or linux-x64. Each has a publish profile of that name, and a contract test keeps
this list and the profiles one set.

.PARAMETER OutputDirectory
Where the archive and SHA256SUMS are written. Defaults to artifacts/package in the repository.

.EXAMPLE
pwsh -File .github/scripts/package.ps1 -RuntimeIdentifier win-x64 -OutputDirectory artifacts/package
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $RuntimeIdentifier,

    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$project = Join-Path $repository 'src' 'AgentSplice.Api' 'AgentSplice.Api.csproj'
$dashboard = Join-Path $repository 'web' 'AgentSplice.Dashboard'
$windows = $RuntimeIdentifier -eq 'win-x64'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repository 'artifacts' 'package'
}

if (-not $windows -and -not $IsLinux) {
    throw "Build the $RuntimeIdentifier package on Linux: an archive written elsewhere cannot record that its executable is executable."
}

function Invoke-Native {
    param([string] $Command, [string[]] $Arguments)

    & $Command @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "'$Command $($Arguments -join ' ')' exited with code $LASTEXITCODE."
    }
}

# The version the build stamps into the binary, so the package's name cannot disagree with what
# /api/v1/system reports; and the .NET runtime the package will carry. A self-contained build takes
# the runtime of the SDK that publishes it, and nothing patches it afterwards, so which one a package
# holds is worth stating in the package.
$properties = (& dotnet msbuild $project -nologo -getProperty:Version -getProperty:BundledNETCoreAppPackageVersion -getProperty:RepositoryUrl | Out-String)

if ($LASTEXITCODE -ne 0) {
    throw "Could not read the version from $project."
}

$properties = ($properties | ConvertFrom-Json).Properties
$version = $properties.Version
$runtime = $properties.BundledNETCoreAppPackageVersion
$repositoryUrl = $properties.RepositoryUrl

if (-not $version -or -not $runtime -or -not $repositoryUrl) {
    throw "Could not read the version, the bundled .NET runtime version, and the repository from $project."
}

# Where the gateway looks for the dashboard, read from the settings it ships with, so the package
# cannot put the build somewhere the gateway does not look.
$settings = Get-Content -Raw (Join-Path $repository 'src' 'AgentSplice.Api' 'appsettings.json') | ConvertFrom-Json
$rootPath = $settings.agentsplice.dashboard.rootPath

if ([string]::IsNullOrWhiteSpace($rootPath) -or [IO.Path]::IsPathRooted($rootPath)) {
    throw "agentsplice:dashboard:rootPath in appsettings.json must be a relative path; it is '$rootPath'."
}

$name = "agentsplice-$version-$RuntimeIdentifier"
$executable = if ($windows) { 'AgentSplice.Api.exe' } else { 'AgentSplice.Api' }
$work = Join-Path ([IO.Path]::GetTempPath()) ("agentsplice-package-" + [guid]::NewGuid().ToString('N'))
$staging = Join-Path $work $name

New-Item -ItemType Directory -Force -Path $staging | Out-Null

try {
    Write-Host "Building the dashboard"
    Invoke-Native pnpm @('--dir', $dashboard, 'install', '--frozen-lockfile')
    Invoke-Native pnpm @('--dir', $dashboard, 'build')

    # Symbols are embedded in each assembly, so a stack trace from the single file still has line
    # numbers and the package carries no loose .pdb files.
    Write-Host "Publishing the gateway for $RuntimeIdentifier"
    Invoke-Native dotnet @(
        'publish', $project,
        '--configuration', 'Release',
        "-p:PublishProfile=$RuntimeIdentifier",
        '-p:DebugType=embedded',
        '--output', $staging,
        '-nologo')

    Copy-Item -Recurse -Path (Join-Path $dashboard 'dist') -Destination (Join-Path $staging $rootPath)

    # The license the package is distributed under, and the notices of everything it bundles (ADR 0020).
    # Without them the package would carry code nobody may pass on, so a missing one stops the build.
    foreach ($notice in @('LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md')) {
        $path = Join-Path $repository $notice

        if (-not (Test-Path $path)) {
            throw "$notice is missing from the repository root; every package carries it (ADR 0020)."
        }

        Copy-Item -Path $path -Destination $staging
    }

    # The package also carries the .NET runtime and ASP.NET Core, so it carries their license and
    # third-party notices as Microsoft ships them in the runtime packs. The SDK says which packs, and from
    # which directory, this publish used; asked right after the publish, it answers for the restore that
    # publish ran. ASP.NET Core 8's pack has no license file of its own: it is MIT under the same holder,
    # whose notice THIRD-PARTY-NOTICES.md carries.
    $resolved = (& dotnet msbuild $project -nologo "-p:PublishProfile=$RuntimeIdentifier" '-p:Configuration=Release' `
            -t:ResolveFrameworkReferences -getItem:ResolvedRuntimePack | Out-String)

    if ($LASTEXITCODE -ne 0) {
        throw "Could not ask the SDK which runtime packs the publish used."
    }

    $packs = @(($resolved | ConvertFrom-Json).Items.ResolvedRuntimePack)
    $licenses = Join-Path $staging 'licenses'
    New-Item -ItemType Directory -Force -Path $licenses | Out-Null

    foreach ($pack in @(
            @{ Framework = 'Microsoft.NETCore.App'; Prefix = 'dotnet-runtime'; Required = @('LICENSE', 'THIRD-PARTY-NOTICES') },
            @{ Framework = 'Microsoft.AspNetCore.App'; Prefix = 'aspnetcore-runtime'; Required = @('THIRD-PARTY-NOTICES') })) {
        $item = @($packs | Where-Object { $_.FrameworkName -eq $pack.Framework })

        if ($item.Count -ne 1 -or -not $item[0].PackageDirectory -or -not (Test-Path $item[0].PackageDirectory)) {
            throw "The SDK names no runtime pack directory for $($pack.Framework); cannot ship the notices of the runtime the package carries."
        }

        $directory = $item[0].PackageDirectory

        # Matched without regard to case: the packs name the same files LICENSE.TXT and LICENSE.txt.
        $files = @(Get-ChildItem -File -Path $directory | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)\.txt$' })

        foreach ($required in $pack.Required) {
            if (-not ($files | Where-Object { $_.BaseName -ieq $required })) {
                throw "$directory holds no $required.TXT; cannot ship the notices of the runtime the package carries."
            }
        }

        foreach ($file in $files) {
            Copy-Item -Path $file.FullName -Destination (Join-Path $licenses "$($pack.Prefix)-$($file.BaseName.ToUpperInvariant()).TXT")
        }
    }

    $start = if ($windows) { '.\AgentSplice.Api.exe' } else { './AgentSplice.Api' }
    $platform = if ($windows) { 'Windows (x64)' } else { 'Linux (x64)' }
    $patching = if ($windows) { 'Windows Update does' } else { "your distribution's updates do" }
    $requirement = if ($windows) { '' } else { "`nIt runs on x64 distributions based on glibc, with OpenSSL installed." }

    # On Linux the folder's mode is what keeps the store to its owner: SQLite creates the file readable
    # by whomever the umask allows.
    $privacy = if ($windows) { '' } else {
        "`n`nKeep this folder to your account, so that the store in it is too:`n`n    chmod 700 ."
    }

    # The guide at the release's own tag, when this is a release's build; the repository otherwise, so
    # a package built from anything else never links to a tag that does not exist.
    $guide = if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ceq "v$version") {
        "$repositoryUrl/blob/v$version/docs/INSTALLATION.md"
    }
    else {
        "docs/INSTALLATION.md in $repositoryUrl"
    }

    $readme = @"
AgentSplice $version for $platform

An experimental alpha. AgentSplice is a local trace proxy for OpenAI-compatible model
runtimes such as LM Studio: it forwards requests unchanged and records where the time went.
It needs no .NET installation: it carries .NET runtime $runtime, which $patching
not patch -- upgrade AgentSplice to receive .NET security fixes.$requirement

Start it from this folder:

    $start

or from any other folder by its full path. It listens on http://127.0.0.1:5280 only.
Point a client at http://127.0.0.1:5280/v1, and open http://127.0.0.1:5280/ for the
dashboard. It forwards to LM Studio at http://127.0.0.1:1234/v1 unless configured
otherwise. Stop it with Ctrl+C.

Configure it with command-line arguments or environment variables rather than by editing
appsettings.json, which an upgrade replaces:

    $start --agentsplice:runtimes:0:baseUrl=http://127.0.0.1:1234/v1

Trace metadata -- never prompts or model output -- is kept in agentsplice.db in this folder
unless agentsplice:persistence:connectionString names another file.$privacy

On a machine other people use, set an administrative token, as the instructions show:
without one, every account on it can read the trace metadata through /api/v1.

AgentSplice is licensed under the Apache License 2.0: see LICENSE and NOTICE. The software
it bundles is listed with its licenses in THIRD-PARTY-NOTICES.md, and the .NET runtime's
own license and notices are in the licenses folder.

Full instructions: $guide
"@

    # ASCII either way. CRLF on Windows, so Notepad on any version shows it as written; LF on Linux.
    $newline = if ($windows) { "`r`n" } else { "`n" }
    [IO.File]::WriteAllText(
        (Join-Path $staging 'README.txt'),
        ($readme -replace "`r?`n", $newline) + $newline,
        [Text.Encoding]::ASCII)

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

    $archiveName = if ($windows) { "$name.zip" } else { "$name.tar.gz" }
    $archive = Join-Path $OutputDirectory $archiveName
    $sums = Join-Path $OutputDirectory 'SHA256SUMS'

    foreach ($previous in @($archive, $sums)) {
        if (Test-Path $previous) {
            Remove-Item -Path $previous
        }
    }

    # One top-level folder, so extracting the archive never scatters files into a directory that
    # already holds something.
    if ($windows) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory(
            $staging,
            $archive,
            [IO.Compression.CompressionLevel]::Optimal,
            $true)
    }
    else {
        # The executable must keep its execute bit, which is what a tar.gz records and a zip written
        # by most tools does not. Owner and group are written as 0 by number, so the archive names no
        # account of the machine that built it; modes are written as rw-r--r--, or rwxr-xr-x for the
        # executable and the folders, so nothing in it is writable by anyone but its owner, whatever the
        # build machine's umask; and entries are sorted, so the order does not depend on the file system.
        $mode = [IO.File]::GetUnixFileMode((Join-Path $staging $executable))

        if (-not $mode.HasFlag([IO.UnixFileMode]::UserExecute)) {
            throw "The published $executable is not executable ($mode)."
        }

        # Every file's mode is set rather than kept: NuGet extracts its packages' files with an execute
        # bit, and the copies keep it, so the runtime's notices and SQLite's library would otherwise be
        # executable too. Only the executable is.
        foreach ($file in Get-ChildItem -Path $staging -Recurse -File) {
            [IO.File]::SetUnixFileMode($file.FullName, [IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead')
        }

        [IO.File]::SetUnixFileMode(
            (Join-Path $staging $executable),
            [IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute')

        Invoke-Native tar @(
            '--create', '--gzip',
            '--file', $archive,
            '--directory', $work,
            '--owner=0', '--group=0', '--numeric-owner',
            '--mode=u=rwX,go=rX',
            '--sort=name',
            $name)
    }

    $hash = (Get-FileHash -Algorithm SHA256 -Path $archive).Hash.ToLowerInvariant()

    # sha256sum's format: digest, two spaces, file name, LF.
    [IO.File]::WriteAllText($sums, "$hash  $archiveName`n", [Text.Encoding]::ASCII)

    $size = [math]::Round((Get-Item $archive).Length / 1MB, 1)
    Write-Host "Wrote $archive ($size MB)"
    Write-Host "Wrote $sums"
}
finally {
    Remove-Item -Recurse -Force -Path $work -ErrorAction SilentlyContinue
}
