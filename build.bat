@echo off
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /codepage:65001 /optimize+ /target:winexe /out:"%~dp0KoEnShow.exe" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:"%FW%\WPF\UIAutomationClient.dll" /r:"%FW%\WPF\UIAutomationTypes.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  "%~dp0KoEnShow.cs"
