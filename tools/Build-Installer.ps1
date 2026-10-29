<#
.SYNOPSIS
  테스트 → self-contained publish → Inno Setup 컴파일로 artifacts\installer\AITokenUsage-Setup-<버전>.exe를 만든다 (v1.1 설계 §5.4).
#>
[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    Write-Error "Inno Setup 6 이 없습니다. 설치: winget install JRSoftware.InnoSetup"
}

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { Write-Error 'Directory.Build.props 에 <Version> 이 없습니다.' }

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'AITokenUsage.sln') -c Release
    if ($LASTEXITCODE -ne 0) { Write-Error '테스트 실패' }
}

$publish = Join-Path $root 'artifacts\publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\AITokenUsage\AITokenUsage.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishReadyToRun=true -o $publish
if ($LASTEXITCODE -ne 0) { Write-Error 'publish 실패' }

& $iscc "/DAppVersion=$version" (Join-Path $root 'installer\AITokenUsage.iss')
if ($LASTEXITCODE -ne 0) { Write-Error 'ISCC 실패' }

$setup = Join-Path $root "artifacts\installer\AITokenUsage-Setup-$version.exe"
Write-Host "완성: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
