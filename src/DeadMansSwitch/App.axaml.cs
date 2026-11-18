using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace DeadMansSwitch
{
    public class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            // The self-test and screenshots build their own window on the headless platform.
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                var window = new MainWindow(Settings.Load());
                desktop.MainWindow = window;
                SingleInstance.ListenForShow(() => Dispatcher.UIThread.Post(window.ShowWindow));
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
