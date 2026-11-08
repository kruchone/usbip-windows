# USB/IP for Windows

**Download:** https://kruchone.github.io/usbip/

A tray app with two modes. **Use remote devices:** attach USB devices shared over the network as if they were
plugged into this PC. **Share this PC:** let other computers attach this PC's USB devices.
It pairs with [usbip-android](https://github.com/kruchone/usbip-android), and also works with any standard USB/IP server, such as Linux `usbipd`.

## Install

Download `UsbIp.exe` and run it. It's self-contained, so no .NET install is needed. On first run it:

1. installs itself per-user to `%LOCALAPPDATA%\Programs\UsbIp`, with a Start menu shortcut and an "Installed apps" entry;
2. offers to install the two signed open-source drivers it builds on, with **one** UAC prompt:
   - [usbip-win2](https://github.com/vadimgrn/usbip-win2) 0.9.7.8: the virtual USB host controller, for attaching remote devices;
   - [usbipd-win](https://github.com/dorssel/usbipd-win) 5.3.0: the server, for sharing local devices.

   Installers are downloaded from their GitHub releases (x64 or ARM64) and checked against pinned SHA-256 hashes,
   the same ones winget uses. The elevated helper re-verifies them before running anything.

Running a newer `UsbIp.exe` replaces the installed one. Uninstall from Installed apps; the drivers have their own entries.

## Features

**Share this PC**
- Lists this PC's USB devices, with Share / Stop sharing (bind/unbind; one UAC prompt each) and who's using each one.
- While anything is shared, it advertises the PC as `_usbip._tcp` over mDNS, so other USB/IP apps find it automatically.
- Devices this PC has imported from elsewhere are never offered for sharing.
- **Who can connect** sets the remote addresses usbipd-win's inbound firewall rule (`usbipd`, TCP 3240) accepts: this
  PC's subnet only (the default), specific IPs, networks or ranges (e.g. `LocalSubnet, 10.0.20.0/24, 100.64.0.0/10`), or anyone.
  Applying it takes one UAC prompt. The app warns if the rule later stops matching, e.g. after a usbipd-win upgrade.

**Use remote devices**

- **Finding servers:** finds servers automatically over mDNS (`_usbip._tcp`), or you add one by address (e.g. over Tailscale).
- **Device list:** shows each device's name, VID:PID and bus speed, and whether it's free or in use by another PC.
  Names and "in use" status come from usbip-android's info extension; other servers show generic names.
- **Attaching:** one-click Attach/Detach. **Auto** re-attaches a device whenever it's available, backing off after failures.
- **Tray:** a menu for attaching and detaching, notifications when devices attach or disconnect, and **Start with Windows**.
- **No admin rights:** the driver lets any user attach devices, so the app never asks for elevation.

## Device names

USB drivers mostly call themselves "USB Input Device", so names are built from better sources, in this order:
1. Your own label: right-click any device → **Rename…**. Labels are remembered per device (local devices by Windows
   instance ID, remote ones by server + bus ID + VID:PID).
2. The device's own product string (Windows' bus-reported description), or the name usbip-android sends.
3. The [USB ID database](http://www.linux-usb.org/usb.ids), bundled as `usb.ids.gz` (GPL-2.0-or-later / BSD-3-Clause):
   it supplies vendor and product names by VID:PID.

Each device also shows its vendor and what it contains (Keyboard, Mouse, Audio, Storage, Serial port, Camera, …).
For local devices that comes from the Windows drivers bound to it; for remote devices, from their USB interface classes.

## Build

Requires the .NET 10 SDK. It runs on Windows 10 (2004) or later.

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o dist
```

Use `-r win-arm64` for ARM. Options: `--tray` starts minimized, `--set-firewall <list>` applies an access list (elevated), `--portable` skips self-install, and `--uninstall` removes the app.

## Notes

- Attach and detach go through `usbip.exe` (usbip-win2); sharing goes through `usbipd.exe` (usbipd-win). Device lists are fetched directly over the
  USB/IP protocol (`UsbIpWire.cs`).
- The app speaks one protocol extension: op `0x8F01` gets `0x0F01` + u32 length + JSON
  `{"name", "devices":[{"busid","name","attachedBy"}]}`. Servers that don't support it are re-checked only every 10 minutes.
- `UsbIp.exe --snapshot out.png [share]` renders the window to an image and exits. It's useful for checking the UI over remote sessions.
