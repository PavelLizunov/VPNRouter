using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.Core.Models;

namespace VPNRouter.App.ViewModels;

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

    public CustomRule ToModel() => new()
    {
        Action = Action,
        Type = Type,
        Value = Value,
        Comment = Comment,
        Enabled = Enabled,
    };

    public string ActionDisplay => Action ?? "direct";

    public string ActionChipBg => Action?.ToLowerInvariant() switch
    {
        "proxy" => "WarningSolidBrush",
        "block" => "DangerSolidBrush",
        _       => "AccentSolidBrush",
    };

    public string TypeDisplay => Type ?? "domain_suffix";

    public string ValueDisplay => Value ?? string.Empty;

    public double RowOpacity => Enabled ? 1.0 : 0.5;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(ActionDisplay) or nameof(ActionChipBg)
            or nameof(TypeDisplay) or nameof(ValueDisplay) or nameof(RowOpacity))
            return;
        _onChanged?.Invoke(this);
    }

    [RelayCommand]
    private void Remove() => _onRemoveRequested?.Invoke(this);

    public override string ToString()
    {
        var commentSuffix = string.IsNullOrWhiteSpace(Comment) ? string.Empty : $" — {Comment}";
        var enabledSuffix = Enabled ? string.Empty : " (off)";
        return $"{Action} {Type} {Value}{commentSuffix}{enabledSuffix}";
    }
}
