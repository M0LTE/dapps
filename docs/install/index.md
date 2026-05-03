# Install

DAPPS ships as a single self-contained binary per platform — no .NET runtime install, no shared library dance. Drop it on disk, make it executable (Linux/macOS), and you're done.

The release matrix:

| Platform     | RID           | Binary                          |
|--------------|---------------|---------------------------------|
| Linux x86-64 | `linux-x64`   | `dapps-linux-x64`               |
| Linux ARM64  | `linux-arm64` | `dapps-linux-arm64`             |
| Linux ARM32  | `linux-arm`   | `dapps-linux-arm` (Pi, Cubie)   |
| Windows x64  | `win-x64`     | `dapps-win-x64.exe`             |
| macOS ARM64  | `osx-arm64`   | `dapps-osx-arm64`               |

[Latest release](https://github.com/M0LTE/dapps/releases/latest) on GitHub.

## Pick a platform

- [**Linux (systemd)**](linux.md) — recommended; long-running daemon with the supervised-update story baked in.
- [**Docker**](docker.md) — published image; useful if you already orchestrate with compose / a homelab stack.
- [**Windows**](windows.md) — runs as a console app today; service install is manual.
- [**macOS**](macos.md) — same shape as Windows; `launchd` plist optional.

## Compatibility notes

- **.NET 8 LTS** is the baseline runtime. Binaries are self-contained — you do not need .NET installed.
- **glibc 2.23 or newer** on Linux. Raspberry Pi OS Bullseye (glibc 2.31) and everything more recent works fine. We deliberately stayed on .NET 8 rather than 10 specifically to keep Pi OS Bullseye in scope.
- **Windows**: any 64-bit Windows 10 / Server 2016 or newer.
- **macOS**: Apple Silicon (M-series). Intel macs are not in the matrix; build from source if you need them.

## Storage and ports

A running DAPPS node writes:

- A SQLite database at `data/dapps.db` (relative to the working directory) or `/var/lib/dapps/dapps.db` if you use the systemd recipe.
- An MQTT broker on TCP 1883 (configurable; bind to localhost in most cases).
- An HTTP listener on TCP 5000 (configurable; this is the dashboard, REST API, and MCP endpoint).
- Optional UDP datagram listener on a port of your choosing (off by default; used by the test stand-in for MeshCore).

It opens an outbound TCP connection to your packet node's AGW port (default 8000 on BPQ).

The full list of operator-tunable knobs is on the [Configure](../configure.md) page.
