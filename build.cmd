@echo off
rem Builds PodBattery.exe with the C# compiler built into Windows (.NET Framework 4.x).
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:"%~dp0PodBattery.exe" /win32icon:"%~dp0PodBattery.ico" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.WindowsRuntime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Devices.winmd" /r:"%WM%\Windows.Storage.winmd" ^
  "%~dp0PodBattery.cs"
