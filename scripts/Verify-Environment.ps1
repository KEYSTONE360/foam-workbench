[CmdletBinding()]
param(
    [string]$Distribution = "Ubuntu-24.04",
    [string]$Bashrc = "/opt/openfoam14/etc/bashrc",
    [string]$ParaView = "C:\Program Files\ParaView 6.1.0\bin\paraview.exe"
)

$ErrorActionPreference = "Stop"

Write-Host "Foam Workbench environment check" -ForegroundColor Cyan
Write-Host ""

$wslVersion = & wsl.exe -l -v
Write-Host $wslVersion

$probe = "if [ -f '$Bashrc' ]; then source '$Bashrc'; fi; " +
         "foamVersion 2>&1 || printf 'OpenFOAM not installed\n'; " +
         "command -v blockMesh || printf 'blockMesh not found\n'; " +
         "command -v foamRun || printf 'foamRun not found\n'"

& wsl.exe -d $Distribution -- bash -lc $probe

if (Test-Path -LiteralPath $ParaView) {
    Write-Host "ParaView: $ParaView" -ForegroundColor Green
} else {
    Write-Host "ParaView: not found at $ParaView" -ForegroundColor Yellow
}
