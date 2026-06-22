# WindhawkXYZ

A fork of **Windhawk** focused on **headless, scriptable, fleet-provisionable** use:
a command-line interface for managing mods, profile-based provisioning, automatic
mod updates, an optional no-tray mode, and an unattended service installer.

## What is Windhawk?

[Windhawk](https://github.com/ramensoftware/windhawk) (by Ramen Software,
<https://windhawk.net>) is an open-source customization platform for Windows. It applies
small C++ "mods" that hook into running Windows programs — the taskbar, File Explorer,
the Start menu, individual apps — using runtime global injection. Nothing is patched on
disk; every change is reversible by disabling the mod or stopping the engine.

For everything about Windhawk itself, the mod catalog, and writing mods, see the
**upstream project: <https://github.com/ramensoftware/windhawk>**.

## What this fork adds

Stock Windhawk is a tray app driven by a GUI. This fork makes the same engine usable as
a quiet, automatable system component you can roll out across many machines:

- **`whcli` — a command-line interface** for the full mod lifecycle: search the catalog,
  install / update / uninstall, enable / disable, change settings — no GUI required.
- **Profile-based provisioning** — snapshot a machine's mods and settings to a
  `profile.json`, then reproduce that exact set on other machines (`export` / `apply`).
- **Automatic mod updates** — optionally update installed mods *before the engine starts*
  (so machines come up current) and again on a randomized ~23–24h cycle.
- **Optional no-tray / headless mode** — run with no system-tray icon.
- **`whsetup` — an unattended installer** that installs the engine as a Windows service,
  applies configuration, starts it, and verifies readiness — suitable for provisioning
  scripts.

It installs under a **distinct identity (`WindhawkXYZ`)** so it can coexist with an
official Windhawk install.

## Installation

Run the installer (`whsetup.exe`) as Administrator. Interactive:

```
whsetup.exe
```

Unattended (e.g. from a provisioning script):

```
whsetup.exe --silent --auto-updates --no-system-tray --add-defender-exclusion
```

| Flag | Effect |
|------|--------|
| `--silent`, `-S` | No prompts |
| `--auto-updates` | Update mods before engine start and every ~23–24h |
| `--no-system-tray` | Hide the tray icon |
| `--add-defender-exclusion` | Add Windows Defender exclusions for the install (see below) |
| `--install-dir <path>` | Install location (default `C:\Program Files\WindhawkXYZ`) |
| `--uninstall` | Stop + remove the service and the install |

The installer registers the `WindhawkXYZ` service, starts it, and only completes once a
readiness check passes — so a script can install mods immediately afterward.

> Building from source: this is a fork of upstream Windhawk applied as a patch set (see
> `patches/`) plus the `whcli`/`whsetup` tools. Build it by cloning upstream Windhawk at
> the commit noted in `patches/README.md`, applying the patch, and running the scripts in
> `scripts/` (and `whcli/build.ps1` / `whsetup/build.ps1`).

## Basic usage (`whcli`)

`whcli` operates on an install identified by `--root <dir>` (and `--service <name>` for a
service install). For the default service install:

```
whcli list   --root "C:\Program Files\WindhawkXYZ"
whcli catalog "taskbar"                                 # browse the remote mod catalog
whcli install disable-feedback-hub-hotkey --root "C:\Program Files\WindhawkXYZ"
whcli set-setting <mod-id> <name> <value> --root "C:\Program Files\WindhawkXYZ"
whcli disable <mod-id> --root "C:\Program Files\WindhawkXYZ"
whcli update --root "C:\Program Files\WindhawkXYZ"      # upgrade outdated mods
whcli status --root "C:\Program Files\WindhawkXYZ" --service WindhawkXYZ
```

Other commands: `uninstall`, `enable`, `search` (alias of `catalog`), `export`, `apply`,
`tray <show|hide>` (show/hide the system-tray icon at runtime, independent of the startup
default).

## Scripting & unattended provisioning

The intended workflow for managing a fleet:

1. **Capture** a reference machine's setup to a profile and pin the mod sources:
   ```
   whcli export --out profile.json --bundle mods
   ```
   Commit `profile.json` and the `mods/` sources to your provisioning repo.

2. **Provision** a target machine unattended — install, then apply the profile:
   ```
   whsetup.exe --silent --auto-updates --add-defender-exclusion
   whcli apply profile.json --root "C:\Program Files\WindhawkXYZ" --app-settings
   ```
   `apply` installs, compiles, configures, and enables every mod in the profile. Because
   the installer gates on a readiness check, the `whcli apply` step can run immediately.

3. **Verify** (use as the gate in scripts — exits non-zero until ready):
   ```
   whcli status --root "C:\Program Files\WindhawkXYZ" --service WindhawkXYZ
   ```

`apply` resolves each mod's source from the bundled `mods/` folder first and falls back
to fetching from the official catalog, so provisioning is reproducible offline.

## Windows Defender

Windhawk's engine injects a DLL into other processes — legitimate, but a pattern that
Windows Defender's behavioral ML can flag (e.g. `Behavior:Win32/DefenseEvasion.A!ml`) and
quarantine, especially for a self-built, unsigned binary with no cloud reputation. Two
mitigations, used together:

1. **Code signing.** Release binaries are Authenticode-signed by the maintainer, so you
   can verify their integrity and origin. In a managed environment you control, you may
   choose to trust that publisher certificate (import it into *Trusted Publishers*) — but
   only after verifying it yourself; don't import a third-party root just because a README
   says to.
2. **Defender exclusion.** The reliable way to stop the behavioral detection if you trust
   this software. Pass `--add-defender-exclusion` to the installer, or deploy the
   exclusions centrally via Intune/GPO (paths `C:\Program Files\WindhawkXYZ` and
   `C:\ProgramData\WindhawkXYZ`; processes `windhawk.exe`, `whcli.exe`).

> An exclusion turns off AV coverage for those paths/processes — appropriate only for
> software you trust. If you'd rather not exclude or trust a prebuilt binary, build from
> source (see above). This is a known false-positive for legitimate injection-based
> customization tools, including upstream Windhawk.

## License

This project is a derivative of Windhawk, which is licensed under the **GNU General Public
License v3.0**. GPL-3.0 is a copyleft license, so this fork is — and must remain —
**GPL-3.0** as well (it cannot be relicensed under a permissive license such as MIT). The
full license text is in [`LICENSE`](LICENSE), and source is published accordingly.

Based on **Windhawk** by **Ramen Software** (m417z) —
<https://github.com/ramensoftware/windhawk>. Portions © Ramen Software.

Copyright © 2026 HansonXYZ.
