# perUser is a true WiX perUser package

AlliePack offers three install scopes: `perMachine`, `perUser` and `perUserOrMachine`. Until this decision, `perUser` was built as WiX `perUserOrMachine` (ALLUSERS=2, MSIINSTALLPERUSER=1) with a build warning. The reason was the default install directory, `[ProgramFiles(64)Folder]\<Manufacturer>\<Name>`. A WiX `perUser` package leaves ALLUSERS empty, so MSI does not redirect Program Files, and a non-admin install there fails.

`perUser` now emits WiX `Scope="perUser"`. To make that installable without elevation, the default install directory under `perUser` is `[LocalAppDataFolder]\Programs\<Manufacturer>\<Name>`. That is the same `%LOCALAPPDATA%\Programs` location that `perUserOrMachine` redirects to, so users see the same path either way. AlliePack's `[ProgramFiles]` token and relative installDir paths follow the scope. Explicit WiX folder properties such as `[ProgramFiles64Folder]` are passed through as written.

Content that needs admin rights is a build warning under `perUser`, not an error. This covers per-machine paths (Program Files, CommonAppData, Windows and similar), HKLM and HKU registry values, `scope: machine` environment variables, and services. It compiles, and an admin running the MSI elevated can install it, but a non-admin install fails. `--strict` turns these warnings into errors. The rule for `--strict` is general: it escalates warnings about things that build but will fail at install time.

Alternatives considered:
- **Keep the perUserOrMachine fallback.** Rejected because `perUser` did not mean what it says, and `perUserOrMachine` already exists for the dual-purpose case.
- **Rewrite explicit Program Files paths to LocalAppData.** Rejected because silently moving a path the author wrote is worse than a warning.
- **Always fail on admin-only content.** Rejected because an elevated install of such a package still succeeds, so that is the author's call, and `--strict` makes it enforceable.

## Consequences

Existing `perUser` Package Definitions change behaviour. They now produce a plain per-user package with no ALLUSERS, which never elevates and cannot be forced per-machine by clearing MSIINSTALLPERUSER. A definition that needs the old behaviour should say `installScope: perUserOrMachine`.

Registry entries default to `root: HKLM`, so a `perUser` definition that omits `root` now warns. Set `root: HKCU` for per-user values.
