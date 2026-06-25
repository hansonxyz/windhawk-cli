# Windows 11 Start Menu — XAML inspection & styler research

How developers discover and customize the XAML element tree of the Windows 11 Start
menu (`StartMenuExperienceHost.exe`), in the context of the Windhawk **Windows 11 Start
Menu Styler** mod (`windows-11-start-menu-styler`, by m417z).

> Sourced from a multi-agent deep-research pass (15 sources fetched, 25 claims
> adversarially verified, 22 confirmed / 3 killed). Primary sources are linked inline.
> **Time-sensitivity:** Start menu element names/tree change across Win11 feature updates
> (22H2 → 25H2) and Insider builds. Re-verify any specific selector on your target build.

---

## TL;DR

- The Start menu is a **UWP/XAML app** hosted in `StartMenuExperienceHost.exe`.
- You discover element names by attaching a **live XAML inspector** to that process —
  **UWPSpy** is the canonical tool (same author as Windhawk). It reads the *real XAML
  object tree* (`x:Name` + class names), not the accessibility/UIA tree.
- The styler **only restyles / hides / repositions EXISTING elements**. It cannot add
  new native XAML controls. The only place you can *add* anything is the search WebView
  (via `webContentCustomJs` / `webContentStyles`).
- For a *fully custom* layout (different grid, added sections, forced width), you need a
  full Start **replacement** (Start11, StartAllBack, Open-Shell, ExplorerPatcher), not
  Windhawk.

---

## 1. The tool: UWPSpy

- **Repo:** https://github.com/m417z/UWPSpy — **Site:** https://ramensoftware.com/uwpspy
- **Author:** Michael Maltsev (m417z) — the same author as Windhawk and the styler mod.
- **How it works:** uses the in-process **XAML Diagnostics APIs**
  (`InitializeXamlDiagnosticsEx`, `xamlom.h`), added in **Windows 10 1703+**. It reads
  and live-edits the **actual XAML object model**, distinct from the UIA tree. One window
  per UI thread. Edits are **temporary** (lost on app restart) — persistence comes only
  from a Windhawk styler mod.
- **Works on the Start menu specifically:** ✅ `StartMenuExperienceHost.exe`, plus the
  taskbar, File Explorer, and Notification Center.
- **Does NOT work on:** the Start **search** overlay — that's `SearchHost.exe`, a
  **WebView2** component, not XAML. For that, use **Edge remote debugging** instead.
- **Styler-ready output:** UWPSpy emits short `Class#Name` paths that paste directly into
  the styler's `target` selectors.

### Tools that DON'T fit (and why)

| Tool | Verdict |
|---|---|
| **Snoop / SnoopWPF** | WPF-only — will not attach to a UWP/XAML host. |
| **inspect.exe / Accessibility Insights** | Read the **UIA** tree → accessibility names, *not* `x:Name`/class. Wrong layer for styler targets. |
| **VS Live Visual Tree / XamlPeek** | XAML-Diagnostics-based, *might* attach, but nobody in this ecosystem uses them. UWPSpy is the dominant path. |

---

## 2. Published documentation

| Resource | What it is |
|---|---|
| https://github.com/ramensoftware/windows-11-start-menu-styling-guide | **Canonical reference.** "Commonly requested start menu styling customizations." Themes + a **"Finding targets"** section. |
| https://github.com/bbmaster123/FWFU/blob/main/Guides/uwpspy.md | "How to find targets using UWPSpy" — the step-by-step (linked from the guide's "Finding targets"). |
| https://windhawk.net/mods/windows-11-start-menu-styler | The mod page; recommends UWPSpy verbatim, documents the selector grammar. |
| https://github.com/ramensoftware/windhawk-mods/blob/main/mods/windows-11-start-menu-styler.wh.cpp | Mod source. |
| https://github.com/ramensoftware/windows-11-taskbar-styling-guide | Sibling guide (same grammar; useful for examples). |

> There is **no Microsoft-published map** of the Start menu's XAML tree. Element names are
> reverse-engineered with UWPSpy and shift across builds.

---

## 3. Selector + setter grammar (verbatim from docs)

**Selectors** are CSS-like:

