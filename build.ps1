param(
    [string]$OutFile = "YAHCW.exe",
    [switch]$RunTests
)

$ErrorActionPreference = 'Stop'

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw "Compilador csc.exe nao encontrado no sistema."
}

Push-Location $PSScriptRoot
try {
    $sources = Get-ChildItem -Path $PSScriptRoot -Filter "*.cs" | Select-Object -ExpandProperty FullName
    if (-not $sources) {
        throw "Nenhum arquivo de codigo .cs encontrado em $PSScriptRoot."
    }

    $manifestPath = Join-Path $PSScriptRoot "app.manifest"
    $manifestArg = if (Test-Path -LiteralPath $manifestPath) { "/win32manifest:$manifestPath" } else { "" }

    Write-Host "Compilando YAHCW com $($compiler)..."
    $argsList = @(
        "/nologo",
        "/target:winexe",
        "/platform:x64",
        "/optimize+",
        "/out:$OutFile",
        "/reference:System.dll",
        "/reference:System.Core.dll",
        "/reference:System.Drawing.dll",
        "/reference:System.Windows.Forms.dll"
    )

    if ($manifestArg -ne "") {
        $argsList += $manifestArg
    }

    $argsList += $sources

    & $compiler $argsList

    if ($LASTEXITCODE -ne 0) {
        throw "Falha na compilacao de YAHCW."
    }

    Write-Host "Compilacao concluida com sucesso!"
    Write-Host "Binario gerado: $PSScriptRoot\$OutFile"

    if ($RunTests) {
        Write-Host "Executando auto-testes deterministas..."
        & (Join-Path $PSScriptRoot $OutFile) "--self-test" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Auto-testes falharam." }
    }
}
finally {
    Pop-Location
}
