Set UAC = CreateObject("Shell.Application")
Set fso = CreateObject("Scripting.FileSystemObject")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
ps1Path = fso.BuildPath(scriptDir, "remove-override.ps1")
UAC.ShellExecute "powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File """ & ps1Path & """", "", "runas", 1
