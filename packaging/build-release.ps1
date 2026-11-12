#Requires -Version 7.4
<#
.SYNOPSIS
Builds the release files of mssql-readonly-mcp for one runtime: the archive, or (with -BundleFrom) the MCPB bundle.

.DESCRIPTION
Used by .github/workflows/release.yml and by hand (docs/releasing.md). The two halves run separately, so the job that
builds the archives runs no npm code:

  Archive (default): publishes the server self-contained (not trimmed, not Native AOT) into
    <WorkDirectory>/publish/<rid> and writes to -OutputDirectory
      mssql-readonly-mcp-<version>-<rid>.zip      (win-x64)
      mssql-readonly-mcp-<version>-<rid>.tar.gz   (linux-x64, osx-arm64)
    One folder inside: the published build, LICENSE and README.txt.

  Bundle (-BundleFrom <publish folder> -McpbTools <folder>): runs no dotnet; packs an existing publish folder into
      mssql-readonly-mcp-<version>-<rid>.mcpb
    with manifest.json (from packaging/mcpb/manifest.json), LICENSE and the build under server/.

Run it on Linux or macOS for the linux-x64 and osx-arm64 files: on Windows, tar and the MCPB CLI do not store the
executable bit.

.EXAMPLE
pwsh packaging/build-release.ps1 -Version 0.0.0-dev -Rid linux-x64 -OutputDirectory /tmp/release/dist `
  -WorkDirectory /tmp/release/work -ArtifactsPath /tmp/release/artifacts

.EXAMPLE
pwsh packaging/build-release.ps1 -Version 0.0.0-dev -Rid linux-x64 -OutputDirectory /tmp/release/bundles `
  -WorkDirectory /tmp/release/bundle-work -BundleFrom /tmp/release/work/publish/linux-x64 `
  -McpbTools /tmp/release/mcpb-tools
#>
[CmdletBinding(DefaultParameterSetName = 'Archive')]
param(
    # Semantic version without the leading v, for example 0.1.0 or 0.0.0-dev.
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string] $Version,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'osx-arm64')][string] $Rid,
    # Receives the archive or the bundle. Created if missing.
    [Parameter(Mandatory)][string] $OutputDirectory,
    # Staging space. Archive: publish/<rid> (kept, the bundle half reads it) and stage/<rid>. Bundle: bundle/<rid>.
    # The <rid> folders are emptied first.
    [Parameter(Mandatory)][string] $WorkDirectory,
    # Archive, optional: passed to dotnet publish as --artifacts-path, so bin/ and obj/ of the checkout stay unchanged.
    [Parameter(ParameterSetName = 'Archive')][string] $ArtifactsPath,
    # Bundle: the publish folder of this RID made by the archive half.
    [Parameter(Mandatory, ParameterSetName = 'Bundle')][string] $BundleFrom,
    # Bundle: a folder where `npm ci --ignore-scripts` ran on copies of packaging/mcpb/package.json and
    # package-lock.json (the MCPB CLI, version pinned there).
    [Parameter(Mandatory, ParameterSetName = 'Bundle')][string] $McpbTools
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest

$repoRoot = Split-Path $PSScriptRoot -Parent
$name = "mssql-readonly-mcp-$Version-$Rid"
$exeName = if ($Rid.StartsWith('win-')) { 'mssql-readonly-mcp.exe' } else { 'mssql-readonly-mcp' }

function Resolve-FullPath([string] $path) {
    # Relative to the current PowerShell location (not the process directory), whether or not the path exists yet.
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)
}

function New-EmptyDirectory([string] $path) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    New-Item -ItemType Directory -Force $path | Out-Null
}

function Set-Executable([string] $path) {
    # A downloaded workflow artifact loses file modes, so the bundle half sets the bit again.
    if (-not $IsWindows -and -not $Rid.StartsWith('win-')) { chmod 0755 $path }
}

$OutputDirectory = Resolve-FullPath $OutputDirectory
$WorkDirectory = Resolve-FullPath $WorkDirectory
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null

if ($PSCmdlet.ParameterSetName -eq 'Archive') {
    # 1. Publish. Run from the repository root so global.json selects the SDK.
    $publishDir = Join-Path $WorkDirectory "publish/$Rid"
    New-EmptyDirectory $publishDir
    $publishArgs = @(
        'publish', 'src/MssqlReadOnlyMcp/MssqlReadOnlyMcp.csproj',
        '-c', 'Release',
        '-r', $Rid,
        '--self-contained', 'true',
        "-p:Version=$Version",
        '-warnaserror',
        '-o', $publishDir
    )
    if ($ArtifactsPath) { $publishArgs += @('--artifacts-path', (Resolve-FullPath $ArtifactsPath)) }
    Push-Location $repoRoot
    try { dotnet @publishArgs } finally { Pop-Location }

    $exePath = Join-Path $publishDir $exeName
    if (-not (Test-Path $exePath)) { throw "The publish output has no $exeName." }
    Set-Executable $exePath

    # 2. Archive: one top-level folder with the build, LICENSE and README.txt.
    $stageRoot = Join-Path $WorkDirectory "stage/$Rid"
    New-EmptyDirectory $stageRoot
    $stage = Join-Path $stageRoot $name
    Copy-Item $publishDir $stage -Recurse
    Copy-Item (Join-Path $repoRoot 'LICENSE') $stage
    $readme = Get-Content -Raw (Join-Path $PSScriptRoot 'README.txt')
    $readme = $readme.Replace('@VERSION@', $Version).Replace('@RID@', $Rid)
    [IO.File]::WriteAllText((Join-Path $stage 'README.txt'), $readme)

    if ($Rid.StartsWith('win-')) {
        $archive = Join-Path $OutputDirectory "$name.zip"
        if (Test-Path $archive) { Remove-Item $archive }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory(
            $stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    }
    else {
        # tar keeps the executable bit set above (on a Linux or macOS runner).
        $archive = Join-Path $OutputDirectory "$name.tar.gz"
        tar -czf $archive -C $stageRoot $name
    }
    Write-Host "archive: $archive"
}
else {
    # 3. MCPB bundle: manifest.json at the root, the build under server/. No dotnet runs here.
    $mcpbCli = Join-Path $McpbTools 'node_modules/@anthropic-ai/mcpb/dist/cli/cli.js'
    if (-not (Test-Path $mcpbCli)) { throw "The MCPB CLI is not installed in $McpbTools." }
    $BundleFrom = Resolve-FullPath $BundleFrom
    if (-not (Test-Path (Join-Path $BundleFrom $exeName))) { throw "$BundleFrom has no $exeName." }

    $bundleDir = Join-Path $WorkDirectory "bundle/$Rid"
    New-EmptyDirectory $bundleDir
    Copy-Item $BundleFrom (Join-Path $bundleDir 'server') -Recurse
    Set-Executable (Join-Path $bundleDir "server/$exeName")
    Copy-Item (Join-Path $repoRoot 'LICENSE') $bundleDir

    $manifest = Get-Content -Raw (Join-Path $PSScriptRoot 'mcpb/manifest.json') | ConvertFrom-Json
    $manifest.version = $Version
    $manifest.server.entry_point = "server/$exeName"
    # The executable name is written out in full for Windows. MANIFEST.md says hosts add .exe on Windows themselves;
    # the reference config code in the MCPB package (getMcpConfigForManifest) substitutes variables and adds nothing.
    $manifest.server.mcp_config.command = "`${__dirname}/server/$exeName"
    $platform = switch ($Rid) { 'win-x64' { 'win32' } 'linux-x64' { 'linux' } 'osx-arm64' { 'darwin' } }
    $manifest.compatibility.platforms = @($platform)
    [IO.File]::WriteAllText((Join-Path $bundleDir 'manifest.json'), ($manifest | ConvertTo-Json -Depth 20))

    $bundle = Join-Path $OutputDirectory "$name.mcpb"
    if (Test-Path $bundle) { Remove-Item $bundle }
    node $mcpbCli validate (Join-Path $bundleDir 'manifest.json')
    node $mcpbCli pack $bundleDir $bundle
    if (-not (Test-Path $bundle)) { throw "mcpb pack wrote no $bundle." }
    Write-Host "bundle: $bundle"
}
