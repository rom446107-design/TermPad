[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '1.0.0',
    [string]$OutputDir = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$appProject = Join-Path $root 'Termpad.csproj'
$installerProject = Join-Path $root 'installer\TermpadSetup.csproj'
$payloadDir = Join-Path $root 'installer\payload'
$payloadZip = Join-Path $root 'installer\payload.zip'
$installerOut = Join-Path $root 'installer\publish'

function Resolve-DotNet {
    $candidates = @()
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $candidate }
    }

    throw 'Не найден .NET 8 SDK. Установите его: https://dotnet.microsoft.com/download'
}

function Invoke-Step([string]$Title, [scriptblock]$Action) {
    Write-Host ''
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Шаг завершился с кодом ${LASTEXITCODE}: $Title" }
}

$dotnet = Resolve-DotNet
Write-Host "dotnet: $dotnet"
Write-Host "версия: $Version, конфигурация: $Configuration, платформа: $Runtime"

if (Test-Path -LiteralPath $payloadDir) { Remove-Item -LiteralPath $payloadDir -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDir | Out-Null

Invoke-Step 'Публикация Termpad' {
    & $dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None -p:Version=$Version -o $payloadDir
}

Invoke-Step 'Упаковка payload.zip' {
    if (Test-Path -LiteralPath $payloadZip) { Remove-Item -LiteralPath $payloadZip -Force }
    Compress-Archive -Path (Join-Path $payloadDir '*') -DestinationPath $payloadZip -CompressionLevel Optimal
}

Invoke-Step 'Сборка установщика' {
    & $dotnet publish $installerProject -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None -p:Version=$Version -o $installerOut
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$setup = Join-Path $installerOut 'TermpadSetup.exe'
if (-not (Test-Path -LiteralPath $setup)) { throw "Установщик не найден: $setup" }

$target = Join-Path $OutputDir 'TermpadSetup.exe'
Copy-Item -LiteralPath $setup -Destination $target -Force

$sizeMb = [math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 1)
Write-Host ''
Write-Host "Готово: $target ($sizeMb МБ)" -ForegroundColor Green
