@echo off
setlocal
cd /d %~dp0

REM Build Muse C# version with csc.exe (.NET Framework 4.8, WinForms + WebView2)
REM No SDK needed. Looks for Roslyn csc (Visual Studio) first, then the in-box one.

set CSC=
for %%V in (2022 2019) do for %%E in (Community Professional Enterprise BuildTools) do (
  if exist "C:\Program Files\Microsoft Visual Studio\%%V\%%E\MSBuild\Current\Bin\Roslyn\csc.exe" set CSC="C:\Program Files\Microsoft Visual Studio\%%V\%%E\MSBuild\Current\Bin\Roslyn\csc.exe"
  if exist "C:\Program Files (x86)\Microsoft Visual Studio\%%V\%%E\MSBuild\Current\Bin\Roslyn\csc.exe" set CSC="C:\Program Files (x86)\Microsoft Visual Studio\%%V\%%E\MSBuild\Current\Bin\Roslyn\csc.exe"
)
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set CSC="%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not defined CSC (
  echo [ERROR] csc.exe not found. Install .NET Framework 4.8 or Visual Studio.
  echo [ERROR] csc.exe not found. Install .NET Framework 4.8 or Visual Studio. > build.log
  pause
  exit /b 1
)
echo Using compiler: %CSC%
if not exist bin mkdir bin
set RES=/resource:res\mihomo.exe,Muse.mihomo.exe /resource:res\geoip.metadb,Muse.geoip.metadb
if exist res\muse.ico set RES=%RES% /resource:res\muse.ico,Muse.muse.ico
%CSC% /nologo /target:winexe /out:bin\Muse.exe /optimize /langversion:5 ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Data.dll ^
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  /reference:System.Net.Http.dll /reference:System.Xml.dll ^
  /reference:lib\Microsoft.Web.WebView2.Core.dll ^
  /reference:lib\Microsoft.Web.WebView2.WinForms.dll ^
  /reference:lib\YamlDotNet.dll /reference:lib\Newtonsoft.Json.dll ^
  %RES% ^
  src\*.cs > build.log 2>&1
type build.log
if errorlevel 1 (
  echo [ERROR] Build failed. See build.log for details.
  pause
  exit /b 1
)
copy /y lib\*.dll bin\ >nul
echo [OK] Built: bin\Muse.exe
echo First run extracts the built-in proxy core (mihomo.exe + geoip.metadb)
echo to the MuseData folder automatically. See README.
pause
