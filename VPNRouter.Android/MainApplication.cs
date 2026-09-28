using System;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace VPNRouter.Android;

[Application]
public class MainApplication : AvaloniaAndroidApplication<AndroidApp>
{
    protected MainApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
