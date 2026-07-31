# Windows Toast Watcher (CLI)

This is a PowerShell command-line watcher that listens for Windows notification delivery events and prints notification metadata.

It also includes a C# command-line implementation that can be compiled with .NET Framework toolchains (without dotnet CLI).

## Requirements

- Windows 10/11
- PowerShell 5.1+ (built into Windows)

## Run

```powershell
Set-Location "e:\06_Coding\XPanelPCService"
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\ToastWatcher.ps1
```

## C# (.NET Framework) Build And Run

Build executable:

```powershell
Set-Location "e:\06_Coding\XPanelPCService"
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\Build-ToastWatcherNetFx.ps1
```

Run executable:

```powershell
.\bin\ToastWatcherNetFx.exe
```

Optional parameter:

```powershell
.\bin\ToastWatcherNetFx.exe --raw-xml
```

Source file:

- `ToastWatcherNetFx/Program.cs`

Optional parameter:

```powershell
# Print full event XML for each toast event
.\ToastWatcher.ps1 -ShowRawXml
```

## Output Example

```text
=== Toast Captured ===
Time : 2026-05-22 11:30:00
App  : WeChat
TrackingId: 55875
SessionId : 1
MessageId : {04ef963f-4017-45c5-bf8d-4d7d32608e3f}
```

## Notes

- Data source: `Microsoft-Windows-PushNotification-Platform/Operational` event log.
- Captured event ids:
	- `3052` (Toast)
	- `3053` (Badge, commonly seen for Teams)
	- `3054` (Cancel)
	- `3055` (Clear)
- The watcher prints metadata from delivery events. Some apps do not expose title/body text in this channel.
- C# and PowerShell versions use the same event source and output structure.
- Press `Ctrl+C` to stop watching.
