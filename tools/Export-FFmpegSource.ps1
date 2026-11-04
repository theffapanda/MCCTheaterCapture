param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vendor=Join-Path $root 'deps/FFmpeg'
$manifest=Get-Content -LiteralPath (Join-Path $vendor 'build-manifest.json') -Raw | ConvertFrom-Json
if((Get-FileHash -LiteralPath (Join-Path $root $manifest.buildScript) -Algorithm SHA256).Hash -ine $manifest.buildScriptSha256){throw 'Source bundle build recipe differs from the recorded media manifest.'}
foreach($source in $manifest.sources){
    if((Get-FileHash -LiteralPath (Join-Path $vendor ('source/'+$source.file)) -Algorithm SHA256).Hash -ine $source.sha256){throw "Corresponding source changed: $($source.file)"}
}
$files=@('source','build-manifest.json','manifest.json','LICENSE','README.txt','SOURCE-BUILD.txt','zlib-LICENSE.txt','x264-LICENSE.txt','ffmpeg-config.h','ffmpeg-config_components.h','ffmpeg-config.asm','x264-config.h') | ForEach-Object {Join-Path $vendor $_}
$files+=@('Build-MinimalFFmpeg.ps1','Build-MinimalFFmpeg.sh','Test-MinimalFFmpeg.ps1') | ForEach-Object {Join-Path $PSScriptRoot $_}
Compress-Archive -LiteralPath $files -DestinationPath $OutputPath -CompressionLevel Optimal
