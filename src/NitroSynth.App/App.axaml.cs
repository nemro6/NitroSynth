using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace NitroSynth.App;

public partial class App : Application
{
    public override void Initialize()
    {
        try
        {
            AvaloniaXamlLoader.Load(this);
        }
        catch (XamlLoadException ex) when (IsMissingPrecompiledXaml(ex, typeof(App)))
        {
            ApplyFallbackAppTheme();
        }
    }

    private void ApplyFallbackAppTheme()
    {
        Resources["MeterTrackBrush"] = new SolidColorBrush(Color.Parse("#333333"));
        Resources["MeterBorderBrush"] = new SolidColorBrush(Color.Parse("#404040"));
        Resources["MeterFillBrush"] = new SolidColorBrush(Color.Parse("#2FBF2F"));
        Resources["MeterPulseBrush"] = new SolidColorBrush(Color.Parse("#7CFF7C"));

        Styles.Clear();
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Avalonia.Controls.DataGrid/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static bool IsMissingPrecompiledXaml(XamlLoadException ex, Type targetType)
    {
        string message = ex.Message ?? string.Empty;
        return message.Contains("No precompiled XAML found", StringComparison.Ordinal)
               && message.Contains(targetType.FullName ?? targetType.Name, StringComparison.Ordinal);
    }
}
