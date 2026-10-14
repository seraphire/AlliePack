using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace AlliePack
{
    /// <summary>
    /// WXS post-processing for features WixSharp has no first-class model for:
    /// the Finish-page launch checkbox, programs run after install, install-time XML
    /// edits, file associations, and standard-UI artwork.  Actions reference installed files by File Id;
    /// <see cref="InstallerBuilder"/> pins those Ids before the source is generated.
    /// </summary>
    internal class InstallerExtras
    {
        static readonly XNamespace Wix  = "http://wixtoolset.org/schemas/v4/wxs";
        static readonly XNamespace Util = "http://wixtoolset.org/schemas/v4/wxs/util";

        public (string FileId, string Text, string? Args, bool Checked)? LaunchOnExit { get; set; }
        public List<(string FileId, string? Args, string Condition)> RunAfterInstall { get; } = new();
        public List<(string FileId, string Path, string? Name, string Value)> XmlEdits { get; } = new();
        public List<FileAssociation> FileAssociations { get; } = new();
        public string? BannerBmp { get; set; }
        public string? DialogBmp { get; set; }

        public bool HasAny =>
            LaunchOnExit != null || RunAfterInstall.Any() || XmlEdits.Any() || FileAssociations.Any()
            || BannerBmp != null || DialogBmp != null;

        public void Apply(XDocument doc)
        {
            var package = doc.Descendants(Wix + "Package").FirstOrDefault();
            if (package == null) return;

            if (BannerBmp != null) AddWixVariable(package, "WixUIBannerBmp", BannerBmp);
            if (DialogBmp != null) AddWixVariable(package, "WixUIDialogBmp", DialogBmp);

            if (LaunchOnExit is { } launch)
            {
                var (fileId, text, args, isChecked) = launch;
                // WixUI's ExitDialog shows its optional checkbox whenever this text
                // property is set (and the product was not already installed).
                package.Add(new XElement(Wix + "Property",
                    new XAttribute("Id", "WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT"),
                    new XAttribute("Value", text)));
                if (isChecked)
                    package.Add(new XElement(Wix + "Property",
                        new XAttribute("Id", "WIXUI_EXITDIALOGOPTIONALCHECKBOX"),
                        new XAttribute("Value", "1")));

                package.Add(ExeAction("apLaunchOnExit", fileId, args));

                // Runs in the UI process, so the app starts as the interactive user.
                GetOrAdd(package, "UI").Add(new XElement(Wix + "Publish",
                    new XAttribute("Dialog",    "ExitDialog"),
                    new XAttribute("Control",   "Finish"),
                    new XAttribute("Event",     "DoAction"),
                    new XAttribute("Value",     "apLaunchOnExit"),
                    new XAttribute("Condition", "WIXUI_EXITDIALOGOPTIONALCHECKBOX = 1 AND NOT Installed")));
            }

            string after = "InstallFinalize";
            for (int i = 0; i < RunAfterInstall.Count; i++)
            {
                var (fileId, args, condition) = RunAfterInstall[i];
                string id = "apRunAfterInstall" + (i + 1);
                package.Add(ExeAction(id, fileId, args));

                var custom = new XElement(Wix + "Custom",
                    new XAttribute("Action", id),
                    new XAttribute("After", after));
                if (!string.IsNullOrWhiteSpace(condition))
                    custom.Add(new XAttribute("Condition", condition));
                GetOrAdd(package, "InstallExecuteSequence").Add(custom);
                after = id;
            }

            for (int i = 0; i < XmlEdits.Count; i++)
            {
                var (fileId, path, name, value) = XmlEdits[i];
                var file = FindFile(doc, fileId);
                // The edit belongs to the component that installs the file, so it is
                // applied whenever that file is (re)installed.
                var component = file?.Parent;
                if (component == null) continue;

                var xmlFile = new XElement(Util + "XmlFile",
                    new XAttribute("Id", "apXmlEdit" + (i + 1)),
                    new XAttribute("File", $"[#{fileId}]"),
                    new XAttribute("ElementPath", path),
                    new XAttribute("Action", "setValue"),
                    new XAttribute("Value", value),
                    new XAttribute("Sequence", i + 1));
                if (!string.IsNullOrEmpty(name))
                    xmlFile.Add(new XAttribute("Name", name));
                component.Add(xmlFile);
            }

            foreach (var assoc in FileAssociations)
            {
                var component = FindFile(doc, assoc.FileId)?.Parent;
                if (component == null) continue;

                // Non-advertised: WiX writes these as Registry rows on the file's
                // component.  Icon is the Id of an installed file with an icon resource.
                var extension = new XElement(Wix + "Extension",
                    new XAttribute("Id", assoc.Extension));
                if (!string.IsNullOrEmpty(assoc.ContentType))
                    extension.Add(new XAttribute("ContentType", assoc.ContentType));
                foreach (var verb in assoc.Verbs)
                {
                    var v = new XElement(Wix + "Verb",
                        new XAttribute("Id", verb.Id),
                        new XAttribute("TargetFile", assoc.FileId),
                        new XAttribute("Argument", verb.Args));
                    if (!string.IsNullOrEmpty(verb.Label))
                        v.Add(new XAttribute("Command", verb.Label));
                    extension.Add(v);
                }

                var progId = new XElement(Wix + "ProgId",
                    new XAttribute("Id", assoc.ProgId),
                    new XAttribute("Icon", assoc.IconFileId),
                    new XAttribute("IconIndex", assoc.IconIndex));
                if (!string.IsNullOrEmpty(assoc.Description))
                    progId.Add(new XAttribute("Description", assoc.Description));
                progId.Add(extension);
                component.Add(progId);
            }
        }

        static XElement? FindFile(XDocument doc, string fileId) =>
            doc.Descendants(Wix + "File").FirstOrDefault(f => (string?)f.Attribute("Id") == fileId);

        static XElement ExeAction(string id, string fileId, string? args)
        {
            // WiX requires ExeCommand with FileRef; an empty value means "no arguments".
            return new XElement(Wix + "CustomAction",
                new XAttribute("Id", id),
                new XAttribute("FileRef", fileId),
                new XAttribute("ExeCommand", args ?? string.Empty),
                new XAttribute("Return", "asyncNoWait"));
        }

        static void AddWixVariable(XElement package, string id, string value)
        {
            package.Elements(Wix + "WixVariable")
                .Where(v => (string?)v.Attribute("Id") == id)
                .Remove();
            package.Add(new XElement(Wix + "WixVariable",
                new XAttribute("Id", id),
                new XAttribute("Value", value)));
        }

        static XElement GetOrAdd(XElement package, string name)
        {
            var el = package.Elements(Wix + name).FirstOrDefault();
            if (el == null)
            {
                el = new XElement(Wix + name);
                package.Add(el);
            }
            return el;
        }
    }

    /// <summary>A file association with its files already pinned to File Ids.</summary>
    internal class FileAssociation
    {
        public string Extension { get; set; } = string.Empty;
        public string ProgId { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string FileId { get; set; } = string.Empty;
        public string IconFileId { get; set; } = string.Empty;
        public int IconIndex { get; set; }
        public string? ContentType { get; set; }
        public List<FileAssociationVerbConfig> Verbs { get; set; } = new();
    }
}
