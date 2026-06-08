using Microsoft.Extensions.FileSystemGlobbing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;
using WixSharp;
using WixSharp.CommonTasks;
using WixSharp.UI.WPF;
using YamlDotNet.Serialization;
using File = WixSharp.File;

namespace AlliePack
{
    public class InstallerBuilder
    {
        private readonly AlliePackConfig _config;
        private readonly PathResolver _resolver;
        private readonly SolutionResolver _solutionResolver;
        private readonly Options _options;
        private readonly IReadOnlyList<string> _activeFlags;

        private void Debug(string message) { if (_options.Debug) Console.WriteLine($"  [debug] {message}"); }

        public InstallerBuilder(AlliePackConfig config, PathResolver resolver, SolutionResolver solutionResolver, Options options, IReadOnlyList<string> activeFlags)
        {
            _config = config;
            _resolver = resolver;
            _solutionResolver = solutionResolver;
            _options = options;
            _activeFlags = activeFlags;
        }

        public void Build()
        {
            if (string.IsNullOrWhiteSpace(_config.Product.UpgradeCode))
                throw new InvalidOperationException(
                    "product.upgradeCode is required and must be a stable GUID. " +
                    "Add 'upgradeCode: <your-guid>' to the product: block in allie-pack.yaml. " +
                    "Generate one with: [System.Guid]::NewGuid() (PowerShell) or uuidgen (Linux/Mac).");

            // Resolve WiX tool location: YAML wixToolsPath > WIXSHARP_WIXLOCATION env var > WixSharp discovery.
            // If wixToolsPath contains an unresolved token (e.g. "[WixTools]" with no --define
            // supplied), treat it as absent so the env-var fallback can still apply.
            string? wixLocation = null;
            if (_config.WixToolsPath != null)
            {
                string resolved = _resolver.Resolve(_config.WixToolsPath);
                if (!resolved.Contains('['))          // fully resolved -- no remaining tokens
                    wixLocation = resolved;
                else
                    Debug($"wixToolsPath token not resolved: {resolved} -- falling back to env var");
            }
            wixLocation ??= Environment.GetEnvironmentVariable("WIXSHARP_WIXLOCATION");

            if (!string.IsNullOrEmpty(wixLocation))
            {
                // Prepend to PATH so this wix.exe is found before any system-installed version.
                string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                Environment.SetEnvironmentVariable("PATH", wixLocation + Path.PathSeparator + currentPath);
                if (_options.IsVerbose)
                    Console.WriteLine($"  wix tools: {wixLocation}");
            }
            else
            {
                Debug("wix tools: using PATH discovery (WIXSHARP_WIXLOCATION not set)");
            }

            // Tell WixSharp which WiX version to use.
            // Must pass Environment.CurrentDirectory (not wixLocation) because WixSharp's
            // GlobalWixVersion walks upward from CWD looking for .config/dotnet-tools.json.
            // Writing it into wixLocation (e.g. the dotnet tools dir) puts it somewhere
            // WixSharp never searches, leaving GlobalWixVersion null and causing wix.tools.csproj
            // to use version="*" which resolves to WiX 7.
            WixTools.SetWixVersion(Environment.CurrentDirectory, "5.0.2");
            WixExtension.UI.PreferredVersion   = "5.0.2";
            WixExtension.Util.PreferredVersion = "5.0.2";

            // Debug: dump resolved config
            if (_options.Debug)
            {
                Console.WriteLine($"  [debug] Wix Settings:");
                Console.WriteLine($"  [debug] WixTools.WixSharpToolDir      : {WixTools.WixSharpToolDir}");
                Console.WriteLine($"  [debug] WixTools.WixExtensionsDir     : {WixTools.WixExtensionsDir}");
                Console.WriteLine($"  [debug] WixTools.DtfWindowsInstaller  : {WixTools.DtfWindowsInstaller}");
                Console.WriteLine();

                string resolvedVer = _config.Product.Version.Resolve(_resolver);
                Console.WriteLine($"  [debug] product.name       : {_config.Product.Name}");
                Console.WriteLine($"  [debug] product.version    : {resolvedVer}");
                Console.WriteLine($"  [debug] product.platform   : {_config.Product.Platform}");
                Console.WriteLine($"  [debug] product.installScope: {_config.Product.InstallScope.Resolve(_activeFlags)}");
                Console.WriteLine($"  [debug] product.installDir : {_config.Product.InstallDir?.Resolve(_activeFlags) ?? "(default)"}");
                Console.WriteLine($"  [debug] product.upgradeCode: {_config.Product.UpgradeCode}");
                Console.WriteLine($"  [debug] aliases ({_config.Aliases.Count}):");
                foreach (var a in _config.Aliases)
                    Console.WriteLine($"  [debug]   {a.Key}: -> {_resolver.Resolve(a.Value)}");
                if (_config.Variables.Count > 0)
                {
                    Console.WriteLine($"  [debug] variables ({_config.Variables.Count}):");
                    foreach (var p in _config.Variables)
                        Console.WriteLine($"  [debug]   [{p.Key}] = {p.Value} -> {_resolver.Resolve(p.Value)}");
                }
                Console.WriteLine($"  [debug] active flags: [{string.Join(", ", _activeFlags)}]");
                Console.WriteLine($"  [debug] yaml dir    : {_resolver.WorkingDirectory}");
            }

            var allFiles = new List<ResolvedFile>();
            foreach (var element in _config.Structure)
                allFiles.AddRange(ProcessElement(element));

            // Build WixSharp Feature objects and collect feature-tagged files
            var duplicateIds = _config.Features
                .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicateIds.Any())
                throw new InvalidOperationException(
                    $"Duplicate feature id(s) in features: block: {string.Join(", ", duplicateIds)}. Each id must be unique.");

            var wixFeatures = new Dictionary<string, Feature>(StringComparer.OrdinalIgnoreCase);
            foreach (var fc in _config.Features)
            {
                var wixFeature = new Feature(fc.Name, fc.Description, fc.Default)
                {
                    Display = ParseFeatureDisplay(fc.Display)
                };
                wixFeatures[fc.Id] = wixFeature;

                foreach (var element in fc.Structure)
                {
                    var featureFiles = ProcessElement(element);
                    foreach (var f in featureFiles)
                        f.WixFeature = wixFeature;
                    allFiles.AddRange(featureFiles);
                }
            }

            // Deduplicate
            var uniqueFiles = DeduplicateFiles(allFiles);

            if (_options.Debug)
            {
                Console.WriteLine($"  [debug] files resolved: {uniqueFiles.Count}");
                foreach (var f in uniqueFiles)
                    Console.WriteLine($"  [debug]   {f.SourcePath}");
                Console.WriteLine($"  [debug]   -> destination paths:");
                foreach (var f in uniqueFiles)
                    Console.WriteLine($"  [debug]   {f.RelativeDestinationPath}");
            }

            var entities = new List<WixEntity>();

            // --- Scope resolution ---
            string rawInstallScope = _config.Product.InstallScope.Resolve(_activeFlags);
            string effectiveScope  = ComputeEffectiveScope(rawInstallScope);
            bool isMachine = effectiveScope.Equals("perMachine", StringComparison.OrdinalIgnoreCase);

            // --- installDir resolution ---
            string resolvedInstallDir = _config.Product.InstallDir?.Resolve(_activeFlags) ?? string.Empty;
            string installPath;
            if (!string.IsNullOrEmpty(resolvedInstallDir))
                installPath = resolvedInstallDir;
            else
                installPath = _resolver.Tokens.Substitute(_config.Product.Manufacturer)
                            + "\\" + _resolver.Tokens.Substitute(_config.Product.Name);

            installPath = installPath.Replace('/', '\\');
            
            bool is64 = _config.Product.Platform.Equals("x64", StringComparison.OrdinalIgnoreCase) || 
                        _config.Product.Platform.Equals("arm64", StringComparison.OrdinalIgnoreCase);

            if (installPath.StartsWith("[ProgramFiles]\\", StringComparison.OrdinalIgnoreCase))
            {
                string pfFolder = is64 ? "[ProgramFiles64Folder]" : "[ProgramFilesFolder]";
                installPath = pfFolder + "\\" + installPath.Substring("[ProgramFiles]\\".Length);
            }

            if (!installPath.Contains("[") && !Path.IsPathRooted(installPath))
            {
                // Use the platform-appropriate Program Files folder as the default root.
                string defaultPf = is64 ? "[ProgramFiles64Folder]" : "[ProgramFilesFolder]";
                installPath = Path.Combine(defaultPf, installPath);
            }
            
            Dir? rootDir = null;
            Dir? targetDir = null;

            string[] pathParts = installPath.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length > 0 && pathParts[0].StartsWith("[") && pathParts[0].EndsWith("]"))
            {
                rootDir = new Dir(pathParts[0]);
                targetDir = rootDir;
                for (int i = 1; i < pathParts.Length; i++)
                {
                    var next = new Dir(pathParts[i]);
                    targetDir.Dirs = new[] { next };
                    targetDir = next;
                }
            }
            else
            {
                // Default WixSharp InstallDir behavior
                var idir = new InstallDir(installPath);
                rootDir = idir;
                targetDir = idir;
            }

            // Convert to WixSharp hierarchy
            var (hierarchy, fileMap) = ConvertResolvedFilesToEntities(uniqueFiles, installPath);
            foreach (var entity in hierarchy)
            {
                if (entity is Dir childDir) 
                {
                    targetDir.Dirs = (targetDir.Dirs ?? new Dir[0]).Concat(new[] { childDir }).ToArray();
                }
                else if (entity is File childFile) 
                {
                    targetDir.Files = (targetDir.Files ?? new File[0]).Concat(new[] { childFile }).ToArray();
                }
                else 
                {
                    entities.Add(entity);
                }
            }
            // Environment variables -- attached to INSTALLDIR so they
            // are installed/removed with the product.
            foreach (var ev in _config.Environment)
            {
                string evScope = ResolveEnvScope(ev, rawInstallScope, isMachine);
                bool isSystem = evScope.Equals("machine", StringComparison.OrdinalIgnoreCase);
                var envVar = new EnvironmentVariable(ev.Name, ev.Value.Resolve(_activeFlags))
                {
                    System = isSystem,
                    Permanent = false,
                };
                targetDir.GenericItems = (targetDir.GenericItems ?? new IGenericEntity[0])
                    .Concat(new IGenericEntity[] { envVar }).ToArray();
            }

