Task Manager (Startup Tab)

Startup Folder

Press Win + R, type shell:startup & shell:common startup

Registry

Press Win + R, type regedit, Check these keys and remove entries:

HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run

HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Run

HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\RunOnce

HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\RunOnce

HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run

HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run

HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Active Setup\Installed Components

Scheduled Tasks

Press Win + R, type taskschd.msc, press Enter. Disable tasks in Task Scheduler Library.

System Configuration (msconfig)

Press Win + R, type msconfig. Go to Startup tab.

Group Policy (Windows Pro/Enterprise)

Press Win + R, type gpedit.msc. Computer Configuration -> Administrative Templates -> System -> Logon.

Services

Press Win + R, type services.msc. Change startup type to Manual or Disabled.

Windows Startup Scripts

Press Win + R, type gpedit.msc . Computer Configuration -> Windows Settings -> Scripts (Startup/Shutdown).

Winlogon

Check HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon for Userinit and Shell values.

BootExecute

Check HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\BootExecute.