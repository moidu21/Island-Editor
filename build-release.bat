@echo off
setlocal
cd /d "%~dp0"
dotnet build Anno1800IslandTool.sln -c Release -p:Platform=x64
pause
