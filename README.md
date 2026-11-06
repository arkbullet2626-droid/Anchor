<p align="center"><img src="assets/anchor-256.png" width="128" alt="Anchor logo"></p>

# Anchor

**Anchor by Opifex Studios** — your windows stay anchored where you put them.

A tiny, free Windows tray app that remembers where every window belongs (which monitor, position, size, maximized or not) and puts them back when Windows scrambles them: after sleep, a monitor turning off and on, or a game changing resolution.

> **Status: early test version.** Not released yet.

## What works so far

- **Puts windows back automatically** after sleep, waking the screen, unlocking, a monitor turning off and on, or a game changing the resolution. It waits until things are still, restores within a few seconds, and never fights a fullscreen game.
- Keeps a separate memory for each resolution, so a game's temporary low resolution never overwrites your normal layout.
- Remembers every window's monitor, position, size and state (normal, maximized, minimized, snapped) every few seconds.
- Knows *which* monitors are connected by their model and serial number, not their Windows number, and keeps a separate memory for each monitor setup.
- Tray menu: **Restore now**, **Save layout**, **Undo last restore**, **Pause**, **Quit**.
- Handles mixed scaling (per-monitor DPI), several windows of the same app, and windows that aren't open (skipped quietly).
- Windows of programs running as administrator can't be moved by a normal app; Anchor skips them and says so.

Coming next: settings, start with Windows, and an installer.

## Privacy

No account, no analytics. Everything Anchor saves stays in `%LOCALAPPDATA%\Opifex\Anchor` on your PC.

## Building

```
dotnet build Anchor.sln -c Release
dotnet tests/Anchor.Tests/bin/Release/net8.0/Anchor.Tests.dll
```

Every push is built and tested on Windows by GitHub Actions, including a test that moves real windows around and checks Anchor puts them back.

## Licence

MIT — see [LICENSE](LICENSE).
