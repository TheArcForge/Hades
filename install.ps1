<#
Hades installer for Windows.

    irm https://raw.githubusercontent.com/TheArcForge/Hades/main/install.ps1 | iex

Downloads the release MSI for this machine's architecture, verifies its checksum, and installs it
per-user.

WHY THIS SCRIPT EXISTS, STATED PLAINLY. Hades is not code-signed. Windows shows SmartScreen's
"Windows protected your PC" dialog for unsigned installers - but only for files carrying a
Mark-of-the-Web, the `Zone.Identifier` alternate data stream that browsers attach to downloads.
Command-line downloaders do not attach it. That was measured on Windows 11 26200 rather than assumed:
`curl.exe` 8.21.0, `Invoke-WebRequest` and `System.Net.WebClient` all wrote no `Zone.Identifier`,
checked against a control file with a hand-written stream on the same NTFS volume to prove the check
could see one if it were there. So an MSI you download in a browser is warned about; the same MSI
fetched by this script is not.

NOTHING HERE DISABLES OR WORKS AROUND A SECURITY CHECK. It does not touch SmartScreen settings, does
not strip a Mark-of-the-Web, does not modify Defender, and does not need Administrator. It is a
stopgap until the MSI is properly signed, at which point the channel stops mattering and this script
becomes unnecessary. On Windows 11 machines with Smart App Control enabled, unsigned code is blocked
outright with no override - this script cannot help there, and does not pretend to.

If you would rather not run a script you have not read, download it first and read it:

    irm https://raw.githubusercontent.com/TheArcForge/Hades/main/install.ps1 -OutFile install.ps1
    notepad install.ps1
    powershell -ExecutionPolicy Bypass -File install.ps1

MAINTAINERS: $Version and the two $Sha256 values below are what to bump per release. The checksums
come from `Get-FileHash -Algorithm SHA256` against the artifacts ACTUALLY ATTACHED to the release -
never a value copied from a local build. Both must be set; the script refuses to run while either is
still the sentinel.
#>

$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------------------- pinned per release

$Version = '2.1.0'

# Sentinel, not a placeholder to be quietly ignored: the script hard-fails while either is unset,
# because an installer that skips checksum verification is worse than one that refuses to run.
$Sha256 = @{
    'win-x64'   = 'REPLACE_AT_RELEASE'
    'win-arm64' = 'REPLACE_AT_RELEASE'
}

$Repo = 'TheArcForge/Hades'

# -----------------------------------------------------------------------------------------------

