# launch-mockui.ps1
# Launches a Debug build of zRover.Retriever in screenshot mode — the UI is
# populated with synthetic sessions, MCP clients, remote retrievers, a fake
# external URL, a live-generated QR code, and a fake bearer token. No real
# services are started, no listener is bound, no firewall rule is created.
#
# Prereq: Debug MSIX is installed (run .\deploy-dev.ps1 first).
#
# How it works: AppContainer apps don't inherit your console env vars by
# default. We set the env var system-wide on the user, launch the app, then
# remove it afterwards so the next normal launch behaves normally.
[CmdletBinding()] param()

$ErrorActionPreference = 'Stop'
$varName = 'ZROVER_MOCK_UI'
$aumid   = 'shell:AppsFolder\zRover.Retriever_psswvz4095eeg!App'

# Stop any running instance so the next launch picks up the mock env var.
Get-Process zRover.Retriever -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# Set user-scoped env var so the AppContainer process inherits it.
[Environment]::SetEnvironmentVariable($varName, '1', 'User')
Write-Host "Set $varName=1 (User scope)"

try {
    explorer.exe $aumid
    Write-Host "Launched zRover.Retriever in screenshot mode."
    Write-Host "Take screenshots, then close the window when done."

    # Wait for the user to close the window so we can clean up the env var.
    while ($true) {
        $p = Get-Process zRover.Retriever -ErrorAction SilentlyContinue
        if (-not $p) { break }
        Start-Sleep -Seconds 1
    }
}
finally {
    [Environment]::SetEnvironmentVariable($varName, $null, 'User')
    Write-Host "Cleared $varName."
}