| Form | Meaning |
|---|---|
| `Class` or `Namespace.ClassName#ElementName` | The `#` part is the `x:Name`; namespace prefix optional. e.g. `Border#AcrylicBorder`, `StartMenu.PinnedList` |
| `A > B` | `>` is the parent/child combinator |
| `A > * > B` | `*` between two `>` matches any number of intermediate parents |
| `Class#Name[2]` | `[n]` selects the n-th child |
| `Class#Name[Prop1=Val1][Prop2=Val2]` | filter by property value(s) |
| `:root` (leftmost) | require the next part has no parent |
| `Class#Name@VisualStateGroupName` | target a visual-state group |

Full example from the guide:
```
Windows.UI.Xaml.Controls.Button#ZoomOutButton > Windows.UI.Xaml.Controls.ContentPresenter#ContentPresenter > Windows.UI.Xaml.Controls.TextBlock
```

**Property setters:**

| Operator | Use | Examples |
|---|---|---|
| `=` | scalar / simple values | `Visibility=Collapsed`, `Height=0`, `Margin=0,0,0,24`, `BorderThickness=0`, `CornerRadius=15`, `HorizontalAlignment=2` |
| `:=` | **complex XAML object** values parsed from inline markup (via `XamlReader`) | `Background:=<AcrylicBrush BackgroundSource="Backdrop" TintColor="Pink" TintOpacity="0.25" />`, `Fill:=<SolidColorBrush Color="Red"/>` |

`:=` is the most powerful and least-used capability — it lets you swap in whole brushes /
objects, not just scalars.

### In our config (`controlStyles`)

The mod stores rules as indexed settings. Each rule =
`controlStyles[N].target` (a selector string) + one or more
`controlStyles[N].styles[M]` (a `Property=Value` / `Property:=<Xaml/>` setter).

Our current Start-menu config lives at
`HKLM\SOFTWARE\WindhawkCLI\Engine\Mods\windows-11-start-menu-styler\Settings`.

---

## 4. The customization ceiling (important)

- **Styler = restyle / hide / reposition EXISTING elements only.** The styling guide
  documents **no** mechanism to add new native XAML controls/tiles to the Start menu
  tree. Element *addition* is fundamentally out of scope for the styler architecture.
