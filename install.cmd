@echo off
setlocal
set "VSWHERE=C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -property installationPath`) do set "VS=%%i"
if not defined VS (echo ??? Visual Studio ???? & pause & exit /b 1)
"%VS%\Common7\IDE\VSIXInstaller.exe" "%~dp0PdbFinder.Vsix\bin\Release\net472\PdbFinder.Vsix.vsix"
pause
