using Timeline.Web.Services;

namespace Timeline.Web.Components.Pages;

public partial class Image
{
    private sealed class FileTreeDirectory
    {
        private readonly Dictionary<string, FileTreeDirectory> _directories = new(StringComparer.OrdinalIgnoreCase);

        public FileTreeDirectory(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public IReadOnlyCollection<FileTreeDirectory> Directories => _directories.Values;
        public List<ImageItemRow> Files { get; } = [];

        public FileTreeDirectory GetOrAddDirectory(string name)
        {
            if (!_directories.TryGetValue(name, out var directory))
            {
                directory = new FileTreeDirectory(name);
                _directories.Add(name, directory);
            }

            return directory;
        }
    }

    private sealed record ImageFileTreeRow(string Name, int Depth, ImageItemRow? File, IReadOnlyList<string> ItemIds);
}