- **The one exception — the search WebView:** the mod supports `webContentCustomJs`
  (JavaScript injection — `document.createElement` works; its own example injects
  m417z's DOM inspector via `document.createElement('script')`) and `webContentStyles`
  (CSS). That's **DOM, not XAML**, and limited to the search pane.
- **Why container-width overrides don't "take":** you can only push on properties the
  existing layout engine already honors. Leaf properties (e.g. `UserTileView`
  `Visibility` / `HorizontalAlignment`) apply; parent container widths get recomputed by
  the layout pass back to their measured size. This is a **real architectural limit**,
  not a config mistake — it's why we couldn't force the pinned area wider.
- **Fully custom layout** (different grid, added sections, forced width) ⇒ requires a
  full Start **replacement**: Start11, StartAllBack, Open-Shell, ExplorerPatcher. These
  swap out the menu entirely rather than restyling Microsoft's.

---

## 5. Practical workflow — find an element to target

1. Run **`UWPSpy.exe`** (the 64-bit build for the 64-bit Start host).
2. Select **`StartMenuExperienceHost.exe`** as the target process.
3. Open the Start menu, hover the element you care about, **Ctrl+D** to jump to it in the
   tree.
4. **Live-edit its properties** in UWPSpy to confirm the change does what you want
   (temporary — nothing persists yet).
5. **Copy its `Class#Name` path** (UWPSpy formats it styler-ready).
6. Paste selector + setters into the mod's `controlStyles[N].target` / `.styles[M]`.

> The Start host can disappear/relaunch (e.g. it idles out). If UWPSpy loses it, reopen
> Start to respawn `StartMenuExperienceHost.exe` and re-attach.

---

## 6. Open questions (not resolved by research)

- Do VS Live Visual Tree / XamlPeek attach to `StartMenuExperienceHost.exe`, and how do
  they compare to UWPSpy for emitting styler-compatible selectors?
- Is there *any* supported Windhawk mechanism (another mod?) to add NEW native XAML
  controls to the main Start tree, or is it permanently out of scope?
- Which element names are stable across 22H2 → 25H2 vs. which changed? No versioned
  reference exists.

---

## Sources (primary)

- https://github.com/m417z/UWPSpy + `/blob/main/README.md`
- https://ramensoftware.com/uwpspy
- https://github.com/m417z/UWPSpy/issues/3 (Start menu works; SearchHost doesn't)
- https://windhawk.net/mods/windows-11-start-menu-styler
- https://github.com/ramensoftware/windows-11-start-menu-styling-guide (+ README, Themes/TranslucentStartMenu/README.md)
- https://github.com/ramensoftware/windhawk-mods/blob/main/mods/windows-11-start-menu-styler.wh.cpp
- https://github.com/bbmaster123/FWFU/blob/main/Guides/uwpspy.md
- https://github.com/ramensoftware/windows-11-taskbar-styling-guide/blob/main/README.md

---

## Appendix A — Discovering & verifying targets on YOUR build

The styler's predefined themes (e.g. `SideBySideMinimal`) are written against a *specific*
Start-menu build. Element names drift across Win11 feature updates, so a theme rule can
silently match nothing on a newer build. This appendix is the procedure to (1) find the
real element names on the machine in front of you, and (2) verify what actually rendered.

### A.1 Discover names with UWPSpy (interactive — the real XAML tree)

1. Run **`tools\uwpspy\UWPSpy.exe`** (the x64 build for the 64-bit Start host).
2. In the process list, select **`StartMenuExperienceHost.exe`**.
3. Open the Start menu. Hover the element you care about and press **Ctrl+D** to jump to
   it in UWPSpy's tree.
4. UWPSpy shows the element's **`Namespace.ClassName#x:Name`** and lets you **live-edit
   properties** (temporary) — change `Width`/`Visibility`/`ColumnDefinitions` right there
   to confirm the element actually drives what you want *before* writing a mod rule.
5. Copy the `Class#Name` path; it's already styler-compatible. Paste it into
   `controlStyles[N].target` with your setters.

> The Start **search** box is `SearchHost.exe` (WebView2, not XAML) — UWPSpy can't see it;
> use Edge remote debugging + the mod's `webContentStyles`/`webContentCustomJs` for that.
> If UWPSpy loses the process, reopen Start to respawn `StartMenuExperienceHost.exe`.

### A.2 Verify what actually rendered (headless — UI Automation dump)

UWPSpy is a GUI. To verify *programmatically* whether a rule took effect (e.g. in a
provisioning smoke test), walk the live **UI Automation** tree and read bounding boxes.
UIA exposes automation peers (`Pinned`, `All apps`, `Navigation menu`), **not** the XAML
`x:Name`s — so it's useless for *finding* targets, but perfect for *measuring results*
(did the menu get wider? is the user tile present and on the right?).

```powershell
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,WindowsBase
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class U {
  [DllImport("user32.dll")] public static extern void keybd_event(byte b,byte s,uint f,UIntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c,string n);
}
"@
[U]::keybd_event(0x5B,0,0,[UIntPtr]::Zero); [U]::keybd_event(0x5B,0,2,[UIntPtr]::Zero)  # tap Win
Start-Sleep -Milliseconds 1200
$el = [System.Windows.Automation.AutomationElement]::FromHandle([U]::FindWindow("Windows.UI.Core.CoreWindow","Start"))
$w  = [System.Windows.Automation.TreeWalker]::ControlViewWalker
function Dump($e,$d){ if(-not $e -or $d -gt 11){return}
  try{ $r=$e.Current.BoundingRectangle
    if(-not [double]::IsInfinity($r.Width) -and $r.Width -gt 0 -and $r.Height -gt 0){
      ('  '*$d)+("{0,-24} [{1}] {2}x{3} @({4},{5})" -f ($e.Current.Name -replace '\s+',' '),$e.Current.ClassName,[int]$r.Width,[int]$r.Height,[int]$r.X,[int]$r.Y) } }catch{}
  $c=$w.GetFirstChild($e); while($c){ Dump $c ($d+1); $c=$w.GetNextSibling($c) } }
Dump $el 0
[U]::keybd_event(0x5B,0,0,[UIntPtr]::Zero); [U]::keybd_event(0x5B,0,2,[UIntPtr]::Zero)  # close
```

> Gotcha: on multi-monitor with **DisplayFusion**, Start opens on whichever monitor the
> mouse is on — keep the cursor on the primary monitor, or expect off-screen (negative-X)
> coordinates in the dump (relative layout is still valid).

### A.3 Case study — build drift on Windows 11 25H2 (build 26200)

Applying `SideBySideMinimal` + width overrides on **25H2 (26200.7840)**, the UIA dump showed:

- **Leaf-property rules WORK:** `StartDocked.UserTileView` `Visibility=Visible` +
  `HorizontalAlignment=2` → user tile present and right-aligned (`@x≈493`), power button
  left (`@x≈26`). Confirmed loaded.
- **Container-width rules NO-OP:** `Grid#MainMenu Width=…` and
  `Grid#SideBySidePinnedWrapper ColumnDefinitions=…` produced **no change** — the menu
  rendered **666px** and the pinned area stayed a **fixed 3-column `GridView`** (96px tiles).
- **Smoking gun:** even the theme's *own* `Grid#MainMenu Width=600` was not honored (menu
  is 666px, not 600) → the names `Grid#MainMenu` / `Grid#FrameRoot` **don't exist in 25H2's
  tree**. The rules fall through to default sizing.
