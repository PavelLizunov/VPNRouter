using System;
using Avalonia;
using Avalonia.Controls;

namespace VPNRouter.App.Controls;

public class DeferredPage : ContentControl
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<DeferredPage, bool>(nameof(IsActive));

    public static readonly StyledProperty<Type?> PageTypeProperty =
        AvaloniaProperty.Register<DeferredPage, Type?>(nameof(PageType));

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Type? PageType
    {
        get => GetValue(PageTypeProperty);
        set => SetValue(PageTypeProperty, value);
    }

    static DeferredPage()
    {
        IsActiveProperty.Changed.AddClassHandler<DeferredPage>((control, args) =>
        {
            if (args.NewValue is bool active)
                control.UpdateState(active);
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateState(IsActive);
    }

    private void UpdateState(bool active)
    {
        IsVisible = active;
        if (active && Content == null && PageType != null)
        {
            try
            {
                Content = Activator.CreateInstance(PageType);
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger?.Error(ex, "[DeferredPage] Failed to instantiate page {Type}", PageType);
            }
        }
    }
}
