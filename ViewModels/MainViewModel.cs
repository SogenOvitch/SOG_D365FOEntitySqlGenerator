using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using D365EntitySqlGenerator.Models;
using D365EntitySqlGenerator.Services;

namespace D365EntitySqlGenerator.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int MaxResults = 300;

    private readonly SettingsStore _settingsStore = new();
    private readonly AppSettings _settings;

    private MetadataIndex? _index;
    private MetadataService? _meta;
    private SqlGenerator? _sql;
    private CancellationTokenSource? _indexCts;

    private EntityInfo? _currentEntity;
    private readonly HashSet<string> _disabledExplicit = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TreeNodeViewModel> _dsNodes = new();
    private readonly List<TreeNodeViewModel> _fieldNodes = new();
    private readonly List<TreeNodeViewModel> _allNodes = new();

    public MainViewModel()
    {
        _settings = _settingsStore.Load();
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        RefreshIndexCommand = new RelayCommand(() => StartIndexing(force: true), () => _index != null);
        CopySqlCommand = new RelayCommand(CopySql, () => HasSql);
        SaveSqlCommand = new RelayCommand(SaveSql, () => HasSql);
        ExpandAllCommand = new RelayCommand(() => SetAllExpanded(true), () => _allNodes.Count > 0);
        CollapseAllCommand = new RelayCommand(() => SetAllExpanded(false), () => _allNodes.Count > 0);

        if (!string.IsNullOrWhiteSpace(_settings.PackagesLocalDirectory)
            && Directory.Exists(_settings.PackagesLocalDirectory))
        {
            PackagesDirectory = _settings.PackagesLocalDirectory!;
            InitializeIndex();
        }
        else
        {
            StatusText = "Set your PackagesLocalDirectory to begin.";
        }
    }

    // ---- Bound properties ----------------------------------------------------------------------

    private string _packagesDirectory = "(not set)";
    public string PackagesDirectory { get => _packagesDirectory; set => Set(ref _packagesDirectory, value); }

    private bool _syncingFacets;

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                Raise(nameof(SearchActive));
                Raise(nameof(SearchEmpty));
                RefreshResults();
            }
        }
    }

    /// <summary>True once the user has typed something → the dropdowns become enabled.</summary>
    public bool SearchActive => SearchText.Trim().Length > 0;

    /// <summary>Drives the search-box placeholder visibility.</summary>
    public bool SearchEmpty => SearchText.Length == 0;

    // Three search facets (enabled when the search box has text).
    public ObservableCollection<EntityIndexEntry> ByNameResults { get; } = new();
    public ObservableCollection<EntityIndexEntry> ByRootResults { get; } = new();
    public ObservableCollection<EntityIndexEntry> ByDataSourceResults { get; } = new();

    private EntityIndexEntry? _selByName;
    public EntityIndexEntry? SelectedByName
    {
        get => _selByName;
        set { if (Set(ref _selByName, value) && value != null && !_syncingFacets) OnFacetSelected(value, 0); }
    }

    private EntityIndexEntry? _selByRoot;
    public EntityIndexEntry? SelectedByRoot
    {
        get => _selByRoot;
        set { if (Set(ref _selByRoot, value) && value != null && !_syncingFacets) OnFacetSelected(value, 1); }
    }

    private EntityIndexEntry? _selByDataSource;
    public EntityIndexEntry? SelectedByDataSource
    {
        get => _selByDataSource;
        set { if (Set(ref _selByDataSource, value) && value != null && !_syncingFacets) OnFacetSelected(value, 2); }
    }

    /// <summary>Selecting in one dropdown clears the other two, then loads the entity.</summary>
    private void OnFacetSelected(EntityIndexEntry value, int which)
    {
        _syncingFacets = true;
        if (which != 0) { _selByName = null; Raise(nameof(SelectedByName)); }
        if (which != 1) { _selByRoot = null; Raise(nameof(SelectedByRoot)); }
        if (which != 2) { _selByDataSource = null; Raise(nameof(SelectedByDataSource)); }
        _syncingFacets = false;
        LoadEntity(value);
    }

    public ObservableCollection<TreeNodeViewModel> Tree { get; } = new();

    private string _generatedSql = "";
    public string GeneratedSql
    {
        get => _generatedSql;
        set { if (Set(ref _generatedSql, value)) Raise(nameof(HasSql)); }
    }
    public bool HasSql => GeneratedSql.Length > 0;

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _selectedEntityName = "";
    public string SelectedEntityName { get => _selectedEntityName; set => Set(ref _selectedEntityName, value); }

    public ICommand BrowseFolderCommand { get; }
    public ICommand RefreshIndexCommand { get; }
    public ICommand CopySqlCommand { get; }
    public ICommand SaveSqlCommand { get; }
    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }

    // ---- Folder / indexing ---------------------------------------------------------------------

    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select the D365 PackagesLocalDirectory" };
        if (_index != null && Directory.Exists(PackagesDirectory))
            dlg.InitialDirectory = PackagesDirectory;

        if (dlg.ShowDialog() == true)
        {
            PackagesDirectory = dlg.FolderName;
            _settings.PackagesLocalDirectory = dlg.FolderName;
            _settingsStore.Save(_settings);
            InitializeIndex();
        }
    }

    private void InitializeIndex()
    {
        StatusText = "Scanning packages…";
        ClearFacets();
        Tree.Clear();
        GeneratedSql = "";

        var index = new MetadataIndex(PackagesDirectory);
        Task.Run(() =>
        {
            index.BuildFileMaps();
            var hadCache = index.TryLoadCache();
            Application.Current.Dispatcher.Invoke(() =>
            {
                _index = index;
                _meta = new MetadataService(index);
                _sql = new SqlGenerator(_meta);
                StatusText = $"{index.EntityFiles.Count:N0} entities, {index.TableFiles.Count:N0} tables found.";
                if (hadCache) StatusText += "  Search index loaded from cache.";
                else StartIndexing(force: false);
                RefreshResults();
            });
        });
    }

    private void StartIndexing(bool force)
    {
        if (_index == null) return;

        _indexCts?.Cancel();
        _indexCts = new CancellationTokenSource();
        var ct = _indexCts.Token;
        var index = _index;

        var progress = new Progress<(int done, int total)>(p =>
            StatusText = $"Indexing datasources… {p.done:N0} / {p.total:N0}");

        Task.Run(() =>
        {
            try
            {
                index.BuildSearchIndex(progress, ct);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusText = $"Search index ready ({index.Entities.Count:N0} entities).";
                    RefreshResults();
                });
            }
            catch (OperationCanceledException) { /* superseded */ }
        }, ct);
    }

    // ---- Search facets -------------------------------------------------------------------------

    private void ClearFacets()
    {
        ByNameResults.Clear();
        ByRootResults.Clear();
        ByDataSourceResults.Clear();
    }

    private void RefreshResults()
    {
        ClearFacets();
        if (_index == null) return;

        var q = SearchText.Trim();
        if (q.Length == 0) return;

        var source = _index.Entities.Count > 0
            ? _index.Entities
            : _index.EntityFiles.Select(kv => new EntityIndexEntry { EntityName = kv.Key, FilePath = kv.Value });
        var list = source.ToList();

        Fill(ByNameResults, list.Where(e =>
            e.EntityName.Contains(q, StringComparison.OrdinalIgnoreCase)));

        Fill(ByRootResults, list.Where(e =>
            e.RootTable.Contains(q, StringComparison.OrdinalIgnoreCase)));

        Fill(ByDataSourceResults, list.Where(e =>
            e.DataSourceTables.Skip(1).Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))));
    }

    private static void Fill(ObservableCollection<EntityIndexEntry> target, IEnumerable<EntityIndexEntry> items)
    {
        foreach (var e in items.OrderBy(e => e.EntityName, StringComparer.OrdinalIgnoreCase).Take(MaxResults))
            target.Add(e);
    }

    // ---- Load + generate -----------------------------------------------------------------------

    private void LoadEntity(EntityIndexEntry entry)
    {
        if (_meta == null || _sql == null) return;
        try
        {
            var path = entry.FilePath.Length > 0 ? entry.FilePath : _index!.EntityFile(entry.EntityName);
            if (path == null) return;

            _currentEntity = _meta.LoadEntityFromFile(path);
            _disabledExplicit.Clear();
            SelectedEntityName = _currentEntity.Name;
            BuildTree(_currentEntity);
            RegenerateSql();
            StatusText = $"Generated SQL for {_currentEntity.Name}.";
        }
        catch (Exception ex)
        {
            GeneratedSql = $"-- Failed to generate: {ex.Message}";
            Tree.Clear();
        }
    }

    private void RegenerateSql()
    {
        if (_currentEntity == null || _sql == null) return;
        GeneratedSql = _sql.Generate(_currentEntity, BuildEffectiveDisabledSet());
    }

    // ---- Tree ----------------------------------------------------------------------------------

    private void BuildTree(EntityInfo entity)
    {
        Tree.Clear();
        _dsNodes.Clear();
        _fieldNodes.Clear();
        _allNodes.Clear();

        var root = new TreeNodeViewModel
        {
            Text = entity.Name,
            Foreground = entity.IsReadOnly ? TreeNodeViewModel.Red : TreeNodeViewModel.Accent,
            ToolTip = $"{entity.Package} / {entity.Model}",
        };
        Tree.Add(root);

        if (!entity.DataManagementEnabled)
            root.Add(new TreeNodeViewModel
            {
                Text = "⚠ NOT data-management enabled (no staging table)",
                Foreground = TreeNodeViewModel.Red,
            });
        if (entity.IsReadOnly)
            root.Add(new TreeNodeViewModel
            {
                Text = "⚠ Read-only entity — every field is RDD_ (not importable)",
                Foreground = TreeNodeViewModel.Red,
            });

        // Data Sources first (per request), then Fields.
        var dsFolder = root.Add(new TreeNodeViewModel { Text = "Data Sources" });
        if (entity.RootDataSource != null)
            dsFolder.Add(BuildDsNode(entity.RootDataSource, isRoot: true));

        var fieldsFolder = root.Add(new TreeNodeViewModel { Text = $"Fields ({entity.Fields.Count})", IsExpanded = false });
        foreach (var f in entity.Fields)
        {
            var node = f.IsComputed
                ? new TreeNodeViewModel
                {
                    Text = $"{f.Name}  (computed {f.UnmappedType})",
                    Foreground = TreeNodeViewModel.Muted,
                    IsExpanded = false,
                }
                : new TreeNodeViewModel
                {
                    Text = $"{f.Name}  ⇐  {f.DataSource}.{f.DataField}",
                    IsExpanded = false,
                    ToolTip = $"{f.DataSource}.{f.DataField} → {f.Name}",
                    FieldDataSource = f.DataSource,
                };
            fieldsFolder.Add(node);
            _fieldNodes.Add(node);
        }

        CollectAllNodes(Tree);
    }

    private TreeNodeViewModel BuildDsNode(EntityDataSourceInfo ds, bool isRoot)
    {
        var flags = new List<string>();
        if (isRoot) flags.Add("root");
        if (ds.IsNestedEntity) flags.Add("nested entity");
        if (ds.IsReadOnly) flags.Add("read-only");
        var suffix = flags.Count > 0 ? $"  [{string.Join(", ", flags)}]" : "";

        Brush color = ds.IsReadOnly ? TreeNodeViewModel.Red
            : ds.IsNestedEntity ? TreeNodeViewModel.Muted
            : TreeNodeViewModel.Normal;

        var node = new TreeNodeViewModel
        {
            Text = $"{ds.Name} : {ds.Table}{suffix}",
            Foreground = color,
            ToolTip = $"Join: {ds.JoinMode}",
            DataSource = ds,
            ShowCheckbox = !isRoot,        // the root is the FROM clause; it cannot be disabled
        };
        node.OnToggled = OnDataSourceToggled;
        _dsNodes.Add(node);

        // Non-checkable "Filters" subnode listing the datasource's ranges / date-effectivity.
        var filters = BuildFilterLines(ds);
        if (filters.Count > 0)
        {
            var filtersNode = node.Add(new TreeNodeViewModel
            {
                Text = "Filters",
                Foreground = TreeNodeViewModel.Muted,
                IsExpanded = false,
            });
            foreach (var f in filters)
                filtersNode.Add(new TreeNodeViewModel
                {
                    Text = f,
                    Foreground = TreeNodeViewModel.Muted,
                    IsExpanded = false,
                });
        }

        foreach (var child in ds.Children)
            node.Add(BuildDsNode(child, isRoot: false));
        return node;
    }

    /// <summary>Human-readable filter descriptions for a datasource: its query ranges (excluding the
    /// implicit DataAreaId company range) plus a date-effectivity line when ApplyDateFilter is set.</summary>
    private static List<string> BuildFilterLines(EntityDataSourceInfo ds)
    {
        var lines = new List<string>();
        foreach (var rg in ds.Ranges)
        {
            if (string.Equals(rg.Field, "DataAreaId", StringComparison.OrdinalIgnoreCase))
                continue;
            lines.Add($"{rg.Field} = {rg.Value}");
        }
        if (ds.ApplyDateFilter)
            lines.Add("@DateExecution BETWEEN ValidFrom AND ValidTo");
        return lines;
    }

    private void OnDataSourceToggled(TreeNodeViewModel node)
    {
        if (node.DataSource == null) return;

        if (node.IsChecked)
        {
            // Re-enable this datasource and its whole subtree.
            foreach (var name in Descendants(node.DataSource))
                _disabledExplicit.Remove(name);
        }
        else
        {
            _disabledExplicit.Add(node.DataSource.Name);
        }

        RecomputeStates();
        RegenerateSql();
    }

    /// <summary>Reflect the effective enabled/disabled state onto every node, then dim accordingly.</summary>
    private void RecomputeStates()
    {
        foreach (var n in _dsNodes)
        {
            if (n.DataSource == null) continue;
            var disabled = IsEffectivelyDisabled(n.DataSource);
            n.SetCheckedSilently(!disabled);
            n.IsDimmed = disabled;
        }
        foreach (var n in _fieldNodes)
        {
            var ds = n.FieldDataSource != null ? _currentEntity?.FindDataSource(n.FieldDataSource) : null;
            n.IsDimmed = ds != null && IsEffectivelyDisabled(ds);
        }
    }

    private bool IsEffectivelyDisabled(EntityDataSourceInfo ds)
    {
        for (var d = ds; d != null; d = d.Parent)
            if (_disabledExplicit.Contains(d.Name)) return true;
        return false;
    }

    private HashSet<string> BuildEffectiveDisabledSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in _dsNodes)
            if (n.DataSource != null && IsEffectivelyDisabled(n.DataSource))
                set.Add(n.DataSource.Name);
        return set;
    }

    private static IEnumerable<string> Descendants(EntityDataSourceInfo ds)
    {
        yield return ds.Name;
        foreach (var c in ds.Children)
            foreach (var n in Descendants(c))
                yield return n;
    }

    private void CollectAllNodes(IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (var n in nodes)
        {
            _allNodes.Add(n);
            CollectAllNodes(n.Children);
        }
    }

    private void SetAllExpanded(bool expanded)
    {
        foreach (var n in _allNodes)
            n.IsExpanded = expanded || ReferenceEquals(n, Tree.FirstOrDefault()); // keep root open on collapse
    }

    // ---- Clipboard / file ----------------------------------------------------------------------

    private void CopySql()
    {
        if (HasSql) Clipboard.SetText(GeneratedSql);
    }

    private void SaveSql()
    {
        if (!HasSql) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save SQL script",
            FileName = (SelectedEntityName.Length > 0 ? SelectedEntityName : "entity") + ".sql",
            DefaultExt = ".sql",
            Filter = "SQL script (*.sql)|*.sql|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, GeneratedSql);
            StatusText = $"Saved {Path.GetFileName(dlg.FileName)}.";
        }
    }
}
