@echo off
setlocal
cd /d "%~dp0"
dotnet publish src\Anno1800IslandTool.UI\Anno1800IslandTool.UI.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained false -o publish\win-x64
pause
