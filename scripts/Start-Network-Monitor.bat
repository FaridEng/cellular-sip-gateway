@echo off
title Point B and Galaxy A07 Network Monitor
mode con: cols=120 lines=40 >nul
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Network-Monitor-PointB.ps1"

if errorlevel 1 (
    echo.
    echo The network monitor stopped with an error.
    pause
)
