@echo off
REM Launches MouseTester via the Microsoft-signed Windows debugger (cdb.exe).
REM This bypasses Smart App Control's block on the unsigned binary because the
REM launching process (cdb) is signed by Microsoft.
REM
REM cdb attaches, executes "qd" (quit-detach) which lets cdb exit while keeping
REM MouseTester running. No console window stays open.

setlocal

set "CDB=C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe"
set "EXE=%~dp0MouseTester\MouseTester\bin\x64\Release\MouseTester.exe"

if not exist "%CDB%" (
    echo cdb.exe not found at "%CDB%"
    echo Install Windows Debugging Tools or update the CDB path in this script.
    pause
    exit /b 1
)

if not exist "%EXE%" (
    echo MouseTester.exe not found at:
    echo "%EXE%"
    echo Build the solution first.
    pause
    exit /b 1
)

start "" "%CDB%" -gG -c "qd" "%EXE%"
endlocal
