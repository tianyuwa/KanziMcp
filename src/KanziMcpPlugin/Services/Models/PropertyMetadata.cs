namespace KanziMcpPlugin.Services
{
    internal class PropertyMetadata
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Category { get; set; } = "";
        public bool IsReadOnly { get; set; }
    }
}
