# Firmware analysis

The full-speed condition persists under automatic control and clears under diagnostic PWM control. Its root cause is unresolved.

## ACPI

The running DSDT was extracted through Windows firmware-table APIs and disassembled with ACPICA iASL. SHA-256: `14CC64E7A4F0664DDC55E0B567C716FC30BF8E8398854E1DF80C24D0C52587E4`. Six external methods remained unresolved.

The ASUS `DEVS` fan-control branches return 1 without checking the result of the internal EC write. A successful host response therefore does not establish that the controller changed state. Factory profile selection, automatic-fan commands, and factory-curve restoration had no effect during the fault.

The ACPI thermal method can retain a previous temperature after an out-of-range EC reading. The DSDT describes host interfaces; the running EC flash contents have not been captured.

## BIOS comparison

The [ASUS GA401IV support page](https://www.asus.com/us/supportonly/ga401iv/helpdesk_bios/) listed BIOS 222 as the latest release on 23 October 2026. Official EZ Flash archives for 220, 221, and 222 matched their published SHA-256 hashes and were unpacked with UEFIExtract A75.

Four host modules have identical executable bodies across those releases:

- `AsusEcDxeBs`
- `AsusEcDxeRt`
- `AsusEcDxeSmm`
- `AsusEcSmmCallback`

The DSDT changes from 221 to 222 add two status-query branches. The fan-control branches are unchanged.

UEFIExtract identifies an AMI Aptio signed capsule. A modified image would require a valid update and recovery path. Parser limitations include non-UEFI data, a VTF inside compressed data, and disabled AMD image parsing in A75.

## Embedded-controller region

A 128 KiB region at capsule offsets `0x31800`–`0x517FF` contains `ITE8226-EC-V0.00` and ASUS project/version strings. Its format and identifiers indicate an EC payload; it has not been matched against the running controller.

| BIOS | Version string | Region SHA-256 |
| --- | --- | --- |
| 220 | `FG040E04.GA401IV.30F` | `1dd92026f52558f3f7845703ee59b28387a2af6eff824d96fc54a868edbdfdc5` |
| 221 | `FG040E05.GA401IV.310` | `a908d4dc9195642b963c87ceaa6d2e7262fec7385ccd3eb3039c44ba5f8fcc46` |
| 222 | `FG040E05.GA401IV.310` | `a908d4dc9195642b963c87ceaa6d2e7262fec7385ccd3eb3039c44ba5f8fcc46` |

The entire region is byte-identical between 221 and 222. Version 220 differs. No change has been tied to the full-speed fault.

| Official archive | SHA-256 |
| --- | --- |
| GA401IVAS220.zip | `CA58B26EBFA9C5728339E9139FB810F4D3C39035677D04198434648BBF29BDB3` |
| GA401IVAS221.zip | `9EB37471A7D77275F65F9FA2865C1EE7DF7F8531351DE7A6BA49A7FD7EF9A411` |
| GA401IVAS222.zip | `EE663F947DC6257769A2C4D23C82B3DE797C575E5BF98BFC6620D7908CD69C48` |

## References

- [AsusFanControl](https://github.com/Karmel0x/AsusFanControl): ASUS diagnostic API.
- [atrofac](https://github.com/cronosun/atrofac), [G14Manager](https://github.com/zllovesuki/G14Manager), [G-Helper](https://github.com/seerge/g-helper): ASUS host controls.
- [Linux ASUS WMI driver](https://github.com/torvalds/linux/blob/v6.12/drivers/platform/x86/asus-wmi.c): fan-control endpoints.
- [zephyrusfanfix-24-25](https://github.com/Undervoltologist/zephyrusfanfix-24-25): runtime EC control for different, 2024–2025 hardware.
