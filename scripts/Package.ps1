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

function Get-AssemblyFacts([string]$path) {
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
    try {
        $pluginAttributes = @($assembly.MainModule.Types | ForEach-Object { $_.CustomAttributes } |
            Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' })
        if ($pluginAttributes.Count -ne 1 -or $pluginAttributes[0].ConstructorArguments.Count -ne 3) {
            throw "$path must contain exactly one three-argument BepInPlugin attribute."
        }
        $plugin = $pluginAttributes[0]
        return [pscustomobject]@{
            assemblyName = $assembly.Name.Name
            assemblyVersion = $assembly.Name.Version.ToString()
            pluginGuid = [string]$plugin.ConstructorArguments[0].Value
            pluginName = [string]$plugin.ConstructorArguments[1].Value
            pluginVersion = [string]$plugin.ConstructorArguments[2].Value
            loaderReferences = @($assembly.MainModule.AssemblyReferences |
                Where-Object { $_.Name.StartsWith('BepInEx', [StringComparison]::Ordinal) } |
                Sort-Object Name | ForEach-Object { $_.Name + ',' + $_.Version })
        }
    }
    finally { $assembly.Dispose() }
}

function Assert-AssemblyFacts([string]$path, [string]$loaderTarget, [string]$version) {
    $facts = Get-AssemblyFacts $path
    if ($facts.assemblyName -cne 'InteractionRange') { throw "$path has assembly name $($facts.assemblyName)." }
    if ($facts.assemblyVersion -cne "$version.0") { throw "$path has assembly version $($facts.assemblyVersion), expected $version.0." }
    if ($facts.pluginGuid -cne 'com.skeptic043.obenseuer.interactionrange') { throw "$path has plugin GUID $($facts.pluginGuid)." }
    if ($facts.pluginName -cne 'Interaction Range') { throw "$path has plugin name $($facts.pluginName)." }
    if ($facts.pluginVersion -cne $version) { throw "$path has plugin version $($facts.pluginVersion), expected $version." }
    $expectedReferences = if ($loaderTarget -eq 'BepInEx5') {
        @('BepInEx,5.4.23.5')
    }
    else {
        @('BepInEx.Core,6.0.0.0','BepInEx.Unity.Mono,6.0.0.0')
    }
    if (($facts.loaderReferences -join "`n") -cne ($expectedReferences -join "`n")) {
        throw "$path has unexpected loader references: $($facts.loaderReferences -join ', ')."
    }
    return $facts
}

# Resolve omitted paths the same way the builds do, then pass them explicitly to every check.
$bep5Paths = Resolve-Paths 'BepInEx5' $BepInEx5CorePath
$bep6Paths = Resolve-Paths 'BepInEx6' $BepInEx6CorePath
if (-not $bep5Paths.GamePath) { throw 'GamePath is not set. Pass -GamePath or create .local/LocalPaths.props.' }
$gamePath = [IO.Path]::GetFullPath($bep5Paths.GamePath).TrimEnd('\')
if ($gamePath -ne [IO.Path]::GetFullPath($bep6Paths.GamePath).TrimEnd('\')) { throw 'Loader targets resolved different GamePath values.' }
$bep5CorePath = [IO.Path]::GetFullPath($bep5Paths.BepInExCorePath).TrimEnd('\')
$bep6CorePath = [IO.Path]::GetFullPath($bep6Paths.BepInExCorePath).TrimEnd('\')
Add-Type -Path (Join-Path $bep5CorePath 'Mono.Cecil.dll')

foreach ($target in @(@('BepInEx5',$bep5CorePath), @('BepInEx6',$bep6CorePath))) {
    & (Join-Path $PSScriptRoot 'Build.ps1') -GamePath $gamePath -BepInExCorePath $target[1] -LoaderTarget $target[0]
    & (Join-Path $PSScriptRoot 'Test.ps1') -GamePath $gamePath -BepInExCorePath $target[1] -LoaderTarget $target[0]
}
$bep5Dll = Join-Path $projectRoot '.local\bin\BepInEx5\InteractionRange\Release\InteractionRange.dll'
$bep6Dll = Join-Path $projectRoot '.local\bin\BepInEx6\InteractionRange\Release\InteractionRange.dll'
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($bep5Dll).Version
$version = '{0}.{1}.{2}' -f $assemblyVersion.Major,$assemblyVersion.Minor,$assemblyVersion.Build
if ([Reflection.AssemblyName]::GetAssemblyName($bep6Dll).Version -ne $assemblyVersion) { throw 'BepInEx 5 and BepInEx 6 builds have different assembly versions.' }
$bep5Facts = Assert-AssemblyFacts $bep5Dll 'BepInEx5' $version
$bep6Facts = Assert-AssemblyFacts $bep6Dll 'BepInEx6' $version
$checkRoot = Join-Path $projectRoot ('.local\package-checks\' + [Guid]::NewGuid().ToString('N'))
$dist = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $checkRoot,$dist -Force | Out-Null

# Explicit allowlists. Ignore rules alone are not a package policy.
$pluginRoot = 'BepInEx/plugins/InteractionRange'
$packageEntries = @{
    "$pluginRoot/InteractionRange.dll" = $bep6Dll
    "$pluginRoot/InteractionRange.BepInEx5.dll" = $bep5Dll
    "$pluginRoot/README.md" = Join-Path $projectRoot 'README.md'
    "$pluginRoot/CHANGELOG.md" = Join-Path $projectRoot 'CHANGELOG.md'
    "$pluginRoot/LICENSE" = Join-Path $projectRoot 'LICENSE'
}
$sourceFiles = @('.gitignore','.gitattributes','README.md','BUILDING.md','CHANGELOG.md','LICENSE',
    'Directory.Build.props','InteractionRange.csproj','InteractionRange.Tests.csproj','nuget.config',
    'scripts/Build.ps1','scripts/Test.ps1','scripts/Package.ps1')
$sourceFiles += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src'),(Join-Path $projectRoot 'tests') -Recurse -File -Filter '*.cs' |
    ForEach-Object { Get-RelativePath $projectRoot $_.FullName })
if (@($sourceFiles | Where-Object { $_ -like '.local/*' }).Count -ne 0) { throw 'Source allowlist includes .local content.' }

$expectedPackageFiles = Get-SortedNames @($packageEntries.Keys)
if ($expectedPackageFiles.Count -ne 5) { throw 'Universal package allowlist must contain exactly five files.' }
$expectedSourceFiles = Get-SortedNames $sourceFiles
$candidateZip = Join-Path $checkRoot "InteractionRange-$version.zip"
$finalZip = Join-Path $dist "InteractionRange-$version.zip"
$otherCurrentPackages = @(Get-ChildItem -LiteralPath $dist -File -Filter "InteractionRange-$version*.zip" |
    Where-Object { $_.FullName -ne $finalZip })
if ($otherCurrentPackages.Count -ne 0) {
    throw "dist contains other $version packages. Preserve or relocate them before creating the single universal candidate: $($otherCurrentPackages.Name -join ', ')"
}
Write-Zip $candidateZip $packageEntries
$names = Get-ZipEntryNames $candidateZip
if (@($names | Where-Object { $_.Contains('\') }).Count -ne 0) { throw "$candidateZip has backslash entry names." }
if (($names -join "`n") -cne ($expectedPackageFiles -join "`n")) { throw "$candidateZip entries differ from the explicit allowlist or order." }

$packageExtract = Join-Path $checkRoot 'package-extracted'
$sourceStage = Join-Path $checkRoot 'source-stage'
[IO.Compression.ZipFile]::ExtractToDirectory($candidateZip, $packageExtract)
New-Item -ItemType Directory -Path $sourceStage -Force | Out-Null
foreach ($relative in $sourceFiles) {
    $destination = Join-Path $sourceStage $relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot $relative) -Destination $destination
}

$actualPackageFiles = Get-SortedNames @(Get-ChildItem -LiteralPath $packageExtract -Recurse -File -Force |
    ForEach-Object { Get-RelativePath $packageExtract $_.FullName })
if (($actualPackageFiles -join "`n") -cne ($expectedPackageFiles -join "`n")) { throw 'Extracted universal package differs from its explicit five-file allowlist.' }
$actualSourceFiles = Get-SortedNames @(Get-ChildItem -LiteralPath $sourceStage -Recurse -File -Force |
    ForEach-Object { Get-RelativePath $sourceStage $_.FullName })
if (($actualSourceFiles -join "`n") -cne ($expectedSourceFiles -join "`n")) { throw 'Internal source stage differs from its explicit file allowlist.' }
$packagedDlls = @(Get-ChildItem -LiteralPath $packageExtract -Recurse -File -Filter '*.dll')
if ($packagedDlls.Count -ne 2 -or
    @($packagedDlls | Where-Object Name -eq 'InteractionRange.dll').Count -ne 1 -or
    @($packagedDlls | Where-Object Name -eq 'InteractionRange.BepInEx5.dll').Count -ne 1) {
    throw 'Universal package must contain exactly the named BepInEx 6 and BepInEx 5 DLLs.'
}
$packagedBep6Dll = Join-Path $packageExtract "$pluginRoot/InteractionRange.dll"
$packagedBep5Dll = Join-Path $packageExtract "$pluginRoot/InteractionRange.BepInEx5.dll"
foreach ($check in @(@($bep5Dll,$packagedBep5Dll), @($bep6Dll,$packagedBep6Dll))) {
    if ((Get-FileHash -LiteralPath $check[0]).Hash -ne (Get-FileHash -LiteralPath $check[1]).Hash) {
        throw "$($check[1]) differs from its verified build."
    }
}
$null = Assert-AssemblyFacts $packagedBep5Dll 'BepInEx5' $version
$null = Assert-AssemblyFacts $packagedBep6Dll 'BepInEx6' $version

# Personal-path scan: top-level folders of this checkout and its references, plus the account name.
$markers = @(@($projectRoot,$gamePath,$bep5CorePath,$bep6CorePath) | ForEach-Object { Get-TopFolder $_ }) + @($env:USERNAME) |
    Where-Object { $_ -and $_.Length -ge 3 } | Sort-Object -Unique
$markers = @($markers) + @($markers | ForEach-Object { $_.Replace('\','/') }) | Sort-Object -Unique
$latin1 = [Text.Encoding]::GetEncoding(28591)
foreach ($file in @(Get-ChildItem -LiteralPath $packageExtract -Recurse -File -Force)) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    foreach ($text in @($latin1.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))) {
        foreach ($marker in $markers) {
            if ($text.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "Personal path marker found in packaged file $($file.FullName)." }
        }
    }
}

$oldPackagePath = $env:INTERACTION_RANGE_PACKAGE_PLUGIN_PATH
try {
    $env:INTERACTION_RANGE_PACKAGE_PLUGIN_PATH = Join-Path $packageExtract $pluginRoot
    foreach ($target in @(@('BepInEx5',$bep5CorePath,$bep5Dll), @('BepInEx6',$bep6CorePath,$bep6Dll))) {
        & (Join-Path $sourceStage 'scripts\Build.ps1') -GamePath $gamePath -BepInExCorePath $target[1] -LoaderTarget $target[0]
        & (Join-Path $sourceStage 'scripts\Test.ps1') -GamePath $gamePath -BepInExCorePath $target[1] -LoaderTarget $target[0]
        $rebuiltDll = Join-Path $sourceStage ".local\bin\$($target[0])\InteractionRange\Release\InteractionRange.dll"
        if ((Get-FileHash -LiteralPath $target[2]).Hash -ne (Get-FileHash -LiteralPath $rebuiltDll).Hash) {
            throw "Fresh source rebuild for $($target[0]) differs from the candidate DLL."
        }
    }
}
finally {
    if ($null -eq $oldPackagePath) { Remove-Item Env:INTERACTION_RANGE_PACKAGE_PLUGIN_PATH -ErrorAction SilentlyContinue }
    else { $env:INTERACTION_RANGE_PACKAGE_PLUGIN_PATH = $oldPackagePath }
}

Copy-Item -LiteralPath $candidateZip -Destination $finalZip -Force
$record = [ordered]@{
    checkedAt = [DateTime]::UtcNow.ToString('o')
    universalZip = $finalZip
    universalZipSha256 = (Get-FileHash -LiteralPath $finalZip).Hash
    bepInEx5DllSha256 = (Get-FileHash -LiteralPath $bep5Dll).Hash
    bepInEx6DllSha256 = (Get-FileHash -LiteralPath $bep6Dll).Hash
    bepInEx5Assembly = $bep5Facts
    bepInEx6Assembly = $bep6Facts
    packageFiles = $expectedPackageFiles
    sourceStageFiles = $expectedSourceFiles
    loaderDiscoveryPluginsPerTarget = 1
    warmCacheUpgradePassed = $true
    personalPathMarkersChecked = @($markers).Count
    personalPathScanClean = $true
    freshSourceRebuildIdentical = $true
    gameInstallationChanged = $false
    liveGameplayTest = $false
    runtimeEvidence = 'No new live test. Package discovery and cache upgrade checks ran outside Unity with the pinned loader assemblies.'
}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $checkRoot 'validation.json') -Encoding utf8
Write-Output "Personal path scan clean ($(@($markers).Count) markers)."
Write-Output "Universal candidate validated: $finalZip"
Write-Output "Universal ZIP SHA256: $($record.universalZipSha256)"
Write-Output "BepInEx 5 DLL SHA256: $($record.bepInEx5DllSha256)"
Write-Output "BepInEx 6 DLL SHA256: $($record.bepInEx6DllSha256)"
Write-Output "Validation record: $(Join-Path $checkRoot 'validation.json')"
