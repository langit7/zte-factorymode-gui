using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ZteModemGui.Models;
using ZteModemGui.ViewModels;
using ZteModemGui.Views;

namespace ZteModemGui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel(new FactoryModeClient());
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.Exit += (_, _) => vm.RunCommand.Cancel();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
