@echo off
"%~dp0dist\InzoneMicSwitch.exe" --enable-switch %*
if errorlevel 1 pause
