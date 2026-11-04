param([Parameter(Mandatory)][string]$ToolDirectory)
$ErrorActionPreference='Stop'
$folder=(Resolve-Path -LiteralPath $ToolDirectory).Path
function Inspect-Media([string]$Tool,[string]$Option) {
    $lines=& (Join-Path $folder ($Tool+'.exe')) -hide_banner $Option 2>&1
    if($LASTEXITCODE -ne 0){throw "$Tool $Option failed: $lines"}
    return ($lines | ForEach-Object ToString) -join "`n"
}
function Require-Names([string]$Text,[string[]]$Names,[string]$Kind) {
    foreach($name in $Names){
        if($Text -notmatch ('(?m)^\s*(?:[A-Z\.]{1,6}\s+)?'+[regex]::Escape($name)+'(?:[,\s]|$)')){throw "Required $Kind missing: $name"}
    }
}
$encoders=Inspect-Media ffmpeg '-encoders'
$configuration=Inspect-Media ffprobe '-buildconf'
if($configuration -match '--enable-small'){throw 'Small-mode profile labels break native capture validation; use size compiler flags with --disable-small.'}
Require-Names $encoders @('libx264','aac','pcm_f32le','pcm_s16le','wrapped_avframe') encoder
$decoders=Inspect-Media ffmpeg '-decoders'
Require-Names $decoders @('h264','aac','png') decoder
foreach($name in @('hevc','libx265','vp8','vp9','libvpx','mp3','flac','opus','av1')) {
    if(($encoders+"`n"+$decoders) -match ('(?m)^\s*[A-Z\.]{1,6}\s+'+[regex]::Escape($name)+'\s')){throw "Unneeded codec shipped: $name"}
}
Require-Names (Inspect-Media ffmpeg '-filters') @('scale','setsar','split','crop','gblur','drawbox','overlay','format','aresample','aformat','anull','null','trim','atrim','setpts','asetpts') filter
Require-Names (Inspect-Media ffmpeg '-muxers') @('mp4','mov','hash','null','f32le') muxer
Require-Names (Inspect-Media ffmpeg '-demuxers') @('mov','concat','ffmetadata','image2','image2pipe','png_pipe') demuxer
$protocols=Inspect-Media ffmpeg '-protocols'
Require-Names $protocols @('file','pipe') protocol
if($protocols -match '(?m)^\s*(?:https?|tcp|udp|rtmp|rtsp|tls)\s*$'){throw 'Network protocol present in app-specific build.'}
Require-Names (Inspect-Media ffprobe '-decoders') @('h264','aac','png') 'probe decoder'
[pscustomobject]@{Codecs='H.264 / AAC / PNG';Encoders='x264, AAC, verification PCM';Network='disabled';ToolsMiB=[math]::Round((Get-ChildItem -LiteralPath $folder -Filter '*.exe' | Measure-Object Length -Sum).Sum/1MB,2)}
