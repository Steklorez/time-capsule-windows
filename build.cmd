@echo off
rem Builds the single-file installer: dist\TimeCapsuleDriveSetup.exe (needs the .NET 10 SDK to build).
rem The result runs on .NET Framework 4.8, which Windows 10 and 11 already have and Windows 7/8 can install.
rem Its libraries are embedded and loaded from memory: nothing is unpacked to a temporary folder at run time
rem (the background drive runs with system rights, so it must not load code from a place other users can write to).
cd /d "%~dp0"
if not exist third_party\winfsp-2.2.26323.msi (
  echo Downloading the WinFsp installer to embed...
  if not exist third_party mkdir third_party
  curl -sL -o third_party\winfsp-2.2.26323.msi https://github.com/winfsp/winfsp/releases/download/v2.2/winfsp-2.2.26323.msi || exit /b 1
)
dotnet build src\TimeCapsuleDrive -c Release -f net48 -nologo -v q || exit /b 1
if not exist dist mkdir dist
copy /y src\TimeCapsuleDrive\bin\Release\net48\win-x64\TimeCapsuleDrive.exe dist\TimeCapsuleDriveSetup.exe >nul || exit /b 1
dist\TimeCapsuleDriveSetup.exe winfsp-check
echo Built dist\TimeCapsuleDriveSetup.exe
