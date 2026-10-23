[CmdletBinding()]
param([ValidateSet('Trial','EnableStartup','Stop')][string]$Mode = 'Trial')
$ErrorActionPreference = 'Stop'
$taskName = 'G14FanGuard'
$installPath = Join-Path $env:ProgramFiles 'G14FanGuard'
$dataPath = Join-Path $env:ProgramData 'G14FanGuard'
$taskPrincipalCheck = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $taskPrincipalCheck.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator token required.' }
$model = (Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS').SystemProductName
if ($model -notmatch '^ROG Zephyrus G14 GA401IV(?:_|$)') { throw 'Unsupported model. ASUS ROG Zephyrus G14 GA401IV required.' }
if ($Mode -eq 'Stop') {
    if (-not (Test-Path -LiteralPath $dataPath)) { return }
    Set-Content -LiteralPath (Join-Path $dataPath 'stop.request') -Value 'stop'
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($null -eq $task -or $task.State -ne 'Running') {
            if ($null -ne $task) { Disable-ScheduledTask -TaskName $taskName | Out-Null }
            'Controller stopped. Scheduled startup disabled.'
            return
        }
        Start-Sleep -Seconds 1
    }
    throw 'The controller has not completed its safe stop. No forced termination was attempted.'
}
function Protect-Folder([string]$Path) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl',$inheritance,$propagation,'Allow')
        $acl.AddAccessRule($rule)
    }
    $readRule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')),'ReadAndExecute',$inheritance,$propagation,'Allow')
    $acl.AddAccessRule($readRule)
    Set-Acl -LiteralPath $Path -AclObject $acl
}
Protect-Folder $installPath
Protect-Folder $dataPath
$serviceExe = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\ASUSSystemAnalysis').ImagePath.Trim('"')
$sourceLibrary = Join-Path (Split-Path $serviceExe) 'AsusWinIO64.dll'
if ((Get-AuthenticodeSignature -LiteralPath $sourceLibrary).Status -ne 'Valid') { throw 'Installed ASUS library signature is invalid.' }
foreach ($copy in @(
    @{Source=(Join-Path $PSScriptRoot 'G14FanGuard.exe');Name='G14FanGuard.exe'},
    @{Source=$sourceLibrary;Name='AsusWinIO64.dll'}
)) {
    $destination = Join-Path $installPath $copy.Name
    if (-not (Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $copy.Source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        Copy-Item -LiteralPath $copy.Source -Destination $destination
    }
}
Remove-Item -LiteralPath (Join-Path $dataPath 'stop.request') -ErrorAction SilentlyContinue
$action = New-ScheduledTaskAction -Execute (Join-Path $installPath 'G14FanGuard.exe') -WorkingDirectory $installPath
$principal = New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval ([TimeSpan]::FromMinutes(1))
$arguments = @{TaskName=$taskName;Action=$action;Principal=$principal;Settings=$settings;Force=$true;Description='GA401IV temperature-controlled fan workaround with paired heartbeat watchdogs; restores firmware cooling on sensor failure.'}
if ($Mode -eq 'EnableStartup') { $arguments.Trigger = New-ScheduledTaskTrigger -AtStartup }
Register-ScheduledTask @arguments | Out-Null
$task = Get-ScheduledTask -TaskName $taskName
if ($task.State -ne 'Running') { Start-ScheduledTask -TaskName $taskName }
[pscustomobject]@{
    Time=(Get-Date).ToString('o');Mode=$Mode;TaskName=$taskName;Principal=$task.Principal.UserId
    TriggerCount=@($task.Triggers).Count;TaskState=(Get-ScheduledTask -TaskName $taskName).State.ToString()
    Executable=(Join-Path $installPath 'G14FanGuard.exe');ExecutableSha256=(Get-FileHash (Join-Path $installPath 'G14FanGuard.exe')).Hash
    LibrarySha256=(Get-FileHash (Join-Path $installPath 'AsusWinIO64.dll')).Hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataPath 'installation.json') -Encoding UTF8
"G14FanGuard started; mode: $Mode. Status: $(Join-Path $dataPath 'status.json')"
