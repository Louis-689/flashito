@echo off
rem Desinstala Flashito: cierra la app, borra el programa, el acceso directo
rem del inicio y su configuracion.
taskkill /IM Flashito.exe /F >nul 2>&1
powershell -NoProfile -Command "$w=New-Object -ComObject WScript.Shell; Remove-Item -LiteralPath (Join-Path $w.SpecialFolders('Startup') 'Flashito.lnk') -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath (Join-Path $w.SpecialFolders('Desktop') 'Flashito.lnk') -Force -ErrorAction SilentlyContinue"
rmdir /s /q "%LOCALAPPDATA%\Flashito" >nul 2>&1
rmdir /s /q "%APPDATA%\Flashito" >nul 2>&1
echo Flashito desinstalado.
exit /b 0
