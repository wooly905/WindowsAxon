---
name: windows-update-stale
description: Fix a Windows Update Settings page that shows old status, such as pending download for updates that are already installed or an old last checked date. Use for windows update stuck, pending download, last checked, update status wrong.
metadata:
  tools: "script_create script_run ui_list_windows ui_close_window settings_open ui_screenshot"
---
# When to use
The Windows Update page in Settings shows updates as pending, or an old "Last checked" date, but the updates are installed.

# Background
The Settings page does not read the Windows Update Agent directly. It reads the Update Orchestrator (UsoSvc) state store, C:\ProgramData\USOPrivate\UpdateStore\store.db. If that store is old, the page stays old after the updates install.
`UsoClient StartScan` is a background scan and often does not refresh the store. `UsoClient StartInteractiveScan` is the same as the "Check for updates" button. It refreshes the store and does not need administrator rights.

# Steps
1. Run a read-only PowerShell script (script_create, then script_run). It prints: Get-HotFix for the KB numbers, the last 10 entries of Microsoft.Update.Session QueryHistory with ResultCode (2 = succeeded), Microsoft.Update.AutoUpdate Results.LastSearchSuccessDate, and the LastWriteTime of store.db.
2. If the history shows the updates failed or are missing, stop. Tell the user the updates are not installed. This skill does not apply.
3. Run ui_list_windows. Close each Settings window with ui_close_window, so that it does not keep the old page.
4. Run a PowerShell script with elevated false. It saves the store.db LastWriteTime, runs `UsoClient StartInteractiveScan`, then checks store.db every 5 seconds for up to 90 seconds. It prints the old and new LastWriteTime and the newest Microsoft-Windows-WindowsUpdateClient/Operational events.
5. If store.db LastWriteTime did not change, run an elevated script. It prints the UsoSvc process ID, runs Restart-Service UsoSvc -Force, prints the new process ID, then repeats step 4. If the process ID did not change, report that the restart failed.
6. Run settings_open {"page": "windowsupdate"}, then ui_screenshot. Check that "Last checked" shows today.
7. Tell the user the cause (an old UsoSvc state store), what you refreshed, and the new "Last checked" time. Name any new updates that the scan found. Suggest a restart only if step 5 failed.

# Example
User: windows update says 2 updates are pending download but they are installed
Step: {"thought":"Compare install history with the Settings store first","action":"script_create","args":{"language":"powershell","task":"Read-only: print Get-HotFix, the last 10 Windows Update history entries with ResultCode, AutoUpdate LastSearchSuccessDate, and the LastWriteTime of C:\\ProgramData\\USOPrivate\\UpdateStore\\store.db."}}
