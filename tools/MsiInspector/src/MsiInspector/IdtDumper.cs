using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MsiInspector
{
    /// <summary>
    /// Dumps an MSI database to Microsoft IDT files (one per table).
    /// IDT is the format used by <c>msidb.exe -e</c>: tab-separated values
    /// with a three-line header (column names / column types / table key).
    /// </summary>
    /// <remarks>
    /// Output is intentionally deterministic - rows sorted by primary key,
    /// columns in declared order - so identical input MSIs produce identical
    /// IDT files for clean diffing. Streams (binary blob columns) are listed
    /// with size rather than dumped to disk, keeping the output text-only.
    /// </remarks>
    public static class IdtDumper
    {
        // Microsoft IDT escape characters - documented in Windows Installer SDK.
        // These appear in field values to represent characters that would
        // otherwise break the tab-separated, CRLF-terminated IDT format.
        private const char IdtTabEscape = '';   // DC1: literal tab inside a field
        private const char IdtCrEscape  = '';   // EM:  literal carriage return inside a field
        private const char IdtLfEscape  = '';   // DLE: literal line feed inside a field

        /// <summary>
        /// Dumps all user tables of the MSI at <paramref name="msiPath"/> to
        /// IDT files in <paramref name="outputDir"/>. Creates the output
        /// directory if it does not exist. Existing IDT files are overwritten.
        /// </summary>
        public static void Dump(string msiPath, string outputDir, DumpOptions? options = null)
        {
            if (msiPath == null) throw new ArgumentNullException(nameof(msiPath));
            if (outputDir == null) throw new ArgumentNullException(nameof(outputDir));
            options ??= new DumpOptions();

            Directory.CreateDirectory(outputDir);

            using var inspector = new Inspector(msiPath);

            // Tables: stable order so dump output is reproducible.
            var tables = inspector.ListTables()
                .Where(t => !options.IsExcluded(t))
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();

            foreach (var table in tables)
            {
                var path = Path.Combine(outputDir, table + ".idt");
                WriteTable(inspector, table, path);
            }

            if (options.IncludeSummary)
            {
                WriteSummary(inspector, Path.Combine(outputDir, "_SummaryInformation.txt"));
            }
        }

        private static void WriteTable(Inspector inspector, string tableName, string outputPath)
        {
            var columns = inspector.GetColumns(tableName);
            if (columns.Count == 0) return;

            var rows = inspector.ReadRows(tableName).ToList();

            // Sort rows by primary key columns for deterministic output.
            var pkColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
            if (pkColumns.Count > 0)
            {
                rows.Sort((a, b) =>
                {
                    foreach (var pk in pkColumns)
                    {
                        var av = a.TryGetValue(pk, out var x) ? x?.ToString() ?? string.Empty : string.Empty;
                        var bv = b.TryGetValue(pk, out var y) ? y?.ToString() ?? string.Empty : string.Empty;
                        var cmp = string.CompareOrdinal(av, bv);
                        if (cmp != 0) return cmp;
                    }
                    return 0;
                });
            }

            using var writer = new StreamWriter(outputPath, append: false, encoding: new UTF8Encoding(false));
            writer.NewLine = "\r\n";

            // Line 1: column names, tab-separated.
            writer.WriteLine(string.Join("\t", columns.Select(c => c.Name)));

            // Line 2: column types.
            writer.WriteLine(string.Join("\t", columns.Select(c => c.IdtType)));

            // Line 3: table name + primary key column names (tab-separated).
            var headerRow = new List<string> { tableName };
            headerRow.AddRange(pkColumns);
            writer.WriteLine(string.Join("\t", headerRow));

            // Data rows.
            foreach (var row in rows)
            {
                var values = columns.Select(c =>
                {
                    if (!row.TryGetValue(c.Name, out var v) || v == null) return string.Empty;
                    if (v is byte[] bytes) return FormatStream(bytes);
                    if (v is Stream stream) return FormatStream(stream);
                    return EscapeIdt(v.ToString() ?? string.Empty);
                });
                writer.WriteLine(string.Join("\t", values));
            }
        }

        private static void WriteSummary(Inspector inspector, string outputPath)
        {
            var s = inspector.Summary;
            using var writer = new StreamWriter(outputPath, append: false, encoding: new UTF8Encoding(false));
            writer.NewLine = "\n";
            writer.WriteLine("Title:           " + s.Title);
            writer.WriteLine("Subject:         " + s.Subject);
            writer.WriteLine("Author:          " + s.Author);
            writer.WriteLine("Keywords:        " + s.Keywords);
            writer.WriteLine("Comments:        " + s.Comments);
            writer.WriteLine("Template:        " + s.Template);
            writer.WriteLine("LastSavedBy:     " + s.LastSavedBy);
            writer.WriteLine("RevisionNumber:  " + (s.RevisionNumber == Guid.Empty ? string.Empty : s.RevisionNumber.ToString("B")));
            writer.WriteLine("CreateTime:      " + (s.CreateTime?.ToString("o") ?? string.Empty));
            writer.WriteLine("LastSavedTime:   " + (s.LastSavedTime?.ToString("o") ?? string.Empty));
            writer.WriteLine("PageCount:       " + s.PageCount);
            writer.WriteLine("WordCount:       " + s.WordCount);
            writer.WriteLine("CharacterCount:  " + s.CharacterCount);
            writer.WriteLine("CreatingApp:     " + s.CreatingApp);
            writer.WriteLine("Security:        " + s.Security);
        }

        private static string EscapeIdt(string value)
        {
            // IDT format uses tab as field separator and CRLF as record separator.
            // Replace literal occurrences with Microsoft's documented escape chars
            // (DC1/EM/DLE) so they survive round-trip through msidb.exe -i.
            if (string.IsNullOrEmpty(value)) return value;
            if (value.IndexOfAny(new[] { '\t', '\r', '\n' }) < 0) return value;

            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '\t': sb.Append(IdtTabEscape); break;
                    case '\r': sb.Append(IdtCrEscape);  break;
                    case '\n': sb.Append(IdtLfEscape);  break;
                    default:   sb.Append(ch);           break;
                }
            }
            return sb.ToString();
        }

        private static string FormatStream(byte[] bytes) => "[stream " + bytes.Length + " bytes]";

        private static string FormatStream(Stream stream)
        {
            try { return "[stream " + stream.Length + " bytes]"; }
            catch { return "[stream]"; }
        }
    }

    /// <summary>
    /// Options for <see cref="IdtDumper.Dump"/>.
    /// </summary>
    public sealed class DumpOptions
    {
        /// <summary>
        /// Tables to skip when dumping. Default empty (dump all user tables).
        /// System tables (those starting with '_') are skipped automatically.
        /// </summary>
        public ISet<string> ExcludedTables { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Whether to write a _SummaryInformation.txt sidecar with the summary stream.
        /// Defaults to true.
        /// </summary>
        public bool IncludeSummary { get; set; } = true;

        internal bool IsExcluded(string tableName)
        {
            if (string.IsNullOrEmpty(tableName)) return true;
            if (tableName[0] == '_') return true; // skip system tables
            return ExcludedTables.Contains(tableName);
        }
    }
}
