# Publica Umbra (Release) y compila el instalador en installer\output\
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version

$iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Falta Inno Setup 6: winget install JRSoftware.InnoSetup" }

dotnet publish "$root\src\Umbra.App\Umbra.App.csproj" -c Release
& $iscc "/DAppVersion=$version" "$PSScriptRoot\Umbra.iss"
Write-Host "Listo: $PSScriptRoot\output\Umbra-Setup-$version.exe"
