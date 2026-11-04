param(
    [string]$BuildDirectory = "$PSScriptRoot/dist/MCCTheaterCapture",
    [switch]$Build,
    [string]$DotNetPath = 'dotnet',
    [string]$WebView2Runtime = "$PSScriptRoot/deps/WebView2Fixed/Microsoft.WebView2.FixedVersionRuntime.156.0.4314.50.x64"
)
$ErrorActionPreference = 'Stop'
if ($Build) {
    # A unique build directory prevents stale output from entering a release.
    $BuildDirectory = Join-Path $PSScriptRoot ('artifacts/release-build-' + [guid]::NewGuid().ToString('N'))
    & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $BuildDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
}
$source = [IO.Path]::GetFullPath($BuildDirectory)
$release = Join-Path $PSScriptRoot ('dist/releases/' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $release 'MCCTheaterCapture'
if (!(Test-Path -LiteralPath (Join-Path $WebView2Runtime 'msedgewebview2.exe'))) {
    throw 'A Microsoft Fixed Version x64 WebView2 runtime is required. Set -WebView2Runtime to its extracted directory.'
}
# Only runtime files are eligible. Never recursively copy a working app folder:
# it can contain recordings, capture caches, browser data and old build files.
$files = @('native/NativeReplayBackend.exe', 'native/freetype.dll')
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) { throw "Missing runtime file: $file. Run ./package.ps1 -Build with the build tools installed." }
}
New-Item -ItemType Directory -Path $payload -Force | Out-Null
# Publish into an empty directory so SDK-selected runtime dependencies are complete
# without carrying any developer output, browser profiles or recordings.
& $DotNetPath publish (Join-Path $PSScriptRoot 'shell/MCCTheaterCapture.csproj') -c Release -r win-x64 --self-contained true --nologo --output $payload --source https://api.nuget.org/v3/index.json -p:UseSharedCompilation=false -p:DebugType=none -p:DebugSymbols=false -p:PublishReadyToRun=false -p:SatelliteResourceLanguages=en
if ($LASTEXITCODE -ne 0) { throw 'Standalone .NET publish failed.' }
Copy-Item -LiteralPath $WebView2Runtime -Destination (Join-Path $payload 'WebView2Runtime') -Recurse
foreach ($file in $files) {
    $destination = Join-Path $payload $file
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination $destination
}
# FreeType uses the VC runtime dynamically; app-local deployment avoids an installer.
Copy-Item -LiteralPath (Join-Path $WebView2Runtime 'vcruntime140.dll') -Destination (Join-Path $payload 'native/vcruntime140.dll')
$licenses = Join-Path $payload 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
foreach ($notice in @(
    @('deps/FFmpeg/zlib-LICENSE.txt', 'FFmpeg-zlib-LICENSE.txt'),
    @('deps/FFmpeg/x264-LICENSE.txt', 'FFmpeg-x264-LICENSE.txt'),
    @('deps/FFmpeg/LICENSE', 'FFmpeg-LICENSE.txt'),
    @('deps/FFmpeg/README.txt', 'FFmpeg-README.txt'),
    @('deps/WebView2/LICENSE.txt', 'WebView2-LICENSE.txt'),
    @('deps/WebView2/NOTICE.txt', 'WebView2-NOTICE.txt'),
    @('deps/FreeType/FreeType-FTL.txt', 'FreeType-FTL.txt'),
    @('native/THIRD_PARTY_LICENSES.txt', 'NativeHost-MIT.txt')
)) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $notice[0]) -Destination (Join-Path $licenses $notice[1])
}
$toolFolder = Join-Path $payload 'tools'
New-Item -ItemType Directory -Path $toolFolder -Force | Out-Null
foreach ($tool in @('ffmpeg.exe', 'ffprobe.exe')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "deps/FFmpeg/bin/$tool") -Destination (Join-Path $toolFolder $tool)
}
@'
MCC Theater Capture

Requires Windows x64 and an installed copy of MCC. .NET, WebView2, FFmpeg,
and FFprobe are bundled. No runtime installation or PATH setup is required.
Open MCCTheaterCapture.exe, add Theater films to Library, arrange the sequence,
choose export settings, then Start export. Keep all files in this folder together.
UserData is created on use; it contains personal data and cached media and should
not be included when sharing the app. Third-party notices are in licenses/.
'@ | Set-Content -LiteralPath (Join-Path $payload 'README.txt') -Encoding utf8
$archive = Join-Path $release 'MCCTheaterCapture.zip'
& (Join-Path $PSScriptRoot 'tools/Test-MinimalFFmpeg.ps1') -ToolDirectory $toolFolder | Out-Null
& (Join-Path $PSScriptRoot 'tools/Export-FFmpegSource.ps1') -OutputPath (Join-Path $release 'FFmpeg-source.zip')
Compress-Archive -LiteralPath $payload -DestinationPath $archive -CompressionLevel Optimal
$bytes = (Get-ChildItem -LiteralPath $payload -Recurse -File | Measure-Object Length -Sum).Sum
[pscustomobject]@{ Folder = $payload; UnpackedMB = [math]::Round($bytes / 1MB, 2); Archive = $archive; CompressedMB = [math]::Round((Get-Item -LiteralPath $archive).Length / 1MB, 2) }
