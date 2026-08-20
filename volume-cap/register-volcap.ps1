# Register VolCap as an at-logon task for the device user (pass -User).
# Second trigger repeats every 30 min as SELF-HEAL: if the process ever dies,
# the scheduler relaunches it (mutex makes extra starts no-ops).
# volcap.exe MUST be built /target:winexe — a console build pops a closable
# window on her touchscreen (that's how v1 died: 0xC000013A, window tapped shut).
param([Parameter(Mandatory=$true)][string]$User)
$a  = New-ScheduledTaskAction -Execute 'C:\Users\Public\Documents\volcap.exe'
$t1 = New-ScheduledTaskTrigger -AtLogOn -User $User
$t2 = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
      -RepetitionInterval (New-TimeSpan -Minutes 30) -RepetitionDuration (New-TimeSpan -Days 3650)
$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
     -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
$s.ExecutionTimeLimit = 'PT0S'
Register-ScheduledTask -TaskName 'VolCap' -Action $a -Trigger $t1,$t2 -Settings $s -User $User -Force
Start-ScheduledTask -TaskName 'VolCap'
Start-Sleep -Seconds 2
(Get-ScheduledTask -TaskName 'VolCap').State
