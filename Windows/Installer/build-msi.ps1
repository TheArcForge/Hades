<#
.SYNOPSIS
  Builds the Hades MSI for one runtime identifier.

.DESCRIPTION
  Publishes the shell, the CLI and the core self-contained for the given RID, assembles them into
  the layout the installed app expects, and hands that to WiX.

  SELF-CONTAINED for all three: a user installing a tray app has not agreed to install a .NET
  runtime, and "download .NET first" is a support burden on exactly the machines least able to
  answer for it.

  THE LAYOUT IS NOT ARBITRARY. Shell and CLI publish side by side at the staging root so that
  hades.exe lands directly in the directory the MSI puts on PATH; the core goes under core\ because
  that is precisely where Hades.Shell's CoreLifetime and Hades.Cli's CoreLocator both look for it.
  Change this and both of those stop finding it.

  Publishing two self-contained apps into ONE directory is safe here and worth stating plainly:
  they carry the same .NET version, so the shared runtime files are byte-identical, and everything
  app-specific (.deps.json, .runtimeconfig.json, the apphost) is named after its own assembly.

.EXAMPLE
  .\build-msi.ps1 -Rid win-x64 -Version 2.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$repoRoot = Split-Path (Split-Path $installerDir -Parent) -Parent
$staging = Join-Path $installerDir "obj\staging\$Rid"
$outputDir = Join-Path $installerDir 'bin'
$msi = Join-Path $outputDir "Hades-$Version-$Rid.msi"

Write-Host "== Hades $Version ($Rid) ==" -ForegroundColor Cyan
Write-Host "repo:    $repoRoot"
Write-Host "staging: $staging"

# Cleared every run. A stale file from a previous RID would be harvested into the payload and
# shipped - an arm64 MSI quietly carrying an x64 binary is the exact failure this avoids.
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging, $outputDir | Out-Null

function Publish-Project {
    param([string]$Project, [string]$Destination, [string]$Label)

    Write-Host "== Publishing $Label ($Rid) ==" -ForegroundColor Cyan
    dotnet publish $Project `
        -c $Configuration `
        -r $Rid `
        --self-contained true `
        -p:Version=$Version `
        -o $Destination

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Label" }
}

Publish-Project -Project (Join-Path $repoRoot 'Windows\Hades.Shell\Hades.Shell.csproj') `
                -Destination $staging -Label 'Hades.Shell'

Publish-Project -Project (Join-Path $repoRoot 'Core\src\Hades.Cli\Hades.Cli.csproj') `
                -Destination $staging -Label 'hades (CLI)'

Publish-Project -Project (Join-Path $repoRoot 'Core\src\Hades.Server\Hades.Server.csproj') `
                -Destination (Join-Path $staging 'core') -Label 'Hades.Server (core)'

# Fail here rather than shipping an MSI that installs cleanly and then does nothing. Each of these
# is load-bearing: the shell is the app, hades.exe is what the PATH entry exists for, and the core
# is what both of them supervise.
foreach ($required in @('Hades.Shell.exe', 'hades.exe', 'core\Hades.Server.exe')) {
    $path = Join-Path $staging $required
    if (-not (Test-Path $path)) { throw "publish did not produce $required (looked for $path)" }
}

Write-Host '== Building MSI ==' -ForegroundColor Cyan

# -arch must match the payload: it sets the MSI's own Template Summary, which is what stops an
# arm64 package from installing on x64 and vice versa.
$arch = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }

# The product icon Hades.wxs points ARPPRODUCTICON at. It is the shell's OWN ApplicationIcon
# source, not a copy staged beside the payload: the icon reaches the executable at compile time as
# a Win32 resource, so it never appears in the publish output for a staged path to find. Checked
# explicitly because a WiX Icon element with an unresolvable SourceFile fails deep inside the
# build with a light-up error about a file id, which is a poor way to learn a path is wrong.
$iconFile = Join-Path $repoRoot 'Windows\Hades.Shell\Icons\app.ico'
if (-not (Test-Path $iconFile)) {
    throw "product icon not found at $iconFile (run Windows\Hades.Shell\Icons\generate-icons.ps1)"
}

wix build (Join-Path $installerDir 'Hades.wxs') `
    -arch $arch `
    -define "Version=$Version" `
    -define "StagingDir=$staging" `
    -define "IconFile=$iconFile" `
    -out $msi

if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

# WiX treats a MISSING or EMPTY staging directory as a WARNING - WIX8601 "Missing directory for
# harvesting files" and WIX8600 "zero files harvested" - and still exits 0. The result is a ~100KB
# MSI that installs perfectly and delivers nothing. Measured, not assumed: a mistyped StagingDir
# produced a 0 MB MSI and `wix build` reported success. WiX 7.0.0 has no warnings-as-errors switch
# (`wix build -h` lists none), so the assertion has to live here.
#
# Counted exactly rather than sanity-checked against a size floor: the MSI's File table carries one
# row per staged file, so this catches a PARTIAL harvest too, not only a total one.
function Get-MsiFileCount {
    param([string]$Path)

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db   = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Path, 0))
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @('SELECT `File` FROM `File`'))

    # Every InvokeMember result must be suppressed - PowerShell would otherwise emit each one and
    # this function would return an array instead of the count.
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, @($null)) | Out-Null
    $count = 0
    while ($null -ne $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, @())) { $count++ }
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, @()) | Out-Null

    return $count
}

$stagedCount = @(Get-ChildItem $staging -Recurse -File).Count
$packagedCount = Get-MsiFileCount $msi

if ($packagedCount -ne $stagedCount) {
    throw "MSI carries $packagedCount files but staging holds $stagedCount. " +
          'The payload was not harvested correctly - check that StagingDir reached wix intact.'
}

Write-Host "Payload verified: $packagedCount files staged and packaged." -ForegroundColor Green

$size = [Math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host ""
Write-Host "Built $msi ($size MB)" -ForegroundColor Green
Write-Host ""
Write-Host "This MSI is UNSIGNED. SmartScreen will warn on first run, and that warning is correct -"
Write-Host "nothing here has been signed by anyone. See Spec #5 section 8.2 for the dated position on that."
