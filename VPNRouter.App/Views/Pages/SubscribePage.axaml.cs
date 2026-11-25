using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VPNRouter.App.Views.Pages;

public partial class SubscribePage : UserControl
{
    public SubscribePage()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<SubscriptionServerPicker>("AdvancedPicker")!.LinkTransferred +=
            this.FindControl<SubscriptionLinkEditor>("AdvancedLink")!.SetDraft;
    }
}
