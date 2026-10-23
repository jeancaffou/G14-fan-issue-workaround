# G14 fan issue workaround

The **ASUS ROG Zephyrus G14 GA401IV (2020)** can randomly run both fans at full power for no obvious reason, despite low CPU/GPU temperatures and responsive Windows. The fans can stay near **7,800/7,400 RPM** until a forced power-off, ignoring normal fan-profile changes.

**G14 Fan Guard** controls fan speed through ASUS's diagnostic interface and runs automatically with Windows. It works around the problem without powering off. The underlying fault remains unresolved.

## Fan curve

Both fans use the highest CPU, GPU, or ACPI temperature.

| Temperature | PWM duty |
| --- | --- |
| Below 50 °C | 0% |
| 50–59 °C | 15% |
| 60–69 °C | 20% |
| 70–74 °C | 25% |
| 75–79 °C | 35% |
| 80–84 °C | 50% |
| 85–89 °C | 75% |
| 90 °C or above | 100% |

Fans restart with a minimum 50% duty. At CPU/ACPI 90 °C or GPU 85 °C, cooling returns to firmware control. Sensor errors and a worker/guardian watchdog also trigger fallback.

## Install

Requires Windows x64, .NET Framework 4.x, ASUS System Analysis with its diagnostic driver, NVIDIA NVML, and readable temperature sensors. The installer supports GA401IV only.

Build in Windows PowerShell:

```powershell
.\Build.ps1
```

Run a trial from **administrator PowerShell**:

```powershell
.\Manage-G14FanGuard.ps1 -Mode Trial
Get-Content "$env:ProgramData\G14FanGuard\status.json"
```

Enable automatic startup after checking live temperatures and fan speeds:

```powershell
.\Manage-G14FanGuard.ps1 -Mode EnableStartup
```

Stop and restore firmware control:

```powershell
.\Manage-G14FanGuard.ps1 -Mode Stop
```

Returning to firmware control can bring the full-speed fault back. Use the stop command instead of terminating both processes.

## Documentation

Tested on one GA401IV. Physical zero-RPM operation, restart from rest, sleep/resume, boot startup, and sustained load remain untested. Cooling depends on functioning sensors and the ASUS driver.

- [Setup and troubleshooting](docs/usage.md)
- [Test results](docs/validation.md)
- [Firmware analysis](docs/firmware-investigation.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)

ASUS diagnostic interface: [Karmel0x/AsusFanControl](https://github.com/Karmel0x/AsusFanControl/tree/76893a7389bcf79033fca5d290e4394abb4ef209). ASUS libraries and drivers are not included.
