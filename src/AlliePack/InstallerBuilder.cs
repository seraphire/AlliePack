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
        private readonly Options _options;

        public InstallerBuilder(AlliePackConfig config, PathResolver resolver, Options options)
        {
            _config = config;
            _resolver = resolver;
            _options = options;
        }

        public void Build()
        {
            var entities = new List<WixEntity>();
            entities.Add(new InstallDir(@"[ProgramFilesFolder]\" + _config.Product.Manufacturer + "\\" + _config.Product.Name));
            
            foreach (var element in _config.Structure)
            {
                entities.AddRange(ProcessElement(element));
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
                if (files.Count == 0)
                {
                    // Fallback to direct resolution if no files found (might be a single non-existent file)
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
            else
            {
                throw new Exception("Invalid structure element: must have folder or source.");
            }
            return result;
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
