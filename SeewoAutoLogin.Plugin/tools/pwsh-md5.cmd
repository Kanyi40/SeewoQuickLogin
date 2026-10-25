@echo off
rem pwsh shim: PluginSdk targets invoke
rem   <bin> -ep bypass -NoLogo -File <ps1> <cipx-dir>
rem No PowerShell Core on this machine; delegate to python replica.
rem The LAST argument is the cipx directory.
set "PY=C:\Users\ken21\.workbuddy\binaries\python\versions\3.13.12\python.exe"
for %%a in (%*) do set "CIPX_DIR=%%~a"
"%PY%" "%~dp0gen_md5.py" "%CIPX_DIR%"
exit /b %ERRORLEVEL%