function Install-Hades {

    function Write-Step { param([string]$m) Write-Host "==> $m" -ForegroundColor White }
    function Write-Note { param([string]$m) Write-Host "    $m" }

    # ------------------------------------------------------------------- preconditions

    if (-not ($IsWindows -or $env:OS -eq 'Windows_NT')) {
        throw 'Hades'' MSI is for Windows. On macOS use install.sh.'
    }

    # A per-user MSI installs into the LOCALAPPDATA of whoever runs it. Elevated, that is the
    # ADMINISTRATOR's profile, not yours - the app would land somewhere you never look, the PATH
    # entry would be on the wrong account, and the tray app would not start with your session.
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw @'
Do not run this from an elevated prompt - Hades installs per-user, into your own profile.
  Run it from a normal PowerShell window.
'@
    }

    # PROCESSOR_ARCHITECTURE reports "x86" when a 32-bit PowerShell runs on a 64-bit OS; in that
    # case PROCESSOR_ARCHITEW6432 carries the real one. Checking both is why this works from
    # whichever PowerShell the user happens to have open.
    $rawArch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    $rid = switch ($rawArch) {
        'AMD64' { 'win-x64' }
        'ARM64' { 'win-arm64' }
        default {
            throw "Hades needs a 64-bit machine; this reports $rawArch. There is no 32-bit build."
        }
    }

    # Read the build from the registry, NOT from [Environment]::OSVersion - and not from MSI's
    # WindowsBuild either, which is frozen at Windows 8.1's 9600 for unmanifested packages. The
    # MSI's own launch condition reads this same registry value for exactly that reason; see
    # Windows/Installer/Hades.wxs.
    $build = [int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name CurrentBuildNumber).CurrentBuildNumber
    if ($build -lt 14393) {
        throw "Hades needs Windows 10 version 1607 (build 14393) or later; this is build $build."
    }

    if ($Sha256[$rid] -eq 'REPLACE_AT_RELEASE') {
        throw @"
This copy of install.ps1 has no checksum pinned for $rid, so it cannot verify what it downloads.
  That is a packaging mistake, not something you did. Please report it:
  https://github.com/$Repo/issues
"@
    }

    # Installing over a running shell leaves MSI to either fail on locked files or schedule a
    # reboot. Refusing is clearer than either, and says how to fix it.
    if (Get-Process -Name 'Hades.Shell' -ErrorAction SilentlyContinue) {
        throw @'
Hades is currently running.
  Quit it from the tray icon (right-click > Quit) and run this again.
'@
    }

    # ------------------------------------------------------------------- download + verify

    $msiName = "Hades-$Version-$rid.msi"
    $url = "https://github.com/$Repo/releases/download/v$Version/$msiName"

    $workDir = Join-Path ([IO.Path]::GetTempPath()) ("hades-install-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $workDir -Force | Out-Null
    $msi = Join-Path $workDir $msiName

    try {
        Write-Step "Downloading Hades $Version ($rid)"
        Write-Note $url

        # curl.exe preferred - in-box since Windows 10 1803 and insensitive to PowerShell version or
        # proxy plumbing. Below 1803 it is absent, hence the fallback: Invoke-WebRequest was
        # measured to write no Mark-of-the-Web either, so this is a real alternative rather than a
        # compromise. TLS 1.2 must be forced because Windows PowerShell 5.1 still defaults lower on
        # some builds, and GitHub refuses those.
        $curl = Get-Command 'curl.exe' -ErrorAction SilentlyContinue
        if ($curl) {
            & $curl.Source -fL --proto '=https' --tlsv1.2 --progress-bar -o $msi $url
            if ($LASTEXITCODE -ne 0) { throw "curl exited $LASTEXITCODE" }
        }
        else {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $prev = $ProgressPreference
            $ProgressPreference = 'SilentlyContinue'
            try { Invoke-WebRequest -Uri $url -OutFile $msi -UseBasicParsing }
            finally { $ProgressPreference = $prev }
        }

        if (-not (Test-Path $msi)) { throw 'the download produced no file' }

        Write-Step 'Verifying checksum'
        $actual = (Get-FileHash -Path $msi -Algorithm SHA256).Hash.ToLowerInvariant()
        $expected = $Sha256[$rid].ToLowerInvariant()
        if ($actual -ne $expected) {
            throw @"
Checksum mismatch - refusing to install.
  expected: $expected
  actual:   $actual
  The download may be corrupted or truncated. Try again; if it keeps failing,
  please open an issue at https://github.com/$Repo/issues rather than installing anyway.
"@
        }
        Write-Note 'sha256 OK'

        # --------------------------------------------------------------- install

        Write-Step 'Installing'
        $log = Join-Path $workDir 'install.log'
        $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$msi`"", '/qn', '/l*v', "`"$log`"") -PassThru -Wait

        if ($p.ExitCode -ne 0) {
            # 1602 is the user cancelling; anything else is worth the log. Copied out of $workDir
            # because the finally block below is about to delete it.
            $kept = Join-Path ([IO.Path]::GetTempPath()) 'hades-install.log'
            if (Test-Path $log) { Copy-Item $log $kept -Force }
            throw @"
msiexec failed with exit code $($p.ExitCode).
  A verbose log was kept at: $kept
  Please attach it to an issue at https://github.com/$Repo/issues
"@
        }
    }
    finally {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    # ------------------------------------------------------------------- report honestly

    $installDir = Join-Path $env:LOCALAPPDATA 'Programs\Hades'

    Write-Host ''
    Write-Host "Hades $Version installed." -ForegroundColor Green
    Write-Host ''
    Write-Note "Location:  $installDir"
    Write-Note 'Start it:  Start Menu > Hades'
    Write-Host ''
    Write-Note 'The `hades` command is on your PATH, but ONLY in terminals opened from now on -'
    Write-Note 'Windows hands each process its environment at launch, so windows already open'
    Write-Note 'will not see it. Open a new one and run: hades status'
    Write-Host ''
    Write-Note 'Your data lives in %LOCALAPPDATA%\Hades and is never removed by uninstalling.'
}

try {
    Install-Hades
}
catch {
    # `throw` rather than `exit` throughout, because this script is designed to be run through
    # `iex` - where `exit` would terminate the user's whole PowerShell session, not just the
    # install. Catching here turns a fatal into a readable message and leaves the session alive.
    Write-Host ''
    Write-Host "error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
}
