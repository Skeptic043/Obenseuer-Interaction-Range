param(
    [string]$GamePath = '',
    [string]$BepInExCorePath = '',
    [ValidateSet('BepInEx5','BepInEx6')]
    [string]$LoaderTarget = 'BepInEx5',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$env:MSBuildEnableWorkloadResolver = 'false'
$project = Join-Path $PSScriptRoot '..\InteractionRange.csproj'
# Omitted paths come from MSBuild: .local/LocalPaths.props or the repository defaults.
$properties = @("-p:LoaderTarget=$LoaderTarget")
foreach ($name in 'GamePath','BepInExCorePath') {
    $value = Get-Variable -Name $name -ValueOnly
    if ($value) { $properties += "-p:$name=" + $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($value).TrimEnd('\') }
}
& dotnet build $project --configuration $Configuration @properties --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE. Do not use existing output." }
