using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.Core.Models;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// v2.30.0-r2 — single rule row VM for the structured rules list in
/// Network → Rules. Mirrors a <see cref="CustomRule"/> entry from
/// <see cref="AppSettings.App.CustomRules"/>; binding to ToggleSwitch /
/// Edit / Delete buttons in the row template.
///
/// <para>Two-way sync with the underlying List&lt;CustomRule&gt; happens
/// in MainWindowViewModel: the VM list is rebuilt on settings load,
/// and changes to row properties trigger a SaveSettings via the
/// PropertyChanged → owner.SyncCustomRules callback.</para>
/// </summary>
public partial class CustomRuleViewModel : ObservableObject
{
    private readonly Action<CustomRuleViewModel>? _onChanged;
    private readonly Action<CustomRuleViewModel>? _onRemoveRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionDisplay))]
    [NotifyPropertyChangedFor(nameof(ActionChipBg))]
    private string _action = "direct";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeDisplay))]
    private string _type = "domain_suffix";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueDisplay))]
    private string _value = string.Empty;

    [ObservableProperty]
    private string _comment = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private bool _enabled = true;

    public CustomRuleViewModel(
        CustomRule source,
        Action<CustomRuleViewModel>? onChanged = null,
        Action<CustomRuleViewModel>? onRemoveRequested = null)
    {
        _action = source.Action ?? "direct";
        _type = source.Type ?? "domain_suffix";
        _value = source.Value ?? string.Empty;
        _comment = source.Comment ?? string.Empty;
        _enabled = source.Enabled;
        _onChanged = onChanged;
        _onRemoveRequested = onRemoveRequested;
    }

    /// <summary>Convert back to model.</summary>
    public CustomRule ToModel() => new()
    {
        Action = Action,
        Type = Type,
        Value = Value,
        Comment = Comment,
        Enabled = Enabled,
    };

    /// <summary>Display the action as a UI chip label. Same as raw action
    /// for now (action vocab is already user-friendly).</summary>
    public string ActionDisplay => Action ?? "direct";

    /// <summary>Action chip background color. Maps to design tokens:
    /// direct=accent (blue), proxy=warning (orange), block=danger (red).
    /// XAML binds via DynamicResource lookup of the returned key.</summary>
    public string ActionChipBg => Action?.ToLowerInvariant() switch
    {
        "proxy" => "WarningSolidBrush",
        "block" => "DangerSolidBrush",
        _       => "AccentSolidBrush", // direct
    };

    /// <summary>Type with monospace formatting hint. Plain string for now;
    /// if we add icons per-type later, this becomes the icon key.</summary>
    public string TypeDisplay => Type ?? "domain_suffix";

    /// <summary>Value preview — passes through. Long values get
    /// TextTrimming via the XAML row template (CharacterEllipsis).</summary>
    public string ValueDisplay => Value ?? string.Empty;

    /// <summary>Row opacity — disabled rules render at 50% so they're
    /// visually distinct from enabled ones in the list.</summary>
    public double RowOpacity => Enabled ? 1.0 : 0.5;

    /// <summary>Fires when any field changes — owner re-syncs the
    /// underlying CustomRules list.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Skip computed properties (they don't represent user-edits).
        if (e.PropertyName is nameof(ActionDisplay) or nameof(ActionChipBg)
            or nameof(TypeDisplay) or nameof(ValueDisplay) or nameof(RowOpacity))
            return;
        _onChanged?.Invoke(this);
    }

    [RelayCommand]
    private void Remove() => _onRemoveRequested?.Invoke(this);

    /// <summary>v2.30.7-r2 — accessible name for UIA/screen readers (was
    /// leaking "VPNRouter.App.ViewModels.CustomRuleViewModel"). Compact
    /// form mirrors the row layout: action + type + value [+ comment].</summary>
    public override string ToString()
    {
        var commentSuffix = string.IsNullOrWhiteSpace(Comment) ? string.Empty : $" — {Comment}";
        var enabledSuffix = Enabled ? string.Empty : " (off)";
        return $"{Action} {Type} {Value}{commentSuffix}{enabledSuffix}";
    }
}
