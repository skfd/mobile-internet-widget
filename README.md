# Mobile Data Widget

A Windows 11 Widgets Board widget that shows how much mobile **data** you have
left on your Canadian carrier plan — at a glance, no app or login each time.

Supported carriers: **Rogers** and **Fido** (more can be added).

## How it works

A Windows widget runs headless and can't drive a browser, and the carriers have
no public data-usage API. So there are two pieces:

1. **Collector** — a small browser-automation tool. You sign in to the carrier
   website once (it remembers your session, including 2FA), it reads your usage,
   and saves it to a local file.
2. **Widget** — reads that file and shows *GB remaining*, a progress bar, and
   your billing-cycle reset date. It never logs in or touches the network.

Because you tether from your phone, the widget can't auto-detect the carrier —
you just add the **Rogers** or **Fido** widget yourself.

## Requirements

- Windows 11, Developer Mode enabled
- .NET 8 SDK
- Google Chrome (for the collector)

## Build & install

```powershell
dotnet build MobileData.slnx     # dev build
.\rebuild-deploy.ps1             # build the MSIX and register the widget
```

Then press **Win+W**, click **+**, and add the **Fido Data** widget.

## Refresh your usage

Store your Fido login once (encrypted with Windows DPAPI, per-user):

```powershell
dotnet run --project src/MobileData.Collector -- fido --set-login
```

Then fetch usage any time:

```powershell
dotnet run --project src/MobileData.Collector -- fido
```

A Chrome window opens and the collector auto-fills your login. If Fido asks for
2FA (or the form changes), just finish signing in manually — the browser profile
remembers you, so later runs are unattended. Your usage is written to
`%USERPROFILE%\.mobile-widget\fido.json`. Add `--headless` to run without a
window once a session exists.

### Keep it fresh automatically

Once you've done `--set-login` and one successful fetch, install an hourly task:

```powershell
.\install-collector-task.ps1                 # runs the collector headless every hour
.\install-collector-task.ps1 -Uninstall      # remove it
```

The task runs as you, only while you're logged on, and reuses the collector's
saved session (falling back to your stored credentials if it expires).

Want to see the widget before running the collector? Write sample data:

```powershell
./scripts/write-sample-data.ps1
```

## Tethering detection

The widget shows whether you're currently tethered and to which network. Windows
doesn't automatically treat a phone hotspot as mobile, so mark your hotspot as a
**metered connection** (Windows Settings → Network → Wi-Fi → your hotspot →
*Metered connection: On*). The widget then reports "Tethered via …".

## Status

The Fido widget works end to end: verified data scraping, live cache, tethering
detection, an auto-login collector, and a deployable MSIX (`rebuild-deploy.ps1`).
Rogers ships with placeholder art and shows "No data" until its collector exists.
Package preview images are generated placeholders. See `CLAUDE.md` for details.
