$Out = "$env:USERPROFILE\Desktop\Installed_Software_Name_Version.txt"

$paths = @(
  "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
  "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
  "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*"
)

Get-ItemProperty $paths -ErrorAction SilentlyContinue |
Where-Object { $_.DisplayName } |
Select-Object DisplayName, DisplayVersion |
Sort-Object DisplayName |
ForEach-Object {
    "{0} | {1}" -f $_.DisplayName, ($_.DisplayVersion -as [string])
} |
Set-Content -Encoding UTF8 $Out

"Saved to: $Out"
