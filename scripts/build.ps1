param(
    [string]$DotNet = "dotnet",
    [string]$Configuration = "Release",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionInfo = Get-Content -LiteralPath (Join-Path $projectRoot "version.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$releaseVersion = [string]$versionInfo.version
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "version.json contains an invalid semantic version"
}
$project = Join-Path $projectRoot "Jellyfin.Plugin.AniWorld\Jellyfin.Plugin.AniWorld.csproj"
$output = Join-Path $projectRoot "Jellyfin.Plugin.AniWorld\bin\$Configuration\net10.0"
$dist = Join-Path $projectRoot "dist"

function Get-ContainedPath([string]$Parent, [string]$Child) {
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $childFull = [IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a staging path outside the distribution directory: $childFull"
    }
    return $childFull
}

$pluginStage = Get-ContainedPath $dist (Join-Path $dist "stage-plugin")

$buildArguments = @("build", $project, "--configuration", $Configuration)
if ($NoRestore) {
    $buildArguments += "--no-restore"
}
& $DotNet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
if (Test-Path -LiteralPath $pluginStage) {
    Remove-Item -LiteralPath $pluginStage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $pluginStage | Out-Null

$pluginDll = Join-Path $dist "Jellyfin.Plugin.AniWorld.dll"
Copy-Item -LiteralPath (Join-Path $output "Jellyfin.Plugin.AniWorld.dll") -Destination $pluginDll -Force
Copy-Item -LiteralPath $pluginDll -Destination $pluginStage
Copy-Item -LiteralPath (Join-Path $projectRoot "Jellyfin.Plugin.AniWorld\meta.json") -Destination $pluginStage

$pluginZip = Join-Path $dist "AniWorldRequests_$releaseVersion.zip"
if (Test-Path -LiteralPath $pluginZip) { Remove-Item -LiteralPath $pluginZip -Force }

Compress-Archive -Path (Join-Path $pluginStage "*") -DestinationPath $pluginZip -CompressionLevel Optimal
Remove-Item -LiteralPath $pluginStage -Recurse -Force

$checksums = @($pluginDll, $pluginZip) | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))"
}
Set-Content -LiteralPath (Join-Path $dist "SHA256SUMS.txt") -Value $checksums -Encoding UTF8

Write-Output "Created $pluginDll"
Write-Output "Created $pluginZip"
Write-Output "Created $(Join-Path $dist 'SHA256SUMS.txt')"
