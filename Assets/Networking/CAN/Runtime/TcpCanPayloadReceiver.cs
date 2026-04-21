using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ExcavatorApp.Networking.CAN
{
    /// <summary>
    /// Connects to a TCP server and continuously reads raw 8-byte CAN payload frames.
    /// Runs network IO on a background Task, dispatches decoded PWM to the Unity main thread via event.
    /// </summary>
    public sealed class TcpCanPayloadReceiver : MonoBehaviour
    {
        [Header("TCP")]
        public string host = "127.0.0.1";
        public int port = 9000;
        public bool autoReconnect = true;
        public float reconnectDelaySeconds = 1f;

        public event Action<ushort[]>? OnPwmFrame;
        public event Action<string>? OnStatus;
        public event Action<string>? OnError;

        private CancellationTokenSource? _cts;
        private Task? _worker;

        private readonly object _latestLock = new();
        private ushort[]? _latestPwm;
        private bool _hasLatest;

        private void OnEnable()
        {
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => WorkerLoop(_cts.Token));
        }

        private void OnDisable()
        {
            try
            {
                _cts?.Cancel();
            }
            catch { /* ignore */ }
        }

        private void Update()
        {
            ushort[]? snapshot = null;
            bool has;
            lock (_latestLock)
            {
                has = _hasLatest;
                if (has && _latestPwm != null)
                {
                    snapshot = (ushort[])_latestPwm.Clone();
                    _hasLatest = false;
                }
            }

            if (has && snapshot != null)
                OnPwmFrame?.Invoke(snapshot);
        }

        private async Task WorkerLoop(CancellationToken token)
        {
            byte[] frame = new byte[CanPwmCodec.PayloadSizeBytes];
            ushort[] pwm = new ushort[CanPwmCodec.ChannelCount];

            while (!token.IsCancellationRequested)
            {
                TcpClient? client = null;
                NetworkStream? stream = null;

                try
                {
                    client = new TcpClient();
                    await client.ConnectAsync(host, port);
                    stream = client.GetStream();
                    stream.ReadTimeout = 5000;
                    stream.WriteTimeout = 5000;

                    OnStatus?.Invoke($"TCP connected {host}:{port}");

                    while (!token.IsCancellationRequested && client.Connected)
                    {
                        await ReadExactlyAsync(stream, frame, token);

                        try
                        {
                            CanPwmCodec.Decode(frame, pwm);
                        }
                        catch (Exception ex)
                        {
                            OnError?.Invoke($"Decode error: {ex.Message}");
                            continue;
                        }

                        lock (_latestLock)
                        {
                            _latestPwm ??= new ushort[CanPwmCodec.ChannelCount];
                            Array.Copy(pwm, _latestPwm, CanPwmCodec.ChannelCount);
                            _hasLatest = true;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"TCP receive error: {ex.Message}");
                }
                finally
                {
                    try { stream?.Close(); } catch { /* ignore */ }
                    try { client?.Close(); } catch { /* ignore */ }
                }

                OnStatus?.Invoke("TCP disconnected");

                if (!autoReconnect || token.IsCancellationRequested)
                    break;

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Mathf.Max(0.1f, reconnectDelaySeconds)), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer, read, buffer.Length - read, token);
                if (n <= 0)
                    throw new SocketException((int)SocketError.ConnectionReset);
                read += n;
            }
        }
    }
}

