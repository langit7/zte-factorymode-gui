using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ZteModemGui.Models;

public interface IFactoryModeClient
{
    Task<FactoryResult> RunAsync(FactoryOptions options, IProgress<string> log, CancellationToken token);
}

public sealed class FactoryModeClient : IFactoryModeClient
{
    public async Task<FactoryResult> RunAsync(FactoryOptions o, IProgress<string> log, CancellationToken token)
    {
        o.Validate();
        // Proof table generation can be CPU intensive; keep it off the UI thread.
        return await Task.Run(() => ExecuteAsync(o, log, token), token);
    }

    private static async Task<FactoryResult> ExecuteAsync(FactoryOptions o, IProgress<string> log, CancellationToken token)
    {
        byte[]? clientMac = o.Mac.Length > 0 ? ProofPayload.ParseMac(o.Mac) : null;
        foreach (string user in o.Users)
        foreach (string password in o.Passwords)
        {
            token.ThrowIfCancellationRequested();
            log.Report($"Trying factory account: {user}");
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { BaseAddress = new Uri($"http://{o.Host}:{o.HttpPort}"), Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux armv7l) AppleWebKit/537.36");
            http.DefaultRequestHeaders.Referrer = new Uri($"http://{o.Host}/login.html");
            async Task<(HttpStatusCode Status, byte[] Body)> Request(string path, byte[]? body = null)
            {
                using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
                if (body is not null) request.Content = new ByteArrayContent(body);
                using var response = await http.SendAsync(request, token);
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                if (o.Verbose) log.Report($"{request.Method} {path} → HTTP {(int)response.StatusCode}, {bytes.Length} bytes");
                return (response.StatusCode, bytes);
            }
            Task<(HttpStatusCode Status, byte[] Body)> Post(string command) => Request("/webFac", Encoding.UTF8.GetBytes(command));
            byte[] key;
            int authTime = 0;
            try
            {
                try { await Request("/"); } catch (HttpRequestException) { }
                var reset = await Post("SendSq.gch");
                if (reset.Status != HttpStatusCode.BadRequest && !(reset.Status == HttpStatusCode.OK && reset.Body.Length == 0))
                    throw new InvalidOperationException("Factory session reset failed.");
                log.Report("Session reset.");
                try
                {
                    var start = await Post("RequestFactoryMode.gch");
                    if ((int)start.Status is < 200 or >= 300) throw new InvalidOperationException("Factory mode request rejected.");
                }
                catch (HttpRequestException e) when (e.HttpRequestError is HttpRequestError.ResponseEnded or HttpRequestError.ConnectionError)
                { log.Report("Factory request closed the HTTP connection; continuing handshake."); }
                int rand = RandomNumberGenerator.GetInt32(60);
                var sq = await Post($"SendSq.gch?rand={rand}\r\n");
                RequireOk(sq.Status, "Handshake");
                var (method, index, bridge) = ParseHandshake(rand, sq.Body);
                key = KeyPools.All[method - 1].Skip(index).Take(24).Select(b => (byte)(b ^ 0xA5)).ToArray();
                log.Report($"Protocol method {method} detected.");
                if (method != 1)
                {
                    clientMac ??= DetectMac(o);
                    log.Report($"Client MAC: {string.Join(":", clientMac.Select(b => b.ToString("x2")))}");
                    int count = method == 2 ? 12 : o.Profile == "rerand34" ? 34 : 22;
                    var command = Encoding.ASCII.GetBytes($"SendInfo.gch?info={count}|")
                        .Concat(ProofPayload.Create(method, bridge, clientMac, o.Profile)).ToArray();
                    var info = await Request("/webFacEntry", Encrypt(key, command));
                    RequireOk(info.Status, "Client MAC proof");
                }
                authTime = RandomNumberGenerator.GetInt32(1000);
                string version = o.NewAuth ? $"time{authTime}&version61" : "version50";
                var auth = await Request("/webFacEntry", Encrypt(key, Encoding.UTF8.GetBytes($"CheckLoginAuth.gch?{version}&user={user}&pass={password}")));
                RequireOk(auth.Status, "Factory authentication");
                if (!Decrypt(key, auth.Body, completeBlocks: true).StartsWith("FactoryMode.gch", StringComparison.Ordinal))
                    throw new InvalidOperationException("Invalid factory authentication response.");
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or CryptographicException)
            {
                log.Report(e.Message);
                continue;
            }
            log.Report("Factory authentication accepted.");
            string commandText = o.Service == "serial" ? $"SerialSlience.gch?action={o.Action}"
                : o.Action == "close" ? "FactoryMode.gch?close"
                : $"FactoryMode.gch?{(o.NewAuth ? $"time{RandomNumberGenerator.GetInt32(authTime, 1000)}&" : "")}mode=2&user={(o.Persistence == "Temporary" ? "notused" : o.TelnetUser)}";
            var mode = await Request("/webFacEntry", Encrypt(key, Encoding.UTF8.GetBytes(commandText)));
            RequireOk(mode.Status, $"{o.Service} {o.Action}");
            if (o.Service == "serial" || o.Action == "close")
                return new FactoryResult($"{o.Service} {o.Action} completed.");

            string reply = Decrypt(key, mode.Body);
            var match = Regex.Match(reply, @"[?&]user=([^&]*)&pass=([^&]*)");
            var candidates = new List<(string User, string Password)>();
            if (o.Persistence != "Temporary") candidates.Add((o.TelnetUser, o.TelnetPassword));
            if (match.Success) candidates.Add((match.Groups[1].Value, match.Groups[2].Value));
            if (candidates.Count == 0) throw new InvalidOperationException("Factory reply contains no Telnet credentials.");
            TelnetSession? session = null;
            (string User, string Password) accepted = ("", "");
            foreach (var credentials in candidates)
            {
                try
                {
                    session = await TelnetSession.ConnectAsync(o.Host, o.TelnetPort, credentials.User, credentials.Password, 5, token);
                    accepted = credentials;
                    break;
                }
                catch (Exception e) when (e is SocketException or IOException or TimeoutException)
                { log.Report($"Telnet verification: {e.Message}"); }
            }
            if (session is null) throw new InvalidOperationException("Factory mode opened, but Telnet login could not be verified.");
            using (session)
            {
                log.Report("Telnet login verified.");
                if (o.Persistence == "Temporary") return new FactoryResult("Temporary Telnet verified.", accepted.User, accepted.Password);
                await session.SavePermanentAsync(o, token);
                log.Report("Permanent settings saved. Applying changes…");
                await session.ApplyAsync(o.Persistence.EndsWith("reboot", StringComparison.Ordinal), token);
            }
            bool reboot = o.Persistence.EndsWith("reboot", StringComparison.Ordinal);
            log.Report("Waiting for permanent Telnet login…");
            using var verify = await TelnetSession.ConnectAsync(o.Host, o.TelnetPort, o.TelnetUser, o.TelnetPassword, reboot ? 45 : 15, token, 2);
            var output = await verify.RunAsync("sendcmd 1 DB p TelnetCfg", token);
            if (output.Contains("access denied", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Permanent login accepted, but firmware denies shell commands.");
            return new FactoryResult("Permanent Telnet login verified.", o.TelnetUser, o.TelnetPassword);
        }
        throw new InvalidOperationException("No factory account succeeded. Check credentials, MAC and proof profile.");
    }

    public static (int Method, int Index, byte[] Bridge) ParseHandshake(int rand, byte[] body)
    {
        if (body.Length == 0) return (1, rand, []);
        string text = Encoding.Latin1.GetString(body);
        var old = Regex.Match(text, @"\Anewrand=(\d+)\z");
        var latest = Regex.Match(text, @"\Are_rand=(\d+)&(\d+)&(.{6})\z", RegexOptions.Singleline);
        if (!old.Success && !latest.Success) throw new InvalidOperationException("Unrecognized handshake response.");
        var match = old.Success ? old : latest;
        if (!int.TryParse(match.Groups[1].Value, out int value) || value >= 60 ||
            (latest.Success && (!int.TryParse(match.Groups[2].Value, out int proof) || proof >= 1 << 23)))
            throw new InvalidOperationException("Handshake values are out of range.");
        return (old.Success ? 2 : 3, (((0x1000193 * rand) & 63) ^ value) % 60,
            latest.Success ? body[^6..] : []);
    }

    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var padded = new byte[(plaintext.Length + 15) / 16 * 16];
        plaintext.CopyTo(padded, 0);
        return aes.EncryptEcb(padded, PaddingMode.None);
    }

    public static string Decrypt(byte[] key, byte[] ciphertext, bool completeBlocks = false)
    {
        int length = completeBlocks ? ciphertext.Length / 16 * 16 : ciphertext.Length;
        if (length < 16 || length % 16 != 0) throw new InvalidOperationException("Truncated encrypted device reply.");
        using var aes = Aes.Create();
        aes.Key = key;
        return Encoding.UTF8.GetString(aes.DecryptEcb(ciphertext.AsSpan(0, length), PaddingMode.None)).TrimEnd('\0');
    }

    private static void RequireOk(HttpStatusCode status, string step)
    {
        if (status != HttpStatusCode.OK)
            throw new InvalidOperationException($"{step} failed: HTTP {(int)status}{(status == HttpStatusCode.Unauthorized ? " (credentials or MAC rejected)" : "")}.");
    }

    private static byte[] DetectMac(FactoryOptions o)
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        NetworkInterface? selected;
        if (o.Interface.Length > 0)
            selected = interfaces.FirstOrDefault(i => i.Name == o.Interface || i.Id == o.Interface);
        else
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(o.Host, o.HttpPort);
            var source = ((IPEndPoint)socket.LocalEndPoint!).Address;
            selected = interfaces.FirstOrDefault(i => i.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(source)));
        }
        var mac = selected?.GetPhysicalAddress().GetAddressBytes();
        return mac?.Length == 6 ? mac : throw new ArgumentException("Cannot detect the client MAC. Enter the MAC visible to the modem or select an interface.");
    }
}
