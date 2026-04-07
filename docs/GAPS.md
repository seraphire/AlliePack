# AlliePack Capability Gap Analysis

Sources: InstallShield Editions comparison, InstallAware comparison, WiX v6 schema reference,
WiX extension inventory. Items marked **[Roadmap]** are already documented in ROADMAP.md.

---

## 1. Package Output Formats

| Gap | Notes |
|---|---|
| MSI Patches (MSP) | Diff-based patches for minor upgrades; WiX has full `Patch`/`PatchFamily` support |
| MSIX packages | Modern AppX-style packaging; WiX 6 + FireGiant MSIX extension |
| MSIX bundles | Multi-arch MSIX bundle (`.msixbundle`) |
| MSI-to-MSIX conversion | Convert existing MSI to MSIX; InstallShield advertises this prominently |
| MST transform creation | Build `.mst` customisation transforms from a config delta |
| Merge modules (.msm) | Create reusable component packages; WiX `Module` element |
| Suite/Bundle installer **[Roadmap]** | Bootstrapper that chains prereqs + main MSI; WiX Burn (`Bundle`) |
| EXE self-extractor wrapper | Wrap MSI in a setup.exe; distinct from Burn bundles |
| App-V package builder | Application virtualisation packaging; niche but present in premium tools |
| Streaming installation | Download features on demand during install; WiX `Media`/web deployment |

---

## 2. UI and Dialogs

| Gap | Notes |
|---|---|
| Custom dialog editor | WYSIWYG layout of MSI dialogs beyond the built-in WiX UI sets |
| Dialog themes / skins | Styled progress/welcome/finish screens; WixUI ships several; AlliePack exposes none |
| Dark mode / Mica / Acrylic UI | Windows 11-native installer appearance; InstallAware feature |
| Billboard / progress screens | Slide-show during file copy; WiX `Billboard` element in WixUI |
| Taskbar progress integration | Show install % on taskbar button; WixUI has this but not exposed |
| Multi-language / localisation | Ship installer in N languages; WiX `WixLocalization` (.wxl) files |
| EULA enforcement | Require scroll-to-bottom before enabling Next; WixUI `LicenceDialog` variant |
| System tray minimisable UI | Install continues minimised to tray; InstallAware feature |
| Start Screen / Taskbar pinning | Pin shortcuts to taskbar or Start at install time; Windows 10/11 specific |

---

## 3. File System Operations (beyond copy)

| Gap | Notes |
|---|---|
| XML file editing | Patch existing XML config on the target machine; WiX `XmlFile` (Util ext) |
| INI file editing | Read/write `.ini` values; WiX `IniFile` element (core schema) |
| Text file editing | Search-and-replace in text files; InstallShield "text file changes" |
| File search (AppSearch) | Detect existing files/dirs on the target; WiX `FileSearch`/`DirectorySearch` |
| File copy / move / delete | Copy or remove files that are NOT part of the install tree; WiX `CopyFile`, `RemoveFile` |
| Font installation | Install `.ttf`/`.otf` fonts; WiX `Font` element |
| NTFS permissions (ACL) | Set explicit read/write/execute ACLs; WiX `PermissionEx` (Util ext) |
| Mark file for deletion | Schedule file removal on next reboot; WiX `RemoveFile` with `On` attribute |

---

## 4. Registry (beyond basic values)

| Gap | Notes |
|---|---|
| Registry search | Read existing reg values during install; WiX `RegistrySearch` element |
| NTFS permission on reg keys | Set ACL on a registry key; WiX `PermissionEx` |
| Registry-free COM manifests | Embed COM registration in app manifest instead of HKCR; Application Manifests |

---

## 5. Windows System Configuration

| Gap | Notes |
|---|---|
| User and group creation | Create local accounts/groups; WiX `User`/`Group` (Util ext) |
| User permissions | Grant logon-as-service, logon-locally, etc.; WiX `User` with permission attributes |
| Scheduled tasks | Create Windows Task Scheduler tasks; WiX `ScheduledTask` (Util ext) |
| Windows Features / Roles | Enable/disable optional Windows Features (e.g. IIS, Hyper-V); WiX `WindowsFeature` (Util ext) |
| Server Roles configuration | Enable Windows Server roles at install time |
| Event log source | Register an event source under `HKLM\SYSTEM\...EventLog`; WiX `EventSource` (Util ext) |
| Performance counters | Install `.ini` + `.h` perf counter definitions; WiX `PerfCounter` |
| Environment variable search | Read `%VAR%` values for use in install logic; WiX property + `Environment` search |
| Restart Manager integration | Gracefully close running apps that lock files; WiX `CloseApplication` (Util ext) |

