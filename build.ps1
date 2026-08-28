#requires -Version 5.1

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'FullScreenManager\FullScreenManager.csproj'
$output = Join-Path $PSScriptRoot 'dist'
$localDotnet = Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }

& $dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $output `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "Сборка завершилась с кодом $LASTEXITCODE. Закройте запущенный FullScreenManager.exe и повторите попытку."
}

Write-Host "Готово: $(Join-Path $output 'FullScreenManager.exe')"