- **Second constraint:** the pinned `GridView` shows a fixed column count; widening its
  container alone adds an empty gap, not more tile columns. Forcing wider pins needs *both*
  the real 25H2 width element (found via A.1) *and* the grid's column-count property
  (`MaximumRowsOrColumns` / `DesiredWidth`).

**Takeaway:** before trusting a theme's width/layout rules on a new build, verify with A.2;
if they no-op, re-discover the real names with A.1. Hiding/visibility/alignment rules tend
to survive build drift; width/column/height container rules are the first to break.

---

## Appendix B — Applying our Start-menu config in provisioning

Our shipped Start-menu look = the `SideBySideMinimal` theme (full-height pins, no search
box, no recommended section, power-left) **+ one override** that restores the user tile on
the right. (Width tweaks were dropped — they no-op on 25H2; see A.3.)

**Storage:** mod settings are **system-wide** (HKLM → all users on the box), at
`HKLM\SOFTWARE\WindhawkCLI\Engine\Mods\windows-11-start-menu-styler\Settings`.

**Exact settings:**

| Name | Value |
|---|---|
| `theme` | `SideBySideMinimal` |
| `disableNewStartMenuLayout` | `disableNewLayoutKeepPhoneLink` |
| `controlStyles[0].target` | `StartDocked.UserTileView` |
| `controlStyles[0].styles[0]` | `Visibility=Visible` |
| `controlStyles[0].styles[1]` | `HorizontalAlignment=2` |

> `controlStyles` must be **contiguous from `[0]`** — the mod stops reading at the first
> empty `target`. **Bracket gotcha:** write these with the **.NET registry API**, not
> PowerShell `Set-ItemProperty -Name` — `[0]` is a wildcard there and the write silently
> no-ops.

**Provisioning snippet** (elevated; idempotent):

```powershell
#requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$whcli = 'C:\Program Files\WindhawkCLI\whcli.exe'
$env:WHCLI_ROOT = 'C:\Program Files\WindhawkCLI'
$mod   = 'windows-11-start-menu-styler'

# 1. Install the precompiled mod (auto-enables; starts the service)
& $whcli install $mod

# 2. Write theme + override (HKLM, system-wide). .NET API because of the [0] brackets.
$path = "SOFTWARE\WindhawkCLI\Engine\Mods\$mod\Settings"
$key  = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($path)
$cfg  = [ordered]@{
  'theme'                      = 'SideBySideMinimal'
  'disableNewStartMenuLayout'  = 'disableNewLayoutKeepPhoneLink'
  'controlStyles[0].target'    = 'StartDocked.UserTileView'
  'controlStyles[0].styles[0]' = 'Visibility=Visible'
  'controlStyles[0].styles[1]' = 'HorizontalAlignment=2'
}
foreach ($n in $cfg.Keys) { $key.SetValue($n, $cfg[$n], [Microsoft.Win32.RegistryValueKind]::String) }
$key.Close()

# 3. Reload so the engine re-applies the new settings (safe if Start isn't open yet —
#    rules apply when StartMenuExperienceHost is next launched)
& $whcli disable $mod
& $whcli enable  $mod
```

Alternatively, since this config now lives on the reference machine, a fresh
`whcli export` (or `export-cache`) captures the styler mod **with these settings** into
`profile.json` / the mod-cache, so the standard `install-cache` / `apply` provisioning
path reproduces it with no special-casing.
