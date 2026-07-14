# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Project Overview

Windows 11 Widgets Board widget that shows how much mobile **data** you have left
on a Canadian carrier plan. First carriers: **Rogers** and **Fido**.

Architecture mirrors the sibling project `../llm-token-widget`: a headless COM
out-of-process widget provider renders Adaptive Cards from a local data source.

### Why two parts

A Windows widget runs as a headless background COM server and **cannot drive a
browser**. Carriers have no public data-usage API, so usage is obtained by
browser automation in a *separate* step:

- **Collector** (`MobileData.Collector`): a Playwright console app that scrapes
  remaining/used data and writes a cache file. It does **not** log in itself —
  see "Login: attach to real Chrome" below.
- **Widget** (`MobileData.App`): reads that cache file and renders the card.
  It never authenticates or touches the network. When the cache is stale it shows
  a **Reconnect** button whose `reconnect` verb (`WidgetProvider.OnActionInvoked`)
  opens the carrier login in the attach Chrome profile.

The cache lives at `%USERPROFILE%\.mobile-widget\{provider}.json`
(`rogers.json`, `fido.json`). Format is defined by `UsageCacheRecord` in
`MobileData.Core/UsageCache.cs`.

"Detecting the provider" is a manual choice (add the Rogers or Fido widget) —
when tethering from a phone the PC can't see the carrier.

## Technology Stack

- .NET 8 (`net8.0` for Core/Collector, `net8.0-windows10.0.22621` for App/Providers)
- Windows App SDK 1.5+
- Adaptive Cards (JSON) for UI
- Microsoft.Playwright for the collector
- MSIX packaging via Windows Application Packaging Project

## Solution Structure

```
MobileData.slnx
├── src/MobileData.Core/       # IDataProvider, models, shared UsageCache
├── src/MobileData.Providers/  # RogersProvider, FidoProvider (read cache + watch file)
├── src/MobileData.App/        # COM widget provider + Adaptive Cards
├── src/MobileData.Collector/  # Playwright scraper -> writes cache
└── packaging/MobileData.Package/ # MSIX manifest
```

## Build & deploy

```powershell
dotnet build MobileData.slnx     # fast dev build of the 4 C# projects
.\rebuild-deploy.ps1             # build MSIX + register the widget (Win+W)
.\gen-images.ps1                 # regenerate package icons/logos/previews
.\clean.ps1                      # remove bin/obj
```

The `.wapproj` is built with Visual Studio MSBuild (dotnet CLI can't build it) —
`rebuild-deploy.ps1` locates it via `vswhere`, builds `Debug|x64`, copies
`Images\*.png` into the layout, and registers via `Add-AppxPackage -Register`.
The `.wapproj` is deliberately kept out of `MobileData.slnx` so `dotnet build`
stays fast.

Note: Core and Providers declare `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>`
because the self-contained App pushes that RID down through the wapproj build.

## Login: attach to real Chrome (the important bit)

Carrier login is gated by an **invisible reCAPTCHA** (Fido → `account.fido.ca`,
Rogers' Transmit Security IdP). It detects Playwright's automation window and
blocks sign-in even when a human types the password, so the collector **never
logs in from automation**. Instead:

- A **dedicated real Chrome** runs with a separate profile and remote debugging:
  `--user-data-dir=%USERPROFILE%\.mobile-widget\chrome-attach --remote-debugging-port=9222`.
  You sign into the carrier **once** in that window (reCAPTCHA trusts a real
  browser). The profile persists the session on disk across reboots.
- `--attach` mode connects over CDP to that Chrome (`http://127.0.0.1:9222` —
  IPv4, not `localhost`, which can resolve to `::1` and be refused), opens a
  throwaway tab, and scrapes. reCAPTCHA only fires at *login*, so an
  already-authenticated session scrapes freely, unattended, until the session
  cookie eventually expires (weeks) and you sign in once more.

```powershell
dotnet run --project src/MobileData.Collector -- fido --attach   # scrape via port 9222
```

`run-collector-attach.ps1` is the self-healing runner: it ensures the debug
Chrome is up (launches it minimized if not), then runs `--attach`.
`install-collector-task.ps1` schedules that wrapper every **20 min** as the
logged-on user (task `MobileDataWidget-<provider>-Collector`). When the session
lapses the scrape exits non-zero **without** overwriting the cache, leaving the
last good number; the widget's Reconnect button re-establishes it.

Notes for future work:
- `--attach` waits up to 40s for usage to render (a cold-launched Chrome + the
  carrier SPA is slow); too short a wait looks like "not signed in".
- The non-attach path (persistent automation profile, `--set-login` DPAPI creds,
  `--headless`) still exists in code but is dead for Fido — reCAPTCHA blocks it.

## Critical Architecture Concepts

### GUID Synchronization

One CLSID must match in exactly 3 places or the widget won't activate:

1. `[Guid("...")]` on `WidgetProvider` (`MobileData.App/WidgetProvider.cs`)
2. `CLSID_WidgetProvider` in `MobileData.App/Program.cs`
3. `<com:Class Id>` and `<CreateInstance ClassId>` in `Package.appxmanifest`

Current value: `63025828-B62F-4FE9-AAF5-A83E7162E090`.

Widget definition ids (`Rogers_Data_Widget`, `Fido_Data_Widget`) must match
between the manifest and `WidgetProvider.cs`.

## Fido specifics (verified against a live account)

The dashboard is `https://www.fido.ca/self-serve/wireless` (the old
`/pages/#/usage` now 404s). Usage renders inside an **iframe**, so `body` text
alone comes back blank — the collector reads text from **all frames** and
concatenates. `UsageParser` keys off these labels, all confirmed working:

- `38.78GB` + `Remaining data`  → remaining
- `Total data (plan+added)` `90.00` → total (used = total − remaining)
- `3 days remaining in bill cycle` → reset = today + N days
- line after `My mobile plan` → plan name (e.g. "90GB, Talk & Text - Plus")

## Tethering detection

`NetworkDetector.Detect()` (in Providers, uses `Windows.Networking.Connectivity`)
reports the active connection: SSID/name, metered, cellular, and a best-effort
`IsTethered`. A phone hotspot can't tell the PC its carrier, so `IsTethered` is
inferred from cellular-or-metered. Caveat: Windows does **not** auto-mark a Wi-Fi
hotspot as metered — either mark it metered in Windows settings, or (planned) map
the hotspot SSID → carrier in a `~/.mobile-widget/networks.json` config.

## Known gaps / next steps

- Re-login is manual by necessity (reCAPTCHA): when the attach session expires
  you sign in once in the debug Chrome. There's no way to fully automate this;
  the Reconnect button just shortcuts opening that login. A logon task to
  auto-launch the debug Chrome would keep it warm across reboots (not yet added).
- SSID → carrier mapping config for reliable tether detection.
- Rogers portal patterns (not started — Fido only for now). The Rogers widget
  ships with placeholder art and shows "No data" until its collector exists.
- Package previews (`PreviewFido.png` etc.) are generated placeholders; replace
  `PreviewFido.png` with a real Win+W screenshot when convenient (`gen-images.ps1`
  makes the placeholders).
