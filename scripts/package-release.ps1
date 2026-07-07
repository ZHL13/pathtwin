param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "publish\$Runtime"
$packageDir = Join-Path $repoRoot "artifacts\PathTwin-$Version-$Runtime"
$zipPath = Join-Path $repoRoot "artifacts\PathTwin-$Version-$Runtime.zip"

dotnet publish (Join-Path $repoRoot "src\PathTwin.App\PathTwin.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

if (Test-Path $packageDir) {
    Remove-Item -LiteralPath $packageDir -Recurse -Force
}

New-Item -ItemType Directory -Path $packageDir | Out-Null
Copy-Item -Path (Join-Path $publishDir "*") -Destination $packageDir -Recurse -Force
Get-ChildItem -Path $packageDir -Recurse -Filter "*.pdb" | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $packageDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $packageDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "NOTICE") -Destination $packageDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD_PARTY_NOTICES.md") -Destination $packageDir -Force

$thirdPartyOut = Join-Path $packageDir "third_party\rclone"
New-Item -ItemType Directory -Path $thirdPartyOut | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot "third_party\rclone\COPYING") -Destination $thirdPartyOut -Force

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -Force
Write-Output $zipPath
