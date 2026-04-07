using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using WixSharp;
using WixSharp.UI.WPF;
using YamlDotNet.Serialization;
using Microsoft.Extensions.FileSystemGlobbing;
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
            WixExtension.UI.PreferredVersion   = "6.0.2";
            WixExtension.Util.PreferredVersion = "6.0.2";
            var allFiles = new List<ResolvedFile>();
            foreach (var element in _config.Structure)
            {
                allFiles.AddRange(ProcessElement(element));
            }

            // Deduplicate
            var uniqueFiles = DeduplicateFiles(allFiles);

            var entities = new List<WixEntity>();

            // --- Scope resolution ---
            // rawInstallScope: what the config says (perUser, perMachine, both)
            // effectiveScope:  the actual MSI scope to build (perUser or perMachine)
            string rawInstallScope = _config.Product.InstallScope.Resolve(_activeFlags);
            string effectiveScope  = ComputeEffectiveScope(rawInstallScope);
            bool isMachine = effectiveScope.Equals("perMachine", StringComparison.OrdinalIgnoreCase);

            // --- installDir resolution ---
            // Explicit installDir wins.  For 'both' with no explicit dir, apply smart defaults.
            string resolvedInstallDir = _config.Product.InstallDir?.Resolve(_activeFlags) ?? string.Empty;
            string installPath;
            if (!string.IsNullOrEmpty(resolvedInstallDir))
            {
                installPath = resolvedInstallDir;
            }
            else if (rawInstallScope.Equals("both", StringComparison.OrdinalIgnoreCase))
            {
                installPath = isMachine
                    ? $"[ProgramFiles64Folder]\\{_config.Product.Manufacturer}\\{_config.Product.Name}"
                    : $"[LocalAppDataFolder]\\Programs\\{_config.Product.Manufacturer}\\{_config.Product.Name}";
            }
            else
            {
                installPath = _config.Product.Manufacturer + "\\" + _config.Product.Name;
            }

            installPath = installPath.Replace('/', '\\');
            
            bool is64 = _config.Product.Platform.Equals("x64", StringComparison.OrdinalIgnoreCase) || 
                        _config.Product.Platform.Equals("arm64", StringComparison.OrdinalIgnoreCase);

            if (installPath.StartsWith("[ProgramFilesFolder]", StringComparison.OrdinalIgnoreCase))
            {
                // If they explicitly used [ProgramFilesFolder] but platform is x64, should we switch it? 
                // Maybe not, they might want x86 folder. 
                // But [ProgramFiles] is our own alias, so we can be smart.
            }

            if (installPath.StartsWith("[ProgramFiles]\\", StringComparison.OrdinalIgnoreCase))
            {
                string pfFolder = is64 ? "[ProgramFiles64Folder]" : "[ProgramFilesFolder]";
                installPath = pfFolder + "\\" + installPath.Substring("[ProgramFiles]\\".Length);
            }
            else if (installPath.StartsWith("[ProgramFilesFolder]\\", StringComparison.OrdinalIgnoreCase) && is64)
            {
                // The user explicitly used [ProgramFilesFolder] but they are in x64 mode.
                // We'll trust them, but if they want the standard behavior for AnyCPU we can warn.
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
                // Scope: explicit value wins; null (not set in YAML) defers to effective scope
                // when installScope is 'both', or falls back to 'user'.
                string evScope;
                if (ev.Scope != null)
                    evScope = ev.Scope.Resolve(_activeFlags);
                else if (rawInstallScope.Equals("both", StringComparison.OrdinalIgnoreCase))
                    evScope = isMachine ? "machine" : "user";
                else
                    evScope = "user";

                string evValue = ev.Value.Resolve(_activeFlags);
                bool isSystem = evScope.Equals("machine", StringComparison.OrdinalIgnoreCase);
                var envVar = new EnvironmentVariable(ev.Name, evValue)
                {
                    System = isSystem,
                    Permanent = false,
                };
                targetDir.GenericItems = (targetDir.GenericItems ?? new IGenericEntity[0])
                    .Concat(new IGenericEntity[] { envVar })
                    .ToArray();
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
            {
                string targetPath = ResolvePath(s.Target, installPath);
                if (fileMap.TryGetValue(targetPath, out var wixFile))
                {
                    string folder = ResolveFolder(s.Folder, isMachine);

                    // Ensure the target folder exists as a Dir entity in the project.
                    // Handles both %ProgramMenu% WixSharp keywords and [WixProperty] bracket forms.
                    string? rootKey = null;
                    if (folder.StartsWith("%"))
                    {
                        rootKey = folder.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    }
                    else if (folder.StartsWith("[") && folder.Contains("]"))
                    {
                        int end = folder.IndexOf(']');
                        rootKey = folder.Substring(0, end + 1);
                    }

                    if (rootKey != null)
                    {
                        if (!shortcutRootDirs.TryGetValue(rootKey, out var rootDirEntity))
                        {
                            rootDirEntity = new Dir(rootKey);
                            shortcutRootDirs[rootKey] = rootDirEntity;
                            entities.Add(rootDirEntity);
                        }

                        Dir current = rootDirEntity;
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

                    var shortcut = new FileShortcut(s.Name, folder)
                    {
                        Description = s.Description
                    };
                    wixFile.Shortcuts = (wixFile.Shortcuts ?? new FileShortcut[0]).Concat(new[] { shortcut }).ToArray();
                }
                else
                {
                    Console.WriteLine($"Warning: Shortcut target not found: {s.Target} (Resolved to: {targetPath})");
                }
            }

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

                var si = new ServiceInstaller
                {
                    Name        = svc.Name,
                    DisplayName = svc.DisplayName ?? svc.Name,
                    Description = svc.Description ?? string.Empty,
                    Account     = svc.Account,
                    Start       = ParseSvcStartType(svc.Start),
                    Type        = ParseSvcType(svc.Type),
                    ErrorControl = ParseSvcErrorControl(svc.ErrorControl),
                    // Sensible lifecycle defaults: start when installed,
                    // stop before any file replacement and on uninstall,
                    // remove completely on uninstall.
                    StartOn  = SvcEvent.Install,
                    StopOn   = SvcEvent.InstallUninstall_Wait,
                    RemoveOn = SvcEvent.Uninstall_Wait,
                };

                if (!string.IsNullOrEmpty(svc.Arguments))
                    si.Arguments = svc.Arguments;

                if (!string.IsNullOrEmpty(svc.Password))
                    si.Password = svc.Password;

                if (svc.Interactive.HasValue)
                    si.Interactive = svc.Interactive;

                if (svc.DelayedAutoStart.HasValue)
                    si.DelayedAutoStart = svc.DelayedAutoStart;

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

                svcFile.ServiceInstallers = (svcFile.ServiceInstallers ?? new IGenericEntity[0])
                    .Concat(new IGenericEntity[] { si })
                    .ToArray();

                if (_options.Verbose)
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

                var groupFiles = new List<File>();
                foreach (var item in group.Files)
                {
                    var resolved = _resolver.ResolveGlob(item.Source);
                    if (!resolved.Any())
                    {
                        Console.WriteLine($"Warning: Group '{group.Id}': no files matched '{item.Source}'");
                        continue;
                    }
                    bool neverOverwrite = string.Equals(group.Condition, "notExists", StringComparison.OrdinalIgnoreCase);
                    foreach (var filePath in resolved)
                    {
                        var wixFile = new File(filePath);
                        if (!string.IsNullOrEmpty(item.Rename))
                            wixFile.Name = item.Rename;
                        if (neverOverwrite)
                            wixFile.AttributesDefinition = "Component:NeverOverwrite=yes";
                        groupFiles.Add(wixFile);
                    }
                }

                if (!groupFiles.Any()) continue;

                // Build Dir hierarchy for the destination path
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
                entities.Add(groupRootDir);

                if (_options.Verbose)
                    Console.WriteLine($"Group '{group.Id}': {groupFiles.Count} file(s) -> {destPath}");
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

            project.GUID = new Guid(_config.Product.UpgradeCode);
            project.ControlPanelInfo.Manufacturer = _config.Product.Manufacturer;
            project.Description = _config.Product.Description;
            project.Version = new Version(_config.Product.Version);

            project.AttributesDefinition = isMachine ? "Scope=perMachine" : "Scope=perUser";

            // Suppress "Ambiguous short name" warning as we are explicitly generating them
            // -sw1044: suppress "Ambiguous short name" (AlliePack generates explicit short names)
            // -sw5437: suppress "no longer necessary to define standard directory" (WiX 6 advisory, WixSharp emits these)
            project.WixOptions = "-sw1044 -sw5437";

            // Configure installer UI. Always use ManagedUI for a consistent look;
            // include the licence dialog only when a license file is supplied.
            var ui = new ManagedUI();
            if (!string.IsNullOrEmpty(_config.Product.LicenseFile))
            {
                project.LicenceFile = _resolver.Resolve(_config.Product.LicenseFile!);
                ui.InstallDialogs
                    .Add<WelcomeDialog>()
                    .Add<LicenceDialog>()
                    .Add<InstallDirDialog>()
                    .Add<ProgressDialog>()
                    .Add<ExitDialog>();
            }
            else
            {
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
                                xml = XElement.Load(_resolver.Resolve(fragment.File));
                            }
                            else continue;

                            document.Root!.Add(xml);
                            if (_options.Verbose)
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
                project.BuildMsi();
            }
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
                    if (_options.Verbose)
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

            // For AlliePack purposes, same size and name is usually the same file from different build folders.
            // A more robust check would be MD5, but size is a good heuristic for now.
            return true;
        }

        private List<ResolvedFile> ProcessElement(StructureElement element, string currentPath = "")
        {
            var result = new List<ResolvedFile>();
            
            string newPath = currentPath;
            if (!string.IsNullOrEmpty(element.FolderName))
            {
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
                    if (!string.IsNullOrEmpty(element.Source) && !element.Source.Contains("*") && !element.Source.Contains("?"))
                    {
                        string sourcePath = _resolver.Resolve(element.Source ?? "");
                        result.Add(new ResolvedFile {
                            SourcePath = sourcePath,
                            RelativeDestinationPath = Path.Combine(newPath, Path.GetFileName(sourcePath))
                        });
                    }
                }
                else
                {
                    foreach (var (absPath, relPath) in filesWithPaths)
                    {
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
                var solFiles = _solutionResolver.ResolveSolution(element.Solution ?? "", element.Configuration, element.Platform, element.ExcludeProjects, element.ExcludeFiles);
                foreach (var f in solFiles)
                {
                    f.RelativeDestinationPath = Path.Combine(newPath, f.RelativeDestinationPath);
                }
                result.AddRange(solFiles);
            }
            else if (!string.IsNullOrEmpty(element.Project))
            {
                var projFiles = _solutionResolver.ResolveProject(element.Project ?? "", element.Configuration, element.Platform, element.ExcludeFiles);
                foreach (var f in projFiles)
                {
                    f.RelativeDestinationPath = Path.Combine(newPath, f.RelativeDestinationPath);
                }
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
        // Service helpers
        // -----------------------------------------------------------------------

        private static SvcStartType ParseSvcStartType(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "auto":     return SvcStartType.auto;
                case "demand":
                case "manual":   return SvcStartType.demand;
                case "disabled": return SvcStartType.disabled;
                case "boot":     return SvcStartType.boot;
                case "system":   return SvcStartType.system;
                default:
                    Console.WriteLine($"Warning: Unknown service start type '{s}', using 'auto'");
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
        /// Converts a raw installScope value (perUser / perMachine / both) to the
        /// effective scope for this build.  'both' defers to --scope, defaulting
        /// to perUser when no override is supplied.
        /// </summary>
        private string ComputeEffectiveScope(string rawInstallScope)
        {
            if (!rawInstallScope.Equals("both", StringComparison.OrdinalIgnoreCase))
                return rawInstallScope;

            if (!string.IsNullOrEmpty(_options.Scope))
                return _options.Scope.Equals("perMachine", StringComparison.OrdinalIgnoreCase)
                    ? "perMachine"
                    : "perUser";

            return "perUser";
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
            return dir.Path.Resolve(_activeFlags);
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
            if (rawScope.Equals("both", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"Install scope: both -> {effectiveScope}" +
                    (!string.IsNullOrEmpty(_options.Scope) ? $" (--scope {_options.Scope})" : " (default -- pass --scope perUser|perMachine)"));
            else
                Console.WriteLine($"Install scope: {effectiveScope}");
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
                    else if (rawScope.Equals("both", StringComparison.OrdinalIgnoreCase))
                        evScope = isMachineReport ? "machine" : "user";
                    else
                        evScope = "user";
                    Console.WriteLine($"  [{evScope}] {ev.Name} = {ev.Value.Resolve(_activeFlags)}");
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
                        Console.WriteLine($"  [inline] {f.Inline.Trim().Split('\n')[0].Trim()}...");
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
