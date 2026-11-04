param([string]$OutputDirectory = "")
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'dist/MCCTheaterCapture' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
& dotnet build (Join-Path $PSScriptRoot 'MCCTheaterCapture.csproj') -c Release --nologo --output $OutputDirectory -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'MCC Theater Capture shell build failed.' }
# A Low label inherited from the build folder would restrict the launched app.
& icacls.exe (Join-Path $OutputDirectory 'MCCTheaterCapture.exe') /setintegritylevel M
if ($LASTEXITCODE -ne 0) { throw 'Could not set Medium integrity on MCCTheaterCapture.exe. Run the build from a normal desktop session.' }
Write-Host "MCC Theater Capture: $OutputDirectory/MCCTheaterCapture.exe"
