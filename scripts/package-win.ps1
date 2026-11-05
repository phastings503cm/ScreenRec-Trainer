# Builds a self-contained ScreenRec Trainer for Windows (x64 and arm64): a portable zip plus a WiX MSI installer.
# Usage: ./scripts/package-win.ps1 [-Arch x64|arm64|all]
param([string]$Arch = "all")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$arches = if ($Arch -eq "all") { @("x64", "arm64") } else { @($Arch) }

$version = (([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.VersionPrefix | Select-Object -First 1)
$commits = (git rev-list --count HEAD)
$version = "$version.$commits"

# WiX 5.0.2 builds the MSI; the UI extension gives the install-dir dialog. Both must be on Windows.
dotnet tool install --global wix --version 5.0.2 2>$null
$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
wix extension add -g WixToolset.UI.wixext/5.0.2 2>$null

foreach ($a in $arches) {
  $rid = "win-$a"
  $out = Join-Path $root "artifacts/$rid"
  if (Test-Path $out) { Remove-Item -Recurse -Force $out }
  $pub = Join-Path $out "ScreenRecTrainer"
  dotnet publish (Join-Path $root "src/ScreenRec.App") -c Release -r $rid --self-contained -p:Version=$version -o $pub
  dotnet publish (Join-Path $root "src/ScreenRec.Cli") -c Release -r $rid --self-contained -p:Version=$version -o $pub
  Copy-Item (Join-Path $root "README.md") $pub
  Compress-Archive -Path "$pub/*" -DestinationPath (Join-Path $out "ScreenRecTrainer-$version-$rid.zip") -Force
  wix build -arch $a -ext WixToolset.UI.wixext `
    -d Version=$version -d PublishDir="$pub/" `
    -o (Join-Path $out "ScreenRecTrainer-$version-$rid.msi") (Join-Path $root "packaging/windows/ScreenRecTrainer.wxs")
  Write-Host "Built $out/ScreenRecTrainer-$version-$rid.zip and .msi  (run ScreenRecTrainer.exe; screenrec.exe is the command line)"
}
