@echo off
setlocal

pushd "%~dp0"
dotnet run --project "src\KevinZonda.Terminal.Server\KevinZonda.Terminal.Server.csproj" -- %*
set "exitCode=%ERRORLEVEL%"
popd

exit /b %exitCode%
