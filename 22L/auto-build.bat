@echo off
chcp 65001 > nul
title SeatManagerApp Auto Build & Run
echo.
echo ================================================
echo   SeatManagerApp 자동 빌드 및 실행 시작
echo ================================================
echo.
echo 코드가 변경될 때마다 자동으로 빌드하고 실행됩니다.
echo 종료하려면 이 창을 닫거나 Ctrl+C를 누르세요.
echo. 
powershell -ExecutionPolicy Bypass -File "%~dp0auto-build.ps1"
pause
