# screen-off

*[Русская версия](README.ru.md)*

A night screen mode for Windows laptops — plus a [Claude Code](https://claude.com/claude-code)
skill, so you can trigger it with `/screen-off` from any conversation, including from your phone.

Why: the laptop stays on overnight (remote access, long builds, whatever), and the glowing
panel keeps someone awake. Many laptops have no hardware key for blanking just the screen —
that `Fn` combo is far from universal.

## What it does

Dims the screen **without putting the computer to sleep**: it keeps running and stays
reachable over the network.

By default (`/soft`):

- panel brightness is set to 0 (the original value is remembered and restored on exit);
- a black full-screen window is shown on top, and the cursor is hidden;
- the system is told not to sleep and not to blank the display itself
  (`ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED`).

Panel power is deliberately left alone — see [Why not power-off](#why-not-power-off).

## Tested on

ThinkPad 21A0 (T14 Gen 2), Windows 10 Pro 19045, .NET Framework 4.8. Should work on any
Windows laptop whose brightness is controllable through WMI; if it is not, the mode simply
leaves brightness untouched and the black window still covers the screen.

## Install

Requires Windows with .NET Framework 4.x (present by default). The compiler, `csc.exe`,
comes from there too — nothing to install.

```powershell
git clone https://github.com/iktarru/screen-off.git
cd screen-off
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

`install.ps1` builds `DisplayOff.exe` into `%USERPROFILE%\Tools`, puts the skill into
`%USERPROFILE%\.claude\skills\screen-off\`, and creates a `Display Off` desktop shortcut
with the `Ctrl+Alt+D` hotkey. No administrator rights needed.

**There is no prebuilt binary in this repository, on purpose.** An unsigned executable from
a stranger is a trust problem; building from source takes two seconds and you can read
exactly what you are running first.

## Usage

| Action | How |
|---|---|
| Dim the screen | `Ctrl+Alt+D`, the desktop shortcut, or `/screen-off` in Claude Code |
| Restore it | the same thing — it is a toggle |
| Panic exit | `Esc` three times within 2 seconds |
| Gives up on its own | after 10 hours (`/timeout:SECONDS` changes this) |

The Claude Code skill also understands plain requests in Russian or English — handy when
you are on your phone and the laptop is in another room.

### Arguments

```
DisplayOff.exe [delay_ms] [/timeout:SECONDS] [/soft|/hard]
```

- **delay_ms** — pause before dimming, 700 by default. Needed when launching from the
  keyboard: without it, releasing `Ctrl+Alt+D` wakes the screen right back up. Pass `0`
  from a terminal.
- **/timeout:SECONDS** — safety net, 10 hours by default.
- **/hard** — the old behaviour: panel power is switched off and re-applied every 120 ms.
  Darker, but see below.

## Why not power-off

The obvious way to blank a screen is `WM_SYSCOMMAND / SC_MONITORPOWER`. On machines with
**Modern Standby (S0 Low Power Idle)** that ends badly:

- Windows treats a blanked panel as a reason to drop into connected standby;
- with `CONSOLELOCK = 1` in the power scheme the session gets locked, so coming back needs
  a password;
- and any touch of the touchpad wakes the panel anyway — with exactly the flash of light
  you were trying to avoid.

That wake-up cannot be blocked from inside Windows: the panel comes back in the input
driver stack, before the event reaches any program. This was tested literally — global
hooks swallowing all mouse and keyboard input do not remove the flashes. So `/soft` never
powers the panel down: nothing to blank, nothing to wake, no flashes.

Check your own machine:

```powershell
powercfg /a                                             # is S0 Low Power Idle listed
powercfg -attributes SUB_NONE CONSOLELOCK -ATTRIB_HIDE  # unhide the setting (needs admin)
powercfg /q SCHEME_CURRENT SUB_NONE                     # CONSOLELOCK: 1 = asks for a password
```

Turn the password prompt off (administrator rights required):

```powershell
powercfg /SETACVALUEINDEX SCHEME_CURRENT SUB_NONE CONSOLELOCK 0
powercfg /SETDCVALUEINDEX SCHEME_CURRENT SUB_NONE CONSOLELOCK 0
powercfg /S SCHEME_CURRENT
```

## Known limitations

- **Keyboard backlight is not switched off.** On ThinkPads it is driven by the embedded
  controller: neither Lenovo WMI, nor HID Lighting, nor the BIOS interface exposes it, and
  `Fn` never reaches Windows, so it cannot be emulated either. Turn it off by hand with
  `Fn+Space`.
- Brightness is changed through WMI (`WmiMonitorBrightnessMethods`). On a laptop that does
  not support it the mode leaves brightness alone and relies on the black window.
- WMI can apply a brightness change and then hang for a minute without returning, so every
  such call runs on its own thread with a timeout, and the restore is verified by reading
  the value back, retrying up to three times.

## Emergency stop

If the process ever gets stuck:

```powershell
Stop-Process -Name DisplayOff -Force
(Get-WmiObject -Namespace root/wmi -Class WmiMonitorBrightnessMethods).WmiSetBrightness(0,100)
```

The second line matters: after a forced stop, brightness stays at 0.

A log of every activation and exit, with the reason for the exit, is written to
`%USERPROFILE%\Tools\displayoff.log`.

## Uninstall

```powershell
Remove-Item "$env:USERPROFILE\Tools\DisplayOff.exe", "$env:USERPROFILE\Tools\DisplayOff.cs", "$env:USERPROFILE\Tools\displayoff.log" -ErrorAction SilentlyContinue
Remove-Item "$env:USERPROFILE\.claude\skills\screen-off" -Recurse -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Display Off.lnk') -ErrorAction SilentlyContinue
```

## License

MIT, see [LICENSE](LICENSE).
