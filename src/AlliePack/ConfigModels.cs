using System;
using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace AlliePack
{
    public class AlliePackConfig
    {
        [YamlMember(Alias = "product")]
        public ProductInfo Product { get; set; } = new();

        [YamlMember(Alias = "aliases")]
        public Dictionary<string, string> Aliases { get; set; } = new();

        [YamlMember(Alias = "structure")]
        public List<StructureElement> Structure { get; set; } = new();
    }

    public class ProductInfo
    {
        public string Name { get; set; } = "My Product";
        public string Manufacturer { get; set; } = "My Company";
        public string Version { get; set; } = "1.0.0.0";
        public string Description { get; set; } = string.Empty;
        public string UpgradeCode { get; set; } = Guid.NewGuid().ToString();
        public string InstallScope { get; set; } = "perMachine"; // perMachine or perUser
    }

    public class StructureElement
    {
        [YamlMember(Alias = "folder")]
        public string? FolderName { get; set; }

        [YamlMember(Alias = "destination")]
        public string? Destination { get; set; }

        [YamlMember(Alias = "source")]
        public string? Source { get; set; }

        [YamlMember(Alias = "contents")]
        public List<StructureElement>? Contents { get; set; }
    }
}
