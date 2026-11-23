using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Services;

namespace VPNRouter.Tests;

public sealed class AssistantWindowAutomationTests
{
    [AvaloniaFact]
    public void DiscoversAndUsesUnrelatedControlsWithoutSendingFieldValues()
    {
        var connect = new Button { Name = "Connect", Content = "Connect VPN" };
        var settings = new CheckBox { Name = "UseStrictDns", IsChecked = false };
        var app = new TextBox { Name = "CustomAppInput", Text = "previous.exe" };
        var password = new TextBox { Name = "Password", Text = "private", PasswordChar = '*' };
        var assistant = new StackPanel();
        var assistantButton = new Button { Name = "RunAssistant" };
        assistant.Children.Add(assistantButton);
        var external = new Button { Name = "OpenHelp" };
        foreach (var control in new Control[] { connect, settings, app, password, assistantButton })
            AutomationProperties.SetAutomationId(control, AssistantWindowAutomation.AutomationIdPrefix + control.Name);
        var window = new Window
        {
            Content = new StackPanel { Children = { connect, settings, app, password, assistant, external } },
        };
        int clicks = 0;
        connect.Click += (_, _) => clicks++;
        window.Show();
        try
        {
            var automation = new AssistantWindowAutomation(window, assistant);
            var observed = automation.Observe();
            Assert.DoesNotContain(observed, control => control.Name is "Password" or "RunAssistant" or "OpenHelp");
            Assert.DoesNotContain(observed, control => control.Name.Contains("previous.exe", StringComparison.Ordinal));
            var start = Assert.Single(observed, control => control.Name == "Connect");
            Assert.Equal("Connect", automation.Execute(start, null, null));
            Assert.Equal(1, clicks);
            Assert.Null(automation.Execute(start, null, null));

            var check = Assert.Single(automation.Observe(), control => control.Name == "UseStrictDns");
            Assert.Equal("UseStrictDns", automation.Execute(check, null, true));
            Assert.True(settings.IsChecked);
            var edit = Assert.Single(automation.Observe(), control => control.Name == "CustomAppInput");
            Assert.Equal("CustomAppInput", automation.Execute(edit, "arbitrary-test.exe", null));
            Assert.Equal("arbitrary-test.exe", app.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RejectsDisabledAndDetachedControlsAfterObservation()
    {
        var control = new Button { Name = "Connect" };
        AutomationProperties.SetAutomationId(control, "AssistantAction.Connect");
        var panel = new StackPanel { Children = { control } };
        var window = new Window { Content = panel };
        var automation = new AssistantWindowAutomation(window, new StackPanel());
        int clicks = 0;
        control.Click += (_, _) => clicks++;
        window.Show();
        try
        {
            var original = Assert.Single(automation.Observe(), item => item.Name == "Connect");
            control.IsEnabled = false;
            Assert.Null(automation.Execute(original, null, null));
            control.IsEnabled = true;
            var beforeRemoval = Assert.Single(automation.Observe(), item => item.Name == "Connect");
            panel.Children.Remove(control);
            Assert.Null(automation.Execute(beforeRemoval, null, null));
            Assert.Equal(0, clicks);
        }
        finally { window.Close(); }
    }
}
