using System;
using System.Collections.Generic;
using System.Linq;

namespace MsiInspector
{
    /// <summary>
    /// Read-only inspection of an MSI database. Designed for AlliePack test
    /// infrastructure: open an .msi file, query its tables and summary info
    /// programmatically, or feed it to the IDT dumper for diff-based validation.
    /// </summary>
    public sealed class Inspector : IDisposable
    {
        private readonly IMsiBackend _backend;
        private bool _disposed;

        // Lazy-loaded snapshots; backed by IMsiBackend
        private IReadOnlyList<MsiFile>? _files;
        private IReadOnlyList<MsiDirectory>? _directories;
        private IReadOnlyList<MsiComponent>? _components;
        private MsiSummary? _summary;
        private Dictionary<string, MsiDirectory>? _directoryByKey;

        /// <summary>
        /// Opens an MSI for read-only inspection.
        /// </summary>
        public Inspector(string msiPath)
        {
            if (msiPath == null) throw new ArgumentNullException(nameof(msiPath));
            _backend = new DtfBackend(msiPath);
        }

        // For tests / future swap point — accept a backend directly.
        internal Inspector(IMsiBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        /// <summary>All rows of the File table.</summary>
        public IReadOnlyList<MsiFile> Files
        {
            get
            {
                ThrowIfDisposed();
                return _files ??= LoadFiles();
            }
        }

        /// <summary>All rows of the Directory table.</summary>
        public IReadOnlyList<MsiDirectory> Directories
        {
            get
            {
                ThrowIfDisposed();
                return _directories ??= LoadDirectories();
            }
        }

        /// <summary>All rows of the Component table.</summary>
        public IReadOnlyList<MsiComponent> Components
        {
            get
            {
                ThrowIfDisposed();
                return _components ??= LoadComponents();
            }
        }

        /// <summary>Summary information stream contents.</summary>
        public MsiSummary Summary
        {
            get
            {
                ThrowIfDisposed();
                return _summary ??= _backend.ReadSummary();
            }
        }

        /// <summary>
        /// Looks up a single File row by its primary key. Returns null if not present.
        /// </summary>
        public MsiFile? GetFile(string fileKey)
        {
            ThrowIfDisposed();
            if (fileKey == null) throw new ArgumentNullException(nameof(fileKey));
            // Linear scan is fine — File tables are typically &lt; 10K rows in practice.
            for (var i = 0; i < Files.Count; i++)
            {
                if (string.Equals(Files[i].FileKey, fileKey, StringComparison.Ordinal))
                    return Files[i];
            }
            return null;
        }

        /// <summary>
        /// Resolves the full directory path for a Directory key by walking the
        /// Directory_Parent chain. Returns the concatenated DefaultDir values
        /// joined by '\\', stopping at TARGETDIR (the conventional root).
        /// </summary>
        /// <remarks>
        /// MSI's DefaultDir column has source/target syntax: "TargetName:SourceName"
        /// or "TargetName" alone. This method returns the target component only.
        /// Special tokens (e.g., '.', '..', '*') are returned literally; resolving
        /// against well-known properties like ProgramFiles is the caller's concern.
        /// </remarks>
        public string ResolveDirectoryFullPath(string directoryKey)
        {
            ThrowIfDisposed();
            if (directoryKey == null) throw new ArgumentNullException(nameof(directoryKey));
            EnsureDirectoryIndex();

            var parts = new List<string>();
            var current = directoryKey;
            var guard = 0;
            while (current != null)
            {
                if (++guard > 256) // pathological cycle guard
                    throw new InvalidOperationException(
                        "Directory parent chain exceeded 256 entries — possible cycle near " + directoryKey);

                if (!_directoryByKey!.TryGetValue(current, out var dir))
                    break;

                parts.Add(GetTargetSegment(dir.DefaultDir));
                current = dir.ParentDirectoryKey;
            }
            parts.Reverse();
            return string.Join("\\", parts);
        }

        /// <summary>
        /// Counts how many Directory table rows reference the given DirectoryKey
        /// (i.e., have it as their primary Directory key). Useful for
        /// "is a/b/c listed only once?" assertions.
        /// </summary>
        public int CountDirectoryEntries(string directoryKey)
        {
            ThrowIfDisposed();
            if (directoryKey == null) throw new ArgumentNullException(nameof(directoryKey));
            return Directories.Count(d => string.Equals(d.DirectoryKey, directoryKey, StringComparison.Ordinal));
        }

        /// <summary>
        /// Returns all File rows whose owning Component points at the given Directory key.
        /// </summary>
        public IEnumerable<MsiFile> GetFilesIn(string directoryKey)
        {
            ThrowIfDisposed();
            if (directoryKey == null) throw new ArgumentNullException(nameof(directoryKey));
            var componentsInDir = new HashSet<string>(
                Components
                    .Where(c => string.Equals(c.DirectoryKey, directoryKey, StringComparison.Ordinal))
                    .Select(c => c.ComponentKey),
                StringComparer.Ordinal);
            return Files.Where(f => componentsInDir.Contains(f.ComponentKey));
        }

        /// <summary>
        /// Lists the table names present in the database.
        /// </summary>
        public IEnumerable<string> ListTables()
        {
            ThrowIfDisposed();
            return _backend.ListTables();
        }

        /// <summary>
        /// Escape hatch: run raw MSI SQL against the database.
        /// Returns rows keyed by column name. MSI SQL is a restricted subset
        /// — no JOINs, no subqueries, positional '?' parameters via separate API.
        /// </summary>
        public IEnumerable<IReadOnlyDictionary<string, object?>> Query(string msiSql)
        {
            ThrowIfDisposed();
            if (msiSql == null) throw new ArgumentNullException(nameof(msiSql));
            return _backend.Query(msiSql);
        }

        /// <summary>
        /// Returns column metadata for a table, in column-number order.
        /// </summary>
        public IReadOnlyList<MsiColumn> GetColumns(string tableName)
        {
            ThrowIfDisposed();
            if (tableName == null) throw new ArgumentNullException(nameof(tableName));
            return _backend.GetColumns(tableName);
        }

        /// <summary>
        /// Reads all rows of the named table as untyped column-name dictionaries.
        /// Used by IdtDumper; useful directly for ad-hoc inspection.
        /// </summary>
        public IEnumerable<IReadOnlyDictionary<string, object?>> ReadRows(string tableName)
        {
            ThrowIfDisposed();
            if (tableName == null) throw new ArgumentNullException(nameof(tableName));
            return _backend.ReadRows(tableName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _backend.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Inspector));
        }

