@echo off
rem Builds the single-file installer: dist\TimeCapsuleDriveSetup.exe (needs the .NET 10 SDK).
rem Nothing in the file is unpacked to a temporary folder at run time (the background drive runs with system
rem rights, so it must not load code from a place other users can write to).
cd /d "%~dp0"
if not exist third_party\winfsp-2.2.26323.msi (
  echo Downloading the WinFsp installer to embed...
  if not exist third_party mkdir third_party
  curl -sL -o third_party\winfsp-2.2.26323.msi https://github.com/winfsp/winfsp/releases/download/v2.2/winfsp-2.2.26323.msi || exit /b 1
)
dotnet publish src\TimeCapsuleDrive -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist -nologo -v q || exit /b 1
move /y dist\TimeCapsuleDrive.exe dist\TimeCapsuleDriveSetup.exe >nul || exit /b 1
dist\TimeCapsuleDriveSetup.exe winfsp-check
echo Built dist\TimeCapsuleDriveSetup.exe
