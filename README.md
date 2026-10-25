# DualNet

**Use two internet connections at the same time on Windows** — e.g. LAN on one modem and Wi-Fi on another.

[راهنمای فارسی ←](README-fa.md)

DualNet is a small local proxy (SOCKS5 + HTTP on one port). Every new connection is sent out through one of your network adapters, so downloads that open several connections (IDM, torrents, browsers) get close to the **sum** of both lines. If one line drops, new connections go through the other.

- **Priority mode** — pick a cheap line to use first; the other line only takes new connections when the first is full (80% of its speed) or down. Good for cheap ADSL + metered 5G.
- **Fast mode** — spread connections over all lines.
- **Data cap** — optional monthly or daily cap per line, enforced strictly.
- **Live dashboard** at `http://127.0.0.1:1080/` (Persian, right-to-left, Vazirmatn font built in).
- Single small `.exe`, no install, no admin rights. Builds with the C# compiler that ships with Windows.

## Download and run

1. Download `DualNet.exe` from [Releases](../../releases) and put it in a folder together with the `.bat` files from this repo (or clone the repo and run `build.bat`).
2. Connect both networks (e.g. LAN cable + Wi-Fi).
3. Run `start.bat`. It turns on the Windows proxy while DualNet runs and turns it off when you close the window. Use `start-proxy-only.bat` if you want to set the proxy per app (for example when chaining with a VPN client).
4. In the dashboard, click **اولویت با این** ("make this first") on the line that should be used first, and optionally set its line speed.

If DualNet was killed and websites stop loading, run `fix-internet.bat`.

## Measured example

| | Speed |
|---|---|
| LAN (ADSL) alone | 15.1 Mbps |
| Wi-Fi (5G hotspot) alone | 45.9 Mbps |
| Both through DualNet | **60.5 Mbps** |

## Limits

- One single connection (one video stream, a single-connection download) always uses one line. Combining lines for a single connection needs a server on the other end.
- TCP only. UDP (games, QUIC-based VPNs, WireGuard) does not go through DualNet.
- Only apps that use a proxy are affected.

## Options

Run `DualNet.exe --help` for all options: `--prefer NAME`, `--speed NAME=MBPS`, `--limit NAME=10GB`, `--limit-period day`, `--fast`, `--sticky`, `--weight NAME=N`, `--exclude NAME`, `--only NAME`, `--port N`, `--lan`, `--system-proxy`, `--clear-proxy`.

## Build from source

```bat
build.bat
```

Needs only Windows (uses `csc.exe` from .NET Framework 4.x). Source is in `src/`.

## License

MIT — see [LICENSE](LICENSE). The Vazirmatn font is under the SIL Open Font License (`src/fonts/OFL.txt`).
