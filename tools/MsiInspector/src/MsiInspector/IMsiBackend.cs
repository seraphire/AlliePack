using System;
using System.Collections.Generic;

namespace MsiInspector
{
    /// <summary>
    /// Abstracts the underlying MSI access layer so MsiInspector public API
    /// does not leak implementation details (currently DTF). Swap implementations
    /// by writing a new <see cref="IMsiBackend"/> without touching public API code.
    /// </summary>
    /// <remarks>
    /// Internal by design. The shim exists for future-proofing — see
    /// <c>docs/reviews/_landing.md</c> in the MsiLib repo and the WiX licensing
    /// research for context on why a swap point was wanted.
    /// </remarks>
    internal interface IMsiBackend : IDisposable
    {
        /// <summary>Lists the names of all tables in the database (excluding system tables).</summary>
        IEnumerable<string> ListTables();

        /// <summary>
        /// Lists the column metadata (name, IDT type, primary key flag) for a given table.
        /// </summary>
        IReadOnlyList<MsiColumn> GetColumns(string tableName);

        /// <summary>
        /// Reads all rows of a table, each row keyed by column name. Values are
        /// already typed (string, int, long, byte[], or null) by the backend.
        /// </summary>
        IEnumerable<IReadOnlyDictionary<string, object?>> ReadRows(string tableName);

        /// <summary>Reads the summary information stream.</summary>
        MsiSummary ReadSummary();

        /// <summary>
        /// Executes raw MSI SQL and returns rows keyed by column name.
        /// Escape hatch for queries the typed API does not yet cover.
        /// </summary>
        IEnumerable<IReadOnlyDictionary<string, object?>> Query(string msiSql);
    }
}
