# G14 Fan Guard

A Windows fan-control workaround for the **ASUS ROG Zephyrus G14 GA401IV (2020)** when its fans randomly stick at full speed despite low temperatures.

It controls both fans through the installed ASUS diagnostic interface, using CPU, GPU, and ACPI temperatures. It smooths speed changes, stops fans below 50 °C, and restarts them at 55 °C. Normal duty is limited to 50%; firmware emergency cooling can override that limit. It does not modify firmware.

A guardian restarts the controller after temporary failures. Suspend/resume handling releases diagnostic control before sleep and reconnects after waking. The controller can start automatically with Windows.

## Requirements

- ASUS GA401IV; the installer rejects other models.
- Windows x64 and .NET Framework 4.x.
- ASUS System Control Interface and ASUS System Analysis, including `AsusWinIO64.dll`.
- NVIDIA drivers with NVML and readable temperature sensors.

ASUS libraries and drivers are not included.

## Build

Run Windows PowerShell in the repository directory:

```powershell
.\Build.ps1
```

## Install

From **administrator PowerShell** in the same directory:

```powershell
.\Manage-G14FanGuard.ps1 -Mode EnableStartup
```

Use `-Mode Trial` instead to run without automatic startup. Installation uses a SYSTEM scheduled task and stores the application under `%ProgramFiles%\G14FanGuard`.

## Stop

Run in administrator PowerShell:

```powershell
.\Manage-G14FanGuard.ps1 -Mode Stop
```

This restores firmware control and disables startup. The original full-speed behavior may return under firmware control.

## Status

```powershell
Get-Content "$env:ProgramData\G14FanGuard\status.json"
```

The file contains the current temperatures, duty, and fan speeds. Runtime logs are stored in the same directory.
