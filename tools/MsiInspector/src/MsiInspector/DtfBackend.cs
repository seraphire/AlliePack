using System;
using System.Collections.Generic;
using System.Linq;
using WixToolset.Dtf.WindowsInstaller;

namespace MsiInspector
{
    /// <summary>
    /// DTF-based implementation of <see cref="IMsiBackend"/>. The only place
    /// in MsiInspector that references <c>WixToolset.Dtf.WindowsInstaller</c>.
    /// </summary>
    internal sealed class DtfBackend : IMsiBackend
    {
        private readonly Database _db;
        private bool _disposed;

        public DtfBackend(string msiPath)
        {
            if (msiPath == null) throw new ArgumentNullException(nameof(msiPath));
            _db = new Database(msiPath, DatabaseOpenMode.ReadOnly);
        }

        public IEnumerable<string> ListTables()
        {
            ThrowIfDisposed();
            using var view = _db.OpenView("SELECT `Name` FROM `_Tables`");
            view.Execute();
            Record? record;
            while ((record = view.Fetch()) != null)
            {
                using (record)
                {
                    yield return record.GetString(1);
                }
            }
        }

        public IReadOnlyList<MsiColumn> GetColumns(string tableName)
        {
            ThrowIfDisposed();
            if (tableName == null) throw new ArgumentNullException(nameof(tableName));

            // _Columns has Table, Number, Name, Type
            // The Type field encodes column type and constraints in MSI's compact notation
            // (e.g., "s72" = required string up to 72 chars; "S255" = nullable string up to 255).
            // For IDT export we keep the raw type string; consumers can parse if needed.
            var columns = new List<(int Number, string Name, string Type)>();
            using (var view = _db.OpenView(
                "SELECT `Number`, `Name`, `Type` FROM `_Columns` WHERE `Table` = ?"))
            {
                using var paramRecord = new Record(1);
                paramRecord.SetString(1, tableName);
                view.Execute(paramRecord);
                Record? record;
                while ((record = view.Fetch()) != null)
                {
                    using (record)
                    {
                        columns.Add((record.GetInteger(1), record.GetString(2), record.GetString(3)));
                    }
                }
            }

            // Primary key columns: read PK metadata via _TableInfo? Simpler path:
            // use Database.Tables[tableName].PrimaryKeys.
            var primaryKeys = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var tableInfo = _db.Tables[tableName];
                if (tableInfo != null)
                {
                    foreach (var pk in tableInfo.PrimaryKeys)
                    {
                        primaryKeys.Add(pk);
                    }
                }
            }
            catch
            {
                // Not all tables have accessible PrimaryKeys metadata; ignore.
            }

            return columns
                .OrderBy(c => c.Number)
                .Select(c => new MsiColumn(c.Name, c.Type, primaryKeys.Contains(c.Name)))
                .ToList();
        }

        public IEnumerable<IReadOnlyDictionary<string, object?>> ReadRows(string tableName)
        {
            ThrowIfDisposed();
            if (tableName == null) throw new ArgumentNullException(nameof(tableName));

            var columns = GetColumns(tableName);
            var columnList = string.Join(", ", columns.Select(c => "`" + c.Name + "`"));
            var sql = "SELECT " + columnList + " FROM `" + tableName + "`";

            using var view = _db.OpenView(sql);
            view.Execute();
            Record? record;
            while ((record = view.Fetch()) != null)
            {
                using (record)
                {
                    var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (var i = 0; i < columns.Count; i++)
                    {
                        row[columns[i].Name] = ReadField(record, i + 1, columns[i].IdtType);
                    }
                    yield return row;
                }
            }
        }

        public MsiSummary ReadSummary()
        {
            ThrowIfDisposed();
            using var summaryInfo = _db.SummaryInfo;
            return new MsiSummary(
                title: summaryInfo.Title ?? string.Empty,
                subject: summaryInfo.Subject ?? string.Empty,
                author: summaryInfo.Author ?? string.Empty,
                keywords: summaryInfo.Keywords ?? string.Empty,
                comments: summaryInfo.Comments ?? string.Empty,
                template: summaryInfo.Template ?? string.Empty,
                lastSavedBy: summaryInfo.LastSavedBy ?? string.Empty,
                revisionNumber: TryParseGuid(summaryInfo.RevisionNumber),
                createTime: summaryInfo.CreateTime == DateTime.MinValue ? null : summaryInfo.CreateTime,
                lastSavedTime: summaryInfo.LastSaveTime == DateTime.MinValue ? null : summaryInfo.LastSaveTime,
                pageCount: summaryInfo.PageCount,
                wordCount: summaryInfo.WordCount,
                characterCount: summaryInfo.CharacterCount,
                creatingApp: summaryInfo.CreatingApp ?? string.Empty,
                security: summaryInfo.Security.ToString());
        }

        public IEnumerable<IReadOnlyDictionary<string, object?>> Query(string msiSql)
        {
            ThrowIfDisposed();
            if (msiSql == null) throw new ArgumentNullException(nameof(msiSql));

            using var view = _db.OpenView(msiSql);
            view.Execute();
            Record? record;
            ColumnCollection? columnInfo = null;
            while ((record = view.Fetch()) != null)
            {
                using (record)
                {
                    columnInfo ??= view.Columns;
                    var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (var i = 0; i < columnInfo.Count; i++)
                    {
                        var name = columnInfo[i].Name;
                        row[name] = ReadFieldByGuess(record, i + 1);
                    }
                    yield return row;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _db.Close();
            _db.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DtfBackend));
        }

        private static object? ReadField(Record record, int field, string idtType)
        {
            // IDT type prefix: lowercase = NOT NULL; uppercase = nullable.
            // s/S = string, i/I = int, l/L = localizable string, v/V = stream.
            if (string.IsNullOrEmpty(idtType)) return record.GetString(field);
            var typeChar = char.ToLowerInvariant(idtType[0]);
            switch (typeChar)
            {
                case 's':
                case 'l':
                    return record.IsNull(field) ? null : record.GetString(field);
                case 'i':
                    if (record.IsNull(field)) return null;
                    // i2 = 16-bit, i4 = 32-bit; both fit in int. Use long for safety.
                    return (long)record.GetInteger(field);
                case 'v':
                    return record.IsNull(field) ? null : record.GetStream(field);
                default:
                    return record.IsNull(field) ? null : record.GetString(field);
            }
        }

        private static object? ReadFieldByGuess(Record record, int field)
        {
            if (record.IsNull(field)) return null;
            // Without column-type info, fall back to string.
            try { return record.GetString(field); }
            catch { return null; }
        }

        private static Guid TryParseGuid(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Guid.Empty;
            return Guid.TryParse(value, out var g) ? g : Guid.Empty;
        }
    }
}
