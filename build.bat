@echo off
rem Builds DuoSeat.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist dist mkdir dist
if not exist src\duoseat.ico powershell -NoProfile -ExecutionPolicy Bypass -File src\make-icon.ps1
set REFS=/r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll
"%CSC%" /nologo /optimize+ /platform:anycpu /target:winexe %REFS% /win32manifest:src\app.manifest /win32icon:src\duoseat.ico /resource:web\index.html,DuoSeat.index.html /resource:web\watch.html,DuoSeat.watch.html /out:dist\DuoSeat.exe src\Common.cs src\Injector.cs src\Host.cs src\Web.cs src\Client.cs src\Viewer.cs src\Extras.cs src\Aero.cs src\Launcher.cs || goto :fail
if exist test\TestDriver.cs "%CSC%" /nologo /target:exe %REFS% /out:test\TestDriver.exe src\Common.cs test\TestDriver.cs || goto :fail
if exist test\TestTarget.cs "%CSC%" /nologo /target:winexe %REFS% /out:test\TestTarget.exe src\Common.cs test\TestTarget.cs || goto :fail
echo Built: dist\DuoSeat.exe
exit /b 0
:fail
echo BUILD FAILED
exit /b 1

