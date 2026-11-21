Set UAC = CreateObject("Shell.Application")
Set fso = CreateObject("Scripting.FileSystemObject")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
batPath = fso.BuildPath(scriptDir, "stage-nvidia-rates.bat")
UAC.ShellExecute "cmd.exe", "/c """ & batPath & """", "", "runas", 1
