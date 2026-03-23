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

            // If path is relative, make it relative to YamlDir
            if (!Path.IsPathRooted(path))
            {
                path = Path.GetFullPath(Path.Combine(_yamlDir, path));
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
            string resolvedPattern = Resolve(pattern);
            string? dir = Path.GetDirectoryName(resolvedPattern);
            string? filePattern = Path.GetFileName(resolvedPattern);

            if (dir == null || !Directory.Exists(dir)) return new List<string>();

            return Directory.GetFiles(dir, filePattern).ToList();
        }
    }
}