        private void EnsureDirectoryIndex()
        {
            if (_directoryByKey != null) return;
            var index = new Dictionary<string, MsiDirectory>(StringComparer.Ordinal);
            foreach (var dir in Directories)
            {
                // MSI permits multiple rows with same key only via authoring bugs; last write wins for the index.
                index[dir.DirectoryKey] = dir;
            }
            _directoryByKey = index;
        }

        private IReadOnlyList<MsiFile> LoadFiles()
        {
            var list = new List<MsiFile>();
            foreach (var row in _backend.ReadRows("File"))
            {
                list.Add(new MsiFile(
                    fileKey: GetString(row, "File"),
                    componentKey: GetString(row, "Component_"),
                    fileName: GetString(row, "FileName"),
                    fileSize: GetLong(row, "FileSize"),
                    version: GetStringOrNull(row, "Version"),
                    language: GetStringOrNull(row, "Language"),
                    attributes: GetInt(row, "Attributes"),
                    sequence: GetInt(row, "Sequence")));
            }
            return list;
        }

        private IReadOnlyList<MsiDirectory> LoadDirectories()
        {
            var list = new List<MsiDirectory>();
            foreach (var row in _backend.ReadRows("Directory"))
            {
                list.Add(new MsiDirectory(
                    directoryKey: GetString(row, "Directory"),
                    parentDirectoryKey: GetStringOrNull(row, "Directory_Parent"),
                    defaultDir: GetString(row, "DefaultDir")));
            }
            return list;
        }

        private IReadOnlyList<MsiComponent> LoadComponents()
        {
            var list = new List<MsiComponent>();
            foreach (var row in _backend.ReadRows("Component"))
            {
                list.Add(new MsiComponent(
                    componentKey: GetString(row, "Component"),
                    componentId: GetString(row, "ComponentId"),
                    directoryKey: GetString(row, "Directory_"),
                    attributes: GetInt(row, "Attributes"),
                    condition: GetStringOrNull(row, "Condition"),
                    keyPath: GetStringOrNull(row, "KeyPath")));
            }
            return list;
        }

        private static string GetString(IReadOnlyDictionary<string, object?> row, string column)
            => row.TryGetValue(column, out var v) && v is string s ? s : string.Empty;

        private static string? GetStringOrNull(IReadOnlyDictionary<string, object?> row, string column)
            => row.TryGetValue(column, out var v) ? v as string : null;

        private static int GetInt(IReadOnlyDictionary<string, object?> row, string column)
        {
            if (!row.TryGetValue(column, out var v) || v == null) return 0;
            return v switch
            {
                int i => i,
                long l => (int)l,
                _ => 0
            };
        }

        private static long GetLong(IReadOnlyDictionary<string, object?> row, string column)
        {
            if (!row.TryGetValue(column, out var v) || v == null) return 0;
            return v switch
            {
                int i => i,
                long l => l,
                _ => 0
            };
        }

        // DefaultDir is "Target:Source" or just "Target". Return target component.
        private static string GetTargetSegment(string defaultDir)
        {
            if (string.IsNullOrEmpty(defaultDir)) return string.Empty;
            var colon = defaultDir.IndexOf(':');
            return colon >= 0 ? defaultDir.Substring(0, colon) : defaultDir;
        }
    }
}
