@echo off
setlocal
set "APP=%~dp0FoamWorkbench.exe"
if not exist "%APP%" set "APP=%~dp0publish\FoamWorkbench.exe"
if not exist "%APP%" (
  echo FoamWorkbench.exe was not found beside this launcher or in the publish folder.
  echo Run: dotnet publish FoamWorkbench.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
  pause
  exit /b 1
)
start "" "%APP%" %*
