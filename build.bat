@echo off
rem Compila Flashito.exe con el compilador de C# incluido en Windows (.NET Framework).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo No se encontro el compilador de C# de .NET Framework.
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:Flashito.exe /win32icon:app.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll ^
  Flashito.cs
if errorlevel 1 (
  echo La compilacion fallo.
  exit /b 1
)
echo Listo: Flashito.exe
endlocal
