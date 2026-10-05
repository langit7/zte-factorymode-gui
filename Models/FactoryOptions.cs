using System.Net;

namespace ZteModemGui.Models;

public sealed record FactoryOptions(
    string Host, int HttpPort, int TelnetPort, string[] Users, string[] Passwords,
    string Mac, string Interface, string Service, string Action, string Persistence,
    string TelnetUser, string TelnetPassword, string Profile, bool NewAuth,
    bool SetRegion198, bool Verbose)
{
    public void Validate()
    {
        if (!IPAddress.TryParse(Host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Enter the modem's IPv4 address, for example 192.168.1.1.");
        if (HttpPort is < 1 or > 65535 || TelnetPort is < 1 or > 65535)
            throw new ArgumentException("Ports must be between 1 and 65535.");
        if (Users.Length == 0 || Passwords.Length == 0)
            throw new ArgumentException("Enter a factory username and password, or use the known credentials.");
        if (Users.Concat(Passwords).Any(s => s.IndexOfAny(['&', '\r', '\n', '\0']) >= 0))
            throw new ArgumentException("Factory credentials cannot contain &, line breaks or NUL.");
        if (Mac.Length > 0 && Interface.Length > 0)
            throw new ArgumentException("Choose either a client MAC or an interface; leave both blank for automatic detection.");
        if (Mac.Length > 0) ProofPayload.ParseMac(Mac);
        if (Service is not ("telnet" or "serial") || Action is not ("open" or "close") ||
            Persistence is not ("Temporary" or "Permanent · restart service" or "Permanent · reboot") ||
            Profile is not ("rerand34" or "rerand22"))
            throw new ArgumentException("Select a valid service, action, persistence and proof profile.");
        if (Persistence != "Temporary")
        {
            if (Service != "telnet" || Action != "open")
                throw new ArgumentException("Permanent mode applies only to Telnet open. Select Temporary for other actions.");
            if (new[] { TelnetUser, TelnetPassword }.Any(s =>
                    !System.Text.RegularExpressions.Regex.IsMatch(s, @"\A[A-Za-z0-9_@%+=.,:-]+\z")))
                throw new ArgumentException("Permanent credentials must use letters, digits or _ @ % + = . , : - (no spaces).");
        }
        if (SetRegion198 && Persistence == "Temporary")
            throw new ArgumentException("Setting region 198 requires a permanent Telnet mode.");
    }
}

public sealed record FactoryResult(string Message, string Username = "", string Password = "");
