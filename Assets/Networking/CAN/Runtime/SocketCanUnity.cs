using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using ExcavatorApp.Networking.CAN;
/* ls /dev/tty*
sudo slcand -o -s6 /dev/ttyACM0 can0
sudo ip link set can0 type can bitrate 1000000
sudo ip link set can0 up
查看can口情况ip -details link show can0
*/
public class SocketCanUnity : MonoBehaviour
{
    // ---- CAN / native constants & structs ----
    const int AF_CAN = 29;
    const int SOCK_RAW = 3;
    const int CAN_RAW = 1;
    const uint SIOCGIFINDEX = 0x8933;
    const short POLLIN = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    struct Ifreq
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ifr_name;
        public int ifr_ifindex;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct SockaddrCan
    {
        public ushort can_family;
        public ushort __pad;
        public int can_ifindex;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] _pad;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct CanFrame
    {
        public uint can_id;
        public byte can_dlc;
        public byte __pad;
        public byte __res0;
        public byte __res1;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] data;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    // ---- P/Invoke ----
    [DllImport("libc", SetLastError = true)]
    static extern int socket(int domain, int type, int protocol);
    [DllImport("libc", SetLastError = true)]
    static extern int ioctl(int fd, uint request, ref Ifreq ifr);
    [DllImport("libc", SetLastError = true)]
    static extern int bind(int sockfd, IntPtr addr, uint addrlen);
    [DllImport("libc", SetLastError = true)]
    static extern IntPtr send(int sockfd, IntPtr buf, IntPtr len, int flags);
    [DllImport("libc", SetLastError = true)]
    static extern int read(int fd, byte[] buf, IntPtr count);
    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);
    [DllImport("libc", SetLastError = true)]
    static extern IntPtr strerror(int errnum);
    [DllImport("libc", SetLastError = true)]
    static extern int poll([In, Out] PollFd[] fds, uint nfds, int timeout);

    static string GetStrError(int err)
    {
        try
        {
            IntPtr p = strerror(err);
            if (p != IntPtr.Zero) return Marshal.PtrToStringAnsi(p);
        }
        catch { }
        return "Unknown error";
    }

    // ---- SocketCan wrapper (reuse native buffers + poll) ----
    class SocketCan
    {
        public int sock = -1;
        readonly int frameSize;
        IntPtr framePtr = IntPtr.Zero;
        IntPtr recvPtr = IntPtr.Zero;
        PollFd[] pollFds = new PollFd[1];

        public SocketCan()
        {
            frameSize = Marshal.SizeOf(typeof(CanFrame));
        }

        public bool Open(string ifname)
        {
            sock = socket(AF_CAN, SOCK_RAW, CAN_RAW);
            if (sock < 0)
            {
                int e = Marshal.GetLastWin32Error();
                Debug.LogError($"socket() failed, errno={e} msg={GetStrError(e)}");
                return false;
            }

            Ifreq ifr = new Ifreq();
            ifr.ifr_name = new byte[16];
            var nb = Encoding.ASCII.GetBytes(ifname);
            int copyLen = Math.Min(nb.Length, 15);
            Array.Copy(nb, ifr.ifr_name, copyLen);
            ifr.ifr_name[copyLen] = 0;

            int rc = ioctl(sock, SIOCGIFINDEX, ref ifr);
            if (rc < 0)
            {
                int e = Marshal.GetLastWin32Error();
                Debug.LogError($"ioctl(SIOCGIFINDEX) failed errno={e} msg={GetStrError(e)}");
                Close();
                return false;
            }

            SockaddrCan addr = new SockaddrCan();
            addr.can_family = (ushort)AF_CAN;
            addr.__pad = 0;
            addr.can_ifindex = ifr.ifr_ifindex;
            addr._pad = new byte[8];

            int size = Marshal.SizeOf(addr);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(addr, ptr, false);
                rc = bind(sock, ptr, (uint)size);
                if (rc < 0)
                {
                    int e = Marshal.GetLastWin32Error();
                    Debug.LogError($"bind() failed errno={e} msg={GetStrError(e)}");
                    Close();
                    return false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            // setup poll
            pollFds[0].fd = sock;
            pollFds[0].events = POLLIN;
            pollFds[0].revents = 0;

            // allocate native buffers
            framePtr = Marshal.AllocHGlobal(frameSize);
            recvPtr = Marshal.AllocHGlobal(frameSize);

            return true;
        }

        public void Close()
        {
            if (framePtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(framePtr);
                framePtr = IntPtr.Zero;
            }
            if (recvPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(recvPtr);
                recvPtr = IntPtr.Zero;
            }
            if (sock >= 0)
            {
                try { close(sock); } catch { }
                sock = -1;
            }
        }

        public bool Send(uint canId, byte[] payload)
        {
            if (sock < 0) return false;
            if (payload == null) payload = new byte[0];
            if (payload.Length > 8) throw new ArgumentException("payload max 8 bytes");

            CanFrame frame = new CanFrame();
            frame.can_id = canId & 0x7FFu;
            frame.can_dlc = (byte)payload.Length;
            frame.__pad = frame.__res0 = frame.__res1 = 0;
            frame.data = new byte[8];
            Array.Copy(payload, frame.data, payload.Length);

            Marshal.StructureToPtr(frame, framePtr, false);
            IntPtr sent = send(sock, framePtr, (IntPtr)frameSize, 0);
            int written = sent.ToInt32();
            if (written == frameSize) return true;

            int err = Marshal.GetLastWin32Error();
            Debug.LogError($"CAN send failed: written={written}, errno={err} msg={GetStrError(err)}");
            return false;
        }

        public bool TryReceiveRaw(out uint canId, out byte[] data, int pollTimeoutMs = 200)
        {
            canId = 0; data = null;
            if (sock < 0) return false;

            int pres = poll(pollFds, 1, pollTimeoutMs);
            if (pres == 0) return false;
            if (pres < 0) return false;

            int size = frameSize;
            byte[] buf = new byte[size];
            int r = read(sock, buf, (IntPtr)size);
            if (r <= 0) return false;

            // extract can_id and payload bytes
            Marshal.Copy(buf, 0, recvPtr, size);
            CanFrame frame = (CanFrame)Marshal.PtrToStructure(recvPtr, typeof(CanFrame));
            canId = frame.can_id & 0x1FFFFFFF;
            data = new byte[frame.can_dlc];
            Array.Copy(frame.data, data, frame.can_dlc);
            return true;
        }
    }

    // ---- Public API: events / properties for PWM frames ----
    public event Action<ushort[]>? OnPwmFrame;
    public ushort[] LastPwm { get; private set; } = new ushort[CanPwmCodec.ChannelCount];

    // ---- Internal members ----
    SocketCan socketCan;
    Task readTask = null;
    Task sendTask = null;
    CancellationTokenSource _cts = null;

    // queue decoded pwm frames (background -> main thread)
    ConcurrentQueue<ushort[]> pwmQueue = new ConcurrentQueue<ushort[]>();

    [Header("CAN Settings")]
    public string canInterface = "can0";
    public string sendIdText = "0x19";
    public string listenIdText = "0x59";
    [NonSerialized] public ushort sendId = 0x19;
    [NonSerialized] public ushort listenId = 0x59;

    [Header("Auto Send (periodic)")]
    public bool autoSend = true;
    public float sendFrequencyHz = 100f; // default 100Hz
    [Tooltip("16 hex chars for 8 bytes, e.g. AE112C0000000000")]
    public string autoPayloadHex = "AE112C0000000000";

    byte[] autoPayloadBytes = new byte[8];
    

    void OnValidate()
    {
        ParseSendIdText();
        if (!string.IsNullOrEmpty(autoPayloadHex)) autoPayloadBytes = ParsePayloadHex(autoPayloadHex);
    }

    void Awake()
    {
        ParseSendIdText();
        if (!string.IsNullOrEmpty(autoPayloadHex)) autoPayloadBytes = ParsePayloadHex(autoPayloadHex);
    }

    void ParseSendIdText()
    {
        if (string.IsNullOrEmpty(sendIdText)) return;
        string s = sendIdText.Trim();
        try
        {
            uint val;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                val = Convert.ToUInt32(s.Substring(2), 16);
            else
                val = Convert.ToUInt32(s, 10);
            sendId = (ushort)(val & 0x7FF);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to parse sendIdText '{sendIdText}': {ex.Message}");
        }
    }

    byte[] ParsePayloadHex(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return new byte[8];
        string s = hex.Replace(" ", "").Replace("0x", "").Replace("0X", "");
        if (s.Length % 2 != 0) s = "0" + s;
        int maxBytes = Math.Min(8, s.Length / 2);
        byte[] res = new byte[8];
        for (int i = 0; i < maxBytes; i++)
        {
            string bstr = s.Substring(i * 2, 2);
            res[i] = Convert.ToByte(bstr, 16);
        }
        return res;
    }

    void Start()
    {
        autoPayloadBytes = ParsePayloadHex(autoPayloadHex);            

        _cts = new CancellationTokenSource();
        socketCan = new SocketCan();
        if (!socketCan.Open(canInterface))
        {
            Debug.LogError("Failed to open CAN interface " + canInterface);
            return;
        }

        // ensure when cancelled the socket is closed to wake poll/read
        _cts.Token.Register(() =>
        {
            try { socketCan?.Close(); } catch { }
        });

        // start read and send tasks
        readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        if (autoSend)
            sendTask = Task.Run(() => SendLoopAsync(_cts.Token));
    }

    // Read task: receive raw frames, decode PWM in background, enqueue decoded pwm arrays
    async Task ReadLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool ok = socketCan.TryReceiveRaw(out uint id, out byte[] data, 200);
                if (ok)
                {
                    if ((id & 0x7FFu) == (uint)listenId)
                    {
                        // Expect 8-byte payload for PWM; if DLC < 8 skip
                        if (data != null && data.Length == CanPwmCodec.PayloadSizeBytes)
                        {
                            try
                            {
                                ushort[] pwm = new ushort[CanPwmCodec.ChannelCount];
                                CanPwmCodec.Decode(data, pwm);
                                pwmQueue.Enqueue(pwm);
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"CanPwmCodec.Decode failed: {ex.Message}");
                            }
                        }
                    }
                    // else ignore other IDs
                }
                // else timeout -> loop and check token
                await Task.Yield();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("ReadLoopAsync exception: " + ex.Message);
        }
    }

    // Send loop unchanged (periodic sending)
    async Task SendLoopAsync(CancellationToken token)
    {
        double intervalMs = Math.Max(1.0, 1000.0 / Math.Max(0.0001, sendFrequencyHz));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!token.IsCancellationRequested)
        {
            sw.Restart();
            try
            {
                socketCan?.Send(sendId, autoPayloadBytes);
            }
            catch (Exception ex)
            {
                Debug.LogError("SendLoopAsync exception: " + ex.Message);
            }
            double elapsed = sw.Elapsed.TotalMilliseconds;
            double sleepMs = intervalMs - elapsed;
            if (sleepMs >= 2.0)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(sleepMs), token); }
                catch (OperationCanceledException) { break; }
            }
            else if (sleepMs > 0)
            {
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                while (sw2.Elapsed.TotalMilliseconds < sleepMs)
                {
                    if (token.IsCancellationRequested) break;
                    Thread.SpinWait(10);
                }
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    void Update()
    {
        while (pwmQueue.TryDequeue(out var pwm))
        {
            // update LastPwm (make a copy so external callers cannot mutate our internal buffer)
            ushort[] copy = (ushort[])pwm.Clone();
            LastPwm = copy;
            try
            {
                OnPwmFrame?.Invoke((ushort[])copy.Clone()); // invoke with a clone to be safe
                UnityEngine.Debug.Log($"PWM: {string.Join(", ", copy)}");
            }
            catch (Exception ex)
            {
                Debug.LogError("OnPwmFrame handler threw: " + ex.Message);
            }
        }

        // runtime: if autoSend toggled on/off, ensure sendTask exists/removed
        if (autoSend && (sendTask == null || sendTask.IsCompleted))
        {
            if (_cts != null && !_cts.IsCancellationRequested)
                sendTask = Task.Run(() => SendLoopAsync(_cts.Token));
        }
    }

    async void OnDestroy()
    {
        try
        {
            if (_cts != null)
            {
                _cts.Cancel();
                var tasks = new System.Collections.Generic.List<Task>();
                if (readTask != null) tasks.Add(readTask);
                if (sendTask != null) tasks.Add(sendTask);
                if (tasks.Count > 0)
                {
                    try
                    {
                        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(500));
                    }
                    catch { }
                }
                _cts.Dispose();
                _cts = null;
            }
        }
        catch { /* ignore */ }

        socketCan?.Close();
    }

    static string BytesToHex(byte[] data)
    {
        if (data == null || data.Length == 0) return "";
        StringBuilder sb = new StringBuilder(data.Length * 2);
        foreach (var b in data) sb.AppendFormat("{0:X2}", b);
        return sb.ToString();
    }
}