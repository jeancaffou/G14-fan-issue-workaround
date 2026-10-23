# Test results

GA401IV, BIOS 222, Windows 10 x64. Measurements: 23 October 2026.

## Hardware

| Test | Result |
| --- | --- |
| Fault baseline | Approximately 7,800/7,400 RPM; Windows responsive |
| Factory profiles and automatic-control commands | No reduction in speed |
| Six ASUS services stopped | Fault persisted |
| Factory curves restored | Fault persisted |
| Diagnostic duty at 50% | Approximately 4,800/4,400 RPM |
| Firmware automatic mode | Full-speed behavior returned |
| Temperature control at 15% duty | Approximately 1,900/1,700 RPM; CPU 57 °C, GPU 51 °C, ACPI 52.1 °C; ASUS Silent, CPU boost disabled |
| Coordinated stop | Automatic-control call completed; worker and guardian exited |
| Worker termination | Guardian completed fallback and exited; task restarted successfully |
| Installation | SYSTEM task, startup trigger, battery operation, protected directories |

ASUS diagnostic library: version 1.1.4.0, valid Authenticode signature, SHA-256 `403BCC9882F278FD0DA9BA9C31A75DA98ED65AC15F26025ACC7DD44DA09AC34E`.

The worker-termination test covered the guardian implementation. High-temperature monitoring and the zero-duty branch were subsequently covered by self-tests and an idle run. ACPI remained at 50.1 °C during the idle run, keeping duty above zero.

## Self-tests

`Build.ps1` runs `--self-test` without accessing the fan or sensor APIs:

- Temperature thresholds and monotonicity across 10–120 °C.
- Zero duty below 50 °C and 15–100% duty above that threshold.
- Immediate increases and five-point decreases.
- Startup and restart boost to at least 50% duty.
- CPU/ACPI/GPU high-temperature thresholds.
- NaN rejection.
- RPM validation during stop, coast-down, spin-up, and stall.

## Untested

- Physical zero-RPM operation and restart from rest.
- Boot startup, suspend/resume, hibernation, and shutdown ordering.
- Sustained CPU/GPU load.
- GPU mode transitions.
- Native driver hangs or simultaneous failure of both processes.
- Other hardware, BIOS/driver versions, and Windows releases.
- Long-term reliability.
