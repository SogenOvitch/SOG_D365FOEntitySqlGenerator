using System.Text;
using D365EntitySqlGenerator.Models;

namespace D365EntitySqlGenerator.Services;

public sealed class SqlGenerator
{
    private const string CompanyParam = "@DataAreaId";
    private const string DateParam = "@DateExecution";
    private const string Ind = "    ";      // one indent level

    private readonly MetadataService _meta;
    private readonly RelationResolver _relations = new();
    private readonly EditabilityResolver _editability;

    // Datasource names the user has switched off in the tree (their fields & joins are omitted).
    private IReadOnlySet<string> _disabled = new HashSet<string>();

    public SqlGenerator(MetadataService meta)
    {
        _meta = meta;
        _editability = new EditabilityResolver(meta);
    }

    public string Generate(EntityInfo entity, IReadOnlySet<string>? disabledDataSources = null)
    {
        _disabled = disabledDataSources ?? new HashSet<string>();

        var header = new StringBuilder();
        WriteHeader(header, entity);

        var sb = new StringBuilder();
        var root = entity.RootDataSource;
        var rootAlias = root?.Name ?? "";
        var staging = entity.DataManagementEnabled && entity.StagingTable.Length > 0
            ? _meta.GetTable(entity.StagingTable)
            : null;

        // ---- SELECT ----------------------------------------------------------------------------
        // Fields are grouped by their datasource and emitted in datasource tree order (root first,
        // then children pre-order) with depth-based indentation mirroring the joins. Entity field
        // order is preserved within each group; groups are separated by a blank line. Computed
        // fields have no datasource and come last.
        var byDataSource = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var computed = new List<string>();
        // Entity field name → its output column alias (with RDD_ where applicable). Used by the
        // ROW_NUMBER wrapper to reference primary-key columns; fields on disabled datasources are
        // absent, which the wrapper flags as a missing key field.
        var outputAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in entity.Fields)
        {
            if (field.IsComputed)
            {
                computed.Add($"NULL AS RDD_{field.Name}"
                             + Comment($"computed / unmapped ({field.UnmappedType})"));
                outputAlias[field.Name] = $"RDD_{field.Name}";
                continue;
            }

            if (_disabled.Contains(field.DataSource)) continue;   // datasource switched off in the tree

            var ds = entity.FindDataSource(field.DataSource);
            var edit = _editability.Evaluate(entity, field, ds, staging);
            var dest = (edit.Rdd ? "RDD_" : "") + field.Name;
            var src = field.DataSource.Length > 0
                ? $"{field.DataSource}.{field.DataField}"
                : field.DataField;

            if (!byDataSource.TryGetValue(field.DataSource, out var bucket))
                byDataSource[field.DataSource] = bucket = new List<string>();
            bucket.Add($"{src} AS {dest}{BuildFieldComment(edit)}");
            outputAlias[field.Name] = dest;
        }

        // Assemble groups in datasource tree order, each with its indentation.
        var groups = new List<(int indentLevels, string? banner, List<string> lines)>();
        if (root != null)
            foreach (var ds in PreOrder(root))
            {
                if (_disabled.Contains(ds.Name)) continue;
                if (byDataSource.TryGetValue(ds.Name, out var lines) && lines.Count > 0)
                    groups.Add((Depth(ds) + 1, null, lines));
            }
        if (computed.Count > 0)
            groups.Add((1, "-- ---- computed / view-method fields (no physical source) ----", computed));

        var totalFields = groups.Sum(g => g.lines.Count);

