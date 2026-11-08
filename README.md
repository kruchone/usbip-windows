# USB/IP for Windows

A tray app that attaches USB devices shared over the network as if they were plugged into this PC.
It pairs with [usbip-android](https://github.com/kruchone/usbip-android), and also works with any standard USB/IP server, such as Linux `usbipd`.

The app runs on top of the signed open-source **usbip-win2** driver, which provides the virtual USB host controller.
Install the driver once:

```
winget install vadimgrn.usbip-win2
```

## Features

- **Finding servers:** finds servers automatically over mDNS (`_usbip._tcp`), or you add one by address (e.g. over Tailscale).
- **Device list:** shows each device's name, VID:PID and bus speed, and whether it's free or in use by another PC.
  Names and "in use" status come from usbip-android's info extension; other servers show generic names.
- **Attaching:** one-click Attach/Detach. **Auto** re-attaches a device whenever it's available, backing off after failures.
- **Tray:** a menu for attaching and detaching, notifications when devices attach or disconnect, and **Start with Windows**.
- **No admin rights:** the driver lets any user attach devices, so the app never asks for elevation.

## Build

Requires the .NET 10 SDK on Windows 10 (2004) or later.

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o %LOCALAPPDATA%\Programs\UsbIpClient
```

## Notes

- Attach and detach go through `C:\Program Files\USBip\usbip.exe`. Device lists are fetched directly over the
  USB/IP protocol (`UsbIpWire.cs`).
- The app speaks one protocol extension: op `0x8F01` gets `0x0F01` + u32 length + JSON
  `{"name", "devices":[{"busid","name","attachedBy"}]}`. Servers that don't support it are re-checked only every 10 minutes.
- `UsbIpClient.exe --snapshot out.png` renders the window to an image and exits. It's useful for checking the UI over remote sessions.
