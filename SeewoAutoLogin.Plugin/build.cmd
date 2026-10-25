@echo off
rem Build wrapper: overrides PowershellBinaryName because the PluginSdk
rem targets reset it to "pwsh" and this machine has no PowerShell Core.
dotnet build "%~dp0SeewoAutoLogin.Plugin.csproj" %* -p:PowershellBinaryName="%~dp0tools\pwsh-md5.cmd"
exit /b %ERRORLEVEL%
