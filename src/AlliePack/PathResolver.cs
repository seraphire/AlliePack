using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AlliePack
{
    public class PathResolver
    {
        private readonly string _yamlDir;
        private readonly string? _gitRoot;
        private readonly Dictionary<string, string> _aliases;

        public PathResolver(string yamlFilePath, Dictionary<string, string> aliases)
        {
            _yamlDir = Path.GetDirectoryName(Path.GetFullPath(yamlFilePath)) ?? Environment.CurrentDirectory;
            _gitRoot = FindGitRoot(_yamlDir);
            _aliases = aliases;
        }

        public string Resolve(string path)
        {
            // Replace tokens
            path = path.Replace("[YamlDir]", _yamlDir)
                       .Replace("[CurrentDir]", Environment.CurrentDirectory);
            
            if (_gitRoot != null)
            {
                path = path.Replace("[GitRoot]", _gitRoot);
            }

            // Replace aliases
            foreach (var alias in _aliases)
            {
                if (path.StartsWith(alias.Key + ":"))
                {
                    path = Path.Combine(alias.Value, path.Substring(alias.Key.Length + 1));
                    break; // Only one alias per path
                }
            }

            // Normalize the path, resolving any .. or . segments
            if (!path.Contains("*") && !path.Contains("?"))
            {
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(_yamlDir, path);
                path = Path.GetFullPath(path);
            }
            else if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(_yamlDir, path);
            }

            return path;
        }

        private string? FindGitRoot(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                if (dir.GetDirectories(".git").Any() || dir.GetFiles(".git").Any())
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }

        public List<string> ResolveGlob(string pattern)
        {
            return ResolveGlobWithPaths(pattern).Select(t => t.AbsolutePath).ToList();
        }

        public List<(string AbsolutePath, string RelativePath)> ResolveGlobWithPaths(string pattern)
        {
            string resolvedPattern = Resolve(pattern);

            if (resolvedPattern.Contains("**"))
            {
                int idx = resolvedPattern.IndexOf("**", StringComparison.Ordinal);
                string baseDir = resolvedPattern.Substring(0, idx).TrimEnd('\\', '/');
                string filePattern = resolvedPattern.Substring(idx + 2).TrimStart('\\', '/');
                if (string.IsNullOrEmpty(filePattern)) filePattern = "*";

                if (!Directory.Exists(baseDir)) return new List<(string, string)>();

                return Directory.GetFiles(baseDir, filePattern, SearchOption.AllDirectories)
                    .Select(f => (f, f.Substring(baseDir.Length).TrimStart('\\', '/')))
                    .ToList();
            }

            string? dir = Path.GetDirectoryName(resolvedPattern);
            string? filePat = Path.GetFileName(resolvedPattern);

            if (dir == null || !Directory.Exists(dir)) return new List<(string, string)>();

            return Directory.GetFiles(dir, filePat)
                .Select(f => (f, Path.GetFileName(f)))
                .ToList();
        }
    }
}
