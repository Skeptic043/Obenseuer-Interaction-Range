param(
    [string]$GamePath = '',
    [string]$BepInExCorePath = ''
)
$ErrorActionPreference = 'Stop'
$env:MSBuildEnableWorkloadResolver = 'false'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Get-RelativePath([string]$root, [string]$path) {
    $prefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "$path is outside $root." }
    return $full.Substring($prefix.Length).Replace('\','/')
}

function Get-SortedNames([string[]]$names) {
    $sorted = [string[]]@($names | ForEach-Object { $_.Replace('\','/') })
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return ,$sorted
}

# Entries use forward slashes and a fixed ordinal order on Windows PowerShell 5.1 and PowerShell 7.
function Write-Zip([string]$path, [hashtable]$entries) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in (Get-SortedNames @($entries.Keys))) {
            $source = $entries[$name]
            $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset](Get-Item -LiteralPath $source).LastWriteTime
            $output = $entry.Open()
            try {
                $reader = [IO.File]::OpenRead($source)
                try { $reader.CopyTo($output) } finally { $reader.Dispose() }
            }
            finally { $output.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

function Get-ZipEntryNames([string]$path) {
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try { return ,[string[]]@($archive.Entries | ForEach-Object { $_.FullName }) }
    finally { $archive.Dispose() }
}

function Get-TopFolder([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    $root = [IO.Path]::GetPathRoot($full)
    $first = $full.Substring($root.Length).Split('\')[0]
    if ($first) { return (Join-Path $root $first) }
}

# Resolve omitted paths the same way the build does, then pass them explicitly to every check.
$properties = @()
foreach ($name in 'GamePath','BepInExCorePath') {
    $value = Get-Variable -Name $name -ValueOnly
    if ($value) { $properties += "-p:$name=" + $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($value).TrimEnd('\') }
}
$resolved = (& dotnet msbuild (Join-Path $projectRoot 'InteractionRange.csproj') -getProperty:GamePath -getProperty:BepInExCorePath @properties -nologo) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Could not resolve GamePath and BepInExCorePath (exit code $LASTEXITCODE)." }
$paths = ($resolved | ConvertFrom-Json).Properties
if (-not $paths.GamePath) { throw 'GamePath is not set. Pass -GamePath or create .local/LocalPaths.props.' }
$gamePath = [IO.Path]::GetFullPath($paths.GamePath).TrimEnd('\')
$corePath = [IO.Path]::GetFullPath($paths.BepInExCorePath).TrimEnd('\')

& (Join-Path $PSScriptRoot 'Build.ps1') -GamePath $gamePath -BepInExCorePath $corePath
& (Join-Path $PSScriptRoot 'Test.ps1') -GamePath $gamePath -BepInExCorePath $corePath
$dll = Join-Path $projectRoot '.local\bin\InteractionRange\Release\InteractionRange.dll'
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($dll).Version
$version = '{0}.{1}.{2}' -f $assemblyVersion.Major,$assemblyVersion.Minor,$assemblyVersion.Build
$checkRoot = Join-Path $projectRoot ('.local\package-checks\' + [Guid]::NewGuid().ToString('N'))
$dist = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $checkRoot,$dist -Force | Out-Null

# Explicit allowlists. Ignore rules alone are not a package policy.
$pluginEntries = @{
    'BepInEx/plugins/InteractionRange/InteractionRange.dll' = $dll
    'README.md' = Join-Path $projectRoot 'README.md'
    'CHANGELOG.md' = Join-Path $projectRoot 'CHANGELOG.md'
    'LICENSE' = Join-Path $projectRoot 'LICENSE'
}
$sourceFiles = @('.gitignore','.gitattributes','README.md','BUILDING.md','CHANGELOG.md','LICENSE',
    'Directory.Build.props','InteractionRange.csproj','InteractionRange.Tests.csproj','nuget.config',
    'scripts/Build.ps1','scripts/Test.ps1','scripts/Package.ps1')
$sourceFiles += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src'),(Join-Path $projectRoot 'tests') -Recurse -File -Filter '*.cs' |
    ForEach-Object { Get-RelativePath $projectRoot $_.FullName })
$sourceEntries = @{}
foreach ($relative in $sourceFiles) { $sourceEntries[$relative] = Join-Path $projectRoot $relative }
if (@($sourceFiles | Where-Object { $_ -like '.local/*' }).Count -ne 0) { throw 'Source allowlist includes .local content.' }

$pluginZip = Join-Path $dist "InteractionRange-$version.zip"
$sourceZip = Join-Path $dist "InteractionRange-$version-source.zip"
Write-Zip $pluginZip $pluginEntries
Write-Zip $sourceZip $sourceEntries

$expectedPluginFiles = Get-SortedNames @($pluginEntries.Keys)
$expectedSourceFiles = Get-SortedNames $sourceFiles
foreach ($check in @(@($pluginZip,$expectedPluginFiles), @($sourceZip,$expectedSourceFiles))) {
    $names = Get-ZipEntryNames $check[0]
    if (@($names | Where-Object { $_.Contains('\') }).Count -ne 0) { throw "$($check[0]) has backslash entry names." }
    if (($names -join "`n") -cne ($check[1] -join "`n")) { throw "$($check[0]) entries differ from the explicit allowlist or order." }
}

$pluginExtract = Join-Path $checkRoot 'plugin-extracted'
$sourceExtract = Join-Path $checkRoot 'source-extracted'
[IO.Compression.ZipFile]::ExtractToDirectory($pluginZip, $pluginExtract)
[IO.Compression.ZipFile]::ExtractToDirectory($sourceZip, $sourceExtract)
$actualPluginFiles = Get-SortedNames @(Get-ChildItem -LiteralPath $pluginExtract -Recurse -File -Force | ForEach-Object { Get-RelativePath $pluginExtract $_.FullName })
if (($actualPluginFiles -join "`n") -cne ($expectedPluginFiles -join "`n")) { throw 'Plugin ZIP differs from its explicit file allowlist.' }
$actualSourceFiles = Get-SortedNames @(Get-ChildItem -LiteralPath $sourceExtract -Recurse -File -Force | ForEach-Object { Get-RelativePath $sourceExtract $_.FullName })
if (($actualSourceFiles -join "`n") -cne ($expectedSourceFiles -join "`n")) { throw 'Source ZIP differs from its explicit file allowlist.' }
$extractedDll = Join-Path $pluginExtract 'BepInEx\plugins\InteractionRange\InteractionRange.dll'
if ((Get-FileHash -LiteralPath $dll).Hash -ne (Get-FileHash -LiteralPath $extractedDll).Hash) {
    throw 'Extracted plugin differs from verified build.'
}

# Personal-path scan: top-level folders of this checkout and its references, plus the account name.
$markers = @(@($projectRoot,$gamePath,$corePath) | ForEach-Object { Get-TopFolder $_ }) + @($env:USERNAME) |
    Where-Object { $_ -and $_.Length -ge 3 } | Sort-Object -Unique
$markers = @($markers) + @($markers | ForEach-Object { $_.Replace('\','/') }) | Sort-Object -Unique
$latin1 = [Text.Encoding]::GetEncoding(28591)
foreach ($file in @(Get-ChildItem -LiteralPath $pluginExtract,$sourceExtract -Recurse -File -Force)) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    foreach ($text in @($latin1.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))) {
        foreach ($marker in $markers) {
            if ($text.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "Personal path marker found in packaged file $($file.FullName)." }
        }
    }
}

& (Join-Path $sourceExtract 'scripts\Build.ps1') -GamePath $gamePath -BepInExCorePath $corePath
& (Join-Path $sourceExtract 'scripts\Test.ps1') -GamePath $gamePath -BepInExCorePath $corePath
$rebuiltDll = Join-Path $sourceExtract '.local\bin\InteractionRange\Release\InteractionRange.dll'
if ((Get-FileHash -LiteralPath $dll).Hash -ne (Get-FileHash -LiteralPath $rebuiltDll).Hash) {
    throw 'Fresh source rebuild differs from the candidate DLL.'
}
$record = [ordered]@{
    checkedAt = [DateTime]::UtcNow.ToString('o')
    pluginZip = $pluginZip
    sourceZip = $sourceZip
    pluginZipSha256 = (Get-FileHash -LiteralPath $pluginZip).Hash
    sourceZipSha256 = (Get-FileHash -LiteralPath $sourceZip).Hash
    dllSha256 = (Get-FileHash -LiteralPath $dll).Hash
    pluginFiles = $expectedPluginFiles
    sourceFiles = $expectedSourceFiles
    personalPathMarkersChecked = @($markers).Count
    personalPathScanClean = $true
    freshSourceRebuildIdentical = $true
    gameInstallationChanged = $false
    liveGameplayTest = $false
}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $checkRoot 'validation.json') -Encoding utf8
Write-Output "Personal path scan clean ($(@($markers).Count) markers)."
Write-Output "Candidate packages validated: $pluginZip and $sourceZip"
Write-Output "DLL SHA256: $($record.dllSha256)"
Write-Output "Validation record: $(Join-Path $checkRoot 'validation.json')"