        sb.AppendLine("SELECT");
        var emitted = 0;
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var (indentLevels, banner, lines) = groups[gi];
            if (gi > 0) sb.AppendLine();                       // blank line between groups
            if (banner != null) sb.AppendLine($"{Ind}{banner}");
            foreach (var line in lines)
            {
                emitted++;
                AppendField(sb, indentLevels, line, emitted < totalFields ? "," : "");
            }
        }

        // ---- FROM / JOINs ----------------------------------------------------------------------
        sb.AppendLine();
        if (root != null)
        {
            sb.AppendLine($"FROM {root.Table} AS {root.Name}"
                          + (root.IsReadOnly ? "  -- read-only" : ""));
            foreach (var child in root.Children)
                WriteJoin(sb, child, rootAlias, level: 1);
        }

        // ---- WHERE -----------------------------------------------------------------------------
        WriteWhere(sb, entity, root, rootAlias);

        var inner = sb.ToString().TrimEnd();
        return WrapWithRowNumber(header, inner, entity, outputAlias);
    }

    /// <summary>
    /// Wrap the generated query in an outer SELECT that adds a RDD_INDEX_DISTINCT ROW_NUMBER over
    /// the entity's primary-key fields. With no usable key it falls back to PARTITION BY 1 /
    /// ORDER BY (SELECT NULL) and flags "Missing primary key index".
    /// </summary>
    private static string WrapWithRowNumber(
        StringBuilder header, string inner, EntityInfo entity, Dictionary<string, string> outputAlias)
    {
        string partition, order, endComment = "";

        if (entity.PrimaryKeyFields.Count == 0)
        {
            partition = "1";
            order = "(SELECT NULL)";   // SQL Server rejects a constant (ORDER BY 1) inside OVER()
            endComment = " -- Missing primary key index";
        }
        else
        {
            var cols = new List<string>();
            var missing = false;
            foreach (var kf in entity.PrimaryKeyFields)
            {
                if (outputAlias.TryGetValue(kf, out var alias)) cols.Add(alias);
                else { cols.Add(kf); missing = true; }   // key field not emitted (datasource off / not found)
            }
            partition = string.Join(", ", cols);
            order = partition;
            if (missing) endComment = " -- Missing field in primary key index";
        }

        var sb = new StringBuilder();
        sb.Append(header);
        sb.AppendLine("SELECT");
        sb.AppendLine($"{Ind}ROW_NUMBER() OVER(");
        sb.AppendLine($"{Ind}{Ind}PARTITION BY {partition} -- Insert key fields here --");
        sb.AppendLine($"{Ind}{Ind}ORDER BY {order} -- Insert order fields here --");
        sb.AppendLine($"{Ind}) AS RDD_INDEX_DISTINCT,{endComment}");
        sb.AppendLine($"{Ind}*");
        sb.AppendLine("FROM (");
        foreach (var raw in inner.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            sb.AppendLine(line.Length == 0 ? "" : Ind + line);
        }
        sb.AppendLine(") AS RDD_SOURCE");

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    // -------------------------------------------------------------------------------------------

    private static void WriteHeader(StringBuilder sb, EntityInfo entity)
    {
        sb.AppendLine("-- ============================================================================");
        sb.AppendLine($"-- Entity  : {entity.Name}");
        sb.AppendLine($"-- Root DS : {entity.RootDataSource?.Table} (alias {entity.RootDataSource?.Name})");
        sb.AppendLine(entity.DataManagementEnabled
            ? $"-- Staging : {entity.StagingTable}"
            : "-- Staging : <none>  ***  ENTITY IS NOT DATA-MANAGEMENT ENABLED  ***");
        if (entity.IsReadOnly)
            sb.AppendLine("-- WARNING : entity is READ-ONLY — no field is importable (all prefixed RDD_).");
        sb.AppendLine("-- RDD_ prefix = not importable on create (read-only or AllowEditOnCreate=No).");
        sb.AppendLine("-- ============================================================================");
    }

    private static void AppendField(StringBuilder sb, int indentLevels, string line, string comma)
    {
        var indent = string.Concat(Enumerable.Repeat(Ind, indentLevels));
        // Keep the trailing comment after the comma: "expr AS alias,   -- note"
        var commentIdx = line.IndexOf("  --", StringComparison.Ordinal);
        if (commentIdx >= 0)
        {
            var code = line[..commentIdx];
            var comment = line[commentIdx..];
            sb.AppendLine($"{indent}{code}{comma}{comment}");
        }
        else
        {
            sb.AppendLine($"{indent}{line}{comma}");
        }
    }

    /// <summary>Datasource tree in pre-order (root first, then each child's subtree).</summary>
    private static IEnumerable<EntityDataSourceInfo> PreOrder(EntityDataSourceInfo ds)
    {
        yield return ds;
        foreach (var child in ds.Children)
            foreach (var d in PreOrder(child))
                yield return d;
    }

    /// <summary>Depth of a datasource in the tree (root = 0).</summary>
    private static int Depth(EntityDataSourceInfo ds)
    {
        int depth = 0;
        for (var p = ds.Parent; p != null; p = p.Parent) depth++;
        return depth;
    }

    private void WriteJoin(StringBuilder sb, EntityDataSourceInfo ds, string rootAlias, int level)
    {
        if (_disabled.Contains(ds.Name)) return;   // skip this datasource and its whole subtree

        var indent = string.Concat(Enumerable.Repeat(Ind, level));
        var onIndent = indent + Ind;

        sb.AppendLine(); // blank line between joins for readability
        var flags = new List<string>();
        if (ds.IsNestedEntity) flags.Add("nested entity");
        if (ds.IsReadOnly) flags.Add("read-only");
        var flagComment = flags.Count > 0 ? $"  -- {string.Join("; ", flags)}" : "";
        sb.AppendLine($"{indent}{JoinKeyword(ds.JoinMode)} {ds.Table} AS {ds.Name}{flagComment}");

        var res = _relations.Resolve(ds);
        var conditions = new List<string>(res.Conditions);

        // DataAreaId linkage for company-specific backing tables.
        if (ds.Table_ is { HasDataAreaId: true } && rootAlias.Length > 0)
            conditions.Add($"{ds.Name}.DataAreaId = {rootAlias}.DataAreaId");

        // Datasource ranges (EmploymentType = 'Employee', …) and date-effectivity filter.
        var (extra, extraNotes) = ExtraFilters(ds);
        conditions.AddRange(extra);
        var notes = res.Notes.Concat(extraNotes).ToList();

        if (conditions.Count == 0)
        {
            var why = notes.Count > 0
                ? $"  -- TODO: unresolved join ({string.Join("; ", notes)})"
                : "  -- TODO: no relation found";
            sb.AppendLine($"{onIndent}ON 1 = 1{why}");
        }
        else
        {
            for (int i = 0; i < conditions.Count; i++)
                sb.AppendLine($"{onIndent}{(i == 0 ? "ON " : "AND ")}{conditions[i]}");
            if (notes.Count > 0)
                sb.AppendLine($"{onIndent}-- note: {string.Join("; ", notes)}");
        }

        foreach (var child in ds.Children)
            WriteJoin(sb, child, rootAlias, level + 1);
    }

    private void WriteWhere(StringBuilder sb, EntityInfo entity, EntityDataSourceInfo? root, string rootAlias)
    {
        var conditions = new List<string>();
        if (root?.Table_ is { HasDataAreaId: true } && rootAlias.Length > 0)
            conditions.Add($"{rootAlias}.DataAreaId = {CompanyParam}");

        var notes = new List<string>();
        if (root != null)
        {
            var (extra, extraNotes) = ExtraFilters(root);   // root ranges + date-effectivity
            conditions.AddRange(extra);
            notes = extraNotes;
        }

        if (conditions.Count == 0 && notes.Count == 0)
            return;

        sb.AppendLine();
        if (conditions.Count > 0)
        {
            sb.AppendLine("WHERE");
            for (int i = 0; i < conditions.Count; i++)
                sb.AppendLine($"{Ind}{(i == 0 ? "" : "AND ")}{conditions[i]}");
        }
        foreach (var n in notes)
            sb.AppendLine($"{Ind}-- range (expression): {n}");
    }

    /// <summary>
    /// Extra ON/WHERE conditions for a datasource: its query Ranges (field = value) and, when
    /// ApplyDateFilter is set, a date-effectivity filter (@DateExecution BETWEEN ValidFrom/ValidTo).
    /// DataAreaId ranges are skipped (handled by the company logic); non-literal range values
    /// (AX expressions) are returned as notes rather than emitted as conditions.
    /// </summary>
    private (List<string> conditions, List<string> notes) ExtraFilters(EntityDataSourceInfo ds)
    {
        var conditions = new List<string>();
        var notes = new List<string>();

        foreach (var rg in ds.Ranges)
        {
            if (string.Equals(rg.Field, "DataAreaId", StringComparison.OrdinalIgnoreCase))
                continue;
            if (LooksLikeExpression(rg.Value))
                notes.Add($"{rg.Field} {rg.Value}");
            else
                conditions.Add($"{ds.Name}.{rg.Field} = {FormatRangeValue(rg.Value)}");
        }

        if (ds.ApplyDateFilter)
            conditions.Add($"{DateParam} BETWEEN {ds.Name}.ValidFrom AND {ds.Name}.ValidTo");

        return (conditions, notes);
    }

    private static bool LooksLikeExpression(string v) =>
        v.Length == 0 || v.Contains("..") || v.IndexOfAny(new[] { '(', ')', '=', '<', '>', ' ', '&', '|', ',' }) >= 0;

    private static string FormatRangeValue(string v)
    {
        // Numeric literals pass through; enum/string values are quoted (adjust enum→int for your source).
        bool numeric = v.Length > 0 && v.All(c => char.IsDigit(c) || c == '.' || c == '-');
        return numeric ? v : $"'{v.Replace("'", "''")}'";
    }

    private static string JoinKeyword(string joinMode) => joinMode switch
    {
        "InnerJoin" => "INNER JOIN",
        "OuterJoin" => "LEFT OUTER JOIN",
        "ExistsJoin" => "INNER JOIN /* ExistsJoin */",
        "NoExistsJoin" => "LEFT OUTER JOIN /* NotExistsJoin → filter IS NULL */",
        "" => "INNER JOIN",
        _ => $"INNER JOIN /* {joinMode} */",
    };

    private static string BuildFieldComment(EditabilityResult edit)
    {
        var parts = new List<string>();
        if (edit.Rdd)
            parts.Add($"({string.Join(", ", edit.Reasons)})");
        parts.AddRange(edit.Notes);
        return parts.Count > 0 ? Comment(string.Join("; ", parts)) : "";
    }

    private static string Comment(string text) => $"  -- {text}";
}
