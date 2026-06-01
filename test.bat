@echo off
setlocal
cd /d "%~dp0"
dotnet test tests\Anno1800IslandTool.Tests\Anno1800IslandTool.Tests.csproj -c Debug
pause
