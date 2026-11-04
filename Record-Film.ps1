param(
    [Parameter(Mandatory=$true)][string]$Film,
    [string]$Output='',
    [ValidateRange(0,43200)][double]$DurationSeconds=0,
    [string]$MccRoot='',
    [string]$DataDirectory="$PSScriptRoot/UserData",
    [switch]$ShowPlayerName,
    [switch]$Background,
    [string]$SettingsIni='',
    [ValidateRange(55,120)][int]$Fov=91
)
$ErrorActionPreference='Stop'
$launcher=Join-Path $PSScriptRoot 'MCCTheaterCapture.exe'
if(!(Test-Path -LiteralPath $launcher)){throw 'Run Record-Film.ps1 from the packaged MCCTheaterCapture folder.'}
$filmPath=[IO.Path]::GetFullPath($Film)
$outputPath=if($Output){[IO.Path]::GetFullPath($Output)}else{''}
$dataPath=[IO.Path]::GetFullPath($DataDirectory)
if(!(Test-Path -LiteralPath $filmPath -PathType Leaf)){throw 'The original theater file does not exist.'}
if(!$Background -and (!$outputPath -or $DurationSeconds -le 0)){throw 'Preview recording requires -Output and -DurationSeconds. Use -Background to export the full film silently to Downloads.'}
if($Background -and $ShowPlayerName){throw 'Background exports always hide the player-name bar. Remove -ShowPlayerName.'}
if($outputPath){
    if([IO.Path]::GetExtension($outputPath) -ine '.mp4'){throw 'Output must be a new .mp4 filename.'}
    if(Test-Path -LiteralPath $outputPath){throw 'Output already exists. Existing captures are never overwritten.'}
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
}
New-Item -ItemType Directory -Force -Path $dataPath | Out-Null
$report=Join-Path $dataPath ('capture-report-'+[guid]::NewGuid().ToString('N')+'.json')
$culture=[Globalization.CultureInfo]::InvariantCulture
$arguments=@('--film',$filmPath,
    '--hide-player-name',(!$ShowPlayerName).ToString().ToLowerInvariant(),'--exit-after-record','true',
    '--data-dir',$dataPath,'--report',$report)
if($Background){$arguments+=@('--background','true')}
if($outputPath){$arguments+=@('--record-output',$outputPath)}
if($DurationSeconds -gt 0){
    $arguments+=@('--record-seconds',$DurationSeconds.ToString($culture))
    if(!$Background){$arguments+=@('--auto-close-seconds',([Math]::Ceiling($DurationSeconds)+120).ToString($culture))}
}
if($MccRoot){$arguments+=@('--mcc-root',[IO.Path]::GetFullPath($MccRoot))}
if($SettingsIni){$arguments+=@('--settings-ini',[IO.Path]::GetFullPath($SettingsIni))}
$arguments+=@('--fov',$Fov.ToString($culture))
function Quote-ProcessArgument([string]$value){
    $escaped=[regex]::Replace($value,'(\\*)"','$1$1\"')
    $escaped=[regex]::Replace($escaped,'(\\+)$','$1$1')
    return '"'+$escaped+'"'
}
# These are process arguments, never a command evaluated by another shell.
$argumentLine=($arguments|ForEach-Object{Quote-ProcessArgument $_}) -join ' '
$captureProcess=Start-Process -FilePath $launcher -ArgumentList $argumentLine -WindowStyle Hidden -PassThru
$captureProcess.WaitForExit()
if($captureProcess.ExitCode -ne 0){throw "Capture failed with exit code $($captureProcess.ExitCode). Report: $report"}
if(!(Test-Path -LiteralPath $report)){throw 'The capture process ended without a final report.'}
$result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if(!$outputPath){$outputPath=$result.capture.file}
if($result.capture.state -ne 'stopped' -or $result.capture.audio -ne $true -or $result.capture.frames -le 0 -or $result.capture.droppedFrames -ne 0 -or !(Test-Path -LiteralPath $outputPath)){
    throw "Capture did not confirm a finalized MP4 with game audio. Report: $report"
}
if($Background -and ($result.noViewerWindow -ne $true -or $result.backgroundRecording.complete -ne $true)){
    throw "Background export did not confirm hidden, complete recording. Report: $report"
}
if($Background -and ($result.capture.repeatedFrames -ne 0 -or $result.capture.capturedFrames -ne $result.capture.frames)){
    throw "Background export did not render every output frame. Report: $report"
}
[pscustomobject]@{File=$outputPath;Report=$report;Audio=$result.capture.audio;DurationSeconds=$result.capture.durationSeconds}
