using System;

namespace MsiInspector
{
    /// <summary>
    /// One row of the MSI File table.
    /// </summary>
    public sealed class MsiFile
    {
        public string FileKey { get; }
        public string ComponentKey { get; }
        public string FileName { get; }
        public long FileSize { get; }
        public string? Version { get; }
        public string? Language { get; }
        public int Attributes { get; }
        public int Sequence { get; }

        public MsiFile(
            string fileKey,
            string componentKey,
            string fileName,
            long fileSize,
            string? version,
            string? language,
            int attributes,
            int sequence)
        {
            FileKey = fileKey;
            ComponentKey = componentKey;
            FileName = fileName;
            FileSize = fileSize;
            Version = version;
            Language = language;
            Attributes = attributes;
            Sequence = sequence;
        }
    }

    /// <summary>
    /// One row of the MSI Directory table.
    /// </summary>
    public sealed class MsiDirectory
    {
        public string DirectoryKey { get; }
        public string? ParentDirectoryKey { get; }
        public string DefaultDir { get; }

        public MsiDirectory(string directoryKey, string? parentDirectoryKey, string defaultDir)
        {
            DirectoryKey = directoryKey;
            ParentDirectoryKey = parentDirectoryKey;
            DefaultDir = defaultDir;
        }
    }

    /// <summary>
    /// One row of the MSI Component table.
    /// </summary>
    public sealed class MsiComponent
    {
        public string ComponentKey { get; }
        public string ComponentId { get; }
        public string DirectoryKey { get; }
        public int Attributes { get; }
        public string? Condition { get; }
        public string? KeyPath { get; }

        public MsiComponent(
            string componentKey,
            string componentId,
            string directoryKey,
            int attributes,
            string? condition,
            string? keyPath)
        {
            ComponentKey = componentKey;
            ComponentId = componentId;
            DirectoryKey = directoryKey;
            Attributes = attributes;
            Condition = condition;
            KeyPath = keyPath;
        }
    }

    /// <summary>
    /// MSI summary information stream contents.
    /// </summary>
    public sealed class MsiSummary
    {
        public string Title { get; }
        public string Subject { get; }
        public string Author { get; }
        public string Keywords { get; }
        public string Comments { get; }
        public string Template { get; }
        public string LastSavedBy { get; }
        public Guid RevisionNumber { get; }
        public DateTime? CreateTime { get; }
        public DateTime? LastSavedTime { get; }
        public int PageCount { get; }
        public int WordCount { get; }
        public int CharacterCount { get; }
        public string CreatingApp { get; }
        public string Security { get; }

        public MsiSummary(
            string title,
            string subject,
            string author,
            string keywords,
            string comments,
            string template,
            string lastSavedBy,
            Guid revisionNumber,
            DateTime? createTime,
            DateTime? lastSavedTime,
            int pageCount,
            int wordCount,
            int characterCount,
            string creatingApp,
            string security)
        {
            Title = title;
            Subject = subject;
            Author = author;
            Keywords = keywords;
            Comments = comments;
            Template = template;
            LastSavedBy = lastSavedBy;
            RevisionNumber = revisionNumber;
            CreateTime = createTime;
            LastSavedTime = lastSavedTime;
            PageCount = pageCount;
            WordCount = wordCount;
            CharacterCount = characterCount;
            CreatingApp = creatingApp;
            Security = security;
        }
    }

    /// <summary>
    /// Describes one column of an MSI table for IDT-style export.
    /// </summary>
    public sealed class MsiColumn
    {
        public string Name { get; }
        public string IdtType { get; }
        public bool IsPrimaryKey { get; }

        public MsiColumn(string name, string idtType, bool isPrimaryKey)
        {
            Name = name;
            IdtType = idtType;
            IsPrimaryKey = isPrimaryKey;
        }
    }
}
