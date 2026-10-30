# Schema Reference

Complete field reference for `allie-pack.yaml`. For conceptual explanations of how these features work together, see the [Schema Guide](schema-guide.md).

---

## Top-level structure

```yaml
product:          # required
aliases:          # optional
variables:        # optional
structure:        # optional (at least one file source expected)
shortcuts:        # optional
environment:      # optional
registry:         # optional
services:         # optional
directories:      # optional
groups:           # optional
features:         # optional
releaseFlags:     # optional
defaultActiveFlags: # optional
signing:          # optional
ui:               # optional
runAfterInstall:  # optional
xmlEdits:         # optional
fileAssociations: # optional
wixToolsPath:     # optional
wix:              # optional
```

---

## `product:`

| Field | Type | Required | Default | Notes |
|---|---|---|---|---|
| `name` | string | yes | — | Display name shown in Add/Remove Programs |
| `manufacturer` | string | yes | — | Publisher/company name |
| `version` | string or version block | yes | — | Four-part (`1.0.0.0`), from PE file, or from git tags. See [version sourcing](#version-sourcing). |
| `description` | string | no | `""` | Description shown in Add/Remove Programs |
| `upgradeCode` | GUID string | yes | — | Must stay constant across all versions of your product. Changing this breaks upgrades. |
| `installScope` | string or conditional | no | `perMachine` | `perMachine`, `perUser` or `perUserOrMachine`. See [install scope](#install-scope). |
| `installDir` | string or conditional | no | `[ProgramFilesFolder]\Manufacturer\Name` (`[ProgramFiles64Folder]` on x64/arm64; `[LocalAppDataFolder]\Programs` under `perUser`) | Root install directory. A relative path and the `[ProgramFiles]` token are rooted the same way. |
| `platform` | string | no | `x86` | `x86`, `x64`, or `arm64` |
| `licenseFile` | string | no | — | Path to an `.rtf` file. Adds a license agreement dialog to the installer UI. |
| `icon` | string | no | — | Path to an `.ico` file shown in Add/Remove Programs (`ARPPRODUCTICON`). |
| `comments` | string | no | — | Free text shown as **Comments** in Add/Remove Programs (`ARPCOMMENTS`). Separate from `description`, which is the package summary. |
| `contact` | string | no | — | Support contact shown as **Contact** (`ARPCONTACT`). |
| `helpLink` | string | no | — | URL shown as **Help link** (`ARPHELPLINK`). |
| `supportLink` | string | no | — | URL shown as **Support link** (`ARPURLINFOABOUT`). |
| `updateLink` | string | no | — | URL shown as **Update information** (`ARPURLUPDATEINFO`). |
| `majorUpgrade` | bool | no | `false` | Emit `<MajorUpgrade>` so installing a newer version removes the older one with the same `upgradeCode`. Same-version upgrades are allowed, because MSI ignores the 4th version field when comparing. |

### Install scope

| Value | Behaviour |
|---|---|
| `perMachine` | All users. Requires elevation. |
| `perUser` | Current user only (WiX `perUser`, no ALLUSERS). Never elevates; installs under `%LOCALAPPDATA%\Programs` by default. Content that needs admin rights (per-machine paths such as `[ProgramFiles64Folder]`, `HKLM`/`HKU` registry values, `scope: machine` environment variables, services) is a build warning; `--strict` makes it an error. |
| `perUserOrMachine` | WiX dual-purpose package (ALLUSERS=2, MSIINSTALLPERUSER=1). Installs for the current user by default, with Program Files redirected to `%LOCALAPPDATA%\Programs`; an admin can force a per-machine install with `MSIINSTALLPERUSER=""`. |

> `ui:` is a **top-level** key, not a `product:` field — see [Installer UI](#installer-ui).

### Version sourcing

**Literal:**
```yaml
version: "1.0.0.0"
```

**From a PE file (reads FileVersionInfo at build time):**
```yaml
version:
  file: "bin:MyApp.exe"
  source: "file-version"    # or "product-version"
```

**From git tags** (produces `Major.Minor.Patch.CommitsSinceTag`):
```yaml
version:
  source: "git-tag"
  tagPrefix: "v"            # optional; matches tags like v1.2.3
```

### Installer UI

`ui:` is a **top-level** key (a sibling of `product:`, not a field inside it). It selects the
installer's dialog set and, in block form, whether the user may change the install location.

**Scalar form** — just the dialog set:

```yaml
ui: standard      # or: custom   (omit entirely for the default, standard)
```

**Block form** — dialog set plus options:

```yaml
ui:
  type: standard
  allowInstallDirChange: true   # default true; false locks the install location
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `type` | `standard` \| `custom` | `standard` | `standard` uses WiX's built-in dialog set (no extra DLLs). `custom` uses WixSharp's WPF EmbeddedUI (requires `WixSharp.UI.CA.dll` in the artifact). |
| `allowInstallDirChange` | bool | `true` | Whether the user can choose the install directory. Ignored by `custom`, which always includes its own directory dialog. |
| `banner` | string | — | `standard` only. 493×58 `.bmp` across the top of the inner pages (`WixUIBannerBmp`). |
| `dialog` | string | — | `standard` only. 493×312 `.bmp` behind the Welcome and Finish pages (`WixUIDialogBmp`). Keep the right-hand area plain; text is drawn over it. |
| `launchOnExit` | block | — | `standard` only. Adds a "Launch" checkbox to the Finish page. Fields: `file` (installed path, e.g. `[INSTALLDIR]\\App.exe`), `text`, `args`, `checked` (initial state, default `false`). The app starts in the UI process, as the interactive user. |

```yaml
ui:
  type: standard
  banner: "art\\banner.bmp"
  dialog: "art\\dialog.bmp"
  launchOnExit:
    file: "[INSTALLDIR]\\MyApp.exe"
    text: "Launch My App when setup exits."
```

#### Which pages are shown (`type: standard`)

For the standard dialog set, AlliePack selects the WiX `WixUI_*` variant from three inputs — whether
`features:` are declared, whether `allowInstallDirChange` is true, and whether a `licenseFile:` is
set — and injects the supporting properties automatically:

| `features:` | `allowInstallDirChange` | `licenseFile:` | Dialog set | Wizard pages (in order) |
|---|---|---|---|---|
| yes | true  | any | `WixUI_FeatureTree` | Welcome → **Custom Setup** (feature tree + **Browse…** to change location) → Ready to Install → Installing → Completed |
| yes | false | any | `WixUI_FeatureTree` | Welcome → **Custom Setup** (feature tree; location fixed) → Ready to Install → Installing → Completed |
| no  | true  | any | `WixUI_InstallDir`  | Welcome → **Destination Folder** → Ready to Install → Installing → Completed |
| no  | false | yes | `WixUI_InstallDir`  | Welcome → **Destination Folder** → Ready to Install → Installing → Completed |
| no  | false | no  | `WixUI_Minimal`     | Welcome (combined) → Installing → Completed |

When a `licenseFile:` is set, a **License Agreement** page is inserted right after Welcome in every
set except Minimal. With no `licenseFile:`, AlliePack routes around the license page so WiX does not
substitute its Lorem-ipsum placeholder.

> **Note — the feature tree does both jobs.** `WixUI_FeatureTree`'s Custom Setup page carries the
> feature checkboxes *and* the install-location Browse button, so it covers "choose features + choose
> location" on its own. AlliePack deliberately does **not** use `WixUI_Mondo` here: Mondo only reaches
> its feature tree through a Setup-Type ("Typical / Custom / Complete") page, and routing straight to a
> directory page bypasses it — hiding feature selection entirely.

#### Properties AlliePack injects for you

These are added to the generated `.wxs` automatically; you do not author them:

| Property / attribute | Emitted when | Why |
|---|---|---|
| `WIXUI_INSTALLDIR = INSTALLDIR` (property) | any set with a Destination Folder page (`WixUI_InstallDir`) | The `InstallDirDlg` path control binds to this property. Missing it aborts the UI at launch with **MSI error 2819**. |
| `ConfigurableDirectory="INSTALLDIR"` on each visible `<Feature>` | `features:` **and** `allowInstallDirChange: true` | Enables the **Browse…** button on the feature tree. Without it the button is greyed out and the location is fixed. |

See [Troubleshooting → Installer UI problems](troubleshooting.md#installer-ui-problems) if a built
installer aborts with error 2819 or omits the feature-selection page.

---

## `aliases:`

Short names for directory paths, used in `source:` fields as `alias:pattern`.

```yaml
aliases:
  bin: "$(GitRoot)/src/MyApp/bin/Release/net481"
  assets: "installer/assets"
```

Values may contain built-in tokens. See [Token reference](#built-in-tokens).

---

## `variables:`

Named tokens available as `$(name)` throughout the config. Values may contain built-in tokens. Can be overridden on the command line with `--define name=value`.

```yaml
variables:
  srcRoot: "$(CurrentDir)"
  buildOutput: "$(srcRoot)/bin/Release"
  buildConfig: "Release"     # also usable in `configuration:` fields
```

---

## `structure:`

List of `StructureElement` entries defining the installed file tree under `INSTALLDIR`.

| Field | Type | Notes |
|---|---|---|
| `folder` | string | Creates a subdirectory. Omit for files at the current level. |
| `source` | string | File path or glob. Supports `alias:pattern` and `**` recursive glob. |
| `solution` | string | Path to a `.sln` or `.slnx` file. Resolves all project build outputs. Both classic `.sln` and the VS 2022 17.12+ XML solution format (`.slnx`) are supported. |
| `project` | string | Path to a `.csproj` or `.vbproj` file. Resolves build outputs. |
| `configuration` | string | Build configuration for solution/project resolve. Default: `Release` |
| `platform` | string | Build platform for solution/project resolve. Default: `AnyCPU` |
| `includeProjects` | list | Whitelist of project names to include from a solution. |
| `excludeProjects` | list | Project names to exclude from a solution. |
| `excludeFiles` | list | Glob patterns applied to the resolved file list. **Defaults always excluded:** `**/.vs/**`, `**/.git/**`, `**/*.suo`, `**/*.user` (VS scratch state and user-specific files that should never ship in an installer). Your patterns are additive on top of these. |
| `onEmpty` | string | What to do if no files are found: `warn` (default), `error`, or `ignore`. |
| `contents` | list | Nested `StructureElement` list for subdirectories. |

### Source field syntax

| Pattern | Resolves to |
|---|---|
| `bin:MyApp.exe` | File `MyApp.exe` under the `bin` alias path |
| `bin:*.dll` | All `.dll` files directly under `bin` |
| `bin:**/*.dll` | All `.dll` files recursively under `bin`, preserving subdirectory structure |
| `$(GitRoot)/output/file.exe` | Absolute path using a built-in token |
| `relative/path/file.exe` | Resolved relative to the YAML file location |

---

## `shortcuts:`

| Field | Type | Notes |
|---|---|---|
| `name` | string | Shortcut display name |
| `target` | string | Path, typically `[INSTALLDIR]\\AppName.exe` |
| `description` | string | Tooltip text |
| `icon` | string | Path to an `.ico` file |
| `folder` | string | Destination — WiX folder property or well-known alias |

### Folder values

| Value | Location |
|---|---|
| `[ProgramMenuFolder]` | Start Menu\Programs |
| `[DesktopFolder]` | Desktop |
| `[StartupFolder]` | Startup |
| `startmenu` | Same as `[ProgramMenuFolder]` |
| `desktop` | Same as `[DesktopFolder]` |
| `startup` | Same as `[StartupFolder]` |

All of these follow `installScope`: Windows Installer points them at the All Users profile
for a per-machine install and at the current user's profile for a per-user one. It has no
separate all-users properties; `[CommonProgramMenuFolder]`, `[CommonDesktopFolder]` and
`[CommonStartupFolder]` are accepted as the same folders.

---

## `environment:`

| Field | Type | Notes |
|---|---|---|
| `name` | string | Variable name |
| `value` | string or conditional | Value; supports `[INSTALLDIR]` and WiX properties |
| `scope` | string or conditional | `user` or `machine`. Default inferred from `installScope`. |
| `part` | string | `all` (default) replaces the variable; `last` appends and `first` prepends, `;`-separated. Use `last` for `PATH` -- `all` would overwrite it. |

Variables are removed on uninstall.

---

## `runAfterInstall:`

Installed programs to start, without waiting, after `InstallFinalize`.

| Field | Type | Notes |
|---|---|---|
| `file` | string | Installed path, e.g. `[INSTALLDIR]\\MyApp.exe` |
| `args` | string | Command line; MSI-formatted, so `[ProductVersion]` etc. expand |
| `condition` | string | MSI condition. Default `NOT REMOVE~="ALL"` (never on uninstall). `UILevel <> 5` limits it to silent/basic-UI installs. |

---

## `xmlEdits:`

Set values in installed XML files at install time (`util:XmlFile`, `setValue`). Each edit lives in
the component that installs the file, so it reapplies on repair.

| Field | Type | Notes |
|---|---|---|
| `file` | string | Installed path of the XML file, e.g. `[INSTALLDIR]\\settings.xml` |
| `path` | string | XPath of the node to set; may select an attribute directly (`/Root/@attr`) |
| `name` | string | Optional attribute name on the element selected by `path` |
| `value` | string | MSI-formatted; `[INSTALLDIR]` expands at install time (it ends with `\`) |

```yaml
xmlEdits:
  - file: "[INSTALLDIR]\\settings.xml"
    path: "/Config/@dataDir"
    value: "[INSTALLDIR]data"
```

---

## `fileAssociations:`

Register a file extension to an installed program.  Emits a non-advertised WiX
`ProgId` / `Extension` / `Verb` in the component that installs `file`, so the keys follow
that file's feature and are removed on uninstall.  `HKCR` maps to `HKLM\Software\Classes`
(perMachine) or `HKCU\Software\Classes` (perUser).

| Field | Type | Notes |
|---|---|---|
| `extension` | string | Required. Without the leading dot, e.g. `myx`. Must be unique. |
| `progId` | string | Default: the extension. Must be unique. |
| `description` | string | Shown by Explorer as the file type name. |
| `file` | string | Required. Installed file that opens the extension, e.g. `[INSTALLDIR]\MyApp.exe` |
| `icon` | string | Installed file holding the icon resource (exe, dll or `.ico`). Default: `file`. |
| `iconIndex` | int | Icon resource index. Default `0`. |
| `contentType` | string | Optional MIME type, e.g. `application/x-myx` |
| `verbs` | list | Default: one verb `open`, label `&Open`, args `"%1"` (quoted). |
| `verbs[].id` | string | Verb key, e.g. `open`, `edit`, `print`. Default `open`. |
| `verbs[].label` | string | Context-menu text; `&` marks the access key. |
| `verbs[].args` | string | Command-line arguments; MSI-formatted. Default `"%1"` (quoted). |

```yaml
fileAssociations:
  - extension: myx
    progId: MyApp.Document
    description: "MyApp Document"
    file: "[INSTALLDIR]\MyApp.exe"
```

---

## `registry:`

| Field | Type | Notes |
|---|---|---|
| `root` | string | `HKLM` (default), `HKCU`, `HKCR`, `HKU`. Use `HKCU` in a `perUser` package; `HKLM` and `HKU` need admin rights. |
| `key` | string | Registry key path under the hive |
| `name` | string | Value name. Omit for the default value. |
| `value` | string or conditional | Value data. Supports `[INSTALLDIR]` and WiX properties. |
| `type` | string | `string` (default), `expandString`, `multiString`, `dword`, `qword`, `binary` |
| `win64` | bool | `true` = 64-bit registry view, `false` = 32-bit (WOW64). Default: matches platform. |

---

## `services:`

| Field | Type | Notes |
|---|---|---|
| `name` | string | Internal service name (used by SCM) |
| `displayName` | string | Human-readable name in Services panel. Defaults to `name`. |
| `description` | string | Description shown in Services panel |
| `executable` | string | Path after install, e.g. `[INSTALLDIR]\\MyService.exe` |
| `arguments` | string | Command-line arguments |
| `account` | string | `LocalSystem` (default), `LocalService`, `NetworkService`, or `DOMAIN\user` |
| `password` | string | Password for domain accounts only |
| `start` | string | `auto` (default), `demand` / `manual`, `disabled`, `boot`, `system`. Controls SCM startup type (what happens at next reboot). |
| `startOnInstall` | bool | `true` (default). When `false`, the MSI registers the service but does NOT start it during install. Use for services that need post-install configuration. |
| `type` | string | `ownProcess` (default) or `shareProcess` |
| `errorControl` | string | `ignore`, `normal` (default), `critical` |
| `delayedAutoStart` | bool | Delay startup (auto-start services only) |
| `interactive` | bool | Allow service to interact with the desktop (legacy; requires `LocalSystem` account) |
| `onFailure` | block | Failure recovery actions (see below) |
| `dependsOn` | list | Service or group names this service depends on |

### `services[].onFailure:`

| Field | Type | Notes |
|---|---|---|
| `first` | string | Action on 1st failure: `none` (default), `restart`, `reboot`, `runCommand` |
| `second` | string | Action on 2nd failure |
| `third` | string | Action on 3rd failure |
| `resetAfterDays` | int | Reset failure count after N days of success |
| `restartDelaySeconds` | int | Seconds to wait before restarting after failure |

---

## `directories:`

Named install destinations outside `INSTALLDIR`. Referenced by `id` in `groups:`.

| Field | Type | Notes |
|---|---|---|
| `id` | string | Reference name used in `groups[].destinationDir` |
| `path` | string or conditional | Full path. Ignored when `type:` is set. |
| `type` | string | Well-known location shorthand (see table below) |
| `subPath` | string | Appended to the `type:` base. Required when `type:` is used. |

### Well-known `type:` values

| Type | Per-user path | Per-machine path |
|---|---|---|
| `config` | `%AppData%` | `%ProgramData%` |
| `localdata` | `%LocalAppData%` | `%ProgramData%` |
| `desktop` | User desktop | All-users desktop |
| `startmenu` | User Start Menu | All-users Start Menu |
| `startup` | User Startup folder | All-users Startup folder |
| `psmodules51` | `My Documents\WindowsPowerShell\Modules` | `Program Files\WindowsPowerShell\Modules` |
| `psmodules7` | `My Documents\PowerShell\Modules` | `Program Files\PowerShell\7\Modules` |

---

## `groups:`

Files that need component semantics -- a fixed destination, an overwrite rule, or
an uninstall rule -- rather than plain placement in the install tree.

| Field | Type | Notes |
|---|---|---|
| `id` | string | Group name (used in logging) |
| `destinationDir` | string | A `directories[].id`, or a bracketed WiX path written inline: `[INSTALLDIR]`, `[INSTALLDIR]\Help`, `[CommonAppDataFolder]\Acme` |
| `condition` | string | `notExists` — skip file if destination already exists |
| `permanent` | bool | `true` — file survives uninstall |
| `files` | list | List of `{ source, rename? }` entries |

---

## `features:`

Optional selectable installer features shown as checkboxes in the installer UI.

| Field | Type | Notes |
|---|---|---|
| `id` | string | Internal identifier |
| `name` | string | Display name in feature tree |
| `description` | string | Shown when feature is selected in UI |
| `default` | bool | Whether checked by default. Default: `true` |
| `display` | string | `collapse` (default), `expand`, or `hidden` |
| `structure` | list | StructureElement list for this feature's files |
| `shortcuts` | list | Shortcuts installed with this feature |
| `environment` | list | Environment variables for this feature |
| `registry` | list | Registry entries for this feature |
| `services` | list | Services installed with this feature |
| `groups` | list | File groups for this feature |

---

## `releaseFlags:` and `defaultActiveFlags:`

```yaml
releaseFlags:
  - PerUser
  - PerMachine

defaultActiveFlags:
  - PerUser
```

`releaseFlags:` declares which flag names are valid. `defaultActiveFlags:` sets which flags are active when no `--flag` argument is passed. Flags are activated at build time with `--flag <name>`.

When a flag is active, any field that accepts a **conditional map** resolves to the matching value:

```yaml
installScope:
  PerUser:    perUser
  PerMachine: perMachine
  _else:      perUser     # fallback when no matching flag is active
```

Conditional maps are supported on: `product.installScope`, `product.installDir`, `directories[].path`, `environment[].value`, `environment[].scope`, `registry[].value`.

---

## `signing:`

| Field | Type | Notes |
|---|---|---|
| `thumbprint` | string | SHA1 cert thumbprint in Windows cert store |
| `pfx` | string | Path to PFX file (resolved via aliases/tokens) |
| `pfxPassword` | string | PFX password; supports `$(TOKEN)` substitution |
| `azure` | block | Azure Trusted Signing config (see below) |
| `command` | string | Shell command; `{file}` is replaced with the file path |
| `timestampUrl` | string | RFC 3161 timestamp server URL |
| `signToolPath` | string | Explicit path to `signtool.exe`. Auto-discovered when omitted. |
| `files` | block | Sign packaged files before WiX packages them |

Exactly one of `thumbprint`, `pfx`, `azure`, or `command` is required.

### `signing.azure:`

| Field | Type | Notes |
|---|---|---|
| `endpoint` | string | Regional endpoint, e.g. `https://eus.codesigning.azure.net` |
| `account` | string | Azure Trusted Signing account name |
| `certificateProfile` | string | Certificate profile name |
| `dlibPath` | string | Path to `Azure.CodeSigning.Dlib.dll` |
| `correlationId` | string | Optional audit tracking value; supports `$(TOKEN)` |

### `signing.files:`

| Field | Type | Notes |
|---|---|---|
| `mode` | string | `unsigned` (default) — skip already-signed files. `all` — sign everything. |
| `include` | list | Filename globs. When omitted, the Windows SIP table determines what is signable. |
| `exclude` | list | Filename globs always excluded after the include/SIP check. |

See [Signing](signing.md) for the full reference.

---

## `wixToolsPath:`

```yaml
wixToolsPath: "C:/tools/wix5/bin"
```

Pin AlliePack to a specific WiX installation. Also settable via the `WIXSHARP_WIXLOCATION` environment variable.

---

## `wix:`

Raw WiX XML escape hatch.

| Field | Type | Notes |
|---|---|---|
| `fragments[].inline` | string | Inline WiX XML string |
| `fragments[].file` | string | Path to a `.wxs` file (resolved via aliases/tokens) |

```yaml
wix:
  fragments:
    - inline: |
        <Fragment>
          <Property Id="MYKEY" Value="1" />
        </Fragment>
    - file: "installer/extra.wxs"
```

---

## Built-in tokens

Available in `source:`, `aliases:`, `variables:`, and most string fields.

| Token | Resolves to |
|---|---|
| `$(YamlDir)` | Directory containing the YAML config file |
| `$(GitRoot)` | Root of the nearest git repository (walks up from YAML dir) |
| `$(CurrentDir)` | Working directory when AlliePack is invoked |
| `$(ProgramFiles)` | Build-time shorthand for `installDir`: resolves to `[ProgramFiles64Folder]` on x64/arm64, `[ProgramFilesFolder]` on x86 |
| `$(KEY)` | Value of `--define KEY=VALUE` or `variables.KEY` |

WiX install-time properties (not build-time tokens) are also valid in path and value fields where noted:

| Property | Resolves to (at install time) |
|---|---|
| `[INSTALLDIR]` | The root installation directory chosen by the user |
| `[ProgramFilesFolder]` | `C:\Program Files (x86)` |
| `[ProgramFiles64Folder]` | `C:\Program Files` |
| `[AppDataFolder]` | `%AppData%` (current user) |
| `[LocalAppDataFolder]` | `%LocalAppData%` (current user) |
| `[CommonAppDataFolder]` | `%ProgramData%` (all users) |
| `[PersonalFolder]` | `My Documents` |
| `[WindowsFolder]` | `C:\Windows` |
| `[SystemFolder]` | `C:\Windows\System32` |
