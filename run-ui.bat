@echo off
setlocal
cd /d "%~dp0"
dotnet run --project src\Anno1800IslandTool.UI\Anno1800IslandTool.UI.csproj -c Release -p:Platform=x64
pause
