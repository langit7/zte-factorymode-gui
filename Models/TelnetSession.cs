using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace ZteModemGui.Models;

internal sealed class TelnetSession(TcpClient client) : IDisposable
{
    private readonly NetworkStream stream = client.GetStream();
    public void Dispose() => client.Dispose();

    public static async Task<TelnetSession> ConnectAsync(string host, int port, string user, string password,
        int attempts, CancellationToken token, int interval = 1)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var client = new TcpClient();
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(host, port, deadline.Token);
                var session = new TelnetSession(client);
                await session.WaitAsync(["ogin:", "sername:"], token);
                await session.SendAsync(user, token);
                await session.WaitAsync(["assword:"], token);
                await session.SendAsync(password, token);
                await session.WaitAsync(["#", "$"], token);
                return session;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { client.Dispose(); last = new TimeoutException("Telnet connect timed out."); }
            catch (Exception e) when (e is SocketException or IOException or TimeoutException)
            { client.Dispose(); last = e; }
            catch { client.Dispose(); throw; }
            if (attempt + 1 < attempts) await Task.Delay(TimeSpan.FromSeconds(interval), token);
        }
        throw new IOException("Telnet connection or login failed.", last);
    }

    private Task SendAsync(string command, CancellationToken token) =>
        stream.WriteAsync(Encoding.Latin1.GetBytes(command + "\r\n"), token).AsTask();

    // Parse the whole accumulated buffer, so IAC sequences split across TCP packets survive.
    private static string Filter(byte[] data)
    {
        var output = new List<byte>();
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != 255) { output.Add(data[i]); continue; }
            if (++i >= data.Length) break;
            if (data[i] == 255) output.Add(255);
            else if (data[i] == 250)
            {
                while (i + 1 < data.Length && !(data[i] == 255 && data[i + 1] == 240)) i++;
                i++;
            }
            else if (data[i] is >= 251 and <= 254) i++;
        }
        return Encoding.Latin1.GetString(output.ToArray());
    }

    private async Task<string> WaitAsync(string[] patterns, CancellationToken token, string? echo = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var raw = new MemoryStream();
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                string text = Filter(raw.ToArray());
                int start = echo is null ? 0 : text.IndexOf(echo, StringComparison.Ordinal);
                if (start >= 0)
                {
                    string tail = text[(start + (echo?.Length ?? 0))..];
                    if (patterns.Any(p => tail.Contains(p, StringComparison.Ordinal))) return tail.TrimStart('\r', '\n');
                }
                int read = await stream.ReadAsync(buffer, deadline.Token);
                if (read == 0) throw new IOException("The device closed the Telnet connection.");
                raw.Write(buffer, 0, read);
                if (raw.Length > 1024 * 1024) throw new IOException("Telnet response exceeded 1 MB.");
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Timed out waiting for the device's Telnet prompt."); }
    }

    public async Task<string> RunAsync(string command, CancellationToken token)
    {
        while (stream.DataAvailable)
        {
            var drain = new byte[1024];
            if (await stream.ReadAsync(drain, token) == 0) throw new IOException("Telnet connection closed.");
        }
        await SendAsync(command, token);
        return await WaitAsync(["#", "$"], token, command);
    }

    private static void CheckOutput(string output)
    {
        if (new[] { "access denied", "error", "failed", "invalid", "not found" }
            .Any(word => output.Contains(word, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The device rejected a configuration command; settings may be partially saved.");
    }

    public async Task SavePermanentAsync(FactoryOptions o, CancellationToken token)
    {
        async Task<string> Region() => Regex.Match(await RunAsync("cat /userconfig/flag_type", token),
            @"\bcurrent\s*:\s*(\d+)\b", RegexOptions.IgnoreCase).Groups[1].Value;
        string region = await Region();
        if (region.Length == 0) throw new IOException("Firmware region unavailable; permanent Telnet was not applied.");
        if (region != "198")
        {
            if (!o.SetRegion198) throw new IOException($"Permanent Telnet requires region 198 (found {region}). Select Set region 198 to change it.");
            CheckOutput(await RunAsync("upgradetest sfactoryconf 198", token));
            if (await Region() != "198") throw new IOException("The firmware region was not changed to 198.");
        }
        string[] settings = ["TS_Enable 1", "Lan_Enable 1", $"TS_UName {o.TelnetUser}", $"TS_UPwd {o.TelnetPassword}",
            $"TSLan_UName {o.TelnetUser}", $"TSLan_UPwd {o.TelnetPassword}", "Max_Con_Num 99", "ExitTime 999999",
            "CloseServerTime 9999999", "Lan_EnableAfterOlt 1", "InitSecLvl 3"];
        foreach (string setting in settings)
            CheckOutput(await RunAsync("sendcmd 1 DB set TelnetCfg 0 " + setting, token));
        CheckOutput(await RunAsync("sendcmd 1 DB save", token));
    }

    public async Task ApplyAsync(bool reboot, CancellationToken token)
    {
        string command = "reboot";
        if (!reboot)
        {
            string output = await RunAsync("sendcmd -pc show", token);
            int? pid = null;
            foreach (string line in output.Split('\n'))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (!fields.Contains("telnetd")) continue;
                string value = fields[0] == "telnetd" && fields.Length >= 3 ? fields[2] : fields[0];
                if (int.TryParse(value, out int parsed) && parsed > 0) { pid = parsed; break; }
            }
            if (pid is null) throw new IOException("Cannot find telnetd PID. Settings were saved; select Permanent · reboot to apply them.");
            command = $"sendcmd -pc kill {pid}";
        }
        await SendAsync(command, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var buffer = new byte[1024];
            while (await stream.ReadAsync(buffer, deadline.Token) != 0) { }
        }
        catch (IOException) { /* Connection reset also announces shutdown. */ }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Settings saved, but device did not disconnect after restart/reboot."); }
    }
}
