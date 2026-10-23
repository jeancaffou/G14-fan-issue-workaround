# Setup and troubleshooting

## Requirements

| Component | Configuration |
| --- | --- |
| Laptop | ASUS ROG Zephyrus G14 GA401IV, 2020 |
| Tested BIOS | GA401IV.222 |
| Tested OS | Windows 10 Pro 22H2, build 19045.6456, x64 |
| Tested GPU | NVIDIA RTX 2060 Max-Q |
| Tested ASUS System Control Interface | 3.1.72.0 |
| Tested ASUS diagnostic library | AsusWinIO64.dll 1.1.4.0 |
| Runtime | .NET Framework 4.x with System.Management |

The installer locates `AsusWinIO64.dll` through the `ASUSSystemAnalysis` service, checks its Authenticode signature, and copies it into the application directory. Existing ASUS services and drivers remain installed.

The controller requires two diagnostic fans, NVIDIA NVML at `C:\Windows\System32\nvml.dll`, and ACPI thermal-zone data. Other models, alternate Windows locations, and GPU-disabled configurations are unsupported.

## Build and install

```powershell
git clone https://github.com/jeancaffou/G14-fan-issue-workaround.git
cd G14-fan-issue-workaround
.\Build.ps1
```

The build uses the 64-bit .NET Framework C# compiler and runs `--self-test`. Visual Studio and NuGet are unnecessary. Generated binaries are excluded from Git.

If PowerShell blocks local scripts, review them and set the policy for the current terminal:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
```

Open administrator PowerShell in the repository directory:

```powershell
.\Manage-G14FanGuard.ps1 -Mode Trial
```

Files install under `%ProgramFiles%\G14FanGuard`. The `G14FanGuard` scheduled task runs as SYSTEM, which the ASUS diagnostic interface requires on the tested driver. Trial mode runs until stopped or Windows shuts down, without a startup trigger. The task allows battery operation and three retries at one-minute intervals after failure.

Check the current state:

```powershell
Get-Content "$env:ProgramData\G14FanGuard\status.json"
Get-Content "$env:ProgramData\G14FanGuard\controller.log" -Tail 10
```

The timestamp should advance, the mode should be `TemperatureControlWorkaround`, and sensor readings and fan speeds should be plausible. Enable startup with:

```powershell
.\Manage-G14FanGuard.ps1 -Mode EnableStartup
```

## Stop, update, or uninstall

Run in administrator PowerShell:

```powershell
.\Manage-G14FanGuard.ps1 -Mode Stop
```

Alternatively, right-click `Stop-G14FanGuard.cmd` and select **Run as administrator**. Shutdown waits up to 20 seconds for both processes and disables the task. Firmware automatic control resumes during cleanup; the full-speed fault may return.

For updates, stop the controller, build the new source, then run `Trial` or `EnableStartup`. Resume a stopped installation using either mode.

For removal, complete the stop and confirm no controller process remains. Then remove the task and application directories:

```powershell
Unregister-ScheduledTask -TaskName G14FanGuard -Confirm:$false
Remove-Item -LiteralPath "$env:ProgramFiles\G14FanGuard" -Recurse
Remove-Item -LiteralPath "$env:ProgramData\G14FanGuard" -Recurse
```

## Control loop

Both fans follow the highest temperature from the ASUS CPU sensor, NVIDIA NVML, and ACPI thermal zones. The cycle includes a two-second delay plus sensor-query time, approximately three seconds in testing. The [fan curve](../README.md#fan-curve) specifies electrical PWM duty, not a percentage of maximum RPM.

Below 50 °C, PWM goes directly to zero. Restarting uses at least 50% duty, with two cycles for spin-up. Other increases are immediate; decreases are limited to five percentage points per cycle. After spin-up, a running fan must report at least 500 RPM. Zero RPM is valid at zero duty; negative readings and readings above 15,000 RPM cause fallback.

Diagnostic mode remains enabled at zero PWM. Disabling diagnostic mode restores firmware control. At CPU/ACPI 90 °C or GPU 85 °C, firmware cooling resumes while monitoring continues. The controller resumes its curve when temperatures fall.

Missing or invalid sensor data causes fallback and exit. The guardian monitors the worker heartbeat; the worker monitors the guardian heartbeat. Fallback requires functioning ASUS driver calls. Stale but plausible sensor readings, driver hangs, OS crashes, and simultaneous process failure can prevent effective recovery.

## Runtime files

| File under `%ProgramData%\G14FanGuard` | Contents |
| --- | --- |
| `status.json` | Current mode, timestamp, temperatures, duty, and fan speeds |
| `controller.log` | Periodic readings and fallback events |
| `installation.json` | Installed hashes and task settings |
| `*.heartbeat` | Worker and guardian liveness |
| `stop.request` | Shutdown flag |

Two processes are normal. Application and data directories allow writes only by administrators and SYSTEM. Periodic logs stop at approximately 5 MB; event messages can still append. Status and heartbeats continue updating. Retry a status read if it overlaps a file write.

| Status mode | Meaning |
| --- | --- |
| `TemperatureControlWorkaround` | Temperature-based PWM control |
| `FirmwareCoolingHighTemperature` | Firmware cooling during high temperature |
| `AutomaticFallback` | Worker error |
| `AutomaticControlRestored` | Automatic-control API call completed |

The native restoration call has no detailed hardware acknowledgment. Check log errors and current timestamps when diagnosing a failure. Dependencies include the signed ASUS library, its driver, NVIDIA NVML, and ACPI temperature data. See [test results](validation.md) for coverage.
