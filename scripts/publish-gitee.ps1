# Publish the complete Velopack dist folder as a Gitee Release.
# Requires the version tag to exist on the Gitee remote, and a Gitee token with write access
# (pass -Token, or set the GITEE_TOKEN env var). The token is sent via the Authorization
# header, never in the URL query. $owner/$repository must match UpdateService.RepoUrl.
param(
    [string]$Token = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$owner = "YGWG-LX"
$repository = "vigil-watt"
$repoUrl = "https://gitee.com/$owner/$repository"
$apiUrl = "https://gitee.com/api/v5/repos/$owner/$repository"

if ([string]::IsNullOrWhiteSpace($Token)) { $Token = $env:GITEE_TOKEN }
if ([string]::IsNullOrWhiteSpace($Token)) {
    throw "Set GITEE_TOKEN to a Gitee personal access token with repository write access."
}

$feed = Join-Path $dist "releases.win.json"
$packages = @(Get-ChildItem -LiteralPath $dist -Filter "*.nupkg" -File -ErrorAction SilentlyContinue)
if (-not (Test-Path -LiteralPath $feed) -or $packages.Count -eq 0) {
    throw "Missing Velopack feed or packages in $dist. Run scripts\build-installer.ps1 first."
}

$csproj = Join-Path $root "src\VigilWatt.App\VigilWatt.App.csproj"
$version = ([xml](Get-Content -LiteralPath $csproj -Raw)).Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { throw "Could not read <Version> from the app project." }
$parts = $version.Split('.')
if ($parts.Length -gt 3) { $version = $parts[0..2] -join '.' }
$tag = "v$version"

$tagRef = git ls-remote --exit-code origin "refs/tags/$tag"
if ($LASTEXITCODE -ne 0 -or -not $tagRef) {
    throw "Tag $tag is not present on origin. Push the matching version tag to Gitee before publishing."
}

$existing = $null
$tagUri = "$apiUrl/releases/tags/$([uri]::EscapeDataString($tag))"
try {
    $existing = Invoke-RestMethod -Method Get -Uri $tagUri -Headers @{ Authorization = "token $Token" }
}
catch {
    $statusCode = 0
    if ($_.Exception.Response) { $statusCode = [int]$_.Exception.Response.StatusCode }
    if ($statusCode -ne 404) { throw "Could not check for an existing Gitee Release (HTTP $statusCode)." }
}
if ($existing) { throw "Gitee Release $tag already exists; refusing to replace or duplicate its assets." }

Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromMinutes(10)
# Token goes in the Authorization header, never in the URL query, so it stays out of server/proxy logs.
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("token", $Token)

try {
    $releaseBody = @{
        tag_name = $tag
        name = "VigilWatt $version"
        body = "VigilWatt $version release. The attached files include the Velopack update feed and packages."
        prerelease = $false
    } | ConvertTo-Json
    $createUri = "$apiUrl/releases"
    $json = [System.Net.Http.StringContent]::new($releaseBody, [System.Text.Encoding]::UTF8, "application/json")
    $createResponse = $client.PostAsync($createUri, $json).GetAwaiter().GetResult()
    $createText = $createResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $createResponse.IsSuccessStatusCode) {
        throw "Gitee Release creation failed with HTTP $([int]$createResponse.StatusCode)."
    }
    $release = $createText | ConvertFrom-Json
    $releaseId = $release.id
    if (-not $releaseId) { throw "Gitee did not return a Release id." }

    $files = @(Get-ChildItem -LiteralPath $dist -File | Where-Object Name -ne "releases.win.json" | Sort-Object Name)
    $files += Get-Item -LiteralPath $feed
    foreach ($file in $files) {
        Write-Host "Uploading $($file.Name)"
        $form = [System.Net.Http.MultipartFormDataContent]::new()
        $stream = [System.IO.File]::OpenRead($file.FullName)
        $content = [System.Net.Http.StreamContent]::new($stream)
        $form.Add($content, "file", $file.Name)
        try {
            $uploadUri = "$apiUrl/releases/$releaseId/attach_files"
            $uploadResponse = $client.PostAsync($uploadUri, $form).GetAwaiter().GetResult()
            if (-not $uploadResponse.IsSuccessStatusCode) {
                throw "Gitee attachment upload failed with HTTP $([int]$uploadResponse.StatusCode) for $($file.Name)."
            }
        }
        finally {
            $form.Dispose()
            $stream.Dispose()
        }
    }

    Write-Host "Published $repoUrl/releases/tag/$tag"
}
finally {
    $client.Dispose()
}
