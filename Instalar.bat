@echo off
rem Instala Flashito para el usuario actual: copia el .exe a %LOCALAPPDATA%\Flashito
rem y crea un acceso directo en el inicio automatico de Windows.
setlocal
set "DEST=%LOCALAPPDATA%\Flashito"
if not exist "%~dp0Flashito.exe" (
  echo No encuentro Flashito.exe al lado de este script.
  pause
  exit /b 1
)
taskkill /IM Flashito.exe /F >nul 2>&1
mkdir "%DEST%" 2>nul
copy /y "%~dp0Flashito.exe" "%DEST%\Flashito.exe" >nul
if errorlevel 1 (
  echo No pude copiar Flashito. Cerra la aplicacion e intenta otra vez.
  pause
  exit /b 1
)
powershell -NoProfile -Command "$w=New-Object -ComObject WScript.Shell; $s=$w.CreateShortcut((Join-Path $w.SpecialFolders('Startup') 'Flashito.lnk')); $s.TargetPath=(Join-Path $env:LOCALAPPDATA 'Flashito\Flashito.exe'); $s.Save()"
powershell -NoProfile -Command "$w=New-Object -ComObject WScript.Shell; $s=$w.CreateShortcut((Join-Path $w.SpecialFolders('Desktop') 'Flashito.lnk')); $s.TargetPath=(Join-Path $env:LOCALAPPDATA 'Flashito\Flashito.exe'); $s.Save()" 2>nul
echo.
echo Flashito instalado en %DEST%
echo Se inicia solo cuando prendas la PC (acceso directo en Inicio).
echo Ahora: abri Flashito, pone tu API key y listo.
start "" "%DEST%\Flashito.exe"
exit /b 0
