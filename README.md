# windhawk-cli

A **CLI-only, self-updating** alternative to **Windhawk** — built for **headless,
scriptable, fleet-managed** Windows machines. It runs the same Windhawk engine as a quiet
Windows service, installs mods as **precompiled binaries** (no local compiler), updates
both itself and its mods automatically, and exposes the whole mod lifecycle through a
single command-line tool. There is **no GUI**: the only interactive surface is an optional
tray icon that opens a small native status dialog.

## Why this exists

[Windhawk](https://github.com/ramensoftware/windhawk) (by Ramen Software,
<https://windhawk.net>) is an excellent open-source customization platform for Windows. It
applies small C++ "mods" that hook into running programs — the taskbar, File Explorer, the
Start menu, individual apps — using runtime global injection. Nothing is patched on disk;
every change is reversible by disabling the mod or stopping the engine.

But stock Windhawk is a **desktop application**: a ~780&nbsp;MB install (Electron GUI +
bundled LLVM/Clang compiler) that expects a logged-in user to drive setup through a window
and to click through update prompts. That is the right shape for an enthusiast on their own
PC, and the wrong shape for **machines you provision and maintain at scale**, where you
want unattended install, no user interaction, and updates that just happen.

**windhawk-cli** keeps Windhawk's engine and drops everything that requires a human:

- **No GUI, no compiler.** Mods are downloaded **precompiled** from the official catalog
  (<https://mods.windhawk.net>) instead of being built locally. This removes the 236&nbsp;MB
  Electron UI and the 543&nbsp;MB compiler toolchain — the installer is a single signed file
  of roughly **15–20&nbsp;MB**.
- **Self-updating.** windhawk-cli updates **itself** (the engine + tooling) from its GitHub
  Releases, verifying the downloaded installer's Authenticode signature against a pinned
  certificate before applying it — then updates mods. No prompts, no user.
- **CLI-first.** Every operation — search, install, update, uninstall, enable/disable,
  settings, profile export/apply — is a `whcli` command suitable for provisioning scripts,
  configuration management, and remote execution.
- **Service-based and quiet.** Runs as a Windows service under a **distinct identity
  (`WindhawkCLI`)** so it can coexist with an official Windhawk install. The tray icon is
  optional; when shown it opens a native status dialog (loaded mods + Exit), not a GUI app.

> **Tracking upstream.** windhawk-cli's engine is the upstream Windhawk engine. The
> maintainer intends to **incorporate and merge Windhawk engine updates into windhawk-cli
> releases** as they ship upstream, so fleets stay current with upstream fixes and new mod
> capabilities through windhawk-cli's own self-update channel.

For everything about the engine internals, the mod catalog, and writing mods, see the
**upstream project: <https://github.com/ramensoftware/windhawk>**.

## Installation

The installer is a single self-contained, signed file. Run it as Administrator.

Interactive:

```
windhawk-cli-<version>-installer.exe
```

Unattended (e.g. from a provisioning script):

```
windhawk-cli-<version>-installer.exe --silent --auto-updates --add-defender-exclusion
```

| Flag | Effect |
|------|--------|
| `--silent`, `-S` | No prompts |
| `--auto-updates` | Self-update + update mods before engine start, and again every ~23–24h |
| `--no-system-tray` | Don't show the tray icon (fully headless) |
| `--add-defender-exclusion` | Add Windows Defender exclusions for the install (see below) |
| `--install-dir <path>` | Install location (default `C:\Program Files\WindhawkCLI`) |
| `--update`, `--force` | Replace an existing install (a plain install refuses if one is present) |
| `--uninstall` | Stop + remove the service and the install |

The installer registers the `WindhawkCLI` service, places the mod runtime libraries, starts
the service, and only completes once a readiness check passes — so a script can install
mods immediately afterward. If an install already exists it **refuses unless `--update`** is
given (so you don't clobber one by accident); `whcli self-update` passes `--update`
automatically. On update it stops the running service and app so the binaries can be
replaced, then restarts.

> Building from source: this is a fork of upstream Windhawk applied as a patch set (see
> `patches/`) plus the `whcli`/`whsetup` tools. Build it by cloning upstream Windhawk at the
> commit noted in `patches/README.md`, applying the patch, and running the scripts in
> `scripts/` (and `whcli/build.ps1` / `whsetup/build.ps1`).

## Basic usage (`whcli`)

`whcli` operates on an install identified by `--root <dir>` (and `--service <name>` for a
service install). For the default service install:

```
whcli list    --root "C:\Program Files\WindhawkCLI"
whcli catalog "taskbar"                                  # browse the remote mod catalog
whcli install f1-blocker passkey-popup-blocker          # one OR MANY ids in one call
whcli set-setting <mod-id> <name> <value>
whcli disable <mod-id>                                   # enable/disable also accept many ids
whcli update [<mod-id>...]                               # no ids = all outdated; or name specific mods
whcli mod-status <mod-id>                                # config, compiled DLLs, live injected processes
whcli status                                            # readiness check (exit 0 when ready)
```

(`--root "C:\Program Files\WindhawkCLI"` applies to every command; omitted above for brevity.)

Mods install as **precompiled** DLLs straight from the catalog — no compilation step.
Installing or enabling a mod also **starts the service** if it isn't already running, and the
engine live-reloads, so the change goes online immediately.

Install a **precompiled mod from a local folder** (no network) — the folder holds the mod's
`<id>.wh.cpp` plus DLLs named `<version>_<sub>.dll` (e.g. `1.3.10_64.dll`) or `<sub>.dll`:

```
whcli install-local C:\mods\taskbar-grouping
```

Service control (needs elevation):

```
whcli start | stop | restart        # restart is valid even from a stopped state
```

Self-update and automatic updates:

```
whcli self-update                   # check GitHub Releases, verify the pinned signature, apply
whcli auto-update                   # self-update, then update all mods (used by the service)
```

Other commands: `uninstall`, `enable`, `search` (alias of `catalog`), `export`, `apply`,
`tray <show|hide>` (show/hide the tray icon at runtime, independent of the startup default).
Mutating commands are serialized machine-wide by a mutex, so concurrent automation runs are
safe; failures return a non-zero exit code so scripts can retry.

## Scripting & unattended provisioning

The intended workflow for managing a fleet:

1. **Capture** a reference machine's setup to a profile:
   ```
   whcli export --out profile.json
   ```
   Commit `profile.json` to your provisioning repo.

2. **Provision** a target machine unattended — install, then apply the profile:
   ```
   windhawk-cli-<version>-installer.exe --silent --auto-updates --add-defender-exclusion
   whcli apply profile.json --root "C:\Program Files\WindhawkCLI" --app-settings
   ```
   `apply` downloads (precompiled), configures, and enables every mod in the profile.
   Because the installer gates on a readiness check, the `whcli apply` step can run
   immediately afterward.

3. **Verify** (use as the gate in scripts — exits non-zero until ready):
   ```
   whcli status --root "C:\Program Files\WindhawkCLI"
   ```

With `--auto-updates` enabled, provisioned machines thereafter keep both windhawk-cli and
their mods current on their own — no return visit, no user interaction.

## Worked example (silent install → mods → settings)

A complete, copy-pasteable flow (run elevated). The installer starts the service and only
returns once it is READY, so the `whcli` calls right after it work immediately.

```powershell
$root = "C:\Program Files\WindhawkCLI"
$whcli = "$root\whcli.exe"

# 1. Install silently: service install, auto-updates on, Defender exclusion added.
#    Use --update to replace an existing install (plain install refuses if one is present).
.\windhawk-cli-1.0.0-installer.exe --silent --auto-updates --add-defender-exclusion

# 2. Install a couple of mods (one call → one service start; both go live at once).
& $whcli install disable-feedback-hub-hotkey f1-blocker --root $root

# 3. Configure a mod's settings via the CLI, then change one later.
& $whcli set-setting passkey-popup-blocker timeout 800 --root $root
& $whcli install passkey-popup-blocker --root $root          # install it first
& $whcli set-setting passkey-popup-blocker block_result user_cancelled --root $root

# 4. Inspect a mod: config, compiled DLLs, and which processes it's injected into.
& $whcli mod-status passkey-popup-blocker --root $root

# 5. Service control (the engine live-applies config, so this is only for explicit control).
& $whcli restart --root $root      # e.g. after a big batch of changes
```

If the service is running, every install/enable/disable/set-setting takes effect right away
(the engine watches its config and reloads). If you stopped it, changes are persisted and
load the next time it starts. To update later without waiting for auto-update:
`whcli update` (all mods) or `whcli update <id>`; `whcli self-update` for the app itself.

## Windows Defender

The Windhawk engine injects a DLL into other processes — legitimate, but a pattern that
Windows Defender's behavioral ML can flag (e.g. `Behavior:Win32/DefenseEvasion.A!ml`) and
quarantine, especially for a self-built binary with no cloud reputation. Two mitigations,
used together:

1. **Code signing.** Release binaries are Authenticode-signed by the maintainer, so you can
   verify their integrity and origin (self-update also requires a valid, pinned signature
   before it will apply an update). In a managed environment you control, you may choose to
   trust that publisher certificate (import it into *Trusted Publishers*) — but only after
   verifying it yourself; don't import a third-party root just because a README says to.
2. **Defender exclusion.** The reliable way to stop the behavioral detection if you trust
   this software. Pass `--add-defender-exclusion` to the installer, or deploy the exclusions
   centrally via Intune/GPO (paths `C:\Program Files\WindhawkCLI` and
   `C:\ProgramData\WindhawkCLI`; processes `windhawk.exe`, `whcli.exe`).

> An exclusion turns off AV coverage for those paths/processes — appropriate only for
> software you trust. This is a known false-positive for legitimate injection-based
> customization tools, including upstream Windhawk.

## License

This project is a derivative of Windhawk, which is licensed under the **GNU General Public
License v3.0**. GPL-3.0 is a copyleft license, so this fork is — and must remain —
**GPL-3.0** as well (it cannot be relicensed under a permissive license such as MIT). The
full license text is in [`LICENSE`](LICENSE), and source is published accordingly.

Based on **Windhawk** by **Ramen Software** (m417z) —
<https://github.com/ramensoftware/windhawk>. Portions © Ramen Software.

Copyright © 2026 HansonXYZ.
