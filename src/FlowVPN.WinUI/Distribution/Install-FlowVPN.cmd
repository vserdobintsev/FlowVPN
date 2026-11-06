@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "FLOWVPN_CERTIFICATE_SHA256=D9BF1FF74EFF7D2AB72BD89DA624564FF6C56C551002BE3493038AF13A87C854"
set "FLOWVPN_PACKAGE_COUNT=0"
for %%F in ("%~dp0FlowVPN-*-x64.msix") do if exist "%%~fF" (
  set "FLOWVPN_PACKAGE=%%~fF"
  set /a FLOWVPN_PACKAGE_COUNT+=1 >nul
)
if not "%FLOWVPN_PACKAGE_COUNT%"=="1" (
  echo Expected exactly one FlowVPN release MSIX beside this installer.
  pause
  exit /b 1
)
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-FlowVPN.ps1" -PackagePath "%FLOWVPN_PACKAGE%" -CertificatePath "%~dp0FlowVPN.cer" -ExpectedCertificateSha256 "%FLOWVPN_CERTIFICATE_SHA256%"
set "FLOWVPN_RESULT=%ERRORLEVEL%"
if not "%FLOWVPN_RESULT%"=="0" echo FlowVPN installation was not completed. See the message above.
pause
exit /b %FLOWVPN_RESULT%
