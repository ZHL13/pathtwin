param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.6"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "publish\$Runtime-single"
$versionedExePath = Join-Path $repoRoot "artifacts\PathTwin-$Version-$Runtime.exe"
$latestExePath = Join-Path $repoRoot "artifacts\PathTwin-latest-$Runtime.exe"
$staleZipPath = Join-Path $repoRoot "artifacts\PathTwin-$Version-$Runtime.zip"
$stalePackageDir = Join-Path $repoRoot "artifacts\PathTwin-$Version-$Runtime"

if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
if (Test-Path $stalePackageDir) {
    Remove-Item -LiteralPath $stalePackageDir -Recurse -Force
}
if (Test-Path $staleZipPath) {
    Remove-Item -LiteralPath $staleZipPath -Force
}
if (Test-Path $versionedExePath) {
    Remove-Item -LiteralPath $versionedExePath -Force
}
if (Test-Path $latestExePath) {
    Remove-Item -LiteralPath $latestExePath -Force
}

dotnet publish (Join-Path $repoRoot "src\PathTwin.App\PathTwin.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

$publishedExe = Join-Path $publishDir "PathTwin.App.exe"
Copy-Item -LiteralPath $publishedExe -Destination $versionedExePath -Force
Copy-Item -LiteralPath $publishedExe -Destination $latestExePath -Force

Write-Output $versionedExePath
Write-Output $latestExePath
