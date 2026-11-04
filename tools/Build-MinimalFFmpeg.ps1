param(
    [string]$ToolchainDirectory = "$PSScriptRoot/../artifacts/media-build/toolchain/w64devkit",
    [string]$NasmDirectory = "$PSScriptRoot/../artifacts/media-build/toolchain/nasm/nasm-2.16.03",
    [string]$BashPath = 'bash',
    [ValidateRange(1,64)][int]$Jobs = 4
)
$ErrorActionPreference='Stop'
$ToolchainDirectory=[IO.Path]::GetFullPath($ToolchainDirectory)
$NasmDirectory=[IO.Path]::GetFullPath($NasmDirectory)
$BashPath=(Get-Command $BashPath -CommandType Application -ErrorAction Stop).Source
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest=Get-Content -LiteralPath (Join-Path $root 'deps/FFmpeg/build-manifest.json') -Raw | ConvertFrom-Json
if((Get-FileHash -LiteralPath (Join-Path $root $manifest.buildScript) -Algorithm SHA256).Hash -ine $manifest.buildScriptSha256){throw 'Build recipe differs from the recorded media manifest.'}
$build=Join-Path $root 'artifacts/media-build'
New-Item -ItemType Directory -Force -Path (Join-Path $build 'sources'),(Join-Path $build 'tmp') | Out-Null
foreach($source in $manifest.sources){
    $archive=Join-Path $root ('deps/FFmpeg/source/'+$source.file)
    if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $source.sha256){throw "Source identity mismatch: $($source.file)"}
    & tar.exe -xf $archive -C (Join-Path $build 'sources')
    if($LASTEXITCODE -ne 0){throw "Could not extract $($source.file)"}
}
$shell=Join-Path $ToolchainDirectory 'bin/sh.exe'
if(!(Test-Path -LiteralPath $shell) -or !(Test-Path -LiteralPath (Join-Path $NasmDirectory 'nasm.exe'))){throw 'Provide the portable w64devkit and NASM directories recorded in build-manifest.json.'}
$savedPath=$env:PATH;$savedTmp=$env:TMPDIR;$savedJobs=$env:MEDIA_BUILD_JOBS;$savedBash=$env:MEDIA_BUILD_BASH
Push-Location $root
try {
    $env:PATH=(Join-Path $ToolchainDirectory 'bin')+';'+$NasmDirectory+';'+$env:PATH
    $env:TMPDIR=(Join-Path $build 'tmp').Replace('\','/');$env:MEDIA_BUILD_JOBS=$Jobs.ToString()
    $env:MEDIA_BUILD_BASH=$BashPath.Replace('\','/')
    & $shell tools/Build-MinimalFFmpeg.sh
    if($LASTEXITCODE -ne 0){throw 'Minimal media build failed.'}
    & (Join-Path $PSScriptRoot 'Test-MinimalFFmpeg.ps1') -ToolDirectory (Join-Path $build 'prefix/bin')
    Write-Output "Validated binaries: $(Join-Path $build 'prefix/bin'). Run the export regression suites before replacing vendored binaries."
} finally {Pop-Location;$env:PATH=$savedPath;$env:TMPDIR=$savedTmp;$env:MEDIA_BUILD_JOBS=$savedJobs;$env:MEDIA_BUILD_BASH=$savedBash}