            // Registry values
            foreach (var reg in _config.Registry)
            {
                // Resolve [INSTALLDIR] and WiX properties in the value string.
                // WiX expands these at install time; we preserve any unresolved [Property]
                // tokens so they pass through to the generated XML intact.
                string rawValue = reg.Value.Resolve(_activeFlags);
                rawValue = rawValue.Replace("[INSTALLDIR]", "[INSTALLDIR]"); // passthrough -- WiX expands at runtime
                object typedValue = ParseRegistryValueData(rawValue, reg.Type);

                // Win64: per-entry override > platform default.
                // true  = write to 64-bit registry view (HKLM\SOFTWARE\...)
                // false = write to 32-bit/WOW64 view (HKLM\SOFTWARE\WOW6432Node\...)
                bool regWin64 = reg.Win64 ?? is64;

                var regValue = new RegValue(
                    ParseRegistryHive(reg.Root),
                    reg.Key,
                    reg.Name,
                    typedValue)
                {
                    Win64 = regWin64
                };

                // For types WixSharp can't infer from the CLR object alone, set explicitly.
                string? wixType = MapRegistryTypeName(reg.Type);
                if (wixType != null)
                    regValue.Type = wixType;

                entities.Add(regValue);
            }

            entities.Add(rootDir);

            // Process Shortcuts
            var shortcutRootDirs = new Dictionary<string, Dir>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in _config.Shortcuts)
                AttachShortcut(s, installPath, isMachine, shortcutRootDirs, entities, fileMap);

            // Windows Services
            foreach (var svc in _config.Services)
            {
                string execPath = ResolvePath(svc.Executable, installPath);
                if (!fileMap.TryGetValue(execPath, out var svcFile))
                {
                    Console.WriteLine($"Warning: Service '{svc.Name}': executable not found in install tree: {svc.Executable}");
                    Console.WriteLine($"         (Resolved to: {execPath})");
                    Console.WriteLine($"         Ensure the executable is declared in structure: before services:");
                    continue;
                }

                svcFile.ServiceInstallers = (svcFile.ServiceInstallers ?? new IGenericEntity[0])
                    .Concat(new IGenericEntity[] { BuildServiceInstaller(svc) }).ToArray();

                if (_options.IsVerbose)
                    Console.WriteLine($"Service '{svc.Name}' -> {svc.Executable}");
            }

            // Named directory groups -- files installed outside INSTALLDIR
            var namedDirMap = _config.Directories
                .ToDictionary(d => d.Id, d => ResolveDirectoryPath(d, isMachine), StringComparer.OrdinalIgnoreCase);

            foreach (var group in _config.Groups)
            {
                if (!namedDirMap.TryGetValue(group.DestinationDir, out var destPath))
                {
                    Console.WriteLine($"Warning: Group '{group.Id}' references unknown directory '{group.DestinationDir}'");
                    continue;
                }
                var groupRootDir = BuildGroupDir(group, destPath, feature: null);
                if (groupRootDir != null) entities.Add(groupRootDir);
            }

            // Per-feature content
            foreach (var fc in _config.Features)
            {
                var wixFeature = wixFeatures[fc.Id];

                foreach (var ev in fc.Environment)
                {
                    string evScope = ResolveEnvScope(ev, rawInstallScope, isMachine);
                    bool isSystem = evScope.Equals("machine", StringComparison.OrdinalIgnoreCase);
                    var envVar = new EnvironmentVariable(ev.Name, ev.Value.Resolve(_activeFlags))
                    {
                        System = isSystem,
                        Permanent = false,
                        Feature = wixFeature,
                    };
                    targetDir.GenericItems = (targetDir.GenericItems ?? new IGenericEntity[0])
                        .Concat(new IGenericEntity[] { envVar }).ToArray();
                }

                foreach (var reg in fc.Registry)
                {
                    string rawValue = reg.Value.Resolve(_activeFlags);
                    object typedValue = ParseRegistryValueData(rawValue, reg.Type);
                    bool regWin64 = reg.Win64 ?? is64;
                    var regValue = new RegValue(ParseRegistryHive(reg.Root), reg.Key, reg.Name, typedValue)
                    {
                        Win64 = regWin64,
                        Feature = wixFeature,
                    };
                    string? wixType = MapRegistryTypeName(reg.Type);
                    if (wixType != null) regValue.Type = wixType;
                    entities.Add(regValue);
                }

                foreach (var s in fc.Shortcuts)
                    AttachShortcut(s, installPath, isMachine, shortcutRootDirs, entities, fileMap, featureLabel: fc.Id);

                foreach (var svc in fc.Services)
                {
                    string execPath = ResolvePath(svc.Executable, installPath);
                    if (!fileMap.TryGetValue(execPath, out var svcFile))
                    {
                        Console.WriteLine($"Warning: Feature '{fc.Id}' service '{svc.Name}': executable not found in install tree: {svc.Executable}");
                        continue;
                    }

                    svcFile.ServiceInstallers = (svcFile.ServiceInstallers ?? new IGenericEntity[0])
                        .Concat(new IGenericEntity[] { BuildServiceInstaller(svc) }).ToArray();

                    if (_options.IsVerbose)
                        Console.WriteLine($"Feature '{fc.Id}' service '{svc.Name}' -> {svc.Executable}");
                }

                foreach (var group in fc.Groups)
                {
                    if (!namedDirMap.TryGetValue(group.DestinationDir, out var destPath))
                    {
                        Console.WriteLine($"Warning: Feature '{fc.Id}' group '{group.Id}' references unknown directory '{group.DestinationDir}'");
                        continue;
                    }
                    var groupRootDir = BuildGroupDir(group, destPath, wixFeature, featureLabel: fc.Id);
                    if (groupRootDir != null) entities.Add(groupRootDir);
                }
            }

            var project = new ManagedProject(_config.Product.Name, entities.ToArray());

            if (_config.Product.Platform.Equals("x64", StringComparison.OrdinalIgnoreCase))
            {
                project.Platform = Platform.x64;
            }
            else if (_config.Product.Platform.Equals("arm64", StringComparison.OrdinalIgnoreCase))
            {
                project.Platform = Platform.arm64;
            }

            string resolvedVersion = _config.Product.Version.Resolve(_resolver);

            project.GUID      = new Guid(_config.Product.UpgradeCode);
            project.ProductId = Guid.NewGuid();
            project.ControlPanelInfo.Manufacturer = _config.Product.Manufacturer;
            project.Description = _config.Product.Description;
            project.Version = new Version(resolvedVersion);

            project.AttributesDefinition = isMachine ? "Scope=perMachine" : "Scope=perUser";

            // Suppress "Ambiguous short name" warning as we are explicitly generating them
            // -sw1044: suppress "Ambiguous short name" (AlliePack generates explicit short names)
            // -sw5437: suppress "no longer necessary to define standard directory" (WiX 6 advisory, WixSharp emits these)
            project.WixOptions = "-sw1044 -sw5437";

            // Configure installer UI.
            // ui: standard (default) -- built-in WiX dialog set; wix.exe supplies the dialogs,
            //     no WixSharp WPF assembly (WixSharp.UI.CA.dll) is needed.
            // ui: custom             -- WixSharp WPF EmbeddedUI; full WPF dialog stack.
            bool hasFeatures = _config.Features.Any();
            bool hasLicense  = !string.IsNullOrEmpty(_config.Product.LicenseFile);
            bool useCustomUi = string.Equals(_config.Ui, "custom", StringComparison.OrdinalIgnoreCase);

            if (hasLicense)
                project.LicenceFile = _resolver.Resolve(_config.Product.LicenseFile!);

            if (useCustomUi)
            {
                // WixSharp WPF EmbeddedUI -- requires WixSharp.UI.CA.dll in the output directory.
                var ui = new ManagedUI();
                if (hasLicense)
                {
                    if (hasFeatures)
                        ui.InstallDialogs
                            .Add<WelcomeDialog>()
                            .Add<LicenceDialog>()
                            .Add<InstallDirDialog>()
                            .Add<FeaturesDialog>()
                            .Add<ProgressDialog>()
                            .Add<ExitDialog>();
                    else
                        ui.InstallDialogs
                            .Add<WelcomeDialog>()
                            .Add<LicenceDialog>()
                            .Add<InstallDirDialog>()
                            .Add<ProgressDialog>()
                            .Add<ExitDialog>();
                }
                else
                {
                    if (hasFeatures)
                        ui.InstallDialogs
                            .Add<WelcomeDialog>()
                            .Add<InstallDirDialog>()
                            .Add<FeaturesDialog>()
                            .Add<ProgressDialog>()
                            .Add<ExitDialog>();
                    else
                        ui.InstallDialogs
                            .Add<WelcomeDialog>()
                            .Add<InstallDirDialog>()
                            .Add<ProgressDialog>()
                            .Add<ExitDialog>();
                }
                ui.ModifyDialogs
                    .Add<MaintenanceTypeDialog>()
                    .Add<ProgressDialog>()
                    .Add<ExitDialog>();
                project.ManagedUI = ui;
            }
            else
            {
                // Standard WiX built-in dialog set -- no WixSharp.UI.CA.dll required.
                // WixUI_FeatureTree when features exist; WixUI_InstallDir when a license is
                // present; WixUI_Minimal for the simple service-style case.
                if (hasFeatures)
                    project.UI = WUI.WixUI_FeatureTree;
                else if (hasLicense)
                    project.UI = WUI.WixUI_InstallDir;
                else
                    project.UI = WUI.WixUI_Minimal;
            }

            // When WixUI_FeatureTree is used without a license file, WiX falls back to a
            // Lorem ipsum placeholder.  Suppress the LicenseAgreementDlg entirely by
            // injecting <Publish> elements that override the dialog-flow transitions:
            //   WelcomeDlg  → Next  =>  CustomizeDlg   (was: LicenseAgreementDlg)
            //   CustomizeDlg → Back =>  WelcomeDlg     (was: LicenseAgreementDlg)
            // Order="2" takes priority over the built-in Order="1" transitions shipped
            // with WixUI_FeatureTree in the WixToolset.UI.wixext extension.
            if (hasFeatures && !hasLicense && !useCustomUi)
                project.WixSourceGenerated += SuppressLicenseDialog;

            // Raw WiX XML fragments -- escape hatch for anything AlliePack doesn't cover
            if (_config.Wix?.Fragments.Any() == true)
            {
                var fragments = _config.Wix.Fragments;
                project.WixSourceGenerated += document =>
                {
                    foreach (var fragment in fragments)
                    {
                        try
                        {
                            XElement xml;
                            if (!string.IsNullOrWhiteSpace(fragment.Inline))
                            {
                                xml = XElement.Parse(fragment.Inline);
                            }
                            else if (!string.IsNullOrWhiteSpace(fragment.File))
                            {
                                xml = XElement.Load(_resolver.Resolve(fragment.File!));
                            }
                            else continue;

                            document.Root!.Add(xml);
                            if (_options.IsVerbose)
                                Console.WriteLine($"  wix fragment injected: {xml.Name.LocalName}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Warning: failed to inject WiX fragment: {ex.Message}");
                        }
                    }
                };
            }

            if (_options.ReportOnly)
            {
                GenerateReport(project, entities);
            }
            else if (_options.ExportWxs)
            {
                ExportWxsArtifact(project);
            }
            else
            {
                if (!string.IsNullOrEmpty(_options.OutputPath))
                {
                    // WixSharp uses OutFileName (no extension) + OutDir to control
                    // the output location.
                    string outDir  = Path.GetDirectoryName(Path.GetFullPath(_options.OutputPath)) ?? ".";
                    string outName = Path.GetFileNameWithoutExtension(_options.OutputPath);
                    project.OutDir      = outDir;
                    project.OutFileName = outName;
                }

                if (_options.KeepWxs || Environment.GetEnvironmentVariable("ALLIEPAK_KEEP_WXS") != null)
                    project.PreserveTempFiles = true;

                if (_config.Signing?.Files != null)
                {
                    Console.WriteLine("Signing packaged files...");
                    SigningHelper.SignFiles(uniqueFiles, _config.Signing, _resolver, _options.IsVerbose);
                }

                string msiPath = project.BuildMsi();

                if (_config.Signing != null)
                {
                    if (string.IsNullOrEmpty(msiPath) || !System.IO.File.Exists(msiPath))
                        Console.WriteLine($"Warning: Code signing skipped -- MSI not found at: {msiPath}");
                    else
                        SigningHelper.Sign(msiPath, _config.Signing, _resolver, _options.IsVerbose);
                }
            }
        }

        // -----------------------------------------------------------------------------------------
        // WXS export
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Generates a portable WXS artifact directory containing:
        /// - The WXS source file with $(var.Version) as the version placeholder
        /// - WixSharp runtime DLLs needed to compile the WXS with wix.exe
        /// - build.ps1 that compiles the WXS to an MSI given a -Version parameter
        ///
        /// All source paths in the WXS are rewritten to be relative to the export directory.
        /// System-level WixSharp assets (WxL files, etc.) are copied into the export directory.
        /// </summary>
        private void ExportWxsArtifact(ManagedProject project)
        {
            // Determine export directory
            string exportDir = !string.IsNullOrEmpty(_options.OutputPath)
                ? Path.GetFullPath(_options.OutputPath)
                : Path.GetFullPath(MakeExportDirName(_config.Product.Name));

            Directory.CreateDirectory(exportDir);
            Console.WriteLine($"Export directory: {exportDir}");

            string safeName = MakeSafeFileName(_config.Product.Name);

            // Route WixSharp output (CA DLLs, UI assets) to the export directory
            project.OutDir      = exportDir;
            project.OutFileName = safeName;

            string cwd = Directory.GetCurrentDirectory();
            XNamespace wixNs = "http://wixtoolset.org/schemas/v4/wxs";

            project.WixSourceGenerated += doc =>
            {
                var package = doc.Descendants(wixNs + "Package").FirstOrDefault();

                // Rewrite <Package Version="..."> to a WiX preprocessor variable so the
                // customer CI pipeline can supply the version at wix.exe compile time.
                package?.SetAttributeValue("Version", "$(var.Version)");

                // Remove the auto-generated ProductCode so WiX generates a fresh one at
                // each wix build invocation.  This keeps the committed WXS stable (no
                // random GUID changing every Milestone-deploy) while still ensuring every
                // built MSI gets a unique ProductCode -- which is what triggers the
                // Windows Installer major-upgrade logic to replace the old installation.
                // The UpgradeCode in allie.yaml remains the stable product-family identifier.
                package?.Attribute("ProductCode")?.Remove();

                // Rewrite all Source / SourceFile attributes to be relative to the export
                // directory.  WixSharp writes paths relative to CWD; we normalise them to
                // absolute first, then re-express them relative to the export dir.
                // System-level files (ProgramData, Windows dir) are copied into the export
                // directory so the artifact is self-contained for those assets.
                foreach (var element in doc.Descendants())
                {
                    RewriteSourceAttr(element, "Source",     cwd, exportDir);
                    RewriteSourceAttr(element, "SourceFile", cwd, exportDir);
                }

                // Shorten any identifiers that exceed the WiX 72-char soft limit (WIX1026).
                // WixSharp emits IDs like Component.<dll-name>_<crc> which can exceed 72
                // chars for long Microsoft.Extensions.* DLL names.  Build a deterministic
                // mapping (long -> short) and apply it to every attribute in the document so
                // that Component Id and ComponentRef Id stay in sync.
                ShortenLongIds(doc);

                // Remove auto-generated 8.3 ShortName attributes from File elements (WIX1044).
                // WixSharp generates these for long filenames but modern Windows does not need
                // them, and WiX warns when the auto-incremented suffix (~1, ~2, ...) makes the
                // short name ambiguous across multiple files in the same directory.
                StripShortNames(doc);

                // Mark the WixSharp root feature ("Complete") as required (AllowAbsent="no").
                // The root feature is where WixSharp places all top-level structure: items
                // (files not assigned to a named feature).  These are "always install" content
                // by design -- if something is optional it belongs in a features: entry, not
                // in top-level structure.  WixSharp defaults to AllowAbsent="yes", which
                // would let the user deselect core content like README.txt in the feature
                // tree UI.  Overriding to "no" enforces the intended semantics.
                RequireRootFeature(doc);
            };

            // Generate the WXS.  WixSharp uses project.OutDir + project.OutFileName to
            // determine where to write the WXS and CA DLLs.  BuildWxs does NOT invoke
            // wix.exe -- it produces only the XML source file.
            Compiler.BuildWxs(project, Compiler.OutputType.MSI);

            string wxsPath = Path.Combine(exportDir, safeName + ".wxs");

            // WixSharp stages CA DLLs to the output directory regardless of whether the
            // generated WXS actually references them.  Scan the WXS and remove any that
            // aren't referenced so the export artifact only contains what the build needs.
            PruneUnreferencedCaDlls(wxsPath, exportDir);

            // Detect which WiX extensions are required by scanning the generated WXS for
            // non-core namespaces.  Find each extension DLL on disk and copy it into the
            // export directory so the artifact is self-contained; the build.ps1 will
            // reference the local copy by path rather than by package name.
            var extensions = DetectWixExtensions(wxsPath);

            // Resolve and bundle extension DLLs
            // extRefs: the -ext argument to use in build.ps1 (local filename if bundled,
            //          package name if the DLL couldn't be found locally)
            var extRefs = new List<string>();
            foreach (var ext in extensions)
            {
                // Derive the expected local DLL filename:
                //   "WixToolset.Util.wix4" -> "WixToolset.Util.wixext.dll"
                string localDllName = ext.Replace(".wix4", ".wixext") + ".dll";
                string localDllPath = Path.Combine(exportDir, localDllName);

                // If the DLL is already present in the export dir (committed alongside
                // the WXS, or left by a prior run), reference it directly without
                // touching the global extension cache.  This is the common case on a
                // clean checkout where the delivery repo contains pre-bundled DLLs.
                if (System.IO.File.Exists(localDllPath))
                {
                    extRefs.Add(localDllName);
                    continue;
                }

                // DLL not present locally -- try the global wix extension cache
                // (~/.wix/extensions/{name}/{version}/wixext5/{name}.dll).
                string? dllSrc = FindWixExtensionDll(ext);
                if (dllSrc != null)
                {
                    System.IO.File.Copy(dllSrc, localDllPath);
                    extRefs.Add(localDllName);
                }
                else
                {
                    extRefs.Add(ext);   // package name fallback
                    Console.WriteLine($"  Warning: WiX extension DLL not found for '{ext}'; " +
                                      $"build.ps1 will reference it by package name. " +
                                      $"Run 'wix extension add {ext}' on the target machine if the build fails.");
                }
            }

            // Emit build.ps1 with the bundled extension paths
            WriteBuildScript(exportDir, safeName, extRefs);

            // Build the example wix command for the summary
            string extArgsSummary = extRefs.Count > 0
                ? " " + string.Join(" ", extRefs.Select(e => $"-ext {e}"))
                : "";
            string exampleCmd = $"wix build {safeName}.wxs -d Version=1.0.0.0{extArgsSummary} -o {safeName}-1.0.0.0.msi";

            // Summary
            Console.WriteLine();
            Console.WriteLine($"  {safeName}.wxs");
            Console.WriteLine($"  build.ps1");
            if (extensions.Count > 0)
                Console.WriteLine($"  Extensions bundled: {string.Join(", ", extensions)}");
            Console.WriteLine();
            Console.WriteLine("To build the MSI:");
            Console.WriteLine($"  cd \"{exportDir}\"");
            Console.WriteLine($"  .\\build.ps1 -Version 1.0.0.0");
            Console.WriteLine($"  {exampleCmd}");
        }

        /// <summary>
        /// Rewrites a single Source or SourceFile attribute on an element so its path is
        /// relative to <paramref name="exportDir"/> rather than relative to <paramref name="cwd"/>.
        /// Files that live in system directories (ProgramData, Windows) are copied into the
        /// export directory and referenced by filename only.
        /// </summary>
        /// <summary>
        /// Shortens WXS identifier attributes that exceed the WiX 72-character soft limit
        /// (warning WIX1026).  Builds a deterministic long-to-short mapping using a SHA256
        /// digest and applies it to every attribute in the document so that, for example,
        /// Component/@Id and ComponentRef/@Id remain consistent.
        /// </summary>
        private static void ShortenLongIds(XDocument doc)
        {
            const int MaxLen = 72;

            // Pass 1: collect every over-length Id value and compute a stable short form.
            var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var elem in doc.Descendants())
            {
                var idAttr = elem.Attribute("Id");
                if (idAttr == null) continue;
                var id = idAttr.Value;
                if (id.Length > MaxLen && !mapping.ContainsKey(id))
                    mapping[id] = MakeShortId(id);
            }

            if (mapping.Count == 0) return;

            // Pass 2: apply mapping to every attribute whose value appears in the mapping.
            // This covers both Id= declarations and all *Ref= references (ComponentRef, etc.).
            foreach (var elem in doc.Descendants())
            {
                foreach (var attr in elem.Attributes().ToList())
                {
                    if (mapping.TryGetValue(attr.Value, out var shortId))
                        attr.SetValue(shortId);
                }
            }
        }

        /// <summary>
        /// Produces a stable identifier under 72 chars by hashing the original value with
        /// SHA256 and encoding the first 12 bytes as a lowercase hex string.
        /// Format: "cmp_" + 24 hex chars = 28 chars total.
        /// The "cmp_" prefix is distinct from WixSharp's "Component." prefix so there is
        /// no risk of colliding with non-truncated identifiers.
        /// </summary>
        private static string MakeShortId(string original)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(original));
            return "cmp_" + BitConverter.ToString(bytes, 0, 12).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Removes the auto-generated <c>ShortName</c> attribute from all <c>File</c> elements
        /// in the WXS document.  WixSharp derives 8.3 short names for long filenames, but when
        /// many files share the same prefix (e.g. "Microsoft.Extensions.*"), WiX assigns
        /// sequential suffixes (MICROS~1.DLL, MICROS~2.DLL, ...) and then warns WIX1044 that
        /// these are ambiguous.  Modern Windows targets do not need 8.3 names, so the safest
        /// fix is to omit <c>ShortName</c> entirely and let Windows manage short-name generation
        /// (or skip it when 8.3 support is disabled on the target volume, which is typical).
        /// </summary>
        private static void StripShortNames(XDocument doc)
        {
            foreach (var elem in doc.Descendants())
            {
                elem.Attribute("ShortName")?.Remove();
            }
        }

        /// <summary>
        /// Injects <c>Publish</c> elements into the WXS <c>UI</c> block to bypass the
        /// <c>LicenseAgreementDlg</c> in <c>WixUI_FeatureTree</c> when no license file is
        /// configured.  Without this override WiX substitutes a Lorem ipsum placeholder.
        /// <para>
        /// The built-in dialog transitions (Order=1) are:
        ///   WelcomeDlg → LicenseAgreementDlg → CustomizeDlg.
        /// The injected transitions (Order=2) short-circuit that:
        ///   WelcomeDlg → CustomizeDlg (forward), CustomizeDlg → WelcomeDlg (back).
        /// </para>
        /// </summary>
        private static void SuppressLicenseDialog(XDocument doc)
        {
            XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";

            // Find the Package element -- all UI content lives inside it.
            var package = doc.Descendants(wix + "Package").FirstOrDefault();
            if (package == null) return;

            // Locate an existing <UI> element or create one.
            var ui = package.Elements(wix + "UI").FirstOrDefault();
            if (ui == null)
            {
                ui = new XElement(wix + "UI");
                package.Add(ui);
            }

            // WelcomeDlg Next -> CustomizeDlg (skip LicenseAgreementDlg).
            // WiX 5 uses Condition= attribute; inner text ("1") is no longer valid (WIX0400).
            ui.Add(new XElement(wix + "Publish",
                new XAttribute("Dialog",    "WelcomeDlg"),
                new XAttribute("Control",   "Next"),
                new XAttribute("Event",     "NewDialog"),
                new XAttribute("Value",     "CustomizeDlg"),
                new XAttribute("Order",     "2"),
                new XAttribute("Condition", "1")));

            // CustomizeDlg Back -> WelcomeDlg (skip LicenseAgreementDlg on the way back).
            ui.Add(new XElement(wix + "Publish",
                new XAttribute("Dialog",    "CustomizeDlg"),
                new XAttribute("Control",   "Back"),
                new XAttribute("Event",     "NewDialog"),
                new XAttribute("Value",     "WelcomeDlg"),
                new XAttribute("Order",     "2"),
                new XAttribute("Condition", "1")));
        }

        /// <summary>
        /// Sets <c>AllowAbsent="no"</c> and <c>Display="hidden"</c> on WixSharp's root
        /// feature (always named <c>"Complete"</c>).  Top-level <c>structure:</c> items --
        /// files not assigned to any named feature -- land in "Complete" by WixSharp
        /// convention.  These represent always-install content (e.g. README, core
        /// directories).
        /// <list type="bullet">
        ///   <item><c>AllowAbsent="no"</c> -- prevents the user from deselecting the feature.</item>
        ///   <item><c>Display="hidden"</c> -- removes it from the feature-tree UI entirely so
        ///     the user only sees the named optional features (services), not an ambiguous
        ///     "Complete" entry at the bottom of the list.</item>
        /// </list>
        /// Named optional features retain their own <c>AllowAbsent</c> and <c>Display</c>.
        /// </summary>
        private static void RequireRootFeature(XDocument doc)
        {
            var rootFeature = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Feature"
                                  && e.Attribute("Id")?.Value == "Complete");
            if (rootFeature == null) return;

            rootFeature.SetAttributeValue("AllowAbsent", "no");
            rootFeature.SetAttributeValue("Display", "hidden");
        }

        private static void RewriteSourceAttr(XElement element, string attrName, string cwd, string exportDir)
        {
            var attr = element.Attribute(attrName);
            if (attr == null || string.IsNullOrWhiteSpace(attr.Value)) return;

            string raw = attr.Value;

            // Resolve to absolute path (WixSharp writes paths relative to CWD)
            string abs = Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(cwd, raw));

            string newPath;

            if (abs.StartsWith(exportDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(abs, exportDir, StringComparison.OrdinalIgnoreCase))
            {
                // Already inside the export directory -- express as path relative to export dir
                newPath = abs.Substring(exportDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            else if (IsSystemPath(abs))
            {
                // System asset (e.g. WixUI_en-US.wxl from ProgramData) -- copy alongside WXS
                string dest = Path.Combine(exportDir, Path.GetFileName(abs));
                if (System.IO.File.Exists(abs) && !System.IO.File.Exists(dest))
                    System.IO.File.Copy(abs, dest);
                newPath = Path.GetFileName(abs);
            }
            else
            {
                // Source file (application binary, config, etc.) -- relative path from export dir
                newPath = MakeRelativePath(exportDir, abs);
            }

            attr.SetValue(newPath);
        }

        private static bool IsSystemPath(string absPath)
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string windows     = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return absPath.StartsWith(programData, StringComparison.OrdinalIgnoreCase)
                || absPath.StartsWith(windows,     StringComparison.OrdinalIgnoreCase);
        }

        private static void PruneUnreferencedCaDlls(string wxsPath, string exportDir)
        {
            XNamespace wixNs = "http://wixtoolset.org/schemas/v4/wxs";
            var doc = XDocument.Load(wxsPath);

            var referenced = doc.Descendants(wixNs + "Binary")
                .Select(e => e.Attribute("SourceFile")?.Value)
                .Where(v => v != null && v.EndsWith(".CA.dll", StringComparison.OrdinalIgnoreCase))
                .Select(v => Path.GetFileName(v!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string dll in Directory.GetFiles(exportDir, "*.CA.dll"))
            {
                if (!referenced.Contains(Path.GetFileName(dll)))
                    System.IO.File.Delete(dll);
            }
        }

        /// <summary>Computes a path relative from <paramref name="fromDir"/> to <paramref name="toPath"/>.</summary>
        private static string MakeRelativePath(string fromDir, string toPath)
        {
            if (!fromDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                fromDir += Path.DirectorySeparatorChar;
            var fromUri = new Uri(fromDir);
            var toUri   = new Uri(toPath);
            if (fromUri.Scheme != toUri.Scheme) return toPath;   // different drives, etc.
            var rel = fromUri.MakeRelativeUri(toUri);
            return Uri.UnescapeDataString(rel.ToString())
                      .Replace('/', Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// Produces a safe file-system name from the product name.
        /// Strips invalid filename characters and replaces spaces with hyphens.
        /// </summary>
        private static string MakeSafeFileName(string productName)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(productName
                .Select(c => invalid.Contains(c) ? '-' : c == ' ' ? '-' : c)
                .ToArray());
        }

        private static string MakeExportDirName(string productName)
            => MakeSafeFileName(productName) + "-wxs";

        /// <summary>
        /// Maps WiX extension XML namespace URIs to their extension package names.
        /// Used to detect which -ext flags are needed when compiling an exported WXS.
        /// </summary>
        private static readonly Dictionary<string, string> _wixExtensionMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["http://wixtoolset.org/schemas/v4/wxs/util"]      = "WixToolset.Util.wix4",
                ["http://wixtoolset.org/schemas/v4/wxs/firewall"]   = "WixToolset.Firewall.wix4",
                ["http://wixtoolset.org/schemas/v4/wxs/iis"]        = "WixToolset.Iis.wix4",
                ["http://wixtoolset.org/schemas/v4/wxs/netfx"]      = "WixToolset.NetFx.wix4",
                ["http://wixtoolset.org/schemas/v4/wxs/sql"]        = "WixToolset.Sql.wix4",
                ["http://wixtoolset.org/schemas/v4/wxs/ui"]         = "WixToolset.UI.wix4",
                ["http://wixtoolset.org/schemas/v4/wxs/bal"]        = "WixToolset.BootstrapperApplications.wix4",
            };

        /// <summary>
        /// Locates the compiled DLL for a WiX extension package in the user's wix extension
        /// cache (~/.wix/extensions/).  Returns null if not found.
        ///
        /// WiX stores extension DLLs at:
        ///   %USERPROFILE%\.wix\extensions\{wixextName}\{version}\wixext5\{wixextName}.dll
        /// where {wixextName} is the package name with ".wix4" replaced by ".wixext".
        /// </summary>
        private static string? FindWixExtensionDll(string packageName)
        {
            // Map NuGet package name to the on-disk directory/DLL name
            // WixToolset.Util.wix4  ->  WixToolset.Util.wixext
            string wixextName = packageName.Replace(".wix4", ".wixext");

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string extDir = Path.Combine(userProfile, ".wix", "extensions", wixextName);
            if (!Directory.Exists(extDir)) return null;

            // Find the highest v5-compatible version: look only in wixext5/ subdirectories.
            // wixext6/ DLLs require WixToolset.Extensibility v6 and cannot be loaded by wix.exe v5.
            // Sort version directory names as Version objects so "5.0.10" > "5.0.9" (not string order).
            var versionDirs = Directory.GetDirectories(extDir)
                                       .Select(d => new { Path = d, Name = Path.GetFileName(d) })
                                       .Select(x => new {
                                           x.Path,
                                           Ver = Version.TryParse(x.Name, out var v) ? v : null
                                       })
                                       .Where(x => x.Ver != null)
                                       .OrderByDescending(x => x.Ver)
                                       .Select(x => x.Path)
                                       .ToArray();

            foreach (var vDir in versionDirs)
            {
                // Only use wixext5/ -- wix.exe v5 cannot load v6 extension assemblies
                string dll = Path.Combine(vDir, "wixext5", wixextName + ".dll");
                if (System.IO.File.Exists(dll)) return dll;
            }
            return null;
        }

        /// <summary>
        /// Reads a generated WXS file and returns the list of WiX extension package names
        /// required to compile it (detected by matching element/attribute namespace URIs).
        /// </summary>
        private static List<string> DetectWixExtensions(string wxsPath)
        {
            var found = new List<string>();
            try
            {
                var doc = XDocument.Load(wxsPath);
                // Check namespace declarations and element namespaces
                var namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var el in doc.Descendants())
                {
                    namespaces.Add(el.Name.NamespaceName);
                    foreach (var attr in el.Attributes().Where(a => a.IsNamespaceDeclaration))
                        namespaces.Add(attr.Value);
                }
                foreach (var ns in namespaces)
                    if (_wixExtensionMap.TryGetValue(ns, out string ext) && !found.Contains(ext))
                        found.Add(ext);
            }
            catch { /* best-effort; missing extensions will surface at wix build time */ }
            return found;
        }

        /// <summary>
        /// Writes a build.ps1 script into <paramref name="exportDir"/> that compiles the WXS
        /// to an MSI using wix.exe.  The script must be run from (or will Push-Location to)
        /// the export directory so the relative source paths in the WXS resolve correctly.
        /// </summary>
        private static void WriteBuildScript(string exportDir, string safeName, List<string> extensions)
        {
            // Rules (global CLAUDE.md): set UTF-8 mode for any script that outputs text.
            // IMPORTANT: param() must be the very first statement in a PowerShell script.
            // Use {{ / }} to escape braces inside a C# interpolated verbatim string.

            // Build the -ext flags string.  Extension DLLs bundled alongside the WXS are
            // referenced via Join-Path $scriptDir so they resolve regardless of CWD.
            // -sw1044: suppress ambiguous short name; -sw5437: suppress legacy directory advisory.
            string extLines = extensions.Count > 0
                ? string.Join(Environment.NewLine,
                      extensions.Select(e => e.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                          ? $"$extArgs += @('-ext', (Join-Path $scriptDir '{e}'))"
                          : $"$extArgs += @('-ext', '{e}')"))
                : string.Empty;
            string extArgsInit = extensions.Count > 0
                ? "$extArgs = @()" + Environment.NewLine + extLines + Environment.NewLine
                : string.Empty;
            string extSplat = extensions.Count > 0 ? " @extArgs" : "";

            string script =
$@"param(
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputPath = ''
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding           = [System.Text.Encoding]::UTF8
chcp 65001 | Out-Null

$scriptDir = $PSScriptRoot
$wxsFile   = Join-Path $scriptDir '{safeName}.wxs'

if (-not (Get-Command wix.exe -ErrorAction SilentlyContinue)) {{
    Write-Host 'wix.exe not found on PATH.'
    Write-Host 'Install WiX v5: dotnet tool install --global wix --version 5.*'
    exit 1
}}

if (-not $OutputPath) {{ $OutputPath = $scriptDir }}

{extArgsInit}$msiName = '{safeName}-' + $Version + '.msi'
$msiPath = Join-Path $OutputPath $msiName

Write-Host ""Building: $msiPath""

# Source paths in the WXS are relative to the export directory -- run wix from there.
Push-Location $scriptDir
try {{
    wix build $wxsFile -d Version=$Version{extSplat} -sw1044 -sw5437 -o $msiPath
    if ($LASTEXITCODE -ne 0) {{
        Write-Host ""wix build failed (exit $LASTEXITCODE)""
        exit $LASTEXITCODE
    }}
}} finally {{
    Pop-Location
}}

Write-Host ""Done: $msiPath""
";
            System.IO.File.WriteAllText(
                Path.Combine(exportDir, "build.ps1"),
                script,
                System.Text.Encoding.UTF8);
        }

        private string ResolveEnvScope(EnvVarConfig ev, string rawInstallScope, bool isMachine)
        {
            if (ev.Scope != null)
                return ev.Scope.Resolve(_activeFlags);
            return isMachine ? "machine" : "user";
        }

        private ServiceInstaller BuildServiceInstaller(ServiceConfig svc)
        {
            var si = new ServiceInstaller
            {
                Name         = svc.Name,
                DisplayName  = svc.DisplayName ?? svc.Name,
                Description  = svc.Description ?? string.Empty,
                Account      = svc.Account,
                Start        = ParseSvcStartType(svc.Start),
                Type         = ParseSvcType(svc.Type),
                ErrorControl = ParseSvcErrorControl(svc.ErrorControl),
                StartOn      = svc.StartOnInstall ? SvcEvent.Install : null,
                StopOn       = SvcEvent.InstallUninstall_Wait,
                RemoveOn     = SvcEvent.Uninstall_Wait,
            };

            if (!string.IsNullOrEmpty(svc.Arguments))  si.Arguments          = svc.Arguments;
            if (!string.IsNullOrEmpty(svc.Password))   si.Password           = svc.Password;
            if (svc.Interactive.HasValue)               si.Interactive        = svc.Interactive;
            if (svc.DelayedAutoStart.HasValue)          si.DelayedAutoStart   = svc.DelayedAutoStart;

            if (svc.OnFailure != null)
            {
                si.FirstFailureActionType  = ParseFailureAction(svc.OnFailure.First);
                si.SecondFailureActionType = ParseFailureAction(svc.OnFailure.Second);
                si.ThirdFailureActionType  = ParseFailureAction(svc.OnFailure.Third);
                if (svc.OnFailure.ResetAfterDays.HasValue)
                    si.ResetPeriodInDays = svc.OnFailure.ResetAfterDays;
                if (svc.OnFailure.RestartDelaySeconds.HasValue)
                    si.RestartServiceDelayInSeconds = svc.OnFailure.RestartDelaySeconds;
            }

            if (svc.DependsOn.Any())
                si.DependsOn = svc.DependsOn.Select(d => new ServiceDependency(d)).ToArray();

            return si;
        }

        private void AttachShortcut(
            ShortcutInfo s,
            string installPath,
            bool isMachine,
            Dictionary<string, Dir> shortcutRootDirs,
            List<WixEntity> entities,
            Dictionary<string, File> fileMap,
            string? featureLabel = null)
        {
            string targetPath = ResolvePath(s.Target, installPath);
            if (!fileMap.TryGetValue(targetPath, out var wixFile))
            {
                string label = featureLabel != null ? $"Feature '{featureLabel}' shortcut" : "Shortcut";
                Console.WriteLine($"Warning: {label} target not found: {s.Target} (Resolved to: {targetPath})");
                if (_options.Debug)
                {
                    Console.WriteLine($"  [debug] fileMap contains {fileMap.Count} entries:");
                    foreach (var key in fileMap.Keys)
                        Console.WriteLine($"  [debug]   {key}");
                }
                return;
            }

            string folder = ResolveFolder(s.Folder, isMachine);
            string? rootKey = null;
            if (folder.StartsWith("%"))
                rootKey = folder.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)[0];
            else if (folder.StartsWith("[") && folder.Contains("]"))
                rootKey = folder.Substring(0, folder.IndexOf(']') + 1);

            if (rootKey != null)
            {
                if (!shortcutRootDirs.TryGetValue(rootKey, out var rootDirEntity))
                {
                    rootDirEntity = new Dir(rootKey);
                    shortcutRootDirs[rootKey] = rootDirEntity;
                    entities.Add(rootDirEntity);
                }

                Dir current = shortcutRootDirs[rootKey];
                string[] parts = folder.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i < parts.Length; i++)
                {
                    var existing = current.Dirs.FirstOrDefault(d => d.Name == parts[i]);
                    if (existing == null)
                    {
                        existing = new Dir(parts[i]);
                        current.Dirs = (current.Dirs ?? new Dir[0]).Concat(new[] { existing }).ToArray();
                    }
                    current = existing;
                }
            }

            var shortcut = new FileShortcut(s.Name, folder) { Description = s.Description };
            if (!string.IsNullOrEmpty(s.Icon))
                shortcut.IconFile = _resolver.Resolve(s.Icon!);
            wixFile.Shortcuts = (wixFile.Shortcuts ?? new FileShortcut[0]).Concat(new[] { shortcut }).ToArray();
        }

        private Dir? BuildGroupDir(
            FileGroupConfig group,
            string destPath,
            Feature? feature,
            string? featureLabel = null)
        {
            string groupLabel = featureLabel != null
                ? $"Feature '{featureLabel}' group '{group.Id}'"
                : $"Group '{group.Id}'";

            var groupFiles = new List<File>();
            foreach (var item in group.Files)
            {
                var resolved = _resolver.ResolveGlob(item.Source);
                if (!resolved.Any())
                {
                    Console.WriteLine($"Warning: {groupLabel}: no files matched '{item.Source}'");
                    continue;
                }
                bool neverOverwrite = string.Equals(group.Condition, "notExists", StringComparison.OrdinalIgnoreCase);
                foreach (var filePath in resolved)
                {
                    var wf = new File(filePath);
                    if (feature != null) wf.Feature = feature;
                    if (!string.IsNullOrEmpty(item.Rename)) wf.Name = item.Rename;
                    var attrs = new List<string>();
                    if (neverOverwrite) attrs.Add("Component:NeverOverwrite=yes");
                    if (group.Permanent) attrs.Add("Component:Permanent=yes");
                    if (attrs.Count > 0) wf.AttributesDefinition = string.Join(";", attrs);
                    groupFiles.Add(wf);
                }
            }

            if (!groupFiles.Any()) return null;

            string[] groupPathParts = destPath.Replace('/', '\\')
                .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            Dir groupRootDir = new Dir(groupPathParts[0]);
            Dir leafDir = groupRootDir;
            for (int i = 1; i < groupPathParts.Length; i++)
            {
                var next = new Dir(groupPathParts[i]);
                leafDir.Dirs = new[] { next };
                leafDir = next;
            }
            leafDir.Files = groupFiles.ToArray();

            if (_options.IsVerbose)
                Console.WriteLine($"{groupLabel}: {groupFiles.Count} file(s) -> {destPath}");

            return groupRootDir;
        }

        private List<ResolvedFile> DeduplicateFiles(List<ResolvedFile> files)
        {
            var dict = new Dictionary<string, ResolvedFile>(StringComparer.OrdinalIgnoreCase);
            int duplicateCount = 0;

            foreach (var file in files)
            {
                string key = file.RelativeDestinationPath.Replace('/', '\\').TrimStart('\\');
                if (dict.TryGetValue(key, out var existing))
                {
                    // Check if they are the same file
                    if (IsSameFile(file.SourcePath, existing.SourcePath))
                    {
                        duplicateCount++;
                        continue; 
                    }
                    
                    // If they are different, we'll take the one from the most recent project (last one in the list)
                    // or maybe we should keep both? No, they go to the same destination.
                    // We'll replace and log it if verbose.
                    if (_options.IsVerbose)
                    {
                        Console.WriteLine($"Warning: Different files found for '{key}'. Keeping '{file.SourcePath}'.");
                    }
                    dict[key] = file;
                }
                else
                {
                    dict[key] = file;
                }
            }

            if (duplicateCount > 0)
            {
                Console.WriteLine($"Removed {duplicateCount} duplicate files.");
            }

            return dict.Values.ToList();
        }

        private bool IsSameFile(string path1, string path2)
        {
            if (path1.Equals(path2, StringComparison.OrdinalIgnoreCase)) return true;

            var fi1 = new FileInfo(path1);
            var fi2 = new FileInfo(path2);

            if (!fi1.Exists || !fi2.Exists) return false;
            if (fi1.Length != fi2.Length) return false;

            using var sha = SHA256.Create();
            using var s1 = System.IO.File.OpenRead(path1);
            using var s2 = System.IO.File.OpenRead(path2);
            var h1 = sha.ComputeHash(s1);
            sha.Initialize();
            var h2 = sha.ComputeHash(s2);
            return h1.SequenceEqual(h2);
        }

        /// <summary>
        /// Applies the element's onEmpty policy after a source/project/solution resolves to
        /// zero files.  warn (default) prints a message; error throws; ignore is silent.
        /// </summary>
        private void ApplyOnEmptyPolicy(StructureElement element, string label)
        {
            switch (element.OnEmpty.ToLowerInvariant())
            {
                case "ignore":
                    return;
                case "error":
                    throw new InvalidOperationException(
                        $"No files matched for {label}. " +
                        $"Set onEmpty: warn or onEmpty: ignore to suppress this error.");
                default: // "warn" and anything unrecognised
                    if (!element.OnEmpty.Equals("warn", StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine($"Warning: Unknown onEmpty value '{element.OnEmpty}'; treating as 'warn'.");
                    Console.WriteLine($"Warning: No files matched for {label}.");
                    break;
            }
        }

        private List<ResolvedFile> ProcessElement(StructureElement element, string currentPath = "")
        {
            var result = new List<ResolvedFile>();
            
            string newPath = currentPath;
            if (!string.IsNullOrEmpty(element.FolderName))
            {
                if (element.FolderName!.StartsWith("["))
                    newPath = element.FolderName;
                else
                    newPath = Path.Combine(currentPath, element.FolderName);
            }

            if (element.Contents != null)
            {
                foreach (var child in element.Contents)
                {
                    result.AddRange(ProcessElement(child, newPath));
                }
            }

            if (!string.IsNullOrEmpty(element.Source))
            {
                if (_options.Debug)
                {
                    string resolvedSource = _resolver.Resolve(element.Source!);
                    Console.WriteLine($"  [debug] source: {element.Source}");
                    Console.WriteLine($"  [debug]      -> {resolvedSource}");
                }

                var filesWithPaths = _resolver.ResolveGlobWithPaths(element.Source ?? "");

                // Exclusions
                if (element.ExcludeFiles.Count > 0)
                {
                    var matcher = new Matcher();
                    matcher.AddInclude("**/*");
                    foreach (var exc in element.ExcludeFiles) matcher.AddExclude(exc);

                    filesWithPaths = filesWithPaths.Where(t => {
                        string? dir = Path.GetDirectoryName(t.AbsolutePath);
                        if (dir == null) return true;
                        var res = matcher.Execute(new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(new DirectoryInfo(dir)));
                        return res.Files.Any(m => Path.Combine(dir, m.Path).Equals(t.AbsolutePath, StringComparison.OrdinalIgnoreCase));
                    }).ToList();
                }

                if (filesWithPaths.Count == 0)
                {
                    if (!string.IsNullOrEmpty(element.Source) && !element.Source!.Contains("*") && !element.Source.Contains("?"))
                    {
                        // Non-glob single file reference — add directly (may not exist yet).
                        string sourcePath = _resolver.Resolve(element.Source);
                        Debug($"  source file: {sourcePath}");
                        result.Add(new ResolvedFile {
                            SourcePath = sourcePath,
                            RelativeDestinationPath = Path.Combine(newPath, Path.GetFileName(sourcePath))
                        });
                    }
                    else
                    {
                        Debug($"  source matched 0 files (glob returned nothing)");
                        ApplyOnEmptyPolicy(element, $"source: {element.Source}");
                    }
                }
                else
                {
                    Debug($"  source matched {filesWithPaths.Count} file(s):");
                    foreach (var (absPath, relPath) in filesWithPaths)
                    {
                        Debug($"    {absPath}");
                        result.Add(new ResolvedFile {
                            SourcePath = absPath,
                            RelativeDestinationPath = string.IsNullOrEmpty(newPath)
                                ? relPath
                                : newPath + "\\" + relPath
                        });
                    }
                }
            }
            else if (!string.IsNullOrEmpty(element.Solution))
            {
                string configuration = _resolver.Tokens.Substitute(element.Configuration);
                string platform      = _resolver.Tokens.Substitute(element.Platform);

                if (_options.Debug)
                {
                    string resolvedSln = _resolver.Resolve(element.Solution!);
                    Console.WriteLine($"  [debug] solution:        {element.Solution}");
                    Console.WriteLine($"  [debug]              ->  {resolvedSln}");
                    Console.WriteLine($"  [debug]   configuration: {configuration}");
                    Console.WriteLine($"  [debug]   platform:      {platform}");
                    if (element.IncludeProjects.Any())
                        Console.WriteLine($"  [debug]   includeProjects: {string.Join(", ", element.IncludeProjects)}");
                    if (element.ExcludeProjects.Any())
                        Console.WriteLine($"  [debug]   excludeProjects: {string.Join(", ", element.ExcludeProjects)}");
                    if (element.ExcludeFiles.Any())
                        Console.WriteLine($"  [debug]   excludeFiles:    {string.Join(", ", element.ExcludeFiles)}");
                }

                var solFiles = _solutionResolver.ResolveSolution(element.Solution ?? "", configuration, platform, element.IncludeProjects, element.ExcludeProjects, element.ExcludeFiles);
                foreach (var f in solFiles)
                    f.RelativeDestinationPath = Path.Combine(newPath, f.RelativeDestinationPath);

                Debug($"  solution matched {solFiles.Count} file(s):");
                foreach (var f in solFiles) Debug($"    {f.SourcePath}");

                if (solFiles.Count == 0)
                    ApplyOnEmptyPolicy(element, $"solution: {element.Solution}");

                result.AddRange(solFiles);
            }
            else if (!string.IsNullOrEmpty(element.Project))
            {
                string configuration = _resolver.Tokens.Substitute(element.Configuration);
                string platform      = _resolver.Tokens.Substitute(element.Platform);

                if (_options.Debug)
                {
                    string resolvedProj = _resolver.Resolve(element.Project!);
                    Console.WriteLine($"  [debug] project:         {element.Project}");
                    Console.WriteLine($"  [debug]              ->  {resolvedProj}");
                    Console.WriteLine($"  [debug]   configuration: {configuration}");
                    Console.WriteLine($"  [debug]   platform:      {platform}");
                    if (element.ExcludeFiles.Any())
                        Console.WriteLine($"  [debug]   excludeFiles:    {string.Join(", ", element.ExcludeFiles)}");
                }

                var projFiles = _solutionResolver.ResolveProject(element.Project ?? "", configuration, platform, element.ExcludeFiles);
                foreach (var f in projFiles)
                    f.RelativeDestinationPath = Path.Combine(newPath, f.RelativeDestinationPath);

                Debug($"  project matched {projFiles.Count} file(s):");
                foreach (var f in projFiles) Debug($"    {f.SourcePath}");

                if (projFiles.Count == 0)
                    ApplyOnEmptyPolicy(element, $"project: {element.Project}");

                result.AddRange(projFiles);
            }

            return result;
        }

        private (List<WixEntity> entities, Dictionary<string, File> fileMap) ConvertResolvedFilesToEntities(List<ResolvedFile> files, string installPath)
        {
            var rootDirs = new List<Dir>();
            var rootFiles = new List<File>();
            var fileMap = new Dictionary<string, File>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            seenNames[""] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                string relPath = file.RelativeDestinationPath;
                string fullDestPath = Path.Combine(installPath, relPath);
                string[] parts = relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                
                if (parts.Length == 1)
                {
                    var wixFile = new File(file.SourcePath);
                    if (file.WixFeature != null) wixFile.Feature = file.WixFeature;
                    string fileName = Path.GetFileName(file.SourcePath);
                    string? sfn = GenerateShortName(fileName, seenNames[""]);
                    if (sfn != null)
                    {
                        wixFile.AttributesDefinition = $"ShortName={sfn}";
                        seenNames[""].Add(sfn);
                    }
                    rootFiles.Add(wixFile);
                    fileMap[fullDestPath] = wixFile;
                }
                else
                {
                    // Navigate/Create Dir structure
                    string currentPath = parts[0];
                    Dir current = GetOrCreateDir(rootDirs, parts[0], seenNames[""]);

                    for (int i = 1; i < parts.Length - 1; i++)
                    {
                        string parentPath = currentPath;
                        currentPath = Path.Combine(currentPath, parts[i]);
                        if (!seenNames.ContainsKey(parentPath)) seenNames[parentPath] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        current = GetOrCreateDir(current, parts[i], seenNames[parentPath]);
                    }

                    if (!seenNames.ContainsKey(currentPath)) seenNames[currentPath] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var wixFile = new File(file.SourcePath);
                    if (file.WixFeature != null) wixFile.Feature = file.WixFeature;
                    string fileName = Path.GetFileName(file.SourcePath);
                    string? sfn = GenerateShortName(fileName, seenNames[currentPath]);
                    if (sfn != null)
                    {
                        wixFile.AttributesDefinition = $"ShortName={sfn}";
                        seenNames[currentPath].Add(sfn);
                    }

                    current.Files = (current.Files ?? new File[0]).Concat(new[] { wixFile }).ToArray();
                    fileMap[fullDestPath] = wixFile;
                }
            }

            var entities = new List<WixEntity>();
            entities.AddRange(rootDirs);
            entities.AddRange(rootFiles);
            return (entities, fileMap);
        }

        private Dir GetOrCreateDir(List<Dir> list, string name, HashSet<string> seen)
        {
            var existing = list.FirstOrDefault(d => d.Name == name);
            if (existing != null) return existing;
            
            var @new = new Dir(name);
            if (!name.StartsWith("[") || !name.EndsWith("]"))
            {
                string? sfn = GenerateShortName(name, seen);
                if (sfn != null)
                {
                    @new.AttributesDefinition = $"ShortName={sfn}";
                    seen.Add(sfn);
                }
            }
            list.Add(@new);
            return @new;
        }

        private Dir GetOrCreateDir(Dir parent, string name, HashSet<string> seen)
        {
            var existing = (parent.Dirs ?? new Dir[0]).FirstOrDefault(d => d.Name == name);
            if (existing != null) return existing;
            
            var @new = new Dir(name);
            if (!name.StartsWith("[") || !name.EndsWith("]"))
            {
                string? sfn = GenerateShortName(name, seen);
                if (sfn != null)
                {
                    @new.AttributesDefinition = $"ShortName={sfn}";
                    seen.Add(sfn);
                }
            }
            parent.Dirs = (parent.Dirs ?? new Dir[0]).Concat(new[] { @new }).ToArray();
            return @new;
        }

        private string? GenerateShortName(string longName, HashSet<string> seen)
        {
            string name = Path.GetFileNameWithoutExtension(longName);
            string ext = Path.GetExtension(longName).TrimStart('.');

            // Clean name and ext (only alnum allowed for SFN)
            name = new string(name.Where(char.IsLetterOrDigit).ToArray());
            ext = new string(ext.Where(char.IsLetterOrDigit).ToArray());
            if (ext.Length > 3) ext = ext.Substring(0, 3);

            if (string.IsNullOrEmpty(name)) name = "FILE";

            // If it can be 8.3 natively and isn't seen yet, return it in upper case
            int dotIdx = longName.IndexOf('.');
            string originalBase = dotIdx >= 0 ? longName.Substring(0, dotIdx) : longName;
            string originalExt = dotIdx >= 0 ? longName.Substring(dotIdx + 1) : "";
            if (originalBase.Length <= 8 && originalExt.Length <= 3 && longName.Count(c => c == '.') <= 1 && !longName.Contains(" "))
            {
                // Already 8.3-compliant — no need to set ShortName, WiX handles it automatically
                return null;
            }

            int suffix = 1;
            while (true)
            {
                string suffixStr = "~" + suffix;
                int maxBase = 8 - suffixStr.Length;
                string basePart = name.Length > maxBase ? name.Substring(0, maxBase) : name;
                string candidate = basePart + suffixStr;
                if (!string.IsNullOrEmpty(ext)) candidate += "." + ext;
                
                candidate = candidate.ToUpper();
                if (!seen.Contains(candidate)) return candidate;
                
                suffix++;
                if (suffix > 9999) return null; // Let Wix handle if we have too many collisions
            }
        }

        // -----------------------------------------------------------------------
        // Feature helpers
        // -----------------------------------------------------------------------

        private static FeatureDisplay ParseFeatureDisplay(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "expand":  return FeatureDisplay.expand;
                case "hidden":  return FeatureDisplay.hidden;
                default:        return FeatureDisplay.collapse;
            }
        }

        // -----------------------------------------------------------------------
        // Service helpers
        // -----------------------------------------------------------------------

        private static SvcStartType ParseSvcStartType(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "auto":     return SvcStartType.auto;
                // WiX/SCM canonical name is "demand"; "manual" accepted as a common alias.
                case "demand":
                case "manual":   return SvcStartType.demand;
                case "disabled": return SvcStartType.disabled;
                case "boot":     return SvcStartType.boot;
                case "system":   return SvcStartType.system;
                default:
                    Console.WriteLine($"Warning: Unknown service start type '{s}'. Valid values: auto, demand, disabled, boot, system.");
                    return SvcStartType.auto;
            }
        }

        private static SvcType ParseSvcType(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "ownprocess":
                case "own":      return SvcType.ownProcess;
                case "shareprocess":
                case "share":    return SvcType.shareProcess;
                default:
                    Console.WriteLine($"Warning: Unknown service type '{s}', using 'ownProcess'");
                    return SvcType.ownProcess;
            }
        }

        private static SvcErrorControl ParseSvcErrorControl(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "ignore":   return SvcErrorControl.ignore;
                case "normal":   return SvcErrorControl.normal;
                case "critical": return SvcErrorControl.critical;
                default:
                    Console.WriteLine($"Warning: Unknown service errorControl '{s}', using 'normal'");
                    return SvcErrorControl.normal;
            }
        }

        private static FailureActionType ParseFailureAction(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "none":       return FailureActionType.none;
                case "restart":    return FailureActionType.restart;
                case "reboot":     return FailureActionType.reboot;
                case "runcommand":
                case "run":        return FailureActionType.runCommand;
                default:
                    Console.WriteLine($"Warning: Unknown failure action '{s}', using 'none'");
                    return FailureActionType.none;
            }
        }

        // -----------------------------------------------------------------------
        // Registry helpers
        // -----------------------------------------------------------------------

        private static RegistryHive ParseRegistryHive(string root)
        {
            switch (root.ToUpperInvariant())
            {
                case "HKLM":
                case "HKEY_LOCAL_MACHINE":
                case "LOCALMACHINE":
                    return RegistryHive.LocalMachine;
                case "HKCU":
                case "HKEY_CURRENT_USER":
                case "CURRENTUSER":
                    return RegistryHive.CurrentUser;
                case "HKCR":
                case "HKEY_CLASSES_ROOT":
                case "CLASSESROOT":
                    return RegistryHive.ClassesRoot;
                case "HKU":
                case "HKEY_USERS":
                case "USERS":
                    return RegistryHive.Users;
                default:
                    Console.WriteLine($"Warning: Unknown registry hive '{root}', defaulting to HKLM");
                    return RegistryHive.LocalMachine;
            }
        }

        /// <summary>
        /// Converts the YAML string value to the correct CLR type for WixSharp:
        /// dword -> int, qword -> long, everything else -> string.
        /// </summary>
        private static object ParseRegistryValueData(string value, string type)
        {
            switch (type.ToLowerInvariant())
            {
                case "dword":
                    // Accept decimal or 0x hex
                    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        return Convert.ToInt32(value.Substring(2), 16);
                    if (int.TryParse(value, out int i))
                        return i;
                    Console.WriteLine($"Warning: dword value '{value}' could not be parsed, using 0");
                    return 0;

                case "qword":
                    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        return Convert.ToInt64(value.Substring(2), 16);
                    if (long.TryParse(value, out long l))
                        return l;
                    Console.WriteLine($"Warning: qword value '{value}' could not be parsed, using 0");
                    return 0L;

                default:
                    // string, expandString, multiString, binary -- pass as string;
                    // WixSharp Type property handles the distinction.
                    return value;
            }
        }

        /// <summary>
        /// Returns the WixSharp Type string to set on a RegValue, or null when
        /// WixSharp can infer the correct type from the CLR value alone.
        /// </summary>
        private static string? MapRegistryTypeName(string yamlType)
        {
            switch (yamlType.ToLowerInvariant())
            {
                case "string":        return null;            // WixSharp infers "string" from string CLR type
                case "dword":         return null;            // WixSharp infers "integer" from int CLR type
                case "qword":         return "integer";       // WixSharp may not handle long; force "integer"
                case "expandstring":  return "expandable";
                case "multistring":   return "multiString";
                case "binary":        return "binary";
                default:              return null;
            }
        }

        // -----------------------------------------------------------------------
        // Scope helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Validates and returns the install scope. Valid values are perUser and perMachine.
        /// 'both' was removed in ADR-0004 -- use Flags to produce separate per-user and
        /// per-machine Packages from a single Package Definition instead.
        /// </summary>
        private static string ComputeEffectiveScope(string rawInstallScope)
        {
            if (rawInstallScope.Equals("both", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "installScope: both is no longer supported. " +
                    "Use installScope: perUser or installScope: perMachine, and use Flags " +
                    "to produce separate Packages if you need both variants. " +
                    "See docs/adr/0004-remove-both-scope.md for the migration path.");

            return rawInstallScope;
        }

        /// <summary>
        /// Returns the resolved absolute path for a DirectoryConfig entry.
        /// When Type is set, the well-known base is resolved against the current
        /// effective scope; otherwise the Path conditional string is used.
        /// </summary>
        private string ResolveDirectoryPath(DirectoryConfig dir, bool isMachine)
        {
            if (!string.IsNullOrEmpty(dir.Type))
            {
                string basePath = GetWellKnownBase(dir.Type!, isMachine);
                string sub = dir.SubPath ?? string.Empty;
                return string.IsNullOrEmpty(sub) ? basePath : basePath + "\\" + sub;
            }
            return dir.Path.Resolve(_activeFlags).Replace('/', '\\');
        }

        /// <summary>
        /// Returns the per-user or per-machine WiX folder property for a well-known
        /// directory type.
        /// </summary>
        private static string GetWellKnownBase(string type, bool isMachine)
        {
            switch (type.ToLowerInvariant())
            {
                case "config":
                    return isMachine ? "[CommonAppDataFolder]"     : "[AppDataFolder]";
                case "localdata":
                    return isMachine ? "[CommonAppDataFolder]"     : "[LocalAppDataFolder]";
                case "psmodules51":
                    return isMachine
                        ? "[ProgramFiles64Folder]\\WindowsPowerShell\\Modules"
                        : "[PersonalFolder]\\WindowsPowerShell\\Modules";
                case "psmodules7":
                    return isMachine
                        ? "[ProgramFiles64Folder]\\PowerShell\\7\\Modules"
                        : "[PersonalFolder]\\PowerShell\\Modules";
                case "desktop":
                    return isMachine ? "[CommonDesktopFolder]"     : "[DesktopFolder]";
                case "startmenu":
                    return isMachine ? "[CommonProgramMenuFolder]" : "[ProgramMenuFolder]";
                case "startup":
                    return isMachine ? "[CommonStartupFolder]"     : "[StartupFolder]";
                default:
                    Console.WriteLine($"Warning: Unknown directory type '{type}'");
                    return string.Empty;
            }
        }

        private string ResolvePath(string path, string installPath)
        {
            string result = path.Replace('/', '\\');
            int searchPos = 0;

            while (true)
            {
                int start = result.IndexOf('[', searchPos);
                if (start == -1) break;
                int end = result.IndexOf(']', start);
                if (end == -1) break;

                string alias = result.Substring(start, end - start + 1);
                string? replacement = null;

                if (alias.Equals("[INSTALLDIR]", StringComparison.OrdinalIgnoreCase))
                {
                    replacement = installPath;
                }
                else if (_config.Aliases.TryGetValue(alias.Trim('[', ']'), out string? value))
                {
                    replacement = value;
                }
                else if (IsStandardWixProperty(alias))
                {
                    // Stay as is, just move past it
                    searchPos = end + 1;
                    continue;
                }
                else
                {
                    throw new Exception($"Invalid or unknown alias: {alias}");
                }

                if (replacement != null)
                {
                    result = result.Substring(0, start) + replacement + result.Substring(end + 1);
                    // If replacement contains the same alias, move past it to avoid loop
                    if (replacement.IndexOf(alias, StringComparison.OrdinalIgnoreCase) != -1)
                        searchPos = start + replacement.Length;
                }
            }

            return result;
        }

        private string ResolveFolder(string folder, bool isMachine)
        {
            // Step 1: resolve well-known name aliases (startmenu, desktop, startup)
            switch (folder.Trim().ToLowerInvariant())
            {
                case "startmenu":
                    folder = isMachine ? "[CommonProgramMenuFolder]" : "[ProgramMenuFolder]";
                    break;
                case "desktop":
                    folder = isMachine ? "[CommonDesktopFolder]"     : "[DesktopFolder]";
                    break;
                case "startup":
                    folder = isMachine ? "[CommonStartupFolder]"     : "[StartupFolder]";
                    break;
            }

            string result = folder.Replace('/', '\\');

            // Step 2: convert per-user WiX bracket properties to WixSharp % keywords
            // (WixSharp requires % form for its shortcut Dir entities).
            // Common/all-users variants stay in bracket form -- WiX handles them directly.
            if (result.StartsWith("[ProgramMenuFolder]", StringComparison.OrdinalIgnoreCase))
                result = "%ProgramMenu%" + result.Substring("[ProgramMenuFolder]".Length);
            else if (result.StartsWith("[Desktop]", StringComparison.OrdinalIgnoreCase))
                result = "%Desktop%" + result.Substring("[Desktop]".Length);
            else if (result.StartsWith("[DesktopFolder]", StringComparison.OrdinalIgnoreCase))
                result = "%Desktop%" + result.Substring("[DesktopFolder]".Length);
            else if (result.StartsWith("[StartupFolder]", StringComparison.OrdinalIgnoreCase))
                result = "%Startup%" + result.Substring("[StartupFolder]".Length);

            return result;
        }

        private bool IsStandardWixProperty(string alias)
        {
            string[] standard =
            {
                "[ProgramFilesFolder]", "[ProgramFiles64Folder]", "[CommonFilesFolder]",
                "[AppDataFolder]", "[LocalAppDataFolder]", "[CommonAppDataFolder]",
                "[ProgramMenuFolder]", "[CommonProgramMenuFolder]",
                "[DesktopFolder]", "[CommonDesktopFolder]",
                "[StartupFolder]", "[CommonStartupFolder]",
                "[PersonalFolder]", "[TempFolder]",
            };
            return standard.Any(s => s.Equals(alias, StringComparison.OrdinalIgnoreCase));
        }

        private void GenerateReport(Project project, List<WixEntity> entities)
        {
            string rawScope      = _config.Product.InstallScope.Resolve(_activeFlags);
            string effectiveScope = ComputeEffectiveScope(rawScope);
            bool isMachineReport  = effectiveScope.Equals("perMachine", StringComparison.OrdinalIgnoreCase);

            Console.WriteLine("--- AlliePack MSI Content Report ---");
            Console.WriteLine($"Product: {project.Name}");
            Console.WriteLine($"Manufacturer: {project.ControlPanelInfo.Manufacturer}");
            Console.WriteLine($"Version: {project.Version}");
            Console.WriteLine($"UpgradeCode: {project.GUID}");
            if (_activeFlags.Any())
                Console.WriteLine($"Active flags: {string.Join(", ", _activeFlags)}");
            Console.WriteLine($"Install scope: {effectiveScope}");
            if (!string.IsNullOrEmpty(_config.Product.LicenseFile))
                Console.WriteLine($"License file: {_config.Product.LicenseFile}");
            Console.WriteLine("------------------------------------");

            foreach (var entity in entities)
            {
                PrintEntity(entity, 0);
            }

            if (_config.Environment.Any())
            {
                Console.WriteLine("Environment Variables:");
                foreach (var ev in _config.Environment)
                {
                    string evScope;
                    if (ev.Scope != null)
                        evScope = ev.Scope.Resolve(_activeFlags);
                    else
                        evScope = isMachineReport ? "machine" : "user";
                    Console.WriteLine($"  [{evScope}] {ev.Name} = {ev.Value.Resolve(_activeFlags)}");
                }
            }

            if (_config.Shortcuts.Any())
            {
                Console.WriteLine("Shortcuts:");
                foreach (var s in _config.Shortcuts)
                {
                    string folder = ResolveFolder(s.Folder, isMachineReport);
                    Console.WriteLine($"  [{s.Name}]  folder={folder}");
                    Console.WriteLine($"    target: {s.Target}");
                    if (!string.IsNullOrEmpty(s.Description))
                        Console.WriteLine($"    description: {s.Description}");
                    if (!string.IsNullOrEmpty(s.Icon))
                        Console.WriteLine($"    icon: {s.Icon}");
                }
            }

            if (_config.Wix?.Fragments.Any() == true)
            {
                Console.WriteLine("WiX XML Fragments (escape hatch):");
                foreach (var f in _config.Wix.Fragments)
                {
                    if (!string.IsNullOrWhiteSpace(f.File))
                        Console.WriteLine($"  [file]   {f.File}");
                    else if (!string.IsNullOrWhiteSpace(f.Inline))
                        Console.WriteLine($"  [inline] {f.Inline!.Trim().Split('\n')[0].Trim()}...");
                }
            }

            if (_config.Services.Any())
            {
                Console.WriteLine("Windows Services:");
                foreach (var svc in _config.Services)
                {
                    string startLabel = svc.Start.ToLowerInvariant() == "auto" && svc.DelayedAutoStart == true
                        ? "auto (delayed)"
                        : svc.Start;
                    Console.WriteLine($"  [{svc.Name}]  start={startLabel}  account={svc.Account}");
                    Console.WriteLine($"    executable: {svc.Executable}");
                    if (svc.OnFailure != null)
                        Console.WriteLine($"    on failure: {svc.OnFailure.First} / {svc.OnFailure.Second} / {svc.OnFailure.Third}");
                    if (svc.DependsOn.Any())
                        Console.WriteLine($"    depends on: {string.Join(", ", svc.DependsOn)}");
                }
            }

            if (_config.Registry.Any())
            {
                bool platformWin64 = _config.Product.Platform.Equals("x64", StringComparison.OrdinalIgnoreCase)
                    || _config.Product.Platform.Equals("arm64", StringComparison.OrdinalIgnoreCase);
                Console.WriteLine("Registry Values:");
                foreach (var reg in _config.Registry)
                {
                    bool effectiveWin64 = reg.Win64 ?? platformWin64;
                    string bitNote = reg.Win64.HasValue ? $" [win64={reg.Win64.Value}]" : string.Empty;
                    string val = reg.Value.Resolve(_activeFlags);
                    Console.WriteLine($"  [{reg.Root}\\{reg.Key}]{bitNote}");
                    string displayName = string.IsNullOrEmpty(reg.Name) ? "(Default)" : reg.Name;
                    Console.WriteLine($"    {displayName} ({reg.Type}) = {val}");
                }
            }

            if (_config.Groups.Any())
            {
                Console.WriteLine("File Groups (outside INSTALLDIR):");
                foreach (var group in _config.Groups)
                {
                    var dirCfg = _config.Directories
                        .FirstOrDefault(d => d.Id.Equals(group.DestinationDir, StringComparison.OrdinalIgnoreCase));
                    string dest = dirCfg != null
                        ? ResolveDirectoryPath(dirCfg, isMachineReport)
                        : group.DestinationDir;
                    string condNote = group.Condition != null ? $" [condition: {group.Condition}]" : "";
                    Console.WriteLine($"  [{group.Id}] -> {dest}{condNote}");
                    foreach (var item in group.Files)
                        Console.WriteLine($"    {item.Source}{(item.Rename != null ? $" (as {item.Rename})" : "")}");
                }
            }

            if (_config.Signing != null)
            {
                var s = _config.Signing;
                Console.WriteLine("Code Signing:");
                if (!string.IsNullOrEmpty(s.Thumbprint))
                    Console.WriteLine($"  method    : thumbprint ({s.Thumbprint})");
                else if (!string.IsNullOrEmpty(s.Pfx))
                    Console.WriteLine($"  method    : pfx ({s.Pfx})");
                if (!string.IsNullOrEmpty(s.TimestampUrl))
                    Console.WriteLine($"  timestamp : {s.TimestampUrl}");
                if (!string.IsNullOrEmpty(s.SignToolPath))
                    Console.WriteLine($"  signtool  : {s.SignToolPath}");
                if (s.Files != null)
                {
                    Console.WriteLine($"  files     : mode={s.Files.Mode}");
                    if (s.Files.Include != null)
                        Console.WriteLine($"    include : {string.Join(", ", s.Files.Include)}");
                    else
                        Console.WriteLine($"    include : (SIP check -- signable files only)");
                    if (s.Files.Exclude.Any())
                        Console.WriteLine($"    exclude : {string.Join(", ", s.Files.Exclude)}");
                }
            }

            if (_config.Features.Any())
            {
                Console.WriteLine($"Features ({_config.Features.Count}):");
                foreach (var fc in _config.Features)
                {
                    string defaultLabel = fc.Default ? "on" : "off";
                    Console.WriteLine($"  [{fc.Id}]  name={fc.Name}  default={defaultLabel}  display={fc.Display}");
                    if (!string.IsNullOrEmpty(fc.Description))
                        Console.WriteLine($"    description: {fc.Description}");
                    if (fc.Structure.Any())
                        Console.WriteLine($"    structure  : {fc.Structure.Count} element(s)");
                    if (fc.Shortcuts.Any())
                        Console.WriteLine($"    shortcuts  : {fc.Shortcuts.Count}");
                    if (fc.Services.Any())
                    {
                        Console.WriteLine($"    services   :");
                        foreach (var svc in fc.Services)
                        {
                            string startLabel = svc.Start.ToLowerInvariant() == "auto" && svc.DelayedAutoStart == true
                                ? "auto (delayed)" : svc.Start;
                            Console.WriteLine($"      [{svc.Name}]  start={startLabel}  account={svc.Account}");
                        }
                    }
                    if (fc.Registry.Any())
                        Console.WriteLine($"    registry   : {fc.Registry.Count} value(s)");
                    if (fc.Environment.Any())
                        Console.WriteLine($"    environment: {fc.Environment.Count} variable(s)");
                    if (fc.Groups.Any())
                        Console.WriteLine($"    groups     : {fc.Groups.Count}");
                }
            }
        }

        private void PrintEntity(WixEntity entity, int indent)
        {
            string space = new string(' ', indent * 2);
            if (entity is Dir dir)
            {
                string displayName = dir.Name;
                Console.WriteLine($"{space}[Folder] {displayName}");
                foreach (var childDir in dir.Dirs)
                {
                    PrintEntity(childDir, indent + 1);
                }
                foreach (var childFile in dir.Files)
                {
                    PrintEntity(childFile, indent + 1);
                }
            }
            else if (entity is File file)
            {
                string displayName = file.Name;
                if (Path.IsPathRooted(displayName)) displayName = Path.GetFileName(displayName);
                Console.WriteLine($"{space}[File] {displayName}");
                if (file.Shortcuts != null)
                {
                    foreach (var s in file.Shortcuts)
                    {
                        Console.WriteLine($"{space}  (Shortcut) {s.Name}");
                    }
                }
            }
        }

    }
}
