@echo off
set "taskExecutable=%~dp0dist\Tweakmania\Tweakmania.exe"
if exist "%~dp0dist\latest.txt" (
  set /p "taskRelativeExecutable="<"%~dp0dist\latest.txt"
)
if defined taskRelativeExecutable set "taskExecutable=%~dp0dist\%taskRelativeExecutable%"
if not exist "%taskExecutable%" (
  echo Build the application first with Build.ps1.
  pause
  exit /b 1
)
start "" "%taskExecutable%"
