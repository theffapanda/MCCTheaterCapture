param()
$ErrorActionPreference='Stop'
$executable=Join-Path $PSScriptRoot 'dist/MCCTheaterCapture/MCCTheaterCapture.exe'
if(!(Test-Path -LiteralPath $executable)){throw 'Build this project first with .\build.ps1.'}
Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -WindowStyle Normal
