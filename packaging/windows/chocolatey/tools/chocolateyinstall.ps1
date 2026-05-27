# Chocolatey install script.
# Placeholders are rewritten by the release workflow before packing:
#   $DOWNLOAD_URL$  -> https://github.com/.../releases/download/v1.2.3/GameMasterSoundBoard-Setup-1.2.3.exe
#   $CHECKSUM$      -> SHA256 of the installer
$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = 'gmsoundboard'
  fileType       = 'EXE'
  url            = '$DOWNLOAD_URL$'
  softwareName   = 'Game Master Sound Board*'
  checksum       = '$CHECKSUM$'
  checksumType   = 'sha256'
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER'
  validExitCodes = @(0, 3010, 1641)
}

Install-ChocolateyPackage @packageArgs
