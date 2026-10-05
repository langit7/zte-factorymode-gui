# ZTE Factory Mode GUI

Windows and Linux GUI for enabling Telnet on supported ZTE GPON ONU/ONT devices. Native C# / Avalonia port of [zte_modem_tools](https://github.com/langit7/zte_modem_tools); Python is not required.

## Supported functions

- Open or close temporary factory-mode Telnet and display verified login credentials.
- Save permanent Telnet settings, then restart the service or reboot the modem.
- Open or close serial access (`/proc/serial`).
- Automatically detect three factory protocol generations; support `rerand34` and `rerand22` MAC proofs and version61/time authentication.
- Use manual factory credentials or try built-in known credentials; detect the client MAC or accept an explicit MAC/interface.

Devices/firmware reported by the Python project:

| Device | Firmware |
| --- | --- |
| F6600P | `V9.0.10P5N23`, `V9.0.10P6N33B` |
| F6201B | `V9.3.10P4N3` |
| F670L | `V9.0.11P` |

GUI compatibility depends on firmware. Hardcode-file decryption is available in the Python project only.

## How to use

1. Download your Windows or Linux package from [Releases](https://github.com/langit7/zte-factorymode-gui/releases/latest) and extract it.
2. Windows: run `ZteModemGui.exe`. Linux: run `chmod +x ZteModemGui` and `./ZteModemGui` in a graphical desktop session. Release packages include .NET.
3. Enter the modem IPv4 address (default `192.168.1.1`), HTTP port (`80`) and Telnet port (`23`).
4. Enter the factory username/password, for example `admin` / `admin` if that is your modem's login. Alternatively, check **Use known usernames/passwords**.
5. Select **telnet**, **open**, **Temporary**, then click **Run**. Verified Telnet credentials appear below the log. Passwords are visible.

To close Telnet, select **close** with **Temporary**. For serial control, select **serial** and **open** or **close**.

For permanent Telnet, select **Permanent · restart service** or **Permanent · reboot**. Permanent credentials default to `root` / `Zte521`. Firmware region `198` is required; check **Set region 198** only when you intend to change it. Some firmware accepts permanent login but denies shell commands.

If authentication fails, use your working manual credentials and the client MAC actually seen by the modem. Leave MAC/interface blank for automatic detection. Try `rerand22` for older method-3 firmware; enable **Version61 / time auth** only when required. **HTTP diagnostics** shows request status and byte counts.

## Build from source

Requires .NET 10 SDK:

```sh
dotnet run --project ZteModemGui.csproj
```

## License

[GNU AGPL v3](LICENSE). Based on [langit7/zte_modem_tools](https://github.com/langit7/zte_modem_tools) and [douniwan5788/zte_modem_tools](https://github.com/douniwan5788/zte_modem_tools).
