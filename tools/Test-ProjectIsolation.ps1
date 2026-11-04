[CmdletBinding()]
param(
    [string]$ProjectRoot = (Join-Path $PSScriptRoot '..'),
    [string]$ReportPath = ''
)
$ErrorActionPreference = 'Stop'
$projectPath = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([char[]]'\/')
if (!(Test-Path -LiteralPath $projectPath -PathType Container)) { throw 'ProjectRoot must be an existing source project directory.' }
$projectPrefix = $projectPath + [IO.Path]::DirectorySeparatorChar
$failures = New-Object 'System.Collections.Generic.List[string]'
$checks = New-Object 'System.Collections.Generic.List[string]'
function Relative-Path([string]$Path) { return $Path.Substring($projectPrefix.Length).Replace('\','/') }
function Test-Contained([string]$Path) {
    return $Path.Equals($projectPath,[StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith($projectPrefix,[StringComparison]::OrdinalIgnoreCase)
}
function Check-ProjectPath([string]$Value,[string]$Base,[string]$Origin) {
    if (!$Value) { return }
    foreach ($item in $Value.Split(';')) {
        if (!$item) { continue }
        $expanded = $item.Replace('$(MSBuildProjectDirectory)',$Base).Replace('$(MSBuildThisFileDirectory)',($Base + [IO.Path]::DirectorySeparatorChar))
        if ($expanded -match '\$\(') { $failures.Add("Unresolved build property in $Origin : $item"); continue }
        # .NET Framework rejects wildcard characters in GetFullPath. Normalize
        # harmless placeholders first, then restore the MSBuild glob.
        $canonicalInput = $expanded.Replace('*','__ISOLATION_STAR__').Replace('?','__ISOLATION_QUESTION__').Replace('[','__ISOLATION_BRACKET__')
        try { $resolved = [IO.Path]::GetFullPath((Join-Path $Base $canonicalInput)).Replace('__ISOLATION_STAR__','*').Replace('__ISOLATION_QUESTION__','?').Replace('__ISOLATION_BRACKET__','[') }
        catch { $failures.Add("Invalid path in $Origin : $item"); continue }
        if (!(Test-Contained $resolved)) { $failures.Add("External dependency in $Origin : $item"); continue }
        if ($resolved.IndexOfAny([char[]]'*?[') -ge 0) {
            $wildcardIndex = $resolved.IndexOfAny([char[]]'*?[')
            $staticPart = $resolved.Substring(0,$wildcardIndex)
            $staticRoot = if ($staticPart.EndsWith('\') -or $staticPart.EndsWith('/')) { $staticPart.TrimEnd([char[]]'\/') } else { [IO.Path]::GetDirectoryName($staticPart) }
            if (!(Test-Path -LiteralPath $staticRoot)) { $failures.Add("Missing wildcard root in $Origin : $item"); continue }
            $pattern = $resolved.Replace('\','/').Replace('**','*')
            if (!@($sourceFiles | Where-Object { $_.FullName.Replace('\','/') -like $pattern }).Count) { $failures.Add("Empty source/content wildcard in $Origin : $item") }
        }
        elseif (!(Test-Path -LiteralPath $resolved -PathType Leaf)) { $failures.Add("Missing dependency in $Origin : $item") }
    }
}
# Walk without following junctions or symbolic links; generated output is not source.
$sourceFiles = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
$pending = New-Object 'System.Collections.Generic.Stack[string]'
$pending.Push($projectPath)
while ($pending.Count) {
    $directory = $pending.Pop()
    foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
        # OneDrive uses reparse points for ordinary cloud-backed files and folders.
        # Only actual links redirect the source walk outside this project.
        if ($entry.LinkType -in @('SymbolicLink','Junction')) { $failures.Add("Source link is not portable: $($entry.FullName)"); continue }
        if ($entry.PSIsContainer) {
            $vendored = (Relative-Path $entry.FullName).StartsWith('deps/',[StringComparison]::OrdinalIgnoreCase)
            if ($entry.Name -notin @('.git','bin','obj','dist','UserData','.vs','artifacts') -or $vendored) { $pending.Push($entry.FullName) }
        } else { $sourceFiles.Add($entry) }
    }
}
$checks.Add('Source walk contains no links to another project')
$projects = @($sourceFiles | Where-Object Extension -eq '.csproj')
if (!$projects.Count) { $failures.Add('No C# project was found.') }
$pathItems = @('Compile','Content','None','EmbeddedResource','AdditionalFiles','Resource','Page','ApplicationDefinition','ProjectReference','NativeReference','Analyzer')
foreach ($project in $sourceFiles | Where-Object { $_.Extension -in @('.csproj','.props','.targets') }) {
    try { [xml]$xml = Get-Content -LiteralPath $project.FullName -Raw }
    catch { $failures.Add("Invalid MSBuild XML: $(Relative-Path $project.FullName)"); continue }
    $base = $project.DirectoryName
    foreach ($node in $xml.SelectNodes('//*')) {
        if ($node.LocalName -in $pathItems -and $node.HasAttribute('Include')) { Check-ProjectPath $node.GetAttribute('Include') $base (Relative-Path $project.FullName) }
        if ($node.LocalName -eq 'Import' -and $node.HasAttribute('Project')) { Check-ProjectPath $node.GetAttribute('Project') $base (Relative-Path $project.FullName) }
        if ($node.LocalName -in @('HintPath','ApplicationManifest') -and $node.InnerText) { Check-ProjectPath $node.InnerText $base (Relative-Path $project.FullName) }
    }
}
$checks.Add('All explicit MSBuild sources, assets, references and manifests resolve inside project')
foreach ($source in $sourceFiles | Where-Object { $_.Extension -in @('.cpp','.h') -and (Relative-Path $_.FullName).StartsWith('native/') }) {
    foreach ($match in [regex]::Matches([IO.File]::ReadAllText($source.FullName),'(?m)^\s*#\s*include\s*"([^"]+)"')) { Check-ProjectPath $match.Groups[1].Value $source.DirectoryName (Relative-Path $source.FullName) }
}
$checks.Add('All quoted native includes are project-owned')
$identityFile = Join-Path $projectPath 'deps/vendor-identities.json'
if (!(Test-Path -LiteralPath $identityFile)) { $failures.Add('Missing deps/vendor-identities.json') }
else {
    $identity = Get-Content -LiteralPath $identityFile -Raw | ConvertFrom-Json
    foreach ($file in $identity.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $projectPath $file.path))
        if (!(Test-Contained $path) -or !(Test-Path -LiteralPath $path -PathType Leaf)) { $failures.Add("Missing/invalid vendored binary: $($file.path)"); continue }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256) { $failures.Add("Vendored binary identity changed: $($file.path)") }
    }
    foreach ($notice in $identity.requiredNotices) {
        $path = [IO.Path]::GetFullPath((Join-Path $projectPath $notice))
        if (!(Test-Contained $path) -or !(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { $failures.Add("Missing vendor notice: $notice") }
    }
}
$checks.Add('Vendored FreeType and WebView2 binaries match recorded SHA-256 identities and notices exist')
# Historical migration notes and third-party copyright text may name the old project.
$forbidden = '(?i)([A-Z]:[\\/]Users[\\/]|\.local-smoke[\\/]native-viewer-poc|desktop[\\/]native-viewer-poc|MCCTheaterViewer\.Native(?:POC|Capture)|["'']MCCTheaterViewer["'']\s*,\s*["'']Beta["''])'
foreach ($source in $sourceFiles | Where-Object {
    $relative = Relative-Path $_.FullName
    $_.Extension -in @('.cs','.cpp','.h','.ps1','.csproj','.js','.html','.css') -and
    ($relative.StartsWith('shell/') -or $relative.StartsWith('native/') -or $relative -in @('build.ps1','Record-Film.ps1'))
}) {
    $line = 0
    foreach ($text in [IO.File]::ReadAllLines($source.FullName)) { $line++; if ($text -match $forbidden) { $failures.Add("Old workspace/product path in $(Relative-Path $source.FullName):$line") } }
}
$checks.Add('Runtime sources/build/helper contain no prior viewer paths, settings import or machine-specific FreeType location')
foreach ($script in $sourceFiles | Where-Object Extension -eq '.ps1') {
    $tokens = $null; $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($script.FullName,[ref]$tokens,[ref]$parseErrors)
    foreach ($error in $parseErrors) { $failures.Add("PowerShell syntax: $(Relative-Path $script.FullName):$($error.Extent.StartLineNumber): $($error.Message)") }
}
$checks.Add('All source PowerShell scripts parse without executing them')
$result = [pscustomobject]@{schema='mcc-capture-isolation/v1';project=$projectPath;passed=($failures.Count -eq 0);sourceFiles=$sourceFiles.Count;checks=@($checks);failures=@($failures)}
if ($ReportPath) {
    $report = [IO.Path]::GetFullPath($ReportPath)
    $parent = [IO.Path]::GetDirectoryName($report)
    if (!(Test-Path -LiteralPath $parent -PathType Container)) { throw 'ReportPath parent directory must already exist.' }
    [IO.File]::WriteAllText($report,($result | ConvertTo-Json -Depth 5))
}
$result
if (!$result.passed) { throw "Project isolation check failed ($($failures.Count) findings)." }
