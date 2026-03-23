using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using Microsoft.Extensions.FileSystemGlobbing;

namespace AlliePack
{
    public class SolutionResolver
    {
        private readonly PathResolver _pathResolver;

        public SolutionResolver(PathResolver pathResolver)
        {
            _pathResolver = pathResolver;
        }

        public List<ResolvedFile> ResolveSolution(string solutionPath, string configuration, string platform, List<string> excludeProjects, List<string> excludeFiles)
        {
            var results = new List<ResolvedFile>();
            string fullPath = _pathResolver.Resolve(solutionPath);
            
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Solution file not found.", fullPath);

            // Using Microsoft.VisualStudio.SolutionPersistence 1.0.9
            ISolutionSerializer? serializer = null;
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            if (ext == ".slnx") serializer = SolutionSerializers.SlnXml;
            else serializer = SolutionSerializers.SlnFileV12;

            if (serializer == null)
                throw new NotSupportedException($"Unsupported solution extension: {ext}");

            var solutionModel = serializer.OpenAsync(fullPath, default).Result;

            foreach (var project in solutionModel.SolutionProjects)
            {
                if (project.DisplayName != null && excludeProjects.Contains(project.DisplayName))
                    continue;

                // project.FilePath is relative to the solution file
                if (project.FilePath == null) continue;
                string absoluteProjectPath = Path.Combine(Path.GetDirectoryName(fullPath)!, project.FilePath);
                results.AddRange(ResolveProject(absoluteProjectPath, configuration, platform, excludeFiles));
            }

            return results;
        }

        public List<ResolvedFile> ResolveProject(string projectPath, string configuration, string platform, List<string> excludeFiles)
        {
            var results = new List<ResolvedFile>();
            string fullPath = _pathResolver.Resolve(projectPath);

            if (!File.Exists(fullPath))
                return results; // Or throw

            string outputPath = GetOutputPath(fullPath, configuration, platform);
            if (string.IsNullOrEmpty(outputPath))
                return results;

            if (!Path.IsPathRooted(outputPath))
            {
                outputPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, outputPath));
            }

            if (!Directory.Exists(outputPath))
                return results;

            // Collect files with globbing exclusions
            var matcher = new Matcher();
            matcher.AddInclude("**/*");
            foreach (var pattern in excludeFiles)
            {
                matcher.AddExclude(pattern);
            }

            var dirInfo = new DirectoryInfo(outputPath);
            var matchingResult = matcher.Execute(new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(dirInfo));

            foreach (var file in matchingResult.Files)
            {
                results.Add(new ResolvedFile
                {
                    SourcePath = Path.Combine(outputPath, file.Path),
                    RelativeDestinationPath = file.Path
                });
            }

            return results;
        }

        private string GetOutputPath(string projectPath, string configuration, string platform)
        {
            // Simplified logic to find OutputPath in .csproj
            // For robust resolution, full MSBuild evaluation is needed, 
            // but we'll try a common pattern search first.
            try
            {
                var doc = XDocument.Load(projectPath);
                var ns = doc.Root?.Name.Namespace;
                
                // Try to find PropertyGroup with matching Configuration/Platform
                var propertyGroups = doc.Descendants(ns + "PropertyGroup");
                
                string? bestPath = null;

                foreach (var pg in propertyGroups)
                {
                    string condition = pg.Attribute("Condition")?.Value ?? "";
                    bool matchesConfig = string.IsNullOrEmpty(configuration) || condition.Contains(configuration);
                    bool matchesPlatform = string.IsNullOrEmpty(platform) || condition.Contains(platform);

                    if (matchesConfig && matchesPlatform)
                    {
                        var op = pg.Element(ns + "OutputPath")?.Value;
                        if (!string.IsNullOrEmpty(op))
                        {
                            bestPath = op;
                        }
                    }
                }

                if (bestPath == null)
                {
                    // Fallback to general OutputPath
                    bestPath = doc.Descendants(ns + "OutputPath").FirstOrDefault()?.Value;
                }

                // SDK-style default fallbacks if still null
                if (bestPath == null && doc.Root?.Attribute("Sdk") != null)
                {
                    // Typical SDK path: bin\{Configuration}\{TargetFramework}
                    // Since we don't easily know TargetFramework here without more parsing, 
                    // we'll look for subdirectories in bin\{Configuration}
                    string baseOut = Path.Combine(Path.GetDirectoryName(projectPath)!, "bin", configuration);
                    if (Directory.Exists(baseOut))
                    {
                        var subdirs = Directory.GetDirectories(baseOut);
                        if (subdirs.Length > 0) return subdirs[0]; // Return first TFM folder
                        return baseOut;
                    }
                }

                return bestPath ?? "";
            }
            catch
            {
                return "";
            }
        }
    }

    public class ResolvedFile
    {
        public string SourcePath { get; set; } = string.Empty;
        public string RelativeDestinationPath { get; set; } = string.Empty;
    }
}
