---
name: gh-release
description: Project overrides for the global gh-release skill: MSIX version, signing, zip layout.
---

# gh-release — mobile-internet-widget

Where this project departs from the global `gh-release` defaults.

## Version

- The source of truth is `<Identity Version="1.0.N.0">` in
  `packaging/MobileData.Package/Package.appxmanifest`. The tag is `v1.0.N.0`.
- **There is no `-dev` phase.** MSIX versions are four numeric parts, and
  `rebuild-deploy.ps1` increments N (and the `Mobile Data Widget vN` display
  name) on every local deploy, committed as "Bump package to …". So the release
  version is whatever the manifest says at HEAD, provided that HEAD was deployed
  and tested. Skip steps 3 and 7 of the global skill.
- **Don't run `rebuild-deploy.ps1` while releasing.** It bumps the version and
  desyncs the tag from the tree.

## Build (instead of `dotnet publish`)

From the repo root, using VS MSBuild found via `vswhere -latest -prerelease`:

```powershell
& $msbuild packaging\MobileData.Package\MobileData.Package.wapproj /restore `
  /p:Configuration=Release /p:Platform=x64 /p:UapAppxPackageBuildMode=SideloadOnly `
  /p:AppxBundle=Never /p:AppxPackageSigningEnabled=false
$d = "packaging\MobileData.Package\AppPackages\MobileData.Package_<ver>_x64_Test"
.\sign-release.ps1 -Msix "$d\MobileData.Package_<ver>_x64.msix"   # CN=TestCert; exports .cer beside it
Compress-Archive -Path "$d\*" -DestinationPath "packaging\MobileData.Package\AppPackages\MobileDataWidget_<ver>_x64.zip"
```

- Release output has no `Debug` infix in the folder name.
- `sign-release.ps1` prints "certificate which is not trusted by the trust
  provider". That's expected for a self-signed certificate.
- Zip **after** signing, with the contents at the zip root and no wrapping
  folder (about 140 MB). It includes `Install.ps1`, which end users run.

## Assets and notes

- Attach both the zip and the loose `.cer`.
- Copy the Install and Requirements & caveats sections from the previous
  release (`gh release view`). The `--set-login` flow is dead because reCAPTCHA
  blocks it. Users sign in with `run-collector-attach.ps1`, then run
  `install-collector-task.ps1`.
- When a release changes the repo scripts that the scheduled task runs
  (`run-collector-attach.ps1`, `run-hidden.vbs`), say that upgraders need to
  `git pull`. Those scripts aren't in the MSIX.
