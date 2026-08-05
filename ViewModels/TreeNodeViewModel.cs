using System.Collections.ObjectModel;
using System.Windows.Media;
using D365EntitySqlGenerator.Models;

namespace D365EntitySqlGenerator.ViewModels;

public sealed class TreeNodeViewModel : ObservableObject
{
    public static readonly Brush Normal = Frozen(Color.FromRgb(0xF1, 0xF1, 0xF1));
    public static readonly Brush Muted = Frozen(Color.FromRgb(0xB0, 0xB0, 0xB0));
    public static readonly Brush Red = Frozen(Color.FromRgb(0xE0, 0x52, 0x52));
    public static readonly Brush Accent = Frozen(Color.FromRgb(0x4E, 0xC9, 0xB0)); // teal, VS-ish

    public string Text { get; init; } = "";
    public Brush Foreground { get; init; } = Normal;
    public string? ToolTip { get; init; }

    /// <summary>Datasource node → the backing metadata (used for checkbox cascade). Null otherwise.</summary>
    public EntityDataSourceInfo? DataSource { get; init; }

    /// <summary>Field node → the datasource alias it is sourced from (used for dimming). Null otherwise.</summary>
    public string? FieldDataSource { get; init; }

    /// <summary>Invoked by the VM when the user toggles this node's checkbox (suppressed on programmatic sets).</summary>
    public Action<TreeNodeViewModel>? OnToggled { get; set; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    public TreeNodeViewModel Add(TreeNodeViewModel child)
    {
        Children.Add(child);
        return child;
    }

    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public bool ShowCheckbox { get; init; }

    private bool _suppress;
    private bool _isChecked = true;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value) && !_suppress)
                OnToggled?.Invoke(this);
        }
    }

    /// <summary>Set IsChecked without raising OnToggled (used while the VM rebuilds state).</summary>
    public void SetCheckedSilently(bool value)
    {
        _suppress = true;
        IsChecked = value;
        _suppress = false;
    }

    private bool _isDimmed;
    public bool IsDimmed
    {
        get => _isDimmed;
        set { if (Set(ref _isDimmed, value)) Raise(nameof(Opacity)); }
    }

    public double Opacity => IsDimmed ? 0.4 : 1.0;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
