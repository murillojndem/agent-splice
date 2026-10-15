#Requires -Version 7.2
<#
.SYNOPSIS
Builds the Windows package: a self-contained win-x64 gateway with the dashboard beside it (ADR 0017).

.DESCRIPTION
Builds the dashboard with pnpm, publishes the gateway with the win-x64 publish profile, puts the
dashboard where the shipped agentsplice:dashboard:rootPath looks for it, and writes
agentsplice-<version>-win-x64.zip with one top-level folder, plus SHA256SUMS in the format
`sha256sum --check` reads.

The .NET build never invokes Node (ADR 0015), so this script is where the two builds meet, as the
Dockerfile is for the image. CI runs it on windows-latest; it needs the .NET 8 SDK, Node 22, and
pnpm 10 — the machine that runs the package needs none of them.

.PARAMETER OutputDirectory
Where the zip and SHA256SUMS are written. Defaults to artifacts/package in the repository.

.EXAMPLE
pwsh -File .github/scripts/package-windows.ps1 -OutputDirectory artifacts/package
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$project = Join-Path $repository 'src' 'AgentSplice.Api' 'AgentSplice.Api.csproj'
$dashboard = Join-Path $repository 'web' 'AgentSplice.Dashboard'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repository 'artifacts' 'package'
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
$properties = (& dotnet msbuild $project -nologo -getProperty:Version -getProperty:BundledNETCoreAppPackageVersion | Out-String)

if ($LASTEXITCODE -ne 0) {
    throw "Could not read the version from $project."
}

$properties = ($properties | ConvertFrom-Json).Properties
$version = $properties.Version
$runtime = $properties.BundledNETCoreAppPackageVersion

if (-not $version -or -not $runtime) {
    throw "Could not read the version and the bundled .NET runtime version from $project."
}

# Where the gateway looks for the dashboard, read from the settings it ships with, so the package
# cannot put the build somewhere the gateway does not look.
$settings = Get-Content -Raw (Join-Path $repository 'src' 'AgentSplice.Api' 'appsettings.json') | ConvertFrom-Json
$rootPath = $settings.agentsplice.dashboard.rootPath

if ([string]::IsNullOrWhiteSpace($rootPath) -or [IO.Path]::IsPathRooted($rootPath)) {
    throw "agentsplice:dashboard:rootPath in appsettings.json must be a relative path; it is '$rootPath'."
}

$name = "agentsplice-$version-win-x64"
$work = Join-Path ([IO.Path]::GetTempPath()) ("agentsplice-package-" + [guid]::NewGuid().ToString('N'))
$staging = Join-Path $work $name

New-Item -ItemType Directory -Force -Path $staging | Out-Null

try {
    Write-Host "Building the dashboard"
    Invoke-Native pnpm @('--dir', $dashboard, 'install', '--frozen-lockfile')
    Invoke-Native pnpm @('--dir', $dashboard, 'build')

    # Symbols are embedded in each assembly, so a stack trace from the single file still has line
    # numbers and the package carries no loose .pdb files.
    Write-Host "Publishing the gateway for win-x64"
    Invoke-Native dotnet @(
        'publish', $project,
        '--configuration', 'Release',
        '-p:PublishProfile=win-x64',
        '-p:DebugType=embedded',
        '--output', $staging,
        '-nologo')

    Copy-Item -Recurse -Path (Join-Path $dashboard 'dist') -Destination (Join-Path $staging $rootPath)

    $license = Join-Path $repository 'LICENSE'

    if (Test-Path $license) {
        Copy-Item -Path $license -Destination $staging
    }

    $readme = @"
AgentSplice $version for Windows (x64)

An experimental alpha. AgentSplice is a local trace proxy for OpenAI-compatible model
runtimes such as LM Studio: it forwards requests unchanged and records where the time went.
It needs no .NET installation: it carries .NET runtime $runtime, which Windows Update does
not patch -- upgrade AgentSplice to receive .NET security fixes.

Start it from this folder or any other:

    .\AgentSplice.Api.exe

It listens on http://127.0.0.1:5280 only. Point a client at http://127.0.0.1:5280/v1, and
open http://127.0.0.1:5280/ for the dashboard. It forwards to LM Studio at
http://127.0.0.1:1234/v1 unless configured otherwise. Stop it with Ctrl+C.

Configure it with command-line arguments or environment variables rather than by editing
appsettings.json, which an upgrade replaces:

    .\AgentSplice.Api.exe --agentsplice:runtimes:0:baseUrl=http://127.0.0.1:1234/v1

Trace metadata -- never prompts or model output -- is kept in agentsplice.db in this folder
unless agentsplice:persistence:connectionString names another file.

Full instructions: docs/INSTALLATION.md in the AgentSplice repository.
"@

    # ASCII and CRLF, so Notepad on any Windows version shows it as written.
    [IO.File]::WriteAllText(
        (Join-Path $staging 'README.txt'),
        ($readme -replace "`r?`n", "`r`n") + "`r`n",
        [Text.Encoding]::ASCII)

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

    $archive = Join-Path $OutputDirectory "$name.zip"
    $sums = Join-Path $OutputDirectory 'SHA256SUMS'

    foreach ($previous in @($archive, $sums)) {
        if (Test-Path $previous) {
            Remove-Item -Path $previous
        }
    }

    # One top-level folder, so extracting the archive never scatters files into a directory that
    # already holds something.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $staging,
        $archive,
        [IO.Compression.CompressionLevel]::Optimal,
        $true)

    $hash = (Get-FileHash -Algorithm SHA256 -Path $archive).Hash.ToLowerInvariant()

    # sha256sum's format: digest, two spaces, file name, LF.
    [IO.File]::WriteAllText($sums, "$hash  $name.zip`n", [Text.Encoding]::ASCII)

    $size = [math]::Round((Get-Item $archive).Length / 1MB, 1)
    Write-Host "Wrote $archive ($size MB)"
    Write-Host "Wrote $sums"
}
finally {
    Remove-Item -Recurse -Force -Path $work -ErrorAction SilentlyContinue
}
