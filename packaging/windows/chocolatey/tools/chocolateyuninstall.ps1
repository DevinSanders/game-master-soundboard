$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'gmsoundboard'
  softwareName   = 'Game Master Sound Board*'
  fileType       = 'EXE'
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0, 3010, 1605, 1614, 1641)
}

# Resolve uninstaller from the registry entry Inno Setup created.
[array]$key = Get-UninstallRegistryKey -SoftwareName $packageArgs.softwareName
if ($key.Count -eq 1) {
  $packageArgs.file = "$($key[0].UninstallString)"
  Uninstall-ChocolateyPackage @packageArgs
}
elseif ($key.Count -eq 0) {
  Write-Warning "$($packageArgs.packageName) is not installed."
}
else {
  Write-Warning "$($key.Count) matches found for $($packageArgs.softwareName). Skipping uninstall — clean up manually."
  $key | ForEach-Object { Write-Warning "  - $($_.DisplayName) ($($_.UninstallString))" }
}
