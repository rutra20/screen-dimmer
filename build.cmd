@echo off
setlocal
rem Builds ScreenDimmer.exe with the C# compiler bundled with Windows.
rem No SDK, no NuGet, no admin rights.

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo csc.exe not found - .NET Framework 4.x is required.
    exit /b 1
)

if not exist "%~dp0dist" mkdir "%~dp0dist"

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
    /out:"%~dp0dist\ScreenDimmer.exe" ^
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
    "%~dp0src\ScreenDimmer.cs"

if errorlevel 1 exit /b 1
echo Built dist\ScreenDimmer.exe
