[CmdletBinding()]
param(
    [string]$Distribution = "Ubuntu-24.04"
)

$ErrorActionPreference = "Stop"

Write-Host "This installs the official OpenFOAM Foundation v14 package in WSL." -ForegroundColor Cyan
Write-Host "It adds dl.openfoam.org as an APT repository and installs packages as WSL root." -ForegroundColor Yellow
Write-Host "The operation changes the selected Ubuntu distribution and can download several gigabytes."
Write-Host ""
$confirmation = Read-Host "Type INSTALL OPENFOAM 14 to continue"
if ($confirmation -cne "INSTALL OPENFOAM 14") {
    Write-Host "Cancelled. No changes were requested."
    exit 1
}

$install = @'
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y wget software-properties-common ca-certificates gnupg
wget -qO /etc/apt/trusted.gpg.d/openfoam.asc https://dl.openfoam.org/gpg.key
add-apt-repository -y "http://dl.openfoam.org/ubuntu main dev"
apt-get update
apt-get install -y openfoam14
source /opt/openfoam14/etc/bashrc
foamVersion
command -v blockMesh
command -v foamRun
'@

& wsl.exe -d $Distribution -u root -- bash -lc $install
if ($LASTEXITCODE -ne 0) {
    throw "OpenFOAM installation failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "OpenFOAM 14 is installed and verified." -ForegroundColor Green
