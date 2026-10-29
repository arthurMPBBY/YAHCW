$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw "Compilador csc.exe nao encontrado no sistema."
}

$tempDir = Join-Path $PSScriptRoot 'temp'
if (-not (Test-Path -LiteralPath $tempDir)) {
    New-Item -ItemType Directory -Path $tempDir | Out-Null
}
$testExe = Join-Path $tempDir 'YAHCW.IntegrationTests.exe'

$sources = @(
    (Join-Path $root 'Config.cs'),
    (Join-Path $root 'NativeMethods.cs'),
    (Join-Path $root 'WindowEnumerator.cs'),
    (Join-Path $root 'OverviewForm.cs'),
    (Join-Path $root 'DashboardControls.cs'),
    (Join-Path $root 'DashboardServices.cs'),
    (Join-Path $PSScriptRoot 'IntegrationTests.cs')
)

$argsList = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/optimize+',
    "/out:$testExe",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll'
)
$argsList += $sources

Write-Host "Compilando testes de integracao com $compiler..."
& $compiler $argsList
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar testes de integracao.' }

Write-Host "Executando testes de integracao..."
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Testes de integracao falharam.' }
