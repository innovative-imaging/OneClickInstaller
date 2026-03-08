Set WshShell = CreateObject("WScript.Shell")
' The "0" hides the command prompt window. 
' The "True" tells the script to wait (block) until the batch file finishes.
WshShell.Run chr(34) & "D:\GVP-Pro\App\StartGVPProcesses.bat" & Chr(34), 0, True