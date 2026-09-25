<#
.SYNOPSIS
  Сборка выпуска FAKT: переносная папка dist\FAKT, архив zip и (если установлен Inno Setup) установщик.

.DESCRIPTION
  1. Собирает клиент (Release, x64) и, если указан -RunTests, выполняет модульные тесты.
  2. Берёт поставляемый Python worker из worker\build\out (или собирает его worker\build\Build-Worker.ps1).
  3. Складывает dist\FAKT: FAKT.exe и библиотеки, worker\python, код worker, документацию и
     файл реестра для TLS 1.2 на Windows 7. Приложению не нужен доступ в интернет: всё поставляется в папке.
  4. Пишет dist\FAKT\release-manifest.json (версии и SHA-256 всех файлов) и архив dist\FAKT-<версия>-win-x64.zip.
  5. Если найден ISCC.exe (Inno Setup 6), компилирует installer\FAKT.iss.

  Скрипт выполняется на машине сборки. Для сборки нужен .NET SDK 8+ (см. global.json); worker собирается из кэша
  или папки колёс без сети (см. worker\build\Build-Worker.ps1 -Wheelhouse).

.PARAMETER Output
  Каталог результата (по умолчанию dist в корне репозитория).

.PARAMETER RunTests
  Выполнить модульные тесты перед упаковкой.

.PARAMETER RebuildWorker
  Пересобрать worker, даже если worker\build\out уже есть.

.PARAMETER Wheelhouse
  Передаётся в Build-Worker.ps1 для сборки worker без сети.
#>
[CmdletBinding()]
param(
    [string]$Output,
    [switch]$RunTests,
    [switch]$RebuildWorker,
    [string]$Wheelhouse
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
if (-not $Output) { $Output = Join-Path $root 'dist' }
$app = Join-Path $Output 'FAKT'

Write-Host '1/5 Сборка клиента (Release, x64)…'
& dotnet build (Join-Path $root 'src\Fakt.Desktop\Fakt.Desktop.csproj') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Сборка клиента завершилась с кодом $LASTEXITCODE" }

if ($RunTests) {
    Write-Host '   Модульные тесты…'
    & dotnet test (Join-Path $root 'tests\Fakt.UnitTests\Fakt.UnitTests.csproj') -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw "Модульные тесты не пройдены (код $LASTEXITCODE)" }
}

Write-Host '2/5 Python worker…'
$workerOut = Join-Path $root 'worker\build\out'
if ($RebuildWorker -or -not (Test-Path (Join-Path $workerOut 'python\python.exe'))) {
    $workerArgs = @{}
    if ($Wheelhouse) { $workerArgs['Wheelhouse'] = $Wheelhouse }
    & (Join-Path $root 'worker\build\Build-Worker.ps1') @workerArgs
}

Write-Host "3/5 Папка выпуска: $app"
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null
$bin = Join-Path $root 'src\Fakt.Desktop\bin\Release\net48'
Get-ChildItem $bin -File | Where-Object { $_.Extension -in '.exe', '.dll', '.config' } | Copy-Item -Destination $app

# Worker: интерпретатор и библиотеки из сборки, код — из репозитория (без тестов и кэшей).
$workerDst = Join-Path $app 'worker'
New-Item -ItemType Directory -Force $workerDst | Out-Null
Copy-Item (Join-Path $workerOut 'python') (Join-Path $workerDst 'python') -Recurse
Copy-Item (Join-Path $root 'worker\fakt_worker_main.py') $workerDst
Copy-Item (Join-Path $root 'worker\fakt_worker') (Join-Path $workerDst 'fakt_worker') -Recurse
if (Test-Path (Join-Path $workerOut 'worker-build.json')) { Copy-Item (Join-Path $workerOut 'worker-build.json') $workerDst }
Get-ChildItem $workerDst -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force

$docs = Join-Path $app 'docs'
New-Item -ItemType Directory -Force $docs | Out-Null
foreach ($doc in 'README.md', 'docs\SETUP.md', 'docs\PROVIDERS.md', 'docs\COMPATIBILITY.md', 'docs\TEST_REPORT.md', 'docs\ARCHITECTURE.md') {
    $source = Join-Path $root $doc
    if (Test-Path $source) { Copy-Item $source $docs }
}
Copy-Item (Join-Path $root 'build\windows7') (Join-Path $app 'windows7') -Recurse

Write-Host '4/5 Проверка worker из папки выпуска…'
$python = Join-Path $workerDst 'python\python.exe'
$request = '{"v":1,"id":"1","cmd":"hello","args":{}}' + "`n" + '{"v":1,"id":"2","cmd":"shutdown","args":{}}' + "`n"
$hello = ($request | & $python -I -X utf8 -u (Join-Path $workerDst 'fakt_worker_main.py') | Select-Object -First 1) | ConvertFrom-Json
if (-not $hello.ok) { throw 'Worker из папки выпуска не ответил на hello.' }

# ProductVersion содержит и коммит сборки («1.0.0+<commit>»): версия — для имени архива, коммит — в манифест.
$informational = (Get-Item (Join-Path $app 'FAKT.exe')).VersionInfo.ProductVersion
$version = $informational.Split('+')[0]
$commit = if ($informational.Contains('+')) { $informational.Split('+')[1] } else { $null }
$files = Get-ChildItem $app -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($app.Length + 1)
        size = $_.Length
        sha256 = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant()
    }
}
$manifest = [ordered]@{
    product = 'FAKT'
    version = $version
    commit = $commit
    built_at_utc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    framework = '.NET Framework 4.8 (x64)'
    worker = [ordered]@{ python = $hello.result.python_version; pandas = $hello.result.pandas_version; numpy = $hello.result.numpy_version; defusedxml = $hello.result.defusedxml_version }
    files = $files
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $app 'release-manifest.json') -Encoding UTF8

$zip = Join-Path $Output "FAKT-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $app -DestinationPath $zip -CompressionLevel Optimal
Write-Host ("   Файлов: {0}; архив: {1} ({2:N1} МБ)" -f @($files).Count, $zip, ((Get-Item $zip).Length / 1MB))

Write-Host '5/5 Установщик…'
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc "/DAppVersion=$version" "/DSourceDir=$app" "/O$Output" (Join-Path $root 'installer\FAKT.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup завершился с кодом $LASTEXITCODE" }
} else {
    Write-Host '   Inno Setup 6 не найден: установщик не собран (достаточно переносной папки или архива).'
}

Write-Host "Готово: $app"