---

## 6. COM, Assemblies, and Legacy Windows

| Gap | Notes |
|---|---|
| COM class registration | Register `.dll` COM servers in HKCR; WiX `Class`, `ProgId`, `Interface` elements |
| COM+ applications | Deploy COM+ components and applications; WiX ComPlus extension |
| Type library registration | Register `.tlb` files; WiX `TypeLib` element |
| COM self-registration | Fallback `regsvr32`-style registration; WiX `File/@SelfRegCost` |
| APPID registration | Register COM application identity; WiX `AppId` element |
| .NET assembly GAC install | Install strongly-named assemblies to GAC; WiX `File/@Assembly` |
| File association | Register file extensions with verbs (open, edit); WiX `Extension`/`Verb`/`MIME` |
| VBScript/JScript custom actions **[Roadmap]** | Inline script-based custom actions; WiX `CustomAction` with `Script` |

---

## 7. Networking and Server Features

| Gap | Notes |
|---|---|
| IIS website/virtual directory | Create IIS sites, apps, app pools; WiX IIS extension (`WebSite`, `WebApp`, etc.) |
| IIS SSL certificate binding | Bind a cert to an HTTPS site; WiX IIS extension |
| IIS web.config transforms | Apply XML transforms to `web.config` during install |
| SQL Server script execution | Run `.sql` scripts at install time; WiX SQL extension (`SqlDatabase`, `SqlScript`) |
| MySQL / Oracle scripts | SQL deployment to non-MSSQL servers; InstallShield / InstallAware advertise this |
| Windows Firewall rules | Open/close inbound/outbound ports; WiX `FirewallException` (WixToolset.Firewall ext) |
| HTTP URL reservations | `netsh http add urlacl`; WiX `UrlReservation` (Util ext) |
| Certificate installation | Install PFX/CER into Windows cert stores; WiX `Certificate` (Util ext) |

---

## 8. Security and Signing

| Gap | Notes |
|---|---|
| MSI signing **[Roadmap]** | `signtool`/`osslsigncode` applied to the final `.msi` |
| Binary/EXE signing at build time | Sign included binaries before packaging |
| Azure Key Vault signing | Cloud HSM-backed signing; InstallShield Premier feature |
| Azure Trusted Signing | Microsoft's hosted signing service (successor to Authenticode EV) |
| AWS CloudHSM signing | AWS HSM-backed code signing |
| EV certificate support | Extended-Validation Authenticode; removes SmartScreen warnings |
| MSIX code signing | MSIX must be signed; separate from MSI signing |
| SHA-256 Authenticode | Default for modern Windows; ensure toolchain uses it not SHA-1 |

---

## 9. Prerequisites and Bootstrapper

| Gap | Notes |
|---|---|
| Burn bootstrapper **[Roadmap]** | Chain MSIs, EXEs, MSPs into one setup.exe with prereq detection |
| .NET runtime detection/install | Detect + install .NET 6/7/8/9 desktop/ASP/server runtime |
| .NET Framework detection | Check 4.x presence; WiX NetFx extension |
| Visual C++ redistributable | Detect + install VC++ runtimes; very common prereq |
| DirectX detection | Check DirectX caps; WiX DirectX extension |
| Windows version / build check | `LaunchCondition` based on `VersionNT`, `WindowsBuild` properties |
| Disk space validation | Enforce minimum free space before install begins |
| Admin privilege check | Enforce or detect elevation; `Privileged` MSI property |
| Existing version detection | `MajorUpgrade` / `FindRelatedProducts` upgrade flow |
| Winget manifest generation **[Roadmap]** | Output `.winget` manifest alongside the MSI |

---

## 10. Patching and Updates

| Gap | Notes |
|---|---|
| MSP patch creation | Binary-diff based patch between two MSI versions |
| Minor upgrade MSI | Re-install over existing with `REINSTALLMODE=vomus` |
| Online update check | Check URL for newer version at install/launch time |
| Web media blocks | Download individual features from CDN during install |
| One-click / differential patching | Binary-diff patch applied without full reinstall |

---

## 11. Build, CI, and DevOps

