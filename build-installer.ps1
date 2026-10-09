# Builds the installer: tests -> self-contained publish -> Inno Setup.
# Usage: powershell -ExecutionPolicy Bypass -File build-installer.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "== Running tests" -ForegroundColor Cyan
dotnet test --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Tests failed; not building an installer." }

Write-Host "== Publishing (self-contained, win-x64)" -ForegroundColor Cyan
Remove-Item artifacts\publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src\MicOptimizer.App -c Release -r win-x64 --self-contained true -o artifacts\publish --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

Write-Host "== Building installer" -ForegroundColor Cyan
& $iscc /Q installer\MicOptimizer.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }

$setup = Get-ChildItem artifacts\installer\MicOptimizer-Setup-*.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ("Done: {0} ({1:N0} MB)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
