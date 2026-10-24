@echo off
"%~dp0dist\InzoneMicSwitch.exe" --enable-actions %*
if errorlevel 1 pause
