# Builds a portable, self-contained ScreenRec Trainer for Windows (x64 and arm64) as zips.
# Usage: ./scripts/package-win.ps1 [-Arch x64|arm64|all]
param([string]$Arch = "all")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$arches = if ($Arch -eq "all") { @("x64", "arm64") } else { @($Arch) }
foreach ($a in $arches) {
  $rid = "win-$a"
  $out = Join-Path $root "artifacts/$rid"
  if (Test-Path $out) { Remove-Item -Recurse -Force $out }
  dotnet publish (Join-Path $root "src/ScreenRec.App") -c Release -r $rid --self-contained -o (Join-Path $out "ScreenRecTrainer")
  dotnet publish (Join-Path $root "src/ScreenRec.Cli") -c Release -r $rid --self-contained -o (Join-Path $out "ScreenRecTrainer")
  Compress-Archive -Path (Join-Path $out "ScreenRecTrainer/*") -DestinationPath (Join-Path $out "ScreenRecTrainer-$rid.zip") -Force
  Write-Host "Built $out/ScreenRecTrainer-$rid.zip  (run ScreenRecTrainer.exe; screenrec.exe is the command line)"
}
