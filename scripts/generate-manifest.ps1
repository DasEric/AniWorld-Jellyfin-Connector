param(
    [Parameter(Mandatory = $true)]
    [string]$RepositorySlug,
    [string]$ReleaseTag
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionInfo = Get-Content -LiteralPath (Join-Path $projectRoot "version.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$versionInfo.version
$versionFourPart = [string]$versionInfo.versionFourPart
$targetAbi = [string]$versionInfo.targetAbi
if ($version -notmatch '^\d+\.\d+\.\d+$' -or
    $versionFourPart -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
    $targetAbi -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "version.json contains an invalid version"
}
if ($RepositorySlug -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "RepositorySlug must use the owner/repository format"
}

if ([string]::IsNullOrWhiteSpace($ReleaseTag)) {
    $ReleaseTag = "v$version"
}
if ($ReleaseTag -ne "v$version") {
    throw "Release tag '$ReleaseTag' does not match version v$version"
}

$archiveName = "AniWorldRequests_$version.zip"
$archivePath = Join-Path $projectRoot "dist\$archiveName"
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    throw "Release archive not found: $archivePath. Run scripts/build.ps1 first."
}

$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm MD5).Hash.ToUpperInvariant()
$sourceUrl = "https://github.com/$RepositorySlug/releases/download/$ReleaseTag/$archiveName"
    $currentVersion = [ordered]@{
        version = $versionFourPart
        changelog = "Bugfixes für Discover, Suche und Fortschrittsbalken."
        targetAbi = $targetAbi
        sourceUrl = $sourceUrl
        checksum = $checksum
        timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    }
    $versions = @($currentVersion)
    $historyPath = Join-Path $projectRoot "manifest-history.json"
    if (Test-Path -LiteralPath $historyPath -PathType Leaf) {
        $history = @(Get-Content -LiteralPath $historyPath -Raw -Encoding UTF8 | ConvertFrom-Json)
        $versions += @($history | Where-Object { [string]$_.version -ne $versionFourPart })
    }
    $manifest = @(
        [ordered]@{
            guid = "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
            name = "AniWorld Requests"
            description = "AniWorld-Suche und Download-Anfragen für alle Jellyfin-Benutzer."
            overview = "Erlaubt Jellyfin-Benutzern, Inhalte auf AniWorld und weiteren Quellen zu suchen und Download-Anfragen zu stellen."
            owner = "Eric"
            category = "General"
            versions = $versions
        }
    )

$repositoryDirectory = Join-Path $projectRoot "repository"
New-Item -ItemType Directory -Force -Path $repositoryDirectory | Out-Null
$manifestPath = Join-Path $repositoryDirectory "manifest.json"
$json = ConvertTo-Json -InputObject $manifest -Depth 8
[IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Created $manifestPath"
$owner, $repositoryName = $RepositorySlug.Split('/')
$pagesPath = if ($repositoryName -ieq "$owner.github.io") { "" } else { "/$repositoryName" }
Write-Output "Jellyfin repository URL after GitHub Pages deployment: https://$owner.github.io$pagesPath/manifest.json"
