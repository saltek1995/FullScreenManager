#requires -Version 5.1

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'FullScreenManager\FullScreenManager.csproj'
$tests = Join-Path $PSScriptRoot 'FullScreenManager.Tests\FullScreenManager.Tests.csproj'
$output = Join-Path $PSScriptRoot 'dist'
$localDotnet = Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }

& $dotnet run --project $tests --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Regression tests failed with exit code $LASTEXITCODE."
}

& $dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $output `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE. Close FullScreenManager.exe and retry."
}

Write-Host "Ready: $(Join-Path $output 'FullScreenManager.exe')"
