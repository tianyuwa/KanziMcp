namespace KanziMcpPlugin.Services
{
    internal class NodeFilter
    {
        public string? Type { get; set; }
        public string? Name { get; set; }
        public string? Path { get; set; }
        public bool IncludeProperties { get; set; }
        public bool Recursive { get; set; } = true;
        public int Limit { get; set; } = 1000;
    }
}
