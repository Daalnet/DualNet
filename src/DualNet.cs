// DualNet: a local SOCKS5 + HTTP proxy that spreads outgoing connections
// across every network adapter that has its own internet gateway
// (e.g. LAN on modem A + Wi-Fi on modem B).
// Written for C# 5 so it builds with the csc.exe that ships with Windows.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DualNet
{
    class Link
    {
        public string Id;
        public string Name;
        public IPAddress Ip;
        public string Gateway;
        public bool Present = true;
        public bool Healthy;
        public bool Enabled = true;
        public double Weight = 1;
        public int LatencyMs = -1;
        public int Active;
        public long TotalConns;
        public long Failures;
        public int ConsecFail;
        public DateTime LastOk = DateTime.MinValue;
        public long BytesDown, BytesUp;
        public long LastDown, LastUp;
        public double RateDown, RateUp;
        public double[] Hist = new double[60];
        public int HistPos;

        // cost awareness
        public bool Preferred;          // the cheap line: used first in LAN-first mode
        public double SpeedBps;         // known line speed (bytes/s) from --speed, 0 = learn it
        public double PeakRate;         // highest download rate seen recently
        public long LimitBytes;         // data cap for this period, 0 = none
        public long UsedBase;           // usage from earlier runs in this period
        public long PeriodStartBytes;   // counter value when the current period began
        public DateTime LastCheck = DateTime.MinValue;
        public bool CapEnforced;
        public readonly HashSet<Socket> Socks = new HashSet<Socket>();

        public long UsedPeriod
        {
            get { return UsedBase + Interlocked.Read(ref BytesDown) + Interlocked.Read(ref BytesUp) - PeriodStartBytes; }
        }
        public bool OverLimit { get { return LimitBytes > 0 && UsedPeriod >= LimitBytes; } }
        public double Capacity { get { return SpeedBps > 0 ? SpeedBps : PeakRate; } }
        // "Full" = downloading at 80% or more of what the line can do.
        public bool Busy { get { return Capacity > 100000 && RateDown >= Capacity * 0.8; } }
    }

    class DialResult
    {
        public Socket Sock;
        public Link Link;
    }

    static class Program
    {
        static readonly object Gate = new object();
        static readonly List<Link> Links = new List<Link>();
        static readonly Dictionary<string, KeyValuePair<string, DateTime>> Sticky =
            new Dictionary<string, KeyValuePair<string, DateTime>>(StringComparer.OrdinalIgnoreCase);
        static int RoundRobin;

        // settings
        static int Port = 1080;
        static bool ListenLan;
        static bool StickyMode;
        static bool SystemProxy;
        static readonly List<string> Exclude = new List<string>();
        static readonly List<string> Only = new List<string>();
        static readonly Dictionary<string, double> Weights =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        static string Prefer;
        static bool ExplicitFast;
        static volatile bool CheapMode;
        static readonly Dictionary<string, double> Speeds =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, long> Limits =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        static string LimitPeriod = "month";
        static string UsageFile;
        static string CurrentPeriod;
        static readonly object UsageGate = new object();
        static readonly Dictionary<string, long> SavedUsage =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        static readonly IPEndPoint[] CheckTargets =
        {
            new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443),
            new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443),
            new IPEndPoint(IPAddress.Parse("4.2.2.4"), 53),
            new IPEndPoint(IPAddress.Parse("9.9.9.9"), 443),
        };

        static string DashboardHtml;

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                if (!ParseArgs(args)) return 1;
            }
            catch (Exception e)
            {
                Console.WriteLine("Bad arguments: " + e.Message);
                PrintHelp();
                return 1;
            }

            using (var rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("dashboard.html"))
            using (var sr = new StreamReader(rs, Encoding.UTF8))
                DashboardHtml = sr.ReadToEnd();

            using (var fs = Assembly.GetExecutingAssembly().GetManifestResourceStream("Vazirmatn.woff2"))
            {
                if (fs != null)
                {
                    var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    FontBytes = ms.ToArray();
                }
            }

            LoadSettings();
            if (Prefer != null && !ExplicitFast && !SettingsFast) CheapMode = true;
            LoadUsage();
            ctrlHandler = type => { OnExit(); return false; };
            SetConsoleCtrlHandler(ctrlHandler, true);
            AppDomain.CurrentDomain.ProcessExit += (s, e) => OnExit();

            try
            {
                return MainAsync().GetAwaiter().GetResult();
            }
            finally
            {
                OnExit();
            }
        }

        static byte[] FontBytes;
        static string SettingsFile;
        static bool SettingsFast;
        static readonly object SettingsGate = new object();

        // settings-<PC name>.txt next to the exe keeps the choices made in the dashboard:
        //   prefer=<adapter name>   mode=cheap|fast   speed=<adapter name>|<Mbps>
        // Command-line options win over the file.
        static void LoadSettings()
        {
            // One file per computer, so a folder shared through Dropbox works on several PCs.
            SettingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings-" + Environment.MachineName + ".txt");
            try
            {
                if (!File.Exists(SettingsFile)) return;
                foreach (var line in File.ReadAllLines(SettingsFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant(), val = line.Substring(eq + 1).Trim();
                    if (key == "prefer" && Prefer == null && val.Length > 0) Prefer = val;
                    else if (key == "mode" && val == "fast") SettingsFast = true;
                    else if (key == "speed")
                    {
                        int bar = val.LastIndexOf('|');
                        double mbps;
                        if (bar > 0 && double.TryParse(val.Substring(bar + 1), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out mbps) && mbps > 0)
                        {
                            string name = val.Substring(0, bar);
                            if (!Speeds.ContainsKey(name)) Speeds[name] = mbps * 125000;
                        }
                    }
                }
            }
            catch { }
        }

        static void SaveSettings()
        {
            lock (SettingsGate)
            {
                try
                {
                    var sb = new StringBuilder();
                    if (Prefer != null) sb.Append("prefer=").Append(Prefer).Append("\r\n");
                    sb.Append("mode=").Append(CheapMode ? "cheap" : "fast").Append("\r\n");
                    foreach (var kv in Speeds)
                        if (kv.Value > 0)
                            sb.Append("speed=").Append(kv.Key).Append('|')
                              .Append((kv.Value / 125000).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
                    File.WriteAllText(SettingsFile, sb.ToString());
                }
                catch { }
            }
        }

        static string Q(string target, string key)
        {
            int qi = target.IndexOf('?');
            if (qi < 0) return null;
            foreach (var part in target.Substring(qi + 1).Split('&'))
            {
                int eq = part.IndexOf('=');
                string k = eq < 0 ? part : part.Substring(0, eq);
                if (k == key) return Uri.UnescapeDataString(eq < 0 ? "" : part.Substring(eq + 1));
            }
            return null;
        }

        static void OnExit()
        {
            SaveUsage();
            RestoreProxy();
        }

        static long ParseSize(string s)
        {
            s = s.Trim().ToUpperInvariant();
            double mult = 1L << 30; // GB by default
            if (s.EndsWith("TB")) { mult = 1L << 40; s = s.Substring(0, s.Length - 2); }
            else if (s.EndsWith("GB")) { s = s.Substring(0, s.Length - 2); }
            else if (s.EndsWith("MB")) { mult = 1L << 20; s = s.Substring(0, s.Length - 2); }
            return (long)(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture) * mult);
        }

        static T ByName<T>(Dictionary<string, T> d, string name, T def)
        {
            foreach (var kv in d)
                if (name.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Value;
            return def;
        }

        static void PrintHelp()
        {
            Console.WriteLine(@"DualNet - use several internet connections at the same time

Usage: DualNet.exe [options]

  --port N            proxy port (default 1080)
  --system-proxy      set the Windows proxy to DualNet while it runs
                      (restored when DualNet closes)
  --clear-proxy       turn the Windows proxy off and exit
                      (use if DualNet was killed and the internet stopped working)
  --prefer NAME       LAN-first mode: use this adapter first; the others only
                      take new connections when it is full (80% of its speed)
                      or down
  --fast              start in fast mode (spread over all) even with --prefer
  --speed NAME=MBPS   line speed in megabits, e.g. --speed Ethernet=16
                      (otherwise DualNet learns it from the fastest rate seen)
  --limit NAME=SIZE   data cap, e.g. --limit Wi-Fi=10GB. Once reached, that
                      adapter is used only if every other one is down
  --limit-period P    month (default) or day
  --sticky            keep each website on the same connection for 10 minutes
                      (for sites that log you out when your IP changes)
  --weight NAME=N     give an adapter more connections, e.g. --weight Ethernet=2
  --exclude NAME      never use adapters whose name contains NAME
  --only NAME         use only adapters whose name contains NAME (repeatable)
  --lan               accept connections from other devices on your network
  --help              show this help

Dashboard: open http://127.0.0.1:1080/ in a browser.");
        }

        static bool ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                switch (a)
                {
                    case "--port": Port = int.Parse(args[++i]); break;
                    case "--system-proxy": SystemProxy = true; break;
                    case "--sticky": StickyMode = true; break;
                    case "--lan": ListenLan = true; break;
                    case "--exclude": Exclude.Add(args[++i]); break;
                    case "--only": Only.Add(args[++i]); break;
                    case "--weight":
                        {
                            string[] kv = args[++i].Split('=');
                            Weights[kv[0].Trim()] = double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
                            break;
                        }
                    case "--prefer": Prefer = args[++i]; break;
                    case "--fast": ExplicitFast = true; break;
                    case "--speed":
                        {
                            string[] kv = args[++i].Split('=');
                            Speeds[kv[0].Trim()] = double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture) * 125000;
                            break;
                        }
                    case "--limit":
                        {
                            string[] kv = args[++i].Split('=');
                            Limits[kv[0].Trim()] = ParseSize(kv[1]);
                            break;
                        }
                    case "--limit-period":
                        LimitPeriod = args[++i].ToLowerInvariant() == "day" ? "day" : "month";
                        break;
                    case "--clear-proxy":
                        ClearProxy();
                        Console.WriteLine("Windows proxy turned off.");
                        return false;
                    case "--help": case "-h": case "/?":
                        PrintHelp();
                        return false;
                    default:
                        throw new ArgumentException("unknown option " + args[i]);
                }
            }
            return true;
        }

        static async Task<int> MainAsync()
        {
            TcpListener listener;
            try
            {
                listener = new TcpListener(ListenLan ? IPAddress.Any : IPAddress.Loopback, Port);
                listener.Start(1024);
            }
            catch (SocketException)
            {
                Console.WriteLine("Port " + Port + " is already in use. Is DualNet already running? Try --port 1081");
                return 1;
            }

            ScanAdapters(true);
            await Task.WhenAll(SnapshotPresent().Select(CheckLink));

            Console.WriteLine();
            Console.WriteLine("DualNet is running.");
            Console.WriteLine("  Proxy (SOCKS5 and HTTP): 127.0.0.1:" + Port);
            Console.WriteLine("  Dashboard:               http://127.0.0.1:" + Port + "/");
            if (CheapMode) Console.WriteLine("  Mode: priority (" + Prefer + " first, others only when it is full or down)");
            else Console.WriteLine("  Mode: fast (spread over all connections)");
            if (Prefer == null) Console.WriteLine("  Tip: choose a priority connection in the dashboard.");
            if (StickyMode) Console.WriteLine("  Sticky: same site -> same connection for 10 minutes");
            PrintLinks();

            if (SystemProxy)
            {
                SetProxy();
                Console.WriteLine("  Windows proxy is ON. It will be turned off when you close this window.");
            }
            Console.WriteLine("Press Ctrl+C to stop.");

            var health = HealthLoop();
            var rates = RateLoop();

            while (true)
            {
                Socket c = await listener.AcceptSocketAsync();
                var ignored = HandleClient(c);
            }
        }

        // ---------------- adapters ----------------

        static bool NameAllowed(string name)
        {
            foreach (var e in Exclude)
                if (name.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (Only.Count == 0) return true;
            foreach (var o in Only)
                if (name.IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static void ScanAdapters(bool quiet)
        {
            var found = new Dictionary<string, Link>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (!NameAllowed(ni.Name)) continue;
                IPInterfaceProperties p;
                try { p = ni.GetIPProperties(); } catch { continue; }

                IPAddress gw = p.GatewayAddresses.Select(g => g.Address)
                    .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !x.Equals(IPAddress.Any));
                if (gw == null) continue;
                IPAddress ip = p.UnicastAddresses.Select(u => u.Address)
                    .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !x.ToString().StartsWith("169.254."));
                if (ip == null) continue;

                found[ni.Id] = new Link { Id = ni.Id, Name = ni.Name, Ip = ip, Gateway = gw.ToString() };
            }

            bool changed = false;
            lock (Gate)
            {
                foreach (var f in found.Values)
                {
                    Link l = Links.FirstOrDefault(x => x.Id == f.Id);
                    if (l == null)
                    {
                        f.Weight = ByName(Weights, f.Name, 1.0);
                        f.Preferred = Prefer != null && f.Name.IndexOf(Prefer, StringComparison.OrdinalIgnoreCase) >= 0;
                        f.SpeedBps = ByName(Speeds, f.Name, 0.0);
                        f.LimitBytes = ByName(Limits, f.Name, 0L);
                        lock (UsageGate)
                        {
                            long used;
                            if (SavedUsage.TryGetValue(f.Name, out used)) f.UsedBase = used;
                        }
                        Links.Add(f);
                        changed = true;
                    }
                    else
                    {
                        if (!l.Present || !l.Ip.Equals(f.Ip)) changed = true;
                        l.Present = true;
                        l.Ip = f.Ip;
                        l.Gateway = f.Gateway;
                        l.Name = f.Name;
                    }
                }
                foreach (var l in Links)
                {
                    if (!found.ContainsKey(l.Id) && l.Present)
                    {
                        l.Present = false;
                        l.Healthy = false;
                        l.LatencyMs = -1;
                        changed = true;
                    }
                }
            }
            if (changed && !quiet) PrintLinks();
        }

        static Link[] SnapshotPresent()
        {
            lock (Gate) return Links.Where(l => l.Present).ToArray();
        }

        static void PrintLinks()
        {
            Link[] all;
            lock (Gate) all = Links.ToArray();
            Console.WriteLine("  Connections found:");
            if (all.Length == 0)
                Console.WriteLine("    (none - connect the LAN cable and Wi-Fi first)");
            foreach (var l in all)
            {
                string state = !l.Present ? "disconnected" : l.Healthy ? "online" : "no internet";
                string tags = (l.Preferred ? " [first]" : "") +
                              (l.LimitBytes > 0 ? " [cap " + (l.LimitBytes / (1L << 20)) + " MB/" + LimitPeriod + "]" : "");
                Console.WriteLine(string.Format("    {0,-22} {1,-16} via {2,-16} {3}{4}", l.Name, l.Ip, l.Gateway, state, tags));
            }
            int up = all.Count(l => l.Present);
            if (up == 1)
                Console.WriteLine("  Only one connection is up, so there is nothing to combine yet.");
        }

        static async Task HealthLoop()
        {
            while (true)
            {
                await Task.Delay(5000);
                try
                {
                    ScanAdapters(false);
                    // In LAN-first mode, check the backup line only every 30 s so an
                    // idle metered line (5G) spends almost no data on health checks.
                    var due = SnapshotPresent().Where(l => Prefer == null || l.Preferred ||
                        (DateTime.UtcNow - l.LastCheck).TotalSeconds >= 30);
                    await Task.WhenAll(due.Select(CheckLink));
                }
                catch { }
            }
        }

        static async Task CheckLink(Link l)
        {
            bool was = l.Healthy;
            l.LastCheck = DateTime.UtcNow;
            foreach (var t in CheckTargets)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    Socket s = await ConnectVia(l, new[] { t.Address }, t.Port, 3000);
                    sw.Stop();
                    s.Close();
                    l.LatencyMs = (int)sw.ElapsedMilliseconds;
                    l.Healthy = true;
                    l.ConsecFail = 0;
                    if (!was) Console.WriteLine("  [" + l.Name + "] online (" + l.LatencyMs + " ms)");
                    return;
                }
                catch { }
            }
            // Health targets may be filtered; trust recent real traffic for a while.
            if ((DateTime.UtcNow - l.LastOk).TotalSeconds > 30)
            {
                l.Healthy = false;
                l.LatencyMs = -1;
                if (was) Console.WriteLine("  [" + l.Name + "] lost internet");
            }
        }

        static async Task RateLoop()
        {
            int tick = 0;
            while (true)
            {
                await Task.Delay(1000);
                if (++tick % 30 == 0) SaveUsage();
                Link[] all;
                lock (Gate) all = Links.ToArray();
                foreach (var l in all)
                {
                    long d = Interlocked.Read(ref l.BytesDown), u = Interlocked.Read(ref l.BytesUp);
                    l.RateDown = l.RateDown * 0.3 + (d - l.LastDown) * 0.7;
                    l.RateUp = l.RateUp * 0.3 + (u - l.LastUp) * 0.7;
                    l.LastDown = d;
                    l.LastUp = u;
                    // learned line speed: jumps up to new highs, fades slowly (~half in 25 min)
                    l.PeakRate = l.RateDown > l.PeakRate ? l.RateDown : l.PeakRate * 0.99954;
                    l.Hist[l.HistPos] = l.RateDown;
                    l.HistPos = (l.HistPos + 1) % l.Hist.Length;
                }
                EnforceCaps(all);
            }
        }

        // ---------------- choosing a connection ----------------

        static Link Pick(string host, HashSet<Link> tried)
        {
            lock (Gate)
            {
                var usable = Links.Where(l => l.Present && l.Enabled && !tried.Contains(l)).ToList();
                var healthy = usable.Where(l => l.Healthy).ToList();
                var pool = healthy.Count > 0 ? healthy : usable;
                // Lines over their data cap are a last resort only.
                var underCap = pool.Where(l => !l.OverLimit).ToList();
                if (underCap.Count > 0) pool = underCap;
                if (pool.Count == 0) return null;

                if (CheapMode)
                {
                    // LAN-first: the cheap line takes everything until it is full;
                    // only then do new connections spill over to the other lines.
                    var first = pool.Where(l => l.Preferred && !l.Busy).ToList();
                    var others = pool.Where(l => !l.Preferred).ToList();
                    if (first.Count > 0) pool = first;
                    else if (others.Count > 0) pool = others;
                }

                if (StickyMode && host != null)
                {
                    KeyValuePair<string, DateTime> s;
                    if (Sticky.TryGetValue(host, out s) && s.Value > DateTime.UtcNow)
                    {
                        Link sl = pool.FirstOrDefault(l => l.Id == s.Key);
                        if (sl != null) return sl;
                    }
                }

                // Weighted least-connections, round-robin on ties.
                int start = RoundRobin++ & 0x7fffffff;
                Link best = null;
                double bestScore = double.MaxValue;
                for (int i = 0; i < pool.Count; i++)
                {
                    Link l = pool[(start + i) % pool.Count];
                    double score = (l.Active + 1) / Math.Max(l.Weight, 0.01);
                    if (score < bestScore) { bestScore = score; best = l; }
                }

                if (StickyMode && host != null)
                {
                    if (Sticky.Count > 5000) Sticky.Clear();
                    Sticky[host] = new KeyValuePair<string, DateTime>(best.Id, DateTime.UtcNow.AddMinutes(10));
                }
                return best;
            }
        }

        static async Task<Socket> ConnectVia(Link l, IPAddress[] addrs, int port, int timeoutMs)
        {
            Exception last = new SocketException((int)SocketError.HostUnreachable);
            foreach (var a in addrs)
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    s.NoDelay = true;
                    s.Bind(new IPEndPoint(l.Ip, 0)); // this is what forces the adapter
                    var ep = new IPEndPoint(a, port);
                    Task t = Task.Factory.FromAsync((cb, st) => s.BeginConnect(ep, cb, st), s.EndConnect, null);
                    if (await Task.WhenAny(t, Task.Delay(timeoutMs)) != t)
                    {
                        var ignore = t.ContinueWith(x => { var e = x.Exception; });
                        throw new TimeoutException();
                    }
                    await t;
                    return s;
                }
                catch (Exception e)
                {
                    last = e;
                    try { s.Close(); } catch { }
                }
            }
            throw last;
        }

        static async Task<DialResult> Dial(string host, int port)
        {
            IPAddress[] addrs;
            IPAddress literal;
            if (IPAddress.TryParse(host, out literal))
                addrs = new[] { literal };
            else
                addrs = await Dns.GetHostAddressesAsync(host);
            addrs = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(4).ToArray();
            if (addrs.Length == 0) return null;

            var tried = new HashSet<Link>();
            while (true)
            {
                Link l = Pick(host, tried);
                if (l == null) return null;
                tried.Add(l);
                Interlocked.Increment(ref l.Active);
                try
                {
                    Socket s = await ConnectVia(l, addrs, port, 8000);
                    Interlocked.Increment(ref l.TotalConns);
                    l.ConsecFail = 0;
                    l.LastOk = DateTime.UtcNow;
                    return new DialResult { Sock = s, Link = l };
                }
                catch
                {
                    Interlocked.Decrement(ref l.Active);
                    Interlocked.Increment(ref l.Failures);
                    if (++l.ConsecFail >= 3) l.Healthy = false;
                    // fall through and try the next connection
                }
            }
        }

        // ---------------- client handling ----------------

        static async Task ReadExact(Stream s, byte[] b, int off, int len)
        {
            while (len > 0)
            {
                int n = await s.ReadAsync(b, off, len);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
                len -= n;
            }
        }

        static async Task HandleClient(Socket c)
        {
            c.NoDelay = true;
            var cs = new NetworkStream(c, true);
            try
            {
                var first = new byte[1];
                await ReadExact(cs, first, 0, 1);
                if (first[0] == 5) await HandleSocks(cs, c);
                else await HandleHttp(cs, c, first[0]);
            }
            catch { }
            finally
            {
                try { cs.Dispose(); } catch { }
            }
        }

        static async Task HandleSocks(NetworkStream cs, Socket c)
        {
            var b = new byte[262];
            await ReadExact(cs, b, 0, 1);
            int nmethods = b[0];
            await ReadExact(cs, b, 0, nmethods);
            await cs.WriteAsync(new byte[] { 5, 0 }, 0, 2); // no auth

            await ReadExact(cs, b, 0, 4);
            byte cmd = b[1], atyp = b[3];
            string host;
            if (atyp == 1)
            {
                await ReadExact(cs, b, 0, 4);
                host = new IPAddress(new[] { b[0], b[1], b[2], b[3] }).ToString();
            }
            else if (atyp == 3)
            {
                await ReadExact(cs, b, 0, 1);
                int len = b[0];
                await ReadExact(cs, b, 0, len);
                host = Encoding.ASCII.GetString(b, 0, len);
            }
            else if (atyp == 4)
            {
                await ReadExact(cs, b, 0, 16);
                var v6 = new byte[16];
                Array.Copy(b, v6, 16);
                host = new IPAddress(v6).ToString();
            }
            else return;
            await ReadExact(cs, b, 0, 2);
            int port = (b[0] << 8) | b[1];

            if (cmd != 1) // only CONNECT
            {
                await cs.WriteAsync(new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
                return;
            }

            DialResult r = null;
            try { r = await Dial(host, port); } catch { }
            if (r == null)
            {
                await cs.WriteAsync(new byte[] { 5, 4, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
                return;
            }

            var lep = (IPEndPoint)r.Sock.LocalEndPoint;
            var reply = new byte[10];
            reply[0] = 5; reply[1] = 0; reply[2] = 0; reply[3] = 1;
            Array.Copy(lep.Address.GetAddressBytes(), 0, reply, 4, 4);
            reply[8] = (byte)(lep.Port >> 8); reply[9] = (byte)lep.Port;
            await cs.WriteAsync(reply, 0, 10);

            await Relay(cs, c, r, null, 0, 0);
        }

        static async Task HandleHttp(NetworkStream cs, Socket c, byte first)
        {
            // read the request header
            var buf = new byte[65536];
            buf[0] = first;
            int len = 1, headerEnd = -1;
            while (headerEnd < 0)
            {
                headerEnd = FindHeaderEnd(buf, len);
                if (headerEnd >= 0) break;
                if (len >= buf.Length) return;
                int n = await cs.ReadAsync(buf, len, buf.Length - len);
                if (n <= 0) return;
                len += n;
            }

            string header = Encoding.ASCII.GetString(buf, 0, headerEnd);
            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] parts = lines[0].Split(' ');
            if (parts.Length < 3) return;
            string method = parts[0], target = parts[1], version = parts[2];
            int extraOff = headerEnd + 4, extraLen = len - extraOff;

            if (method == "CONNECT")
            {
                string host; int port;
                if (!SplitHostPort(target, 443, out host, out port)) { await SendText(cs, 400, "text/plain", "bad target"); return; }
                DialResult r = null;
                try { r = await Dial(host, port); } catch { }
                if (r == null) { await SendText(cs, 502, "text/plain", "DualNet: could not reach " + target); return; }
                byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                await cs.WriteAsync(ok, 0, ok.Length);
                await Relay(cs, c, r, buf, extraOff, extraLen);
                return;
            }

            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                Uri u;
                if (!Uri.TryCreate(target, UriKind.Absolute, out u)) { await SendText(cs, 400, "text/plain", "bad url"); return; }
                DialResult r = null;
                try { r = await Dial(u.Host, u.Port); } catch { }
                if (r == null) { await SendText(cs, 502, "text/plain", "DualNet: could not reach " + u.Host); return; }

                var sb = new StringBuilder();
                sb.Append(method).Append(' ').Append(u.PathAndQuery).Append(' ').Append(version).Append("\r\n");
                for (int i = 1; i < lines.Length; i++)
                {
                    string h = lines[i];
                    if (h.Length == 0) continue;
                    string name = h.Split(':')[0].Trim().ToLowerInvariant();
                    if (name == "proxy-connection" || name == "proxy-authorization" ||
                        name == "connection" || name == "keep-alive") continue;
                    sb.Append(h).Append("\r\n");
                }
                sb.Append("Connection: close\r\n\r\n");
                byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
                var combined = new byte[head.Length + extraLen];
                Array.Copy(head, combined, head.Length);
                Array.Copy(buf, extraOff, combined, head.Length, extraLen);
                await Relay(cs, c, r, combined, 0, combined.Length);
                return;
            }

            // Not a proxy request: it is someone opening the dashboard.
            await ServeDashboard(cs, method, target, lines);
        }

        static int FindHeaderEnd(byte[] b, int len)
        {
            for (int i = 3; i < len; i++)
                if (b[i - 3] == 13 && b[i - 2] == 10 && b[i - 1] == 13 && b[i] == 10) return i - 3;
            return -1;
        }

        static bool SplitHostPort(string s, int defPort, out string host, out int port)
        {
            host = s; port = defPort;
            if (s.StartsWith("["))
            {
                int end = s.IndexOf(']');
                if (end < 0) return false;
                host = s.Substring(1, end - 1);
                if (end + 1 < s.Length && s[end + 1] == ':') return int.TryParse(s.Substring(end + 2), out port);
                return true;
            }
            int c = s.LastIndexOf(':');
            if (c > 0)
            {
                host = s.Substring(0, c);
                return int.TryParse(s.Substring(c + 1), out port);
            }
            return host.Length > 0;
        }

        static async Task Relay(NetworkStream client, Socket clientSock, DialResult r, byte[] pre, int preOff, int preLen)
        {
            Link l = r.Link;
            var remote = new NetworkStream(r.Sock, true);
            lock (l.Socks) l.Socks.Add(r.Sock);
            try
            {
                if (preLen > 0)
                {
                    await remote.WriteAsync(pre, preOff, preLen);
                    Interlocked.Add(ref l.BytesUp, preLen);
                }
                Task up = Pump(client, remote, r.Sock, l, false);
                Task down = Pump(remote, client, clientSock, l, true);
                await Task.WhenAny(up, down);
                // Give the other direction a moment to drain, then close both.
                await Task.WhenAny(Task.WhenAll(up, down), Task.Delay(30000));
            }
            finally
            {
                lock (l.Socks) l.Socks.Remove(r.Sock);
                Interlocked.Decrement(ref l.Active);
                try { remote.Dispose(); } catch { }
            }
        }

        // A line over its data cap gets its open connections closed, as long as
        // another line can take over. Downloaders reconnect, and the new
        // connections avoid the capped line.
        static void EnforceCaps(Link[] all)
        {
            foreach (var l in all)
            {
                if (!l.OverLimit) { l.CapEnforced = false; continue; }
                bool alternative = all.Any(o => o != l && o.Present && o.Enabled && o.Healthy && !o.OverLimit);
                if (!alternative) continue;
                Socket[] open;
                lock (l.Socks) open = l.Socks.ToArray();
                if (open.Length == 0) continue;
                if (!l.CapEnforced)
                    Console.WriteLine("  [" + l.Name + "] reached its data cap; moving its " + open.Length + " connections elsewhere");
                l.CapEnforced = true;
                foreach (var s in open) { try { s.Close(); } catch { } }
            }
        }

        static async Task Pump(Stream from, Stream to, Socket toSock, Link l, bool down)
        {
            var buf = new byte[65536];
            try
            {
                while (true)
                {
                    int n = await from.ReadAsync(buf, 0, buf.Length);
                    if (n <= 0) break;
                    await to.WriteAsync(buf, 0, n);
                    if (down) Interlocked.Add(ref l.BytesDown, n);
                    else Interlocked.Add(ref l.BytesUp, n);
                }
                if (toSock != null) { try { toSock.Shutdown(SocketShutdown.Send); } catch { } }
            }
            catch
            {
                // one side broke: close both so the other pump ends too
                try { from.Dispose(); } catch { }
                try { to.Dispose(); } catch { }
            }
        }

        // ---------------- dashboard ----------------

        static async Task ServeDashboard(NetworkStream cs, string method, string target, string[] lines)
        {
            string path = target.Split('?')[0];
            if (path == "/api/stats")
            {
                await SendText(cs, 200, "application/json", StatsJson());
                return;
            }
            if (path == "/api/toggle" && method == "POST")
            {
                // A custom header blocks other websites from calling this (CORS preflight).
                bool allowed = lines.Any(h => h.StartsWith("X-DualNet:", StringComparison.OrdinalIgnoreCase));
                string id = Uri.UnescapeDataString(target.Contains("id=") ? target.Substring(target.IndexOf("id=") + 3) : "");
                if (allowed)
                {
                    lock (Gate)
                    {
                        Link l = Links.FirstOrDefault(x => x.Id == id);
                        if (l != null) l.Enabled = !l.Enabled;
                    }
                }
                await SendText(cs, allowed ? 200 : 403, "application/json", StatsJson());
                return;
            }
            if (path == "/api/mode" && method == "POST")
            {
                bool allowed = lines.Any(h => h.StartsWith("X-DualNet:", StringComparison.OrdinalIgnoreCase));
                if (allowed && Prefer != null)
                {
                    CheapMode = Q(target, "cheap") == "1";
                    Console.WriteLine(CheapMode ? "  Mode changed: priority line first" : "  Mode changed: fast");
                    SaveSettings();
                }
                await SendText(cs, allowed ? 200 : 403, "application/json", StatsJson());
                return;
            }
            if (path == "/api/prefer" && method == "POST")
            {
                // Pick which connection is used first (empty id = no priority).
                bool allowed = lines.Any(h => h.StartsWith("X-DualNet:", StringComparison.OrdinalIgnoreCase));
                if (allowed)
                {
                    string id = Q(target, "id") ?? "";
                    lock (Gate)
                    {
                        Link pick = Links.FirstOrDefault(x => x.Id == id);
                        Prefer = pick == null ? null : pick.Name;
                        foreach (var l in Links) l.Preferred = pick != null && l == pick;
                        CheapMode = pick != null;
                    }
                    Console.WriteLine(Prefer == null ? "  Priority removed: fast mode" : "  Priority: " + Prefer + " first");
                    SaveSettings();
                }
                await SendText(cs, allowed ? 200 : 403, "application/json", StatsJson());
                return;
            }
            if (path == "/api/speed" && method == "POST")
            {
                // Set a connection's line speed in Mbps (0 or empty = learn it automatically).
                bool allowed = lines.Any(h => h.StartsWith("X-DualNet:", StringComparison.OrdinalIgnoreCase));
                if (allowed)
                {
                    string id = Q(target, "id") ?? "";
                    double mbps;
                    if (!double.TryParse(Q(target, "mbps") ?? "", System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out mbps) || mbps < 0) mbps = 0;
                    lock (Gate)
                    {
                        Link l = Links.FirstOrDefault(x => x.Id == id);
                        if (l != null)
                        {
                            l.SpeedBps = mbps * 125000;
                            Speeds[l.Name] = l.SpeedBps;
                        }
                    }
                    SaveSettings();
                }
                await SendText(cs, allowed ? 200 : 403, "application/json", StatsJson());
                return;
            }
            if (path == "/fonts/Vazirmatn.woff2" && FontBytes != null)
            {
                await SendBytes(cs, 200, "font/woff2", FontBytes, "public, max-age=604800");
                return;
            }
            if (path == "/" || path == "/index.html")
            {
                await SendText(cs, 200, "text/html; charset=utf-8", DashboardHtml);
                return;
            }
            await SendText(cs, 404, "text/plain", "not found");
        }

        static Task SendText(Stream s, int code, string type, string body)
        {
            return SendBytes(s, code, type, Encoding.UTF8.GetBytes(body), "no-store");
        }

        static async Task SendBytes(Stream s, int code, string type, byte[] b, string cache)
        {
            string reason = code == 200 ? "OK" : code == 404 ? "Not Found" : code == 403 ? "Forbidden" : code == 400 ? "Bad Request" : "Bad Gateway";
            string head = "HTTP/1.1 " + code + " " + reason + "\r\nContent-Type: " + type +
                          "\r\nContent-Length: " + b.Length + "\r\nCache-Control: " + cache + "\r\nConnection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            await s.WriteAsync(h, 0, h.Length);
            await s.WriteAsync(b, 0, b.Length);
        }

        static string J(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        static string N(double d)
        {
            return Math.Round(d).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        static string StatsJson()
        {
            Link[] all;
            lock (Gate) all = Links.ToArray();
            var sb = new StringBuilder();
            sb.Append("{\"port\":").Append(Port)
              .Append(",\"sticky\":").Append(StickyMode ? "true" : "false")
              .Append(",\"systemProxy\":").Append(SystemProxy ? "true" : "false")
              .Append(",\"cheap\":").Append(CheapMode ? "true" : "false")
              .Append(",\"prefer\":").Append(Prefer == null ? "null" : J(Prefer))
              .Append(",\"period\":").Append(J(LimitPeriod))
              .Append(",\"links\":[");
            for (int i = 0; i < all.Length; i++)
            {
                Link l = all[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(J(l.Id))
                  .Append(",\"name\":").Append(J(l.Name))
                  .Append(",\"ip\":").Append(J(l.Ip.ToString()))
                  .Append(",\"gw\":").Append(J(l.Gateway))
                  .Append(",\"present\":").Append(l.Present ? "true" : "false")
                  .Append(",\"healthy\":").Append(l.Healthy ? "true" : "false")
                  .Append(",\"enabled\":").Append(l.Enabled ? "true" : "false")
                  .Append(",\"weight\":").Append(l.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"latency\":").Append(l.LatencyMs)
                  .Append(",\"active\":").Append(l.Active)
                  .Append(",\"conns\":").Append(Interlocked.Read(ref l.TotalConns))
                  .Append(",\"fails\":").Append(Interlocked.Read(ref l.Failures))
                  .Append(",\"down\":").Append(Interlocked.Read(ref l.BytesDown))
                  .Append(",\"up\":").Append(Interlocked.Read(ref l.BytesUp))
                  .Append(",\"rateDown\":").Append(N(l.RateDown))
                  .Append(",\"rateUp\":").Append(N(l.RateUp))
                  .Append(",\"preferred\":").Append(l.Preferred ? "true" : "false")
                  .Append(",\"busy\":").Append(l.Busy ? "true" : "false")
                  .Append(",\"capacity\":").Append(N(l.Capacity))
                  .Append(",\"speedMbps\":").Append((l.SpeedBps / 125000).ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"used\":").Append(l.UsedPeriod)
                  .Append(",\"limit\":").Append(l.LimitBytes)
                  .Append(",\"overLimit\":").Append(l.OverLimit ? "true" : "false")
                  .Append(",\"hist\":[");
                for (int k = 0; k < l.Hist.Length; k++)
                {
                    if (k > 0) sb.Append(',');
                    sb.Append(N(l.Hist[(l.HistPos + k) % l.Hist.Length]));
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ---------------- Windows system proxy ----------------

        const string InetKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        static bool proxyChanged;
        static object oldEnable, oldServer, oldOverride;
        delegate bool CtrlHandler(int type);
        static CtrlHandler ctrlHandler;

        [DllImport("wininet.dll", SetLastError = true)]
        static extern bool InternetSetOption(IntPtr h, int option, IntPtr buffer, int len);

        [DllImport("kernel32.dll")]
        static extern bool SetConsoleCtrlHandler(CtrlHandler handler, bool add);

        static void NotifyProxyChange()
        {
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
        }

        static string OurProxy() { return "127.0.0.1:" + Port; }

        static void SetProxy()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(InetKey, true))
            {
                oldEnable = k.GetValue("ProxyEnable");
                oldServer = k.GetValue("ProxyServer");
                oldOverride = k.GetValue("ProxyOverride");
                // If an earlier run crashed and left our own proxy set, restore to "off".
                if (oldServer != null && oldServer.ToString().StartsWith("127.0.0.1:"))
                {
                    oldEnable = 0; oldServer = null; oldOverride = null;
                }
                k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                k.SetValue("ProxyServer", OurProxy(), RegistryValueKind.String);
                k.SetValue("ProxyOverride", "<local>;localhost;127.*;10.*;172.16.*;192.168.*", RegistryValueKind.String);
            }
            proxyChanged = true;
            NotifyProxyChange();
        }

        // ---------------- data usage (survives restarts) ----------------

        static string PeriodKey()
        {
            return LimitPeriod == "day" ? DateTime.Now.ToString("yyyy-MM-dd") : DateTime.Now.ToString("yyyy-MM");
        }

        static void LoadUsage()
        {
            UsageFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "usage-" + Environment.MachineName + ".txt");
            CurrentPeriod = PeriodKey();
            try
            {
                if (!File.Exists(UsageFile)) return;
                string[] lines = File.ReadAllLines(UsageFile);
                if (lines.Length == 0 || lines[0] != "period=" + CurrentPeriod) return; // new month/day
                foreach (var line in lines.Skip(1))
                {
                    int tab = line.LastIndexOf('\t');
                    long v;
                    if (tab > 0 && long.TryParse(line.Substring(tab + 1), out v)) SavedUsage[line.Substring(0, tab)] = v;
                }
            }
            catch { }
        }

        static void SaveUsage()
        {
            if (UsageFile == null) return;
            lock (UsageGate)
            {
                try
                {
                    Link[] all;
                    lock (Gate) all = Links.ToArray();
                    string now = PeriodKey();
                    if (now != CurrentPeriod)
                    {
                        // a new month (or day) started: everyone starts from zero
                        CurrentPeriod = now;
                        SavedUsage.Clear();
                        foreach (var l in all)
                        {
                            l.UsedBase = 0;
                            l.PeriodStartBytes = Interlocked.Read(ref l.BytesDown) + Interlocked.Read(ref l.BytesUp);
                        }
                    }
                    foreach (var l in all) SavedUsage[l.Name] = l.UsedPeriod;
                    var sb = new StringBuilder("period=" + CurrentPeriod + "\r\n");
                    foreach (var kv in SavedUsage) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append("\r\n");
                    File.WriteAllText(UsageFile + ".tmp", sb.ToString());
                    if (File.Exists(UsageFile)) File.Delete(UsageFile);
                    File.Move(UsageFile + ".tmp", UsageFile);
                }
                catch { }
            }
        }

        static void RestoreProxy()
        {
            if (!proxyChanged) return;
            proxyChanged = false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(InetKey, true))
                {
                    k.SetValue("ProxyEnable", oldEnable ?? 0, RegistryValueKind.DWord);
                    if (oldServer != null) k.SetValue("ProxyServer", oldServer); else k.DeleteValue("ProxyServer", false);
                    if (oldOverride != null) k.SetValue("ProxyOverride", oldOverride); else k.DeleteValue("ProxyOverride", false);
                }
                NotifyProxyChange();
                Console.WriteLine("Windows proxy restored.");
            }
            catch { }
        }

        static void ClearProxy()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(InetKey, true))
                k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            NotifyProxyChange();
        }
    }
}
