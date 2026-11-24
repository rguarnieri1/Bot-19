@echo off
cd /d "%~dp0"
set LOG=%~dp0build-release-log.txt
echo === BUILD RELEASE %DATE% %TIME% === > "%LOG%"
dotnet build "19 - Swing trading long.csproj" -c Release >> "%LOG%" 2>&1
if errorlevel 1 (echo BUILD FALLITA >> "%LOG%" & goto fine)
echo BUILD OK >> "%LOG%"
dotnet publish "19 - Swing trading long.csproj" -c Release -f net11.0 -o publish >> "%LOG%" 2>&1
if errorlevel 1 (echo PUBLISH FALLITO >> "%LOG%") else (echo PUBLISH OK >> "%LOG%")
:fine
echo FINE >> "%LOG%"
