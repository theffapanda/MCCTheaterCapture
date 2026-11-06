param(
    [string]$OutputDirectory = "$PSScriptRoot/../dist/MCCTheaterCapture/native",
    [string]$FreeTypeLibrary = "$PSScriptRoot/../deps/FreeType/freetype.dll"
)
$ErrorActionPreference = 'Stop'
$outputPath=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
if (!(Test-Path -LiteralPath $FreeTypeLibrary)) { throw 'Provide -FreeTypeLibrary pointing to the audited FreeType 2.13.2 x64 DLL' }
$freeTypeHash=(Get-FileHash -LiteralPath $FreeTypeLibrary -Algorithm SHA256).Hash.ToLowerInvariant()
if ($freeTypeHash -ne '558f16aec5ae86588afa80d46d9f2029c3081dc21015017ef85db7cf25e293a6') { throw 'FreeType library identity does not match the audited rasterizer' }
$packagedFreeType=Join-Path $outputPath 'freetype.dll'
if (!(Test-Path -LiteralPath $packagedFreeType) -or (Get-FileHash -LiteralPath $packagedFreeType -Algorithm SHA256).Hash.ToLowerInvariant() -ne $freeTypeHash) {
    Copy-Item -LiteralPath $FreeTypeLibrary -Destination $packagedFreeType -Force
}
$vcvars='C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
if (!(Test-Path -LiteralPath $vcvars)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($installation) { $vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat' }
    }
}
if (!(Test-Path -LiteralPath $vcvars)) { throw 'Visual Studio 2022 x64 build tools unavailable. Install the Desktop development with C++ workload (Community or Build Tools).' }
$command='@call "'+$vcvars+'" >nul'+"`r`n"+'cl.exe /nologo /std:c++20 /EHsc /MT /O2 /Zi /W4 /DUNICODE /D_UNICODE "'+$PSScriptRoot+'\main.cpp" "'+$PSScriptRoot+'\HostServices.cpp" "'+$PSScriptRoot+'\NativeEmblems.cpp" "'+$PSScriptRoot+'\NativeVideoSettings.cpp" "'+$PSScriptRoot+'\ReplayViewSettings.cpp" "'+$PSScriptRoot+'\NativeCamera.cpp" "'+$PSScriptRoot+'\NativeFonts.cpp" "'+$PSScriptRoot+'\NativeFontAssets.cpp" "'+$PSScriptRoot+'\NativePlayers.cpp" "'+$PSScriptRoot+'\NativeHud.cpp" "'+$PSScriptRoot+'\NativeHudServices.cpp" "'+$PSScriptRoot+'\NativeRecorder.cpp" "'+$PSScriptRoot+'\NativeAudio.cpp" "'+$PSScriptRoot+'\NativeSilentPlayback.cpp" "'+$PSScriptRoot+'\NativePlaybackTiming.cpp" "'+$PSScriptRoot+'\NativeTitleTiming.cpp" "'+$PSScriptRoot+'\NativeTitleHud.cpp" "'+$PSScriptRoot+'\NativeAudioResync.cpp" /Fe:"'+$outputPath+'\NativeReplayBackend.exe" /Fo:"'+$outputPath+'/" /Fd:"'+$outputPath+'\native-host.pdb" /link /MANIFEST:EMBED /MANIFESTINPUT:"'+$PSScriptRoot+'\app.manifest" /DEBUG /PDB:"'+$outputPath+'\NativeReplayBackend.pdb" d3d11.lib dxgi.lib user32.lib gdi32.lib psapi.lib bcrypt.lib mfplat.lib mfreadwrite.lib mfuuid.lib ole32.lib oleaut32.lib shell32.lib mmdevapi.lib'+"`r`n"+'exit /b %errorlevel%'
# vcvars64.bat calls vswhere.exe by name; make sure the Visual Studio installer folder is on PATH.
$installer=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path -LiteralPath (Join-Path $installer 'vswhere.exe')) -and -not (($env:PATH -split ';') -contains $installer)) { $env:PATH="$installer;$env:PATH" }
$buildFile=Join-Path $outputPath 'build-native.cmd'
[IO.File]::WriteAllText($buildFile,$command,[Text.Encoding]::ASCII)
& $env:ComSpec /d /c $buildFile
if ($LASTEXITCODE -ne 0) { throw "Native POC build failed: $LASTEXITCODE" }
# The renderer must retain normal desktop permissions when writing captures.
& icacls.exe (Join-Path $outputPath 'NativeReplayBackend.exe') /setintegritylevel M
if ($LASTEXITCODE -ne 0) { throw 'Could not set Medium integrity on NativeReplayBackend.exe. Run the build from a normal desktop session.' }
Get-Item -LiteralPath (Join-Path $outputPath 'NativeReplayBackend.exe') | Select-Object FullName,Length
