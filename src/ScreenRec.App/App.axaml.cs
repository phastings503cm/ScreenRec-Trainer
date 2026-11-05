using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ScreenRec.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            if (desktop.Args is { } args && args.Contains("--self-test"))
                window.RunSelfTest(args.SkipWhile(a => a != "--self-test").Skip(1).FirstOrDefault());
        }
        base.OnFrameworkInitializationCompleted();
    }
}
