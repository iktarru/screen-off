---
name: screen-off
description: Погасить экран ноутбука или включить его обратно (ночной режим). Turn the laptop screen off or back on without letting the machine sleep. Use when the user asks to dim, blank or restore the display - "погаси экран", "выключи экран", "выключи дисплей", "включи экран", "верни экран", "screen off", "display off", "night mode" - or asks whether the screen is currently off.
---

# Night screen mode / Ночной режим экрана

Dims the laptop panel **without putting the computer to sleep**: it keeps running and
stays reachable over the network.

Затемняет экран ноутбука, **не усыпляя компьютер**: он продолжает работать и остаётся
доступен по сети.

**ALWAYS check the current state first.** The program is a toggle: the same call both
dims and restores. Running it blindly on "turn the screen back on" will dim it again if
the mode was already off — the user may have exited on their own via `Ctrl+Alt+D` or `Esc`×3.

**ВСЕГДА сначала проверяй состояние** — программа переключатель, и запуск вслепую на
просьбу «включи экран» погасит его повторно.

## Check state / Проверить состояние

```bash
powershell -NoProfile -Command "if (Get-Process DisplayOff -ErrorAction SilentlyContinue) { 'ON: night mode active' } else { 'OFF: normal' }"
```

## Toggle / Погасить или вернуть экран

Same call both ways — but only after the check above confirms the state is the opposite
of what is wanted.

```bash
powershell -NoProfile -Command "Start-Process -FilePath \"$env:USERPROFILE\Tools\DisplayOff.exe\" -ArgumentList '0'"
```

`0` is the delay in ms before dimming. The desktop shortcut passes 700 so that releasing
`Ctrl+Alt+D` does not wake the screen immediately; from a terminal no delay is needed.

Exiting takes 1–2 seconds. To wait for it:
`powershell -NoProfile -Command "Wait-Process -Name DisplayOff -Timeout 30 -EA SilentlyContinue"`

## Two modes / Два режима

- **`/soft` (default)** — brightness 0 plus a black full-screen window, and the system is
  told to keep both itself and the display awake (`ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED`).
  Panel power is left alone.
- **`/hard`** — the panel is powered off and re-powered-off every 120 ms. Darker, but on a
  Modern Standby machine it **locks the session and demands a password**: a blanked panel
  drags the system into S0 standby while `CONSOLELOCK` is 1. Use only when explicitly asked.

## How the user exits without a terminal / Выходы без терминала

- `Ctrl+Alt+D`
- `Esc` three times within 2 seconds
- automatically after 10 hours (`/timeout:SECONDS` changes this)

## If the process hangs / Если процесс завис

```bash
powershell -NoProfile -Command "Stop-Process -Name DisplayOff -Force; Start-Sleep -Milliseconds 400; (Get-WmiObject -Namespace root/wmi -Class WmiMonitorBrightnessMethods).WmiSetBrightness(0,100)"
```

The second half is required: after a forced stop the brightness stays at 0.
После аварийной остановки яркость останется 0 — вернуть её нужно вручную.

## Diagnostics / Диагностика

- Log: `%USERPROFILE%\Tools\displayoff.log`
- Current brightness:
  `powershell -NoProfile -Command "(Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightness).CurrentBrightness"`

## What to tell the user / Что сказать пользователю

Keep it short: dimmed / restored, plus a reminder that `Ctrl+Alt+D` and `Esc`×3 work on
the laptop itself. Отвечать пользователю по-русски.

## Sources / Исходники

- `%USERPROFILE%\Tools\DisplayOff.cs`
- Rebuild / пересборка:

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" -nologo -target:winexe -optimize+ `
  -r:System.Management.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll `
  "-out:$env:USERPROFILE\Tools\DisplayOff.exe" "$env:USERPROFILE\Tools\DisplayOff.cs"
```
