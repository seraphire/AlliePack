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
            if (installPath.StartsWith("[ProgramFiles]\\", StringComparison.OrdinalIgnoreCase))
            {
                installPath = "[ProgramFilesFolder]\\" + installPath.Substring("[ProgramFiles]\\".Length);
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
            var hierarchy = ConvertResolvedFilesToEntities(uniqueFiles);
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
                var files = _resolver.ResolveGlob(element.Source);
                
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
                        string sourcePath = _resolver.Resolve(element.Source);
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
                var solFiles = _solutionResolver.ResolveSolution(element.Solution, element.Configuration, element.Platform, element.ExcludeProjects, element.ExcludeFiles);
                foreach (var f in solFiles)
                {
                    f.RelativeDestinationPath = Path.Combine(newPath, f.RelativeDestinationPath);
                }
                result.AddRange(solFiles);
            }
            else if (!string.IsNullOrEmpty(element.Project))
            {
                var projFiles = _solutionResolver.ResolveProject(element.Project, element.Configuration, element.Platform, element.ExcludeFiles);
                foreach (var f in projFiles)
                {
                    f.RelativeDestinationPath = Path.Combine(newPath, f.RelativeDestinationPath);
                }
                result.AddRange(projFiles);
            }

            return result;
        }

        private List<WixEntity> ConvertResolvedFilesToEntities(List<ResolvedFile> files)
        {
            // This needs to group by directory structure
            var rootDirs = new List<Dir>();
            var rootFiles = new List<File>();

            foreach (var file in files)
            {
                string relPath = file.RelativeDestinationPath;
                string[] parts = relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                
                if (parts.Length == 1)
                {
                    rootFiles.Add(new File(file.SourcePath));
                }
                else
                {
                    // Navigate/Create Dir structure
                    Dir current = GetOrCreateDir(rootDirs, parts[0]);
                    for (int i = 1; i < parts.Length - 1; i++)
                    {
                        current = GetOrCreateDir(current, parts[i]);
                    }
                    var wixFile = new File(file.SourcePath);
                    current.Files = current.Files.Concat(new[] { wixFile }).ToArray();
                }
            }

            var result = new List<WixEntity>();
            result.AddRange(rootDirs);
            result.AddRange(rootFiles);
            return result;
        }

        private Dir GetOrCreateDir(List<Dir> list, string name)
        {
            var existing = list.FirstOrDefault(d => d.Name == name);
            if (existing != null) return existing;
            var @new = new Dir(name);
            list.Add(@new);
            return @new;
        }

        private Dir GetOrCreateDir(Dir parent, string name)
        {
            var existing = parent.Dirs.FirstOrDefault(d => d.Name == name);
            if (existing != null) return existing;
            var @new = new Dir(name);
            parent.Dirs = parent.Dirs.Concat(new[] { @new }).ToArray();
            return @new;
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
                Console.WriteLine($"{space}[Folder] {dir.Name}");
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
                // In WixSharp, File.Name usually contains the source path unless explicitly set.
                // We'll try to show a clean name if possible.
                string fileName = file.Name;
                if (Path.IsPathRooted(fileName)) fileName = Path.GetFileName(fileName);
                Console.WriteLine($"{space}[File] {fileName}");
            }
        }
    }
}
