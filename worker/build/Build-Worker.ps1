<#
.SYNOPSIS
  Воспроизводимая сборка поставляемого Python worker FAKT: Python 3.8.10 embeddable (x64) + закреплённые колёса.

.DESCRIPTION
  1. Скачивает python-3.8.10-embed-amd64.zip с python.org и проверяет MD5, опубликованный на странице релиза.
  2. Устанавливает колёса cp38/win_amd64 из requirements.txt только по SHA-256 (--require-hashes, --only-binary).
  3. Настраивает python38._pth (изолированный sys.path: без PYTHONPATH и пользовательского site-packages).
  4. Копирует код worker и выполняет проверку «hello» по протоколу.

  Пользователю Python устанавливать не нужно. Скрипт выполняется на машине сборки (нужен любой Python 3.8+ с pip
  для загрузки колёс под целевую платформу). Для Windows 7 на целевой машине нужны обновления KB2533623 и
  Universal CRT (KB2999226/KB3118401) — см. docs/SETUP.md.

.PARAMETER Output
  Каталог результата (по умолчанию worker\build\out). Внутри: python\ и код worker.

.PARAMETER HostPython
  Python машины сборки для pip download/install (по умолчанию python из PATH).
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot 'out'),
    [string]$HostPython = 'python',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$workerRoot = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $PSScriptRoot 'cache'
$pythonVersion = '3.8.10'
$zipName = "python-$pythonVersion-embed-amd64.zip"
$zipUrl = "https://www.python.org/ftp/python/$pythonVersion/$zipName"
# Контрольная сумма с официальной страницы релиза https://www.python.org/downloads/release/python-3810/
$zipMd5 = '3acb1d7d9bde5a79f840167b166bb633'
$zipSize = 8211403

New-Item -ItemType Directory -Force $cache | Out-Null
$zipPath = Join-Path $cache $zipName
if (-not (Test-Path $zipPath)) {
    Write-Host "Загрузка $zipUrl"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
}

$actualSize = (Get-Item $zipPath).Length
$actualMd5 = (Get-FileHash -Algorithm MD5 $zipPath).Hash.ToLowerInvariant()
if ($actualSize -ne $zipSize -or $actualMd5 -ne $zipMd5) {
    Remove-Item $zipPath -Force
    throw "Контрольная сумма $zipName не совпадает (размер $actualSize, MD5 $actualMd5). Файл удалён; проверьте источник загрузки."
}
$zipSha256 = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLowerInvariant()
Write-Host "Проверен $zipName (MD5 $actualMd5, SHA-256 $zipSha256)"

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
$pythonDir = Join-Path $Output 'python'
New-Item -ItemType Directory -Force $pythonDir | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $pythonDir

# Изолированный sys.path: стандартная библиотека, каталог интерпретатора и закреплённые пакеты.
$pth = Join-Path $pythonDir 'python38._pth'
Set-Content -Path $pth -Encoding ASCII -Value @('python38.zip', '.', 'Lib\site-packages')

$sitePackages = Join-Path $pythonDir 'Lib\site-packages'
New-Item -ItemType Directory -Force $sitePackages | Out-Null
Write-Host 'Установка закреплённых колёс (только по SHA-256)…'
& $HostPython -m pip install `
    --disable-pip-version-check --no-input `
    --target $sitePackages `
    --platform win_amd64 --python-version 3.8 --implementation cp --abi cp38 `
    --only-binary=:all: --no-deps --require-hashes `
    --cache-dir (Join-Path $cache 'pip') `
    -r (Join-Path $workerRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw "pip install завершился с кодом $LASTEXITCODE" }

# Код worker (без тестов и кэшей).
Copy-Item (Join-Path $workerRoot 'fakt_worker_main.py') $Output
Copy-Item (Join-Path $workerRoot 'fakt_worker') (Join-Path $Output 'fakt_worker') -Recurse
Get-ChildItem $Output -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force

# Проверка: запуск по протоколу и ответ hello.
$python = Join-Path $pythonDir 'python.exe'
$request = '{"v":1,"id":"1","cmd":"hello","args":{}}' + "`n" + '{"v":1,"id":"2","cmd":"shutdown","args":{}}' + "`n"
$hello = $request | & $python -I -X utf8 -u (Join-Path $Output 'fakt_worker_main.py')
if ($LASTEXITCODE -ne 0) { throw "worker завершился с кодом $LASTEXITCODE" }
$first = ($hello | Select-Object -First 1) | ConvertFrom-Json
if (-not $first.ok) { throw "worker не ответил на hello: $hello" }
Write-Host ("Worker готов: Python {0}, pandas {1}, numpy {2}, defusedxml {3}" -f $first.result.python_version, $first.result.pandas_version, $first.result.numpy_version, $first.result.defusedxml_version)

if (-not $SkipTests) {
    Write-Host 'Запуск тестов worker на поставляемом Python…'
    & $python -I -X utf8 (Join-Path $PSScriptRoot 'run_tests.py')
    if ($LASTEXITCODE -ne 0) { throw "Тесты worker не пройдены (код $LASTEXITCODE)" }
}

$manifest = [ordered]@{
    python = $pythonVersion
    embeddable_zip = $zipName
    embeddable_md5 = $zipMd5
    embeddable_sha256 = $zipSha256
    requirements = (Get-Content (Join-Path $workerRoot 'requirements.txt') | Where-Object { $_ -match '^[a-z]' } | ForEach-Object { $_.Trim(' ', '\') })
    built_at_utc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}
$manifest | ConvertTo-Json | Set-Content -Path (Join-Path $Output 'worker-build.json') -Encoding UTF8
Write-Host "Готово: $Output"
