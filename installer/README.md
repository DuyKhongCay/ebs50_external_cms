# Building and maintaining the Windows installer

The installer packages EBS50 E-Tag Backend, a web application for managing
electronic shelf labels, machine models, and machine statuses through desktop
and mobile interfaces.

## Application screenshots

### Desktop dashboard

The desktop dashboard shows tag and model totals, an E-Paper preview, and a tag
list with machine statuses, battery levels, and synchronization statuses.

![EBS50 desktop dashboard with E-Paper preview and tag management](../docs/images/pc_web_ui.png)

### Mobile interface

The mobile interface provides tag search and filters, machine details, battery
and synchronization statuses, image previews, and status updates.

<img src="../docs/images/phone_web_ui.jpg" alt="EBS50 mobile interface with tag details and status controls" width="360">

## Build the installer

Update `MyAppVersion` in `ebs50_setup.iss`, then run from the repository root:

```powershell
.\ebs50_backend\scripts\Build-Installer.ps1
```

The script checks for .NET and Inno Setup 6 before removing the dedicated
`publish/win-x64` directory, publishing current source, and compiling the installer
into `dist`. Cleanup, publish, and compiler failures stop the build. Only `win-x64`
is supported. `-WhatIf` previews the operation without modifying files.

Compiling `ebs50_setup.iss` directly also runs `Publish-App.ps1` first. The build
script passes `/DAppPublished=1` after a successful publish to avoid publishing
twice; do not pass this internal flag for ordinary manual compilation.

`Publish-App.ps1 -OutputDir` accepts only this project's `publish/win-x64` path.
Junctions and symbolic links in the output path or output tree are rejected before
cleanup. Keep production data outside the build output directory.

## Installer behavior

| Installed state | Available actions |
| --- | --- |
| Not installed | Install |
| Older version | Update or uninstall |
| Same numeric version | Reinstall or uninstall |
| Newer or unreadable version | Uninstall only; installation blocked |

The maintenance page displays installed and package versions. Silent setup updates
or reinstalls by default and rejects downgrades. Uninstall launches the registered
uninstaller; on successful completion, setup closes without installing anything.

Updates stop the service and wait up to 60 seconds before replacing files. Service
registration is created or updated, then the service is started. The existing
database is never overwritten. New installations mark the database to survive
automatic uninstaller file removal; the uninstaller asks whether to delete it.
An older installed uninstaller retains its original behavior until upgraded.

## Manual validation on a Windows test machine

Build validation does not install or remove a Windows service. Before release,
exercise the following scenarios on a disposable machine with administrator access:

1. Fresh installation: service starts and dashboard loads.
2. Upgrade and same-version reinstall: version labels/actions are correct, the
   service restarts, and existing database records remain.
3. Newer installed version: update is disabled; silent downgrade also fails.
4. Uninstall from the maintenance page: cancel leaves setup available; success
   closes setup. Test both keeping and deleting the database.
5. A service that cannot stop: setup aborts before replacing application files.
6. Compile the ISS directly after changing source: old publish artifacts disappear
   and the resulting application includes the source change.
