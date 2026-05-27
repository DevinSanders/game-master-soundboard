# Run before upgrade or uninstall. Stops the app cleanly so the installer can
# replace files without "in use" errors.
$ErrorActionPreference = 'SilentlyContinue'

Get-Process -Name 'SoundBoard.Desktop' | ForEach-Object {
  Write-Output "Stopping $($_.Name) (PID $($_.Id))"
  $_ | Stop-Process -Force
}