| Gap | Notes |
|---|---|
| MSBuild / SDK-style integration | Drive AlliePack from `.csproj` as a build step |
| Azure DevOps pipeline task | First-class build task in ADO pipelines |
| GitHub Actions action | Equivalent for GitHub CI |
| Automated VM testing | Launch a clean VM, install, snapshot, verify |
| Build events / pre-post hooks | Run arbitrary scripts before/after MSI compilation |
| Parallel / multi-core builds | Compress cab files using multiple cores |
| Release management | Named release configurations beyond flags (staging, prod, OEM) |
| Source control diff / merge | Diff two YAML configs or two MSI databases |

---

## 12. Custom Actions and Sequencing

| Gap | Notes |
|---|---|
| Custom action authoring **[Roadmap]** | C#/.NET CA DLLs, EXE launchers, deferred/impersonated CAs |
| Install sequence control **[Roadmap]** | Explicit ordering: Before/After standard actions |
| Deferred / commit / rollback CAs **[Roadmap]** | Run under elevated context; with rollback companion |
| Admin image / advertised install | `ADMIN` installs and feature advertisement |
| Quiet execution CA | Run an EXE silently with exit code checking; WiX Util `QtExecCA` |
| WMI queries | Read WMI for hardware/OS info in conditions |
| PowerShell custom actions | Run `.ps1` scripts at install time; WiX PowerShell ext |

---

## 13. Search and Condition System

| Gap | Notes |
|---|---|
| File / directory search | `AppSearch` to detect existing software by file path |
| Registry value search | Read a reg value into an MSI property |
| Component state search | Check if another MSI component is installed |
| Windows Installer property queries | `MsiGetProperty`, component states, feature states |
| Launch conditions | Block install if OS version, disk space, or prereqs not met |
| Compile-time conditional content | Include/exclude whole feature blocks based on a flag (partially done) |

---

## 14. Exotic / Niche

| Gap | Notes |
|---|---|
| Device driver installation | `DIFx` / `DifxApp` for kernel/KMDF/UMDF drivers; WiX Difx extension |
| ODBC data source / driver | Register ODBC DSNs and drivers; WiX `ODBCDataSource`, `ODBCDriver` |
| ISO/IEC 19770-2 SWID tags | Software identification tags for asset management |
| Multi-instance installs | Install the same product multiple times side-by-side |
| Advertised / partial installs | MSI feature advertisement (install on first use) |
| Optical media / CD layout | Split cab across multiple discs; WiX `Media` element |
| 256-bit AES cab encryption | Encrypt the embedded cabinet file |
| Try-and-die DRM | Time-limited trial install; niche but present in InstallAware |
| Cross-platform (macOS/Linux) **[Roadmap Horizon]** | Separate packaging targets; entirely different toolchain |
| App-V sequencing | Application virtualisation; largely legacy |
| MSIX modification packages | Delta packages that modify an installed MSIX |

---

## Summary: Highest-Value Gaps Not Yet on the Roadmap

These are gaps that appear across both InstallShield and InstallAware, are supported natively by
WiX extensions, and would cover real-world use cases that users currently work around via
`wix: fragments:`:

| Priority | Gap | WiX support |
|---|---|---|
| High | Windows Firewall rules | `WixToolset.Firewall.wixext` |
| High | IIS site / app pool / virtual dir | `WixToolset.Iis.wixext` |
| High | XML file patching (`XmlFile`) | `WixToolset.Util.wixext` |
| High | User / group creation | `WixToolset.Util.wixext` |
| High | Scheduled tasks | `WixToolset.Util.wixext` |
| High | Certificate installation | `WixToolset.Util.wixext` |
| Medium | SQL Server script execution | `WixToolset.Sql.wixext` |
| Medium | NTFS permissions (`PermissionEx`) | `WixToolset.Util.wixext` |
| Medium | Windows Features on/off | `WixToolset.Util.wixext` |
| Medium | Event log source registration | `WixToolset.Util.wixext` |
| Medium | File association (ext/verb) | WiX core schema |
| Medium | AppSearch / launch conditions | WiX core schema |
| Medium | INI file editing | WiX core schema |
| Low | COM class/ProgId/TypeLib registration | WiX core schema |
| Low | ODBC data sources | WiX core schema |
| Low | MSP patch creation | WiX core schema (`Patch`) |
| Low | Device drivers (DIFx) | `WixToolset.Difx.wixext` |
