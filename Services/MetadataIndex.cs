using System.IO;
using System.Text.Json;
using System.Xml;
using D365EntitySqlGenerator.Models;

namespace D365EntitySqlGenerator.Services;

/// <summary>
/// Scans a PackagesLocalDirectory and provides:
///  - fast name→file maps for entities and tables (used for loading &amp; nested-entity detection);
///  - a searchable list of entities with their datasource tables (built once, cached to disk).
/// Layout assumed: &lt;root&gt;\&lt;Package&gt;\&lt;Model&gt;\AxDataEntityView\*.xml and \AxTable\*.xml.
/// The parallel \&lt;Package&gt;\XppMetadata\... mirror is skipped.
/// </summary>
public sealed class MetadataIndex
{
    public string Root { get; }

    // name (case-insensitive) → file path
    public Dictionary<string, string> EntityFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TableFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    // package/model tags per entity, keyed by entity name
    private readonly Dictionary<string, (string pkg, string model)> _entityOrigin =
        new(StringComparer.OrdinalIgnoreCase);

    public List<EntityIndexEntry> Entities { get; private set; } = new();

    public MetadataIndex(string root) => Root = root;

    /// <summary>Fast pass: build the entity/table file maps (path enumeration only).</summary>
    public void BuildFileMaps()
    {
        EntityFiles.Clear();
        TableFiles.Clear();
        _entityOrigin.Clear();

        foreach (var packageDir in SafeDirs(Root))
        {
            var package = Path.GetFileName(packageDir);
            foreach (var modelDir in SafeDirs(packageDir))
            {
                var model = Path.GetFileName(modelDir);
                if (string.Equals(model, "XppMetadata", StringComparison.OrdinalIgnoreCase))
                    continue;

                AddFiles(Path.Combine(modelDir, "AxDataEntityView"), EntityFiles,
                    entityName => _entityOrigin[entityName] = (package, model));
                AddFiles(Path.Combine(modelDir, "AxTable"), TableFiles, null);
            }
        }
    }

    private static void AddFiles(string dir, Dictionary<string, string> map, Action<string>? onAdd)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.xml"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            map[name] = file;           // later model/package wins on duplicate name; acceptable
            onAdd?.Invoke(name);
        }
    }

    public bool TableExists(string name) => TableFiles.ContainsKey(name);
    public bool EntityExists(string name) => EntityFiles.ContainsKey(name);

    public string? EntityFile(string name) => EntityFiles.TryGetValue(name, out var p) ? p : null;
    public string? TableFile(string name) => TableFiles.TryGetValue(name, out var p) ? p : null;

    // ---- Searchable datasource index (heavy; cached) -------------------------------------------

    private sealed class CacheDoc
    {
        public string Root { get; set; } = "";
        public List<EntityIndexEntry> Entities { get; set; } = new();
    }

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "D365EntitySqlGenerator", "index-cache.json");

    public bool TryLoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return false;
            var doc = JsonSerializer.Deserialize<CacheDoc>(File.ReadAllText(CachePath));
            if (doc == null || !string.Equals(doc.Root, Root, StringComparison.OrdinalIgnoreCase))
                return false;
            Entities = doc.Entities;
            return Entities.Count > 0;
        }
        catch { return false; }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath,
                JsonSerializer.Serialize(new CacheDoc { Root = Root, Entities = Entities }));
        }
        catch { /* cache is best-effort */ }
    }

    /// <summary>
    /// Build the datasource search index by streaming each entity file. Reports progress as
    /// (done, total). Cancellable. Results are cached to disk on completion.
    /// </summary>
    public void BuildSearchIndex(IProgress<(int done, int total)>? progress, CancellationToken ct)
    {
        var files = EntityFiles.Values.ToList();
        var total = files.Count;
        var result = new List<EntityIndexEntry>(total);

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var name = Path.GetFileNameWithoutExtension(file);
            var origin = _entityOrigin.TryGetValue(name, out var o) ? o : ("", "");

            var entry = new EntityIndexEntry
            {
                EntityName = name,
                Package = origin.Item1,
                Model = origin.Item2,
                FilePath = file,
            };
            try
            {
                ExtractDataSourceTables(file, entry);
            }
            catch { /* skip unreadable files, keep name searchable */ }

            result.Add(entry);
            if (progress != null && (i % 200 == 0 || i == files.Count - 1))
                progress.Report((i + 1, total));
        }

        Entities = result;
        SaveCache();
    }

    private static void ExtractDataSourceTables(string file, EntityIndexEntry entry)
    {
        var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(file, settings);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Table")
            {
                var value = reader.ReadElementContentAsString().Trim();
                if (value.Length == 0 || !seen.Add(value)) continue;
                if (entry.RootTable.Length == 0) entry.RootTable = value; // first Table = root datasource
                entry.DataSourceTables.Add(value);
            }
        }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return Enumerable.Empty<string>(); }
    }
}
