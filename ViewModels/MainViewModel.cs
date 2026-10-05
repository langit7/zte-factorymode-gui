using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZteModemGui.Models;

namespace ZteModemGui.ViewModels;

public partial class MainViewModel(IFactoryModeClient client) : ObservableObject
{
    public string[] Services { get; } = ["telnet", "serial"];
    public string[] Actions { get; } = ["open", "close"];
    public string[] Profiles { get; } = ["rerand34", "rerand22"];
    public string[] PersistenceModes { get; } = ["Temporary", "Permanent · restart service", "Permanent · reboot"];
    public ObservableCollection<string> Logs { get; } = [];
    private static readonly string[] KnownUsers = ["admin", "factorymode", "CMCCAdmin", "CUAdmin", "telecomadmin", "cqadmin", "user", "cuadmin", "lnadmin", "useradmin", "root"];
    private static readonly string[] KnownPasswords = ["admin", "Telkomdso123", "nE%jA@5b", "aDm8H%MdA", "CUAdmin", "nE7jA%5m", "cqunicom", "1620@CTCC", "1620@CUcc", "admintelecom", "cuadmin", "lnadmin", "pONjA%5m"];

    [ObservableProperty] private string host = "192.168.1.1";
    [ObservableProperty] private string httpPort = "80";
    [ObservableProperty] private string telnetPort = "23";
    [ObservableProperty] private string factoryUser = "";
    [ObservableProperty] private string factoryPassword = "";
    [ObservableProperty] private bool useKnownCredentials;
    [ObservableProperty] private string mac = "";
    [ObservableProperty] private string interfaceName = "";
    [ObservableProperty] private string service = "telnet";
    [ObservableProperty] private string action = "open";
    [ObservableProperty] private string persistence = "Temporary";
    [ObservableProperty] private string profile = "rerand34";
    [ObservableProperty] private string telnetUser = "root";
    [ObservableProperty] private string telnetPassword = "Zte521";
    [ObservableProperty] private bool newAuth;
    [ObservableProperty] private bool setRegion198;
    [ObservableProperty] private bool verbose;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private string resultUser = "";
    [ObservableProperty] private string resultPassword = "";

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken token)
    {
        IsBusy = true;
        Error = ResultUser = ResultPassword = "";
        Logs.Clear();
        Status = "Working…";
        var progress = new Progress<string>(line =>
        {
            if (Logs.Count >= 2000) Logs.RemoveAt(0);
            Logs.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        });
        try
        {
            if (!int.TryParse(HttpPort, out int hp) || !int.TryParse(TelnetPort, out int tp))
                throw new ArgumentException("Enter numeric HTTP and Telnet ports.");
            if (!UseKnownCredentials && (string.IsNullOrWhiteSpace(FactoryUser) || FactoryPassword.Length == 0))
                throw new ArgumentException("Enter a factory username and password, or check Use known usernames/passwords.");
            var options = new FactoryOptions(Host.Trim(), hp, tp,
                UseKnownCredentials ? [.. KnownUsers] : [FactoryUser.Trim()],
                UseKnownCredentials ? [.. KnownPasswords] : [FactoryPassword], Mac.Trim(),
                InterfaceName.Trim(), Service, Action, Persistence, TelnetUser, TelnetPassword, Profile, NewAuth, SetRegion198, Verbose);
            options.Validate();
            var result = await client.RunAsync(options, progress, token);
            Status = result.Message;
            ResultUser = result.Username;
            ResultPassword = result.Password;
        }
        catch (OperationCanceledException)
        { Status = token.IsCancellationRequested ? "Cancelled. Device changes already applied remain in effect." : "Device request timed out."; }
        catch (Exception e)
        { Status = "Failed"; Error = e.Message; }
        finally { IsBusy = false; }
    }
}
