# AlliePack

A YAML-driven build tool that transforms a human-readable configuration file into a Windows MSI installer, using WixSharp and WiX v5 as the underlying engine.

## Language

**Package Definition**:
The `allie-pack.yaml` file that declaratively describes a Package. A Package Definition is the primary input to AlliePack -- it describes what the installer should contain, not how to build it.
_Avoid_: Script (implies imperative execution), Manifest (collides with Windows assembly and WinGet manifests), Config (too generic), Definition (ambiguous in isolation)

**Product**:
The software application being packaged for distribution -- what the end user thinks of as "the application they're installing." Described by the `product:` block in `allie-pack.yaml`.
_Avoid_: Application, App, Software

**Flag**:
A named boolean switch declared in a Package Definition and passed on the CLI at build time. Flags condition which content is included in the resulting Package, allowing a single Package Definition to produce multiple distinct Packages.
_Avoid_: Release flag (implies flags are only relevant at release time), Option, Switch

**Profile** *(not yet implemented)*:
A named, reusable combination of Flags. A Profile would let a user select a preconfigured variant by name rather than passing individual Flags on the CLI. Treat as a future concept until designed.

**Alias**:
A named path prefix defined in the Package Definition and referenced in Sources using `$(aliasName):remainder` syntax. An Alias resolves to a single base path at build time -- it is not a glob and cannot expand to multiple paths on its own.
_Avoid_: Variable

**Compile-time Token**:
A `$(Name)` placeholder in a Package Definition resolved by AlliePack on the build machine before the Package is produced. Built-in examples: `$(YamlDir)`, `$(GitRoot)`. User-defined Aliases use the same syntax. See ADR-0003.
_Avoid_: Token (ambiguous -- distinguish from Install-time Token)

**Install-time Token**:
A `[Name]` placeholder in a Package Definition resolved by Windows Installer on the end user's machine when the Package runs. Examples: `[INSTALLDIR]`, `[ProgramFilesFolder]`. These are WiX/MSI values and cannot be changed. See ADR-0003.
_Avoid_: Token (ambiguous -- distinguish from Compile-time Token)

**Source**:
An entry in the `structure:` block of a Package Definition that describes where files come from -- a path, alias, glob, Visual Studio project output, or directory tree. A Source resolves to one or more files at build time; each resolved file becomes its own WiX Component in the generated Package.
_Avoid_: Component (a WiX implementation detail AlliePack generates automatically and does not expose to users)

**Feature**:
A named, user-selectable group of content shown in the installer's custom-setup dialog. Maps directly to a WiX `<Feature>` element. Users opt in or out of Features at install time.
_Avoid_: Option, Module, Component (a distinct WiX concept AlliePack does not expose directly)

**Scope**:
Whether a Package installs for all users on the machine (`perMachine`) or only for the current user (`perUser`, `perUserOrMachine`). Declared in the `product:` section of the Package Definition. Valid values are `perMachine`, `perUser` and `perUserOrMachine`; "both" was removed -- see ADR-0004.
`perUserOrMachine` is the WiX dual-purpose package (ALLUSERS=2, MSIINSTALLPERUSER=1): it installs for the current user by default, with Program Files redirected to %LOCALAPPDATA%\Programs, but an admin can force a per-machine install by clearing MSIINSTALLPERUSER; AlliePack's shortcuts, env vars and typed directories stay per-user either way. `perUser` is a true WiX perUser package (no ALLUSERS): it installs for the current user only, never elevates, and cannot be forced per-machine. Its default install directory is %LOCALAPPDATA%\Programs\<Manufacturer>\<Name>, and AlliePack's `[ProgramFiles]` token resolves there too. Content that needs admin rights (per-machine paths such as `[ProgramFiles64Folder]`, HKLM/HKU registry values, `scope: machine` environment variables, services) builds with a warning, because a non-admin install of it will fail; `--strict` makes those warnings errors.
_Avoid_: "both" (removed), "machine-wide" / "current user" (plain English acceptable verbally, but perMachine / perUser are canonical in the Package Definition)

**Portable WXS Export** *(not yet implemented)*:
A self-contained artifact directory produced by `--export-wxs` containing a WiX source file (with relative paths and a `$(var.Version)` placeholder), bundled WixSharp runtime DLLs, and a `build.ps1` script. A customer CI/CD pipeline can compile it into a versioned MSI using only `wix.exe` -- no AlliePack or WixSharp installation required. The same artifact can be compiled for any version; the pipeline supplies `$Version` at compile time. See ADR-0005 and issue #40.
_Avoid_: Portable installer (that's the MSI output), WXS export (implies just a file, not the full artifact directory)

**Bundle** *(not yet implemented)*:
A WiX Burn bootstrapper `.exe` that chains prerequisites and Packages into a single installer. A Bundle is a distinct output artifact from a Package -- it wraps one or more Packages and manages their sequenced installation.
_Avoid_: Setup, Bootstrapper (acceptable informally, but Bundle is the WiX term)

**Package**:
The `.msi` file AlliePack produces. Maps to WiX v5's `<Package>` element.
_Avoid_: Installer (too broad -- includes Bundles), MSI (implementation detail)
