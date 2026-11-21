using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TimeCapsuleDrive.Afp;

/// <summary>Finds AFP servers on the networks this computer is directly attached to.</summary>
public static class Discovery
{
    /// <summary>
    /// Every host in the local /24 networks that answers on the AFP port, with what it says about itself
    /// (null if it did not answer the question). Takes a second or two.
    /// </summary>
    public static List<(string Host, ServerInfo? Info)> FindServers()
    {
        var candidates = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;
                // Only the /24 around our own address: enough for home networks and quick to scan.
                for (int i = 1; i < 255; i++)
                    if (i != b[3]) candidates.Add($"{b[0]}.{b[1]}.{b[2]}.{i}");
            }
        }
        var open = new List<string>();
        Task.WaitAll(candidates.Select(async ip =>
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var cts = new CancellationTokenSource(700);
                await s.ConnectAsync(IPAddress.Parse(ip), 548, cts.Token);
                lock (open) open.Add(ip);
            }
            catch (Exception) { }
        }).ToArray());

        var res = new List<(string, ServerInfo?)>();
        foreach (var ip in open.OrderBy(x => x))
        {
            ServerInfo? info = null;
            try { info = AfpClient.Probe(ip, 548, 2000); } catch (Exception) { }
            res.Add((ip, info));
        }
        return res;
    }

    /// <summary>
    /// Looks for the server that used to be at another address: same name and same model. Returns its address,
    /// or null when there is no such server or more than one (then guessing would be wrong).
    /// </summary>
    public static string? FindByName(string name, string model)
    {
        if (name.Length == 0) return null;
        var same = FindServers().Where(s => s.Info != null
            && string.Equals(s.Info.Name, name, StringComparison.OrdinalIgnoreCase)
            && (model.Length == 0 || string.Equals(s.Info.Model, model, StringComparison.OrdinalIgnoreCase))).ToList();
        return same.Count == 1 ? same[0].Host : null;
    }
}
