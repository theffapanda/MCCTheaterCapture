param(
    [string]$OutputDirectory = "$PSScriptRoot/dist/MCCTheaterCapture",
    [string]$FreeTypeLibrary = "$PSScriptRoot/deps/FreeType/freetype.dll"
)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'tools/Test-ProjectIsolation.ps1') -ProjectRoot $PSScriptRoot
$outputPath=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
& (Join-Path $PSScriptRoot 'native/build.ps1') -OutputDirectory (Join-Path $outputPath 'native') -FreeTypeLibrary $FreeTypeLibrary
& (Join-Path $PSScriptRoot 'shell/build.ps1') -OutputDirectory $outputPath
$licenses=Join-Path $outputPath 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native/THIRD_PARTY_LICENSES.txt') -Destination (Join-Path $licenses 'NativeHost-MIT.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'deps/FreeType/FreeType-FTL.txt') -Destination (Join-Path $licenses 'FreeType-FTL.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $outputPath 'README.md')
$buildDocs=Join-Path $outputPath 'docs'
New-Item -ItemType Directory -Force -Path $buildDocs | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/BUILD.md') -Destination $buildDocs
$screenshots=Join-Path $buildDocs 'screenshots'
New-Item -ItemType Directory -Force -Path $screenshots | Out-Null
foreach ($image in @('library.png','export.jpg')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs/screenshots/$image") -Destination $screenshots
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Record-Film.ps1') -Destination (Join-Path $outputPath 'Record-Film.ps1')
foreach ($name in @('native/main.obj','native/HostServices.obj','native/NativeEmblems.obj','native/NativeVideoSettings.obj','native/ReplayViewSettings.obj','native/NativeCamera.obj','native/NativeFonts.obj','native/NativeFontAssets.obj','native/NativePlayers.obj','native/NativeHud.obj','native/NativeHudServices.obj','native/NativeRecorder.obj','native/NativeAudio.obj','native/NativeSilentPlayback.obj','native/NativePlaybackTiming.obj','native/NativeTitleTiming.obj','native/NativeTitleHud.obj','native/NativeAudioResync.obj','native/NativeSkinTextures.obj','native/native-host.pdb','native/NativeReplayBackend.pdb','native/NativeReplayBackend.ilk','native/build-native.cmd','MCCTheaterCapture.pdb')) {
    $generated=Join-Path $outputPath $name
    if (Test-Path -LiteralPath $generated) { Remove-Item -LiteralPath $generated }
}
Write-Output "MCC Theater Capture ready: $(Join-Path $outputPath 'MCCTheaterCapture.exe')"
