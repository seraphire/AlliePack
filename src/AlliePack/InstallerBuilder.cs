using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WixSharp;
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

        public InstallerBuilder(AlliePackConfig config, PathResolver resolver, SolutionResolver solutionResolver, Options options)
        {
            _config = config;
            _resolver = resolver;
            _solutionResolver = solutionResolver;
            _options = options;
        }

        public void Build()
        {
            var entities = new List<WixEntity>();
            string installPath = _config.Product.InstallDir ?? (_config.Product.Manufacturer + "\\" + _config.Product.Name);
            if (!installPath.Contains("[") && !Path.IsPathRooted(installPath))
            {
                installPath = Path.Combine("[ProgramFilesFolder]", installPath);
            }
            
            var installDir = new InstallDir(installPath);
            entities.Add(installDir);
            
            foreach (var element in _config.Structure)
            {
                var processed = ProcessElement(element);
                foreach (var entity in processed)
                {
                    if (entity is Dir childDir) installDir.Dirs = installDir.Dirs.Concat(new[] { childDir }).ToArray();
                    else if (entity is File childFile) installDir.Files = installDir.Files.Concat(new[] { childFile }).ToArray();
                    else entities.Add(entity);
                }
            }

            var project = new Project(_config.Product.Name, entities.ToArray());

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
                GenerateReport(project);
            }
            else
            {
                project.BuildMsi();
            }
        }

        private List<WixEntity> ProcessElement(StructureElement element)
        {
            var result = new List<WixEntity>();
            if (!string.IsNullOrEmpty(element.FolderName))
            {
                var dir = new Dir(element.FolderName);
                if (element.Contents != null)
                {
                    foreach (var child in element.Contents)
                    {
                        var childEntities = ProcessElement(child);
                        foreach (var entity in childEntities)
                        {
                            if (entity is Dir childDir) dir.Dirs = dir.Dirs.Concat(new[] { childDir }).ToArray();
                            if (entity is File childFile) dir.Files = dir.Files.Concat(new[] { childFile }).ToArray();
                        }
                    }
                }
                result.Add(dir);
            }
            else if (!string.IsNullOrEmpty(element.Source))
            {
                // Resolve the source path (could be a glob)
                var files = _resolver.ResolveGlob(element.Source);
                
                // Filter files
                if (element.ExcludeFiles.Count > 0)
                {
                    var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher();
                    matcher.AddInclude("**/*");
                    foreach (var exc in element.ExcludeFiles) matcher.AddExclude(exc);
                    
                    files = files.Where(f => {
                        var fileInfo = new FileInfo(f);
                        // For matcher on absolute paths, we need a base. 
                        // But since these are files from a glob, we can just match the filename if the pattern is simple,
                        // or better, use the directory as base.
                        string? dir = Path.GetDirectoryName(f);
                        if (dir == null) return true;
                        var resultMatch = matcher.Execute(new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(new DirectoryInfo(dir)));
                        return resultMatch.Files.Any(m => Path.Combine(dir, m.Path).Equals(f, StringComparison.OrdinalIgnoreCase));
                    }).ToList();
                }

                if (files.Count == 0)
                {
                    if (string.IsNullOrEmpty(element.Source)) return result;
                    if (element.Source.Contains("*") || element.Source.Contains("?")) return result; // Don't add literal wildcards
                    result.Add(new File(_resolver.Resolve(element.Source)));
                }
                else
                {
                    foreach (var f in files)
                    {
                        result.Add(new File(f));
                    }
                }
            }
            else if (!string.IsNullOrEmpty(element.Solution))
            {
                var sol = element.Solution!;
                var files = _solutionResolver.ResolveSolution(sol, element.Configuration, element.Platform, element.ExcludeProjects, element.ExcludeFiles);
                result.AddRange(ConvertResolvedFilesToEntities(files));
            }
            else if (!string.IsNullOrEmpty(element.Project))
            {
                var proj = element.Project!;
                var files = _solutionResolver.ResolveProject(proj, element.Configuration, element.Platform, element.ExcludeFiles);
                result.AddRange(ConvertResolvedFilesToEntities(files));
            }
            else
            {
                throw new Exception("Invalid structure element: must have folder, source, solution or project.");
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

        private void GenerateReport(Project project)
        {
            Console.WriteLine("--- AlliePack MSI Content Report ---");
            Console.WriteLine($"Product: {project.Name}");
            Console.WriteLine($"Manufacturer: {project.ControlPanelInfo.Manufacturer}");
            Console.WriteLine($"Version: {project.Version}");
            Console.WriteLine($"UpgradeCode: {project.GUID}");
            Console.WriteLine("------------------------------------");
            
            // Simple flat print for now using AllFiles/AllDirs
            foreach (var dir in project.AllDirs)
            {
                Console.WriteLine($"[Folder] {dir.Name}");
            }
            foreach (var file in project.AllFiles)
            {
                Console.WriteLine($"[File] {file.Name}");
            }
        }

        private void PrintWixEntity(WixEntity entity, int indent)
        {
             // Not used anymore in this version
        }

        private void PrintDir(string name, int indent)
        {
             string space = new string(' ', indent * 2);
             Console.WriteLine($"{space}[Root] {name}");
        }
    }
}
