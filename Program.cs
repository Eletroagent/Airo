using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WebSocketSharp;
using WebSocketSharp.Server;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace AiroWebRTCServer
{
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //  SignalingBehavior â€” one instance per connected WebRTC client
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public class SignalingBehavior : WebSocketBehavior
    {
        private bool _authorized = false;
        private RTCPeerConnection? _pc;
        private Process? _ffmpegProcess;
        private readonly object _encodingLock = new object();
        private int _isEncoding = 0;                    // 0 = idle, 1 = running (Interlocked)
        private CancellationTokenSource? _encodingCts;

        protected override void OnOpen()
        {
            var pin = Context.QueryString["pin"];
            string ip = Context.UserEndPoint.Address.ToString();
            
            if (Program.IsLockedOut(ip))
            {
                Program.Log($"[Security] Rejected locked-out WebSocket from {ip}");
                Sessions.CloseSession(ID, CloseStatusCode.PolicyViolation, "Locked out");
                return;
            }

            if (pin != Program.AuthPin)
            {
                Program.Log($"[Security] Rejected unauthorized WebSocket from {ip}");
                Program.RecordFailedAttempt(ip);
                Sessions.CloseSession(ID, CloseStatusCode.PolicyViolation, "Invalid PIN");
                return;
            }
            
            _authorized = true;
        }

        // â”€â”€ Signaling message handler â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        protected override async void OnMessage(MessageEventArgs e)
        {
            if (!_authorized) return;
            try
            {
                var msg  = JsonDocument.Parse(e.Data);
                var type = msg.RootElement.GetProperty("type").GetString();

                if (type == "offer")
                {
                    var sdp = msg.RootElement.GetProperty("sdp").GetString();
                    if (sdp != null) 
                    {
                        string clientIp = Context.UserEndPoint.Address.ToString();
                        sdp = Regex.Replace(sdp, @"[a-zA-Z0-9\-]+\.local", clientIp);
                        await HandleOffer(sdp);
                    }
                }
                else if (type == "candidate")
                {
                    var raw   = msg.RootElement.GetProperty("candidate").GetRawText();
                    
                    var cInit = JsonSerializer.Deserialize<RTCIceCandidateInit>(raw);
                    if (cInit != null && _pc != null)
                    {
                        // Safely handle mDNS local addresses in host candidates
                        if (cInit.candidate != null && cInit.candidate.Contains("typ host") && cInit.candidate.Contains(".local"))
                        {
                            string clientIp = Context.UserEndPoint.Address.ToString();
                            var tokens = cInit.candidate.Split(' ');
                            if (tokens.Length >= 6 && tokens[4].EndsWith(".local"))
                            {
                                tokens[4] = clientIp;
                                cInit.candidate = string.Join(" ", tokens);
                                Program.Log($"[ICE] Candidate mDNS resolved safely to: {clientIp}");
                            }
                        }
                        
                        _pc.addIceCandidate(cInit);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log($"[Signaling] Error: {ex.Message}");
            }
        }

        private async Task HandleOffer(string sdp)
        {
            Program.Log("[WebRTC] Offer received — setting up peer connection");

            if (_pc != null)
            {
                Program.Log("[WebRTC] Closing existing peer connection for renegotiation");
                StopVideo();
                _pc.Close("Renegotiating");
                _pc = null;
            }

            var config = new RTCConfiguration
            {
                iceServers = Program.IceServers
            };
            _pc = new RTCPeerConnection(config);

            // ——— Video track: H.264 High Profile Level 4.2 (handles 1080p @ 60fps) ———
            var videoFormat = new SDPAudioVideoMediaFormat(
                new VideoFormat(VideoCodecsEnum.H264, 96, 90000,
                    "profile-level-id=64002a;packetization-mode=1;level-asymmetry-allowed=1"));

            var videoTrack = new MediaStreamTrack(
                SDPMediaTypesEnum.video, false,
                new List<SDPAudioVideoMediaFormat> { videoFormat });
            _pc.addTrack(videoTrack);

            // ——— Connection state → start encoding only when ICE is complete ————
            _pc.onconnectionstatechange += (state) =>
            {
                Program.Log($"[WebRTC] Connection state: {state}");

                if (state == RTCPeerConnectionState.connected)
                {
                    Program.RegisterWebRTCClient(this);
                    
                    if (State == WebSocketState.Open)
                    {
                        Send("{\"type\":\"status\",\"message\":\"Negotiating encoder...\"}");
                    }
                    
                    StartVideo();
                }
                else if (state == RTCPeerConnectionState.closed   ||
                         state == RTCPeerConnectionState.failed   ||
                         state == RTCPeerConnectionState.disconnected)
                {
                    Program.UnregisterWebRTCClient(this);
                    StopVideo();
                }
            };

            _pc.oniceconnectionstatechange += (state) =>
            {
                Program.Log($"[ICE] Connection state: {state}");
            };

            // ——— Trickle ICE ——————————————————————————————————————————————————————
            _pc.onicecandidate += (candidate) =>
            {
                string json = candidate.toJSON();
                Program.Log($"[ICE] Local candidate gathered: {json}");

                if (State == WebSocketState.Open)
                    Send($"{{\"type\":\"candidate\",\"candidate\":{json}}}");
            };

            // ——— SDP negotiation ————————————————————————————————————————————————
            _pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp  = sdp
            });

            var answer = _pc.createAnswer(null);
            await _pc.setLocalDescription(answer);

            // Inject bandwidth hint into the SDP sent to the client.
            string clientSdp = InjectBandwidthHint(answer.sdp, asKbps: 20480);
            
            // Force sendonly direction to fix "Incompatible send direction" browser error
            clientSdp = Regex.Replace(clientSdp, @"a=sendrecv", "a=sendonly");
            clientSdp = Regex.Replace(clientSdp, @"a=recvonly", "a=sendonly");

            Send(JsonSerializer.Serialize(new { type = "answer", sdp = clientSdp }));
        }

        private static string InjectBandwidthHint(string sdp, int asKbps)
        {
            return Regex.Replace(
                sdp,
                @"(m=video[^\n]*\n)",
                $"$1b=AS:{asKbps}\r\n",
                RegexOptions.Multiline);
        }

        protected override void OnClose(CloseEventArgs e)
        {
            Program.UnregisterWebRTCClient(this);
            StopVideo();
            _pc?.Close("WebSocket closed");
        }

        // ——— Video pipeline ————————————————————————————————————————————————
        private void StopVideo()
        {
            lock (_encodingLock)
            {
                _encodingCts?.Cancel();
                try { _ffmpegProcess?.Kill(); } catch { /* already dead */ }
                _ffmpegProcess = null;
                Interlocked.Exchange(ref _isEncoding, 0);
            }
        }

        private void StartVideo()
        {
            lock (_encodingLock)
            {
                // Guard: only one encoder per peer connection
                if (Interlocked.CompareExchange(ref _isEncoding, 1, 0) != 0)
                {
                    Program.Log("[Encoder] Already running for this connection — skipped");
                    return;
                }

                _encodingCts = new CancellationTokenSource();
                var token = _encodingCts.Token;

                Task.Run(() =>
                {
                    try
                    {
                        // ─── 1. Locate FFmpeg ──────────────────────────────────────────
                        string ffmpegPath = FfmpegLocator.Find();
                        if (ffmpegPath == null)
                        {
                            Program.Log("[Encoder] FFmpeg not found!");
                            Interlocked.Exchange(ref _isEncoding, 0);
                            return;
                        }
                        Program.Log($"[Encoder] Using FFmpeg: {ffmpegPath}");

                        // ─── 2. Locate virtual display ────────────────────────────────
                        Screen? vdisp = Program.VirtualDisplay;
                        if (vdisp == null)
                        {
                            Program.Log("[Encoder] Virtual display not available — aborting");
                            Interlocked.Exchange(ref _isEncoding, 0);
                            return;
                        }
                        int x = vdisp.Bounds.X;
                        int y = vdisp.Bounds.Y;
                        int w = vdisp.Bounds.Width;
                        int h = vdisp.Bounds.Height;

                        // ─── 3. Auto-detect GPU encoder ───────────────────────────────
                        string encoder    = EncoderDetector.GetEncoder(ffmpegPath);
                        string encArgs    = BuildEncoderArgs(encoder);
                        string ffmpegArgs =
                            $"-f lavfi " +
                            $"-i ddagrab=framerate=60:offset_x={x}:offset_y={y}:video_size={w}x{h} " +
                            $"{encArgs} " +
                            $"-pix_fmt yuv420p " +
                            $"-aud 1 " +
                            $"-f h264 -";

                        var psi = new ProcessStartInfo
                        {
                            FileName               = ffmpegPath,
                            Arguments              = ffmpegArgs,
                            UseShellExecute        = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError  = true,
                            CreateNoWindow         = true
                        };

                        _ffmpegProcess = Process.Start(psi)!;

                        Task.Run(() =>
                        {
                            try
                            {
                                string? line;
                                while ((line = _ffmpegProcess?.StandardError.ReadLine()) != null)
                                {
                                    if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                        line.Contains("fps="))
                                        Program.Log($"[FFmpeg] {line.Trim()}");
                                }
                            }
                            catch { }
                        });

                        // ─── 4. Run the streaming loop ────────────────────────────────
                        RunStreamLoop(_ffmpegProcess!.StandardOutput.BaseStream, token);
                    }
                    catch (OperationCanceledException) { Program.Log("[Encoder] Encoding cancelled"); }
                    catch (Exception ex) { Program.Log($"[Encoder] Fatal error: {ex.Message}"); }
                    finally
                    {
                        StopVideo();
                        Program.Log("[Encoder] Encoding stopped");
                    }
                }, token);
            }
        }

        private void RunStreamLoop(Stream source, CancellationToken token)
        {
            const int READ_BUF_SIZE = 65_536;
            const int ACC_BUF_SIZE  = 16 * 1024 * 1024;

            byte[] readBuf = new byte[READ_BUF_SIZE];
            byte[] acc     = new byte[ACC_BUF_SIZE];
            int    accLen  = 0;
            const uint RTP_DURATION_60FPS = 1500;

            Program.Log("[Encoder] Streaming loop started");

            while (!token.IsCancellationRequested &&
                   _pc?.connectionState == RTCPeerConnectionState.connected)
            {
                int bytesRead;
                try { bytesRead = source.Read(readBuf, 0, readBuf.Length); }
                catch { break; }

                if (bytesRead <= 0) break;

                if (accLen + bytesRead > acc.Length)
                {
                    accLen = 0;
                    continue;
                }

                Buffer.BlockCopy(readBuf, 0, acc, accLen, bytesRead);
                accLen += bytesRead;
                ExtractAndSendFrames(acc, ref accLen, RTP_DURATION_60FPS);
            }
        }

        private void ExtractAndSendFrames(byte[] buf, ref int len, uint rtpDuration)
        {
            while (true)
            {
                int frameStart = FindAUD(buf, 0, len);
                if (frameStart < 0) break;

                int nextFrame = FindAUD(buf, frameStart + 5, len);
                if (nextFrame < 0) break;

                int frameLen = nextFrame - frameStart;

                if (frameLen > 0 && _pc != null)
                {
                    byte[] frameData = new byte[frameLen];
                    Buffer.BlockCopy(buf, frameStart, frameData, 0, frameLen);
                    try { _pc.SendVideo(rtpDuration, frameData); }
                    catch (Exception ex) { Program.Log($"[Encoder] SendVideo error: {ex.Message}"); return; }
                }

                int remaining = len - nextFrame;
                if (remaining > 0)
                    Buffer.BlockCopy(buf, nextFrame, buf, 0, remaining);
                len = remaining;
            }
        }

        private static int FindAUD(byte[] buf, int start, int len)
        {
            int end = len - 4;
            for (int i = start; i <= end; i++)
            {
                if (buf[i] == 0x00 && buf[i + 1] == 0x00)
                {
                    if (buf[i + 2] == 0x00 && buf[i + 3] == 0x01 && i + 4 < len && buf[i + 4] == 0x09) return i;
                    if (buf[i + 2] == 0x01 && buf[i + 3] == 0x09) return i;
                }
            }
            return -1;
        }

        private static string BuildEncoderArgs(string encoder)
        {
            return encoder switch
            {
                "h264_nvenc" => "-c:v h264_nvenc -preset p4 -tune ll -profile:v high -level 4.2 -rc vbr -cq 23 -b:v 12M -maxrate 18M -bufsize 24M -g 120 -keyint_min 60 -sc_threshold 0 -bf 0 -refs 1 -multipass qres -rc-lookahead 0",
                "h264_amf" => "-c:v h264_amf -quality speed -usage ultralowlatency -profile:v high -level 4.2 -rc vbr_peak -qp_i 22 -qp_p 24 -b:v 12M -maxrate 18M -bufsize 24M -g 120 -bf 0",
                "h264_qsv" => "-c:v h264_qsv -preset veryfast -profile:v high -level 4.2 -look_ahead 0 -async_depth 1 -b:v 12M -maxrate 18M -bufsize 24M -g 120 -bf 0",
                _ => "-c:v libx264 -preset ultrafast -tune zerolatency -profile:v high -level 4.2 -crf 23 -maxrate 18M -bufsize 24M -g 120 -bf 0"
            };
        }
    }

    internal static class FfmpegLocator
    {
        public static string Find()
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(candidate)) return candidate; } catch { }
            }
            string wingetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetRoot))
            {
                var hits = Directory.GetFiles(wingetRoot, "ffmpeg.exe", SearchOption.AllDirectories);
                if (hits.Length > 0) return hits.OrderByDescending(f => f).First();
            }
            string[] common = { @"C:\ffmpeg\bin\ffmpeg.exe", @"C:\Program Files\ffmpeg\bin\ffmpeg.exe" };
            foreach (string p in common) if (File.Exists(p)) return p;
            return null!;
        }
    }

    internal static class EncoderDetector
    {
        private static string? _cached;
        private static readonly object _lock = new();
        public static string GetEncoder(string ffmpegPath)
        {
            if (Environment.GetEnvironmentVariable("AIRO_FORCE_SOFTWARE_ENCODER") == "1")
            {
                Program.Log("[Encoder] Hardware encoding bypassed via AIRO_FORCE_SOFTWARE_ENCODER flag.");
                return "libx264";
            }

            if (_cached != null) return _cached;
            lock (_lock)
            {
                if (_cached != null) return _cached;
                _cached = Detect(ffmpegPath);
                Program.Log($"[Encoder] Auto-selected GPU encoder: {_cached}");
                return _cached;
            }
        }
        private static string Detect(string ffmpegPath)
        {
            string[] candidates = { "h264_nvenc", "h264_amf", "h264_qsv" };
            foreach (string enc in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo { FileName = ffmpegPath, Arguments = $"-f lavfi -i color=black:s=64x64:r=60 -t 0.1 -c:v {enc} -f null -", UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
                    var proc = Process.Start(psi)!;
                    
                    if (!proc.WaitForExit(6000))
                    {
                        try { proc.Kill(); } catch { }
                        continue;
                    }
                    
                    string output = proc.StandardError.ReadToEnd();
                    if (proc.ExitCode == 0 || output.Contains("frame=")) return enc;
                } catch { }
            }
            return "libx264";
        }
    }

    class Program
    {
        public static string AuthPin = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        public static List<RTCIceServer> IceServers = new List<RTCIceServer> { new RTCIceServer { urls = "stun:stun.l.google.com:19302" } };

        private static readonly ConcurrentDictionary<string, (int attempts, DateTime lockoutEnd)> _authAttempts = new();
        
        public static bool IsLockedOut(string ip)
        {
            if (_authAttempts.TryGetValue(ip, out var info))
            {
                if (DateTime.Now < info.lockoutEnd) return true;
                if (DateTime.Now >= info.lockoutEnd && info.attempts >= 5)
                {
                    _authAttempts.TryRemove(ip, out _);
                }
            }
            return false;
        }

        public static void RecordFailedAttempt(string ip)
        {
            _authAttempts.AddOrUpdate(ip, 
                _ => (1, DateTime.MinValue), 
                (_, info) => 
                {
                    int newAttempts = info.attempts + 1;
                    DateTime lockout = newAttempts >= 5 ? DateTime.Now.AddMinutes(5) : info.lockoutEnd;
                    return (newAttempts, lockout);
                });
        }

        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(out CURSORINFO pci);
        [DllImport("user32.dll")] static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        private static bool _isRunning = false;
        private static HttpServer? _wss;
        private static Mutex _mutex = new Mutex(true, "AiroWebRTCServerMutex");

        public static Screen? VirtualDisplay { get; private set; }
        public static List<SignalingBehavior> ActiveWebRTCClients = new();
        public static void RegisterWebRTCClient(SignalingBehavior c)
        {
            lock (ActiveWebRTCClients) { if (!ActiveWebRTCClients.Contains(c)) ActiveWebRTCClients.Add(c); }
        }
        public static void UnregisterWebRTCClient(SignalingBehavior c)
        {
            lock (ActiveWebRTCClients) { ActiveWebRTCClients.Remove(c); }
        }

        // ——— Log file (visible even without a console window) —————————————
        static readonly string _logPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "airo_log.txt");

        public static void Log(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            Console.WriteLine(line);
            try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
        }

        // ——— Entry point ————————————————————————————————————————————————
        [STAThread]
        static void Main(string[] args)
        {
            try { File.WriteAllText(_logPath, $"=== Airo Stream started {DateTime.Now} ==={Environment.NewLine}"); } catch { }

            // Load or create config.json for TURN servers
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            var jsonOpts = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
            if (File.Exists(configPath))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(configPath), jsonOpts);
                    if (cfg.TryGetProperty("iceServers", out var iceServersProp))
                    {
                        var servers = JsonSerializer.Deserialize<List<RTCIceServer>>(iceServersProp.GetRawText(), jsonOpts);
                        if (servers != null && servers.Count > 0 && servers.All(s => !string.IsNullOrWhiteSpace(s.urls)))
                            IceServers = servers;
                        else
                            Log("[Config] config.json had invalid/empty iceServers - using default STUN");
                    }
                }
                catch (Exception ex) { Log($"[Config] Failed to load config.json: {ex.Message}"); }
            }
            else
            {
                string examplePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.example.json");
                if (File.Exists(examplePath))
                {
                    try
                    {
                        File.Copy(examplePath, configPath);
                        Log("[Config] config.json not found. Auto-copied config.example.json.");
                    }
                    catch (Exception ex) { Log($"[Config] Failed to auto-copy config.example.json: {ex.Message}"); }
                }
                else
                {
                    string errorMsg = "config.json is missing, and config.example.json could not be found to auto-copy.\nPlease create config.json in the application directory before running.";
                    Log($"[Config] ERROR: {errorMsg.Replace("\n", " ")}");
                    MessageBox.Show(errorMsg, "Airo Stream - Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Environment.Exit(1);
                    return;
                }
            }

            SetProcessDPIAware();

            // Single-instance guard (handle abandoned mutex from prior crash)
            bool forceHeadless = args.Length > 0 && args[0] == "--force";
            if (!forceHeadless)
            {
                bool hasLock;
                try   { hasLock = _mutex.WaitOne(TimeSpan.Zero, true); }
                catch (AbandonedMutexException) { hasLock = true; } // prior crash — we now own it
                if (!hasLock)
                {
                    MessageBox.Show("Airo Stream is already running.\nCheck the system tray icon.",
                        "Airo Stream", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            AppDomain.CurrentDomain.ProcessExit      += (_, _)  => Log("[CLR] ProcessExit.");
            AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log($"[CLR] Fatal: {ex.ExceptionObject}");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new AiroMainForm();
            try
            {
                // WM_QUIT restart loop.
                // displayswitch.exe / the IDD driver broadcast WM_QUIT when toggling
                // display topology. Standard Application.Run() treats WM_QUIT as an
                // exit signal and returns. Our loop re-attaches the message pump to
                // the same form instance so the user sees nothing change.
                while (!form.IsDisposed)
                {
                    Application.Run(form);
                    if (form.IsDisposed) break;         // user closed via "Stop Server"
                    Log("[UI] WM_QUIT received — restarting message loop.");
                    Thread.Sleep(30);
                }
            }
            finally
            {
                StopServer();
                Log("[Main] Server stopped. Goodbye.");
            }
        }



        public static string GetLocalIPAddress()
        {
            try
            {
                string? fallback = null;
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        string addr = ip.Address.ToString();
                        if (addr.StartsWith("192.168.137.")) return addr;
                        if (addr != "127.0.0.1" && !addr.StartsWith("169.254."))
                            fallback = addr;
                    }
                }
                if (fallback != null) return fallback;
            }
            catch { }
            return "127.0.0.1";
        }



        // ——— Virtual display management ———————————————————————————————————
        static readonly string _driverPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Driver");

        // ── Auto-configure Windows Firewall ──────────────────────────────────
        private static void ConfigureFirewall()
        {
            try
            {
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
                Program.Log($"[Firewall] Adding inbound allow rule for {exePath}");
                RunSilent("netsh", $"advfirewall firewall delete rule name=\"Airo Stream\"", null, 2000);
                RunSilent("netsh", $"advfirewall firewall add rule name=\"Airo Stream\" dir=in action=allow program=\"{exePath}\" enable=yes profile=any", null, 5000);
            }
            catch (Exception ex)
            {
                Program.Log($"[Firewall] Could not add rule: {ex.Message}");
            }
        }

        static void ToggleVirtualDisplay(bool enable)
        {
            try
            {
                Program.Log($"[Display] {(enable ? "Enabling" : "Disabling")} virtual display…");

                // Snapshot existing screens BEFORE enabling so we can diff afterwards
                var screensBefore = Screen.AllScreens
                    .Select(s => s.DeviceName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Run IDD installer
                RunSilent("cmd.exe",
                    $"/c deviceinstaller64.exe enableidd {(enable ? 1 : 0)}",
                    _driverPath, 60000); // Allow up to 60s for installer

                if (enable)
                {
                    Program.Log("[Display] Waiting for virtual display to initialize (this can take up to 40 seconds)...");

                    // Poll until the new virtual screen appears (up to ~40 s)
                    Screen? found = null;
                    for (int i = 0; i < 80 && found == null; i++)
                    {
                        // Every 2 seconds, enforce "Extend" topology in case the driver just finished installing
                        if (i % 4 == 0)
                            RunSilent("displayswitch.exe", "/extend", null!, 5000);

                        Thread.Sleep(500);
                        found = Screen.AllScreens
                            .FirstOrDefault(s => !screensBefore.Contains(s.DeviceName));

                        // If 15 seconds have passed and the screen is still missing, the driver base node
                        // is likely not installed. We will perform a one-time driver installation.
                        if (i == 30 && found == null)
                        {
                            Program.Log("[Display] Virtual display driver might be missing. Attempting one-time installation...");
                            RunSilent("cmd.exe", "/c deviceinstaller64.exe install usbmmIdd.inf usbmmidd", _driverPath, 30000);
                            RunSilent("cmd.exe", "/c deviceinstaller64.exe enableidd 1", _driverPath, 10000);
                        }
                    }

                    if (found != null)
                    {
                        VirtualDisplay = found;
                        Program.Log(
                            $"[Display] Virtual display detected: {found.DeviceName} " +
                            $"bounds={found.Bounds.Width}x{found.Bounds.Height} " +
                            $"at ({found.Bounds.X},{found.Bounds.Y})");

                        if (found.Bounds.Width != 1920 || found.Bounds.Height != 1080)
                            Program.Log(
                                $"[Display] ⚠ Resolution is {found.Bounds.Width}x{found.Bounds.Height}, " +
                                $"not 1920×1080. Open Display Settings and set the virtual display to 1920×1080 @ 60Hz.");
                    }
                    else
                    {
                        // Fallback: last non-primary screen
                        VirtualDisplay = Screen.AllScreens.LastOrDefault(s => !s.Primary)
                                      ?? Screen.PrimaryScreen;
                        Program.Log(
                            $"[Display] ⚠  Could not auto-detect virtual display. " +
                            $"Falling back to: {VirtualDisplay?.DeviceName ?? "Primary"}");
                    }
                }
                else
                {
                    RunSilent("displayswitch.exe", "/internal", null!, 5000);
                    VirtualDisplay = null;
                }
            }
            catch (Exception ex)
            {
                Program.Log($"[Display] Toggle failed: {ex.Message}");
            }
        }

        private static void RunSilent(string exe, string args, string workDir, int timeoutMs = 5000)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName         = exe,
                    Arguments        = args,
                    UseShellExecute  = false,
                    CreateNoWindow   = true
                };
                if (!string.IsNullOrEmpty(workDir))
                    psi.WorkingDirectory = workDir;

                Process.Start(psi)?.WaitForExit(timeoutMs);
            }
            catch (Exception ex)
            {
                Program.Log($"[Process] Failed to run {exe}: {ex.Message}");
            }
        }

        // â”€â”€ HTTP + WebSocket server â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        public static void StartServer()
        {
            if (_isRunning) return;

            ToggleVirtualDisplay(true);

            _isRunning = true;

            string publicFolder = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "public");

            _wss = new HttpServer(5000);
            _wss.DocumentRootPath = publicFolder;

            _wss.OnGet += (_, e) =>
            {
                var req = e.Request;
                var res = e.Response;

                Program.Log($"[HTTP] GET {req.Url.PathAndQuery}");

                string ip = req.UserHostAddress ?? "unknown";
                if (Program.IsLockedOut(ip))
                {
                    res.StatusCode = 403;
                    res.WriteContent(Encoding.UTF8.GetBytes("403 Forbidden - Locked out"));
                    return;
                }

                string path = req.Url.LocalPath;
                if (path == "/") path = "/index.html";

                if ((path == "/index.html" || path.EndsWith(".json")) && path != "/manifest.json")
                {
                    if (req.QueryString["pin"] != Program.AuthPin)
                    {
                        Program.RecordFailedAttempt(ip);
                        res.StatusCode = 403;
                        res.WriteContent(Encoding.UTF8.GetBytes("403 Forbidden - Invalid PIN"));
                        return;
                    }
                }

                if (path == "/config.json")
                {
                    var cleanServers = Program.IceServers
                        .Where(s => !string.IsNullOrWhiteSpace(s.urls))
                        .Select(s => new
                        {
                            urls = new[] { s.urls },
                            username = s.username,
                            credential = s.credential
                        });
                    var jsonOpts = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
                    res.ContentType = "application/json";
                    res.WriteContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { iceServers = cleanServers }, jsonOpts)));
                    return;
                }

                string filePath = Path.Combine(publicFolder, path.TrimStart('/'));

                if (File.Exists(filePath))
                {
                    res.ContentType = Path.GetExtension(filePath).ToLowerInvariant() switch
                    {
                        ".html"        => "text/html; charset=utf-8",
                        ".js"          => "application/javascript; charset=utf-8",
                        ".css"         => "text/css; charset=utf-8",
                        ".json"        => "application/json",
                        ".png"         => "image/png",
                        ".svg"         => "image/svg+xml",
                        ".ico"         => "image/x-icon",
                        ".webmanifest" => "application/manifest+json",
                        _              => "application/octet-stream"
                    };

                    byte[] data = File.ReadAllBytes(filePath);
                    res.ContentLength64 = data.Length;
                    res.Close(data, true);
                }
                else
                {
                    res.StatusCode = 404;
                    res.Close();
                }
            };

            _wss.AddWebSocketService<SignalingBehavior>("/webrtc");

            // Configure firewall before starting
            ConfigureFirewall();

            while (true)
            {
                try
                {
                    _wss.Start();
                    break;
                }
                catch (Exception ex)
                {
                    var result = MessageBox.Show("Port 5000 is already in use — close the conflicting application and try again.\n\nError: " + ex.Message,
                        "Airo Stream - Port Conflict", MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
                    if (result == DialogResult.Cancel)
                    {
                        Environment.Exit(1);
                    }
                }
            }

            Program.Log("â•”â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•—");
            Program.Log("â•‘   Airo Stream Server â€” http://0.0.0.0:5000  â•‘");
            Program.Log("â•šâ•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•");
        }

        public static void StopServer()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _wss?.Stop();
            ToggleVirtualDisplay(false);
            try { RunSilent("netsh", $"advfirewall firewall delete rule name=\"Airo Stream\"", null, 2000); } catch { }
        }

        // ── Legacy JPEG snapshot (used by debug tooling only) ──────────────────
        private static ImageCodecInfo? GetJpegCodec()
        {
            return ImageCodecInfo.GetImageDecoders()
                .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        }

        public static byte[]? ForceGDIFrame()
        {
            try
            {
                Screen target = VirtualDisplay ?? Screen.PrimaryScreen!;
                using var bmp = new Bitmap(target.Bounds.Width, target.Bounds.Height,
                                           PixelFormat.Format32bppArgb);
                using var g   = Graphics.FromImage(bmp);
                g.CopyFromScreen(target.Bounds.X, target.Bounds.Y, 0, 0,
                                 bmp.Size, CopyPixelOperation.SourceCopy);

                using var ms = new MemoryStream();
                var codec = GetJpegCodec();
                var eps   = new EncoderParameters(1);
                eps.Param[0] = new EncoderParameter(
                    System.Drawing.Imaging.Encoder.Quality, 85L);
                bmp.Save(ms, codec!, eps);
                return ms.ToArray();
            }
            catch { return null; }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AiroMainForm — QR code window that minimises to system tray
    // ──────────────────────────────────────────────────────────────────────
    class AiroMainForm : Form
    {
        private readonly NotifyIcon _tray;
        private Label    _status;
        private Label    _urlLabel;
        private PictureBox _qrBox;
        private readonly string _url;

        public AiroMainForm()
        {
            string localIP = Program.GetLocalIPAddress();
            _url = $"http://{localIP}:5000?pin={Program.AuthPin}";

            // ── Window settings
            Text            = "Airo Stream";
            ClientSize      = new Size(340, 430);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            BackColor       = Color.FromArgb(18, 18, 28);

            // ── Title
            Controls.Add(new Label
            {
                Text      = "Airo Stream",
                Font      = new Font("Segoe UI", 20, FontStyle.Bold),
                ForeColor = Color.White,
                Bounds    = new Rectangle(0, 14, 340, 44),
                TextAlign = ContentAlignment.MiddleCenter,
            });

            // ── QR code placeholder
            _qrBox = new PictureBox
            {
                Bounds      = new Rectangle(60, 68, 220, 220),
                BackColor   = Color.FromArgb(30, 30, 45),
                SizeMode    = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle,
            };
            Controls.Add(_qrBox);

            // ── URL label (clickable)
            _urlLabel = new Label
            {
                Text      = "Starting server...",
                Font      = new Font("Segoe UI", 10),
                ForeColor = Color.FromArgb(100, 200, 255),
                Bounds    = new Rectangle(0, 300, 340, 26),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor    = Cursors.Hand,
            };
            _urlLabel.Click += (_, _) =>
                Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
            Controls.Add(_urlLabel);

            // ── Status
            _status = new Label
            {
                Text      = "○  Starting...",
                Font      = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.FromArgb(180, 180, 80),
                Bounds    = new Rectangle(0, 334, 340, 24),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            Controls.Add(_status);

            // ── Hint
            Controls.Add(new Label
            {
                Text      = "Press X to minimise — server stays running in background",
                Font      = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(90, 90, 110),
                Bounds    = new Rectangle(0, 372, 340, 40),
                TextAlign = ContentAlignment.MiddleCenter,
            });

            // ── System tray icon (visible immediately)
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open Airo Stream",        null, (_, _) => ShowWindow_());
            trayMenu.Items.Add($"Open in Browser",        null,
                (_, _) => Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true }));
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Stop Server",             null, (_, _) => ForceClose());

            _tray = new NotifyIcon
            {
                Icon             = SystemIcons.Application,
                Text             = "Airo Stream — Starting...",
                ContextMenuStrip = trayMenu,
                Visible          = true,
            };
            _tray.DoubleClick += (_, _) => ShowWindow_();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Check for FFmpeg dependency before starting
            if (FfmpegLocator.Find() == null)
            {
                MessageBox.Show("FFmpeg was not found on your system.\n\nAiro Stream requires FFmpeg to encode the video stream. Without it, WebRTC will connect successfully but show a black screen.\n\nPlease download FFmpeg (e.g., from https://github.com/BtbN/FFmpeg-Builds/releases), extract it, and add it to your system PATH, or place ffmpeg.exe in C:\\ffmpeg\\bin\\.\n\nThe application will now close. Please relaunch after installing FFmpeg.", 
                    "Missing Dependency: FFmpeg", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
            }

            // Start the server in the background.
            // The IDD driver install can take up to 60 seconds; we show
            // the window immediately and update the UI when it’s ready.
            Task.Run(() =>
            {
                try   { Program.StartServer(); }
                catch (Exception ex) { Program.Log($"[UI] Server start error: {ex}"); }

                // Update UI on the main thread (safe even after WM_QUIT restart)
                if (!IsDisposed)
                    Invoke((Action)OnServerReady);
            });
        }

        void OnServerReady()
        {
            // Generate QR bitmap
            try
            {
                var gen  = new QRCoder.QRCodeGenerator();
                var data = gen.CreateQrCode(_url, QRCoder.QRCodeGenerator.ECCLevel.Q);
                _qrBox.BackColor = Color.White;
                _qrBox.Image     = new QRCoder.QRCode(data).GetGraphic(5);
            }
            catch (Exception ex) { Program.Log($"[QR] {ex.Message}"); }

            _urlLabel.Text = _url;
            _status.Text      = "●  Server Running";
            _status.ForeColor = Color.FromArgb(80, 220, 120);
            _tray.Text        = $"Airo Stream — {_url}";
            _tray.ShowBalloonTip(4000, "Airo Stream is running",
                $"Connect your device to:\n{_url}", ToolTipIcon.Info);
        }

        void ShowWindow_()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        bool _forceClose;
        void ForceClose() { _forceClose = true; Close(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_forceClose && e.CloseReason == CloseReason.UserClosing)
            {
                // X button → hide to tray, keep server running
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(2000, "Airo Stream",
                    "Still running in background.\nRight-click the tray icon to stop.",
                    ToolTipIcon.Info);
            }
            else
            {
                _tray.Visible = false;
                base.OnFormClosing(e);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tray?.Dispose();
            base.Dispose(disposing);
        }
    }
}
