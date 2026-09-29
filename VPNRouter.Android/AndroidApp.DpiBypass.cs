using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private int _dpiSelectedTab;
    private Avalonia.Controls.Button? _dpiTabStatus;
    private Avalonia.Controls.Button? _dpiTabStrategy;
    private Avalonia.Controls.Button? _dpiTabAdvanced;
    private Control? _dpiBodyStatus;
    private Control? _dpiBodyStrategy;
    private Control? _dpiBodyAdvanced;

    private Avalonia.Controls.ComboBox? _dpiStrategyComboBox;

    private Ellipse? _dpiFooterStatusDot;
    private TextBlock? _dpiFooterStatusText;
    private Avalonia.Controls.Button? _dpiFooterToggleBtn;

    private Ellipse? _dpiStatusBarDot;
    private TextBlock? _dpiStatusBarText;


    private void ReseedDpiBypassTabState()
    {
        var mode = AndroidStorage.GetDpiBypassMode();
        if (_dpiStrategyComboBox is not null)
        {
            _dpiStrategyComboBox.SelectedIndex = mode switch
            {
                "standard"   => 1,
                "aggressive" => 2,
                _            => 0,
            };
        }
        if (_dpiStatusBarText is not null)
            _dpiStatusBarText.Text = ZapretStatusLabelForCurrentMode();
        if (_dpiStatusBarDot is not null)
            _dpiStatusBarDot.Fill = GetBrush(mode != "off"
                ? "SuccessSolidBrush" : "TextMutedBrush");
        UpdateDpiFooterState();
    }



    private void UpdateDpiFooterState()
    {
        var mode = AndroidStorage.GetDpiBypassMode();
        var enabled = mode != "off";
        if (_dpiFooterStatusText is not null)
            _dpiFooterStatusText.Text = ZapretStatusLabelForCurrentMode();
        if (_dpiFooterStatusDot is not null)
            _dpiFooterStatusDot.Fill = GetBrush(enabled
                ? "SuccessSolidBrush" : "TextMutedBrush");
        if (_dpiFooterToggleBtn is not null)
            _dpiFooterToggleBtn.Content = enabled
                ? Localization.AndroidDpiBypassFooterToggleOff
                : Localization.AndroidDpiBypassFooterToggleOn;
    }

}
