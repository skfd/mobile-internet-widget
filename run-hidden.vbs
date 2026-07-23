' Launches the collector wrapper with no console window. The scheduled task
' points wscript.exe at this file instead of powershell.exe directly, because
' powershell.exe is a console app and briefly flashes a window every run even
' with -WindowStyle Hidden. WScript.Shell.Run with intWindowStyle=0 starts it
' fully hidden. Arg 1 = provider (default "fido").
Dim provider
provider = "fido"
If WScript.Arguments.Count > 0 Then provider = WScript.Arguments(0)

Dim base
base = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)

CreateObject("WScript.Shell").Run _
  "powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & base & "\run-collector-attach.ps1"" -Provider " & provider & " -Headless", _
  0, False
