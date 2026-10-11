param(
    [string]$GamePath = '',
    [string]$BepInEx5CorePath = '',
    [string]$BepInEx6CorePath = ''
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

function Resolve-Paths([string]$loaderTarget, [string]$corePath) {
    $properties = @("-p:LoaderTarget=$loaderTarget")
    if ($GamePath) { $properties += '-p:GamePath=' + $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($GamePath).TrimEnd('\') }
    if ($corePath) { $properties += '-p:BepInExCorePath=' + $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($corePath).TrimEnd('\') }
    $resolved = (& dotnet msbuild (Join-Path $projectRoot 'InteractionRange.csproj') -getProperty:GamePath -getProperty:BepInExCorePath @properties -nologo) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Could not resolve $loaderTarget paths (exit code $LASTEXITCODE)." }
    return ($resolved | ConvertFrom-Json).Properties
}

# Resolve omitted paths the same way the builds do, then pass them explicitly to every check.
$bep5Paths = Resolve-Paths 'BepInEx5' $BepInEx5CorePath
$bep6Paths = Resolve-Paths 'BepInEx6' $BepInEx6CorePath
if (-not $bep5Paths.GamePath) { throw 'GamePath is not set. Pass -GamePath or create .local/LocalPaths.props.' }
$gamePath = [IO.Path]::GetFullPath($bep5Paths.GamePath).TrimEnd('\')
if ($gamePath -ne [IO.Path]::GetFullPath($bep6Paths.GamePath).TrimEnd('\')) { throw 'Loader targets resolved different GamePath values.' }
$bep5CorePath = [IO.Path]::GetFullPath($bep5Paths.BepInExCorePath).TrimEnd('\')
$bep6CorePath = [IO.Path]::GetFullPath($bep6Paths.BepInExCorePath).TrimEnd('\')

foreach ($target in @(@('BepInEx5',$bep5CorePath), @('BepInEx6',$bep6CorePath))) {
    & (Join-Path $PSScriptRoot 'Build.ps1') -GamePath $gamePath -BepInExCorePath $target[1] -LoaderTarget $target[0]
    & (Join-Path $PSScriptRoot 'Test.ps1') -GamePath $gamePath -BepInExCorePath $target[1] -LoaderTarget $target[0]
}
$bep5Dll = Join-Path $projectRoot '.local\bin\BepInEx5\InteractionRange\Release\InteractionRange.dll'
$bep6Dll = Join-Path $projectRoot '.local\bin\BepInEx6\InteractionRange\Release\InteractionRange.dll'
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($bep5Dll).Version
$version = '{0}.{1}.{2}' -f $assemblyVersion.Major,$assemblyVersion.Minor,$assemblyVersion.Build
if ([Reflection.AssemblyName]::GetAssemblyName($bep6Dll).Version -ne $assemblyVersion) { throw 'BepInEx 5 and BepInEx 6 builds have different assembly versions.' }
$checkRoot = Join-Path $projectRoot ('.local\package-checks\' + [Guid]::NewGuid().ToString('N'))
$dist = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $checkRoot,$dist -Force | Out-Null

# Explicit allowlists. Ignore rules alone are not a package policy.
$bep5Entries = @{
    'BepInEx/plugins/InteractionRange/InteractionRange.dll' = $bep5Dll
    'README.md' = Join-Path $projectRoot 'README.md'
    'CHANGELOG.md' = Join-Path $projectRoot 'CHANGELOG.md'
    'LICENSE' = Join-Path $projectRoot 'LICENSE'
}
$bep6Entries = @{
    'BepInEx/plugins/InteractionRange/InteractionRange.dll' = $bep6Dll
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

$bep5Zip = Join-Path $dist "InteractionRange-$version-BepInEx5.zip"
$bep6Zip = Join-Path $dist "InteractionRange-$version-BepInEx6.zip"
$sourceZip = Join-Path $dist "InteractionRange-$version-source.zip"
Write-Zip $bep5Zip $bep5Entries
Write-Zip $bep6Zip $bep6Entries
Write-Zip $sourceZip $sourceEntries

$expectedBep5Files = Get-SortedNames @($bep5Entries.Keys)
$expectedBep6Files = Get-SortedNames @($bep6Entries.Keys)
$expectedSourceFiles = Get-SortedNames $sourceFiles
foreach ($check in @(@($bep5Zip,$expectedBep5Files), @($bep6Zip,$expectedBep6Files), @($sourceZip,$expectedSourceFiles))) {
    $names = Get-ZipEntryNames $check[0]
    if (@($names | Where-Object { $_.Contains('\') }).Count -ne 0) { throw "$($check[0]) has backslash entry names." }
    if (($names -join "`n") -cne ($check[1] -join "`n")) { throw "$($check[0]) entries differ from the explicit allowlist or order." }
}

$bep5Extract = Join-Path $checkRoot 'bepinex5-extracted'
$bep6Extract = Join-Path $checkRoot 'bepinex6-extracted'
$sourceExtract = Join-Path $checkRoot 'source-extracted'
[IO.Compression.ZipFile]::ExtractToDirectory($bep5Zip, $bep5Extract)
[IO.Compression.ZipFile]::ExtractToDirectory($bep6Zip, $bep6Extract)
[IO.Compression.ZipFile]::ExtractToDirectory($sourceZip, $sourceExtract)
foreach ($check in @(@($bep5Extract,$expectedBep5Files), @($bep6Extract,$expectedBep6Files))) {
    $actualPluginFiles = Get-SortedNames @(Get-ChildItem -LiteralPath $check[0] -Recurse -File -Force | ForEach-Object { Get-RelativePath $check[0] $_.FullName })
    if (($actualPluginFiles -join "`n") -cne ($check[1] -join "`n")) { throw "$($check[0]) differs from its explicit file allowlist." }
    $packagedDlls = @(Get-ChildItem -LiteralPath $check[0] -Recurse -File -Filter '*.dll')
    if ($packagedDlls.Count -ne 1 -or $packagedDlls[0].Name -ne 'InteractionRange.dll') { throw "$($check[0]) must contain exactly the InteractionRange DLL." }
}
$actualSourceFiles = Get-SortedNames @(Get-ChildItem -LiteralPath $sourceExtract -Recurse -File -Force | ForEach-Object { Get-RelativePath $sourceExtract $_.FullName })
if (($actualSourceFiles -join "`n") -cne ($expectedSourceFiles -join "`n")) { throw 'Source ZIP differs from its explicit file allowlist.' }
foreach ($check in @(@($bep5Dll,$bep5Extract), @($bep6Dll,$bep6Extract))) {
    $extractedDll = Join-Path $check[1] 'BepInEx\plugins\InteractionRange\InteractionRange.dll'
    if ((Get-FileHash -LiteralPath $check[0]).Hash -ne (Get-FileHash -LiteralPath $extractedDll).Hash) {
        throw "$($check[1]) plugin differs from its verified build."
    }
}

# Personal-path scan: top-level folders of this checkout and its references, plus the account name.
$markers = @(@($projectRoot,$gamePath,$bep5CorePath,$bep6CorePath) | ForEach-Object { Get-TopFolder $_ }) + @($env:USERNAME) |
    Where-Object { $_ -and $_.Length -ge 3 } | Sort-Object -Unique
$markers = @($markers) + @($markers | ForEach-Object { $_.Replace('\','/') }) | Sort-Object -Unique
$latin1 = [Text.Encoding]::GetEncoding(28591)
foreach ($file in @(Get-ChildItem -LiteralPath $bep5Extract,$bep6Extract,$sourceExtract -Recurse -File -Force)) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    foreach ($text in @($latin1.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))) {
        foreach ($marker in $markers) {
            if ($text.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "Personal path marker found in packaged file $($file.FullName)." }
        }
    }
}

foreach ($target in @(@('BepInEx5',$bep5CorePath,$bep5Dll), @('BepInEx6',$bep6CorePath,$bep6Dll))) {
    & (Join-Path $sourceExtract 'scripts\Build.ps1') -GamePath $gamePath -BepInExCorePath $target[1] -LoaderTarget $target[0]
    & (Join-Path $sourceExtract 'scripts\Test.ps1') -GamePath $gamePath -BepInExCorePath $target[1] -LoaderTarget $target[0]
    $rebuiltDll = Join-Path $sourceExtract ".local\bin\$($target[0])\InteractionRange\Release\InteractionRange.dll"
    if ((Get-FileHash -LiteralPath $target[2]).Hash -ne (Get-FileHash -LiteralPath $rebuiltDll).Hash) {
        throw "Fresh source rebuild for $($target[0]) differs from the candidate DLL."
    }
}
$record = [ordered]@{
    checkedAt = [DateTime]::UtcNow.ToString('o')
    bepInEx5Zip = $bep5Zip
    bepInEx6Zip = $bep6Zip
    sourceZip = $sourceZip
    bepInEx5ZipSha256 = (Get-FileHash -LiteralPath $bep5Zip).Hash
    bepInEx6ZipSha256 = (Get-FileHash -LiteralPath $bep6Zip).Hash
    sourceZipSha256 = (Get-FileHash -LiteralPath $sourceZip).Hash
    bepInEx5DllSha256 = (Get-FileHash -LiteralPath $bep5Dll).Hash
    bepInEx6DllSha256 = (Get-FileHash -LiteralPath $bep6Dll).Hash
    bepInEx5Files = $expectedBep5Files
    bepInEx6Files = $expectedBep6Files
    sourceFiles = $expectedSourceFiles
    personalPathMarkersChecked = @($markers).Count
    personalPathScanClean = $true
    freshSourceRebuildIdentical = $true
    gameInstallationChanged = $false
    liveGameplayTest = $false
    bepInEx6RuntimeEvidence = 'Historical startup on BepInEx 6.0.0-be.755 only. No new live test by user decision.'
}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $checkRoot 'validation.json') -Encoding utf8
Write-Output "Personal path scan clean ($(@($markers).Count) markers)."
Write-Output "Candidate packages validated: $bep5Zip, $bep6Zip and $sourceZip"
Write-Output "BepInEx 5 DLL SHA256: $($record.bepInEx5DllSha256)"
Write-Output "BepInEx 6 DLL SHA256: $($record.bepInEx6DllSha256)"
Write-Output "Validation record: $(Join-Path $checkRoot 'validation.json')"
