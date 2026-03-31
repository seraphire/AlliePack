using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WixSharp;
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

        public InstallerBuilder(AlliePackConfig config, PathResolver resolver, SolutionResolver solutionResolver, Options options)
        {
            _config = config;
            _resolver = resolver;
            _solutionResolver = solutionResolver;
            _options = options;
        }

        public void Build()
        {
            WixExtension.UI.PreferredVersion = "6.0.2";
            var allFiles = new List<ResolvedFile>();
            foreach (var element in _config.Structure)
            {
                allFiles.AddRange(ProcessElement(element));
            }

            // Deduplicate
            var uniqueFiles = DeduplicateFiles(allFiles);

            var entities = new List<WixEntity>();
            string installPath = _config.Product.InstallDir ?? (_config.Product.Manufacturer + "\\" + _config.Product.Name);
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
                installPath = Path.Combine("[ProgramFilesFolder]", installPath);
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
            entities.Add(rootDir);

            // Process Shortcuts
            var shortcutRootDirs = new Dictionary<string, Dir>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var s in _config.Shortcuts)
            {
                string targetPath = ResolvePath(s.Target, installPath);
                if (fileMap.TryGetValue(targetPath, out var wixFile))
                {
                    string folder = ResolveFolder(s.Folder);
                    
                    // Ensure the target folder exists as a Dir entity in the project
                    if (folder.StartsWith("%"))
                    {
                        string[] parts = folder.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            string rootKey = parts[0]; // e.g., %ProgramMenu%
                            if (!shortcutRootDirs.TryGetValue(rootKey, out var rootDirEntity))
                            {
                                rootDirEntity = new Dir(rootKey);
                                shortcutRootDirs[rootKey] = rootDirEntity;
                                entities.Add(rootDirEntity);
                            }

                            Dir current = rootDirEntity;
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

            var project = new Project(_config.Product.Name, entities.ToArray());

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

            if (_config.Product.InstallScope.Equals("perUser", StringComparison.OrdinalIgnoreCase))
            {
                project.AttributesDefinition = "Scope=perUser";
            }
            else
            {
                project.AttributesDefinition = "Scope=perMachine";
            }

            // Suppress "Ambiguous short name" warning as we are explicitly generating them
            project.WixOptions = "-sw1044";

            if (_options.ReportOnly)
            {
                GenerateReport(project, entities);
            }
            else
            {
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
                var files = _resolver.ResolveGlob(element.Source ?? "");
                
                // Exclusions
                if (element.ExcludeFiles.Count > 0)
                {
                    var matcher = new Matcher();
                    matcher.AddInclude("**/*");
                    foreach (var exc in element.ExcludeFiles) matcher.AddExclude(exc);
                    
                    files = files.Where(f => {
                        string? dir = Path.GetDirectoryName(f);
                        if (dir == null) return true;
                        var res = matcher.Execute(new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(new DirectoryInfo(dir)));
                        return res.Files.Any(m => Path.Combine(dir, m.Path).Equals(f, StringComparison.OrdinalIgnoreCase));
                    }).ToList();
                }

                if (files.Count == 0)
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
                    foreach (var f in files)
                    {
                        result.Add(new ResolvedFile { 
                            SourcePath = f, 
                            RelativeDestinationPath = Path.Combine(newPath, Path.GetFileName(f)) 
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

        private string ResolveFolder(string folder)
        {
            string result = folder.Replace('/', '\\');
            
            // Map common properties to WixSharp format (%)
            if (result.StartsWith("[ProgramMenuFolder]", StringComparison.OrdinalIgnoreCase))
                result = "%ProgramMenu%" + result.Substring("[ProgramMenuFolder]".Length);
            else if (result.StartsWith("[Desktop]", StringComparison.OrdinalIgnoreCase))
                result = "%Desktop%" + result.Substring("[Desktop]".Length);
            else if (result.StartsWith("[DesktopFolder]", StringComparison.OrdinalIgnoreCase))
                result = "%Desktop%" + result.Substring("[DesktopFolder]".Length);
            
            return result;
        }

        private bool IsStandardWixProperty(string alias)
        {
            string[] standard = { "[ProgramFilesFolder]", "[ProgramFiles64Folder]", "[CommonFilesFolder]", "[AppDataFolder]", "[LocalAppDataFolder]", "[ProgramMenuFolder]", "[DesktopFolder]", "[TempFolder]" };
            return standard.Any(s => s.Equals(alias, StringComparison.OrdinalIgnoreCase));
        }

        private void GenerateReport(Project project, List<WixEntity> entities)
        {
            Console.WriteLine("--- AlliePack MSI Content Report ---");
            Console.WriteLine($"Product: {project.Name}");
            Console.WriteLine($"Manufacturer: {project.ControlPanelInfo.Manufacturer}");
            Console.WriteLine($"Version: {project.Version}");
            Console.WriteLine($"UpgradeCode: {project.GUID}");
            Console.WriteLine("------------------------------------");
            
            foreach (var entity in entities)
            {
                PrintEntity(entity, 0);
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
