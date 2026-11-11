using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MultiPcKvm.Common;
using MultiPcKvm.Video.Internal;

namespace MultiPcKvm.Video
{
    // ─────────────────────────────────────────────────────────────
    //  조종당하는 쪽(B): 내 화면 보내기  (IScreenSharer, ScreenShare.cs 약속)
    //
    //  WPF 가 켤 때 StartListening → 45002 에서 기다린다.
    //  보는 쪽이 붙으면
    //    1. CanView(그 IP) 로 꼭 확인한다. 아니면 거절하고 끊는다.
    //    2. 허락이면 "캡처 → 바뀐 칸만 압축 → 전송" 을 초당 최대 15장 반복한다.
    //    3. 보는 쪽이 줄을 끊으면 멈춘다.
    //
    //  한 번에 한 명만 본다. 새로 허락된 쪽이 오면 이전 것은 끊는다.
    //  (CanView 가 "지금 나를 조종 중인 PC" 만 허락하므로 보통 한 명이다)
    // ─────────────────────────────────────────────────────────────
    public sealed class ScreenSharer : IScreenSharer
    {
        private readonly object _lock = new object();

        private TcpListener? _listener;
        private CancellationTokenSource? _listenCts;
        private CancellationTokenSource? _activeSession;

        public Func<string, bool>? CanView { get; set; }

        /// <summary>
        /// 초당 최대 화면 수. 압축·전송이 밀리면 이보다 적게 나간다.
        /// </summary>
        public int MaxFramesPerSecond { get; set; } = 15;

        /// <summary>
        /// 화면이 가만히 있을 때 초당 확인 횟수.
        ///
        /// 바뀐 칸 없는 장이 IdleAfterFrames 번 이어지면 이 속도로 낮추고,
        /// 하나라도 바뀌면 바로 MaxFramesPerSecond 로 돌아간다.
        /// (가만히 있는 화면을 초당 15번씩 찍고 비교하는 낭비를 줄인다)
        /// </summary>
        public int IdleFramesPerSecond { get; set; } = 4;

        // 바뀐 것 없는 장이 이만큼 이어지면 쉬는 속도로 (15장 기준 약 0.5초)
        private const int IdleAfterFrames = 8;

        // 보내는 도중 CanView 를 다시 확인하는 간격
        private static readonly TimeSpan AllowedRecheckInterval = TimeSpan.FromSeconds(1);


        // ── 시작 / 종료 ───────────────────────────────────────

        public void StartListening()
        {
            lock (_lock)
            {
                if (_listener != null)
                    return;

                var listener = new TcpListener(IPAddress.Any, Constants.VideoPort);

                // 실패하면 예외를 그대로 던진다. (포트를 다른 프로그램이 쓰는 중 등)
                listener.Start();

                _listener = listener;
                _listenCts = new CancellationTokenSource();

                _ = AcceptLoopAsync(listener, _listenCts.Token);
            }

            Console.WriteLine($"화면 보내기 대기 시작: {Constants.VideoPort}");
        }

        public void StopListening()
        {
            TcpListener? listener;
            CancellationTokenSource? listenCts;
            CancellationTokenSource? session;

            lock (_lock)
            {
                listener = _listener;
                listenCts = _listenCts;
                session = _activeSession;

                _listener = null;
                _listenCts = null;
                _activeSession = null;
            }

            try { listenCts?.Cancel(); } catch { }
            try { session?.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }

            listenCts?.Dispose();
        }

        public void Dispose() => StopListening();


        // ── 받기 ─────────────────────────────────────────────

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    Console.WriteLine($"화면 보내기 접속 받기 오류: {ex.Message}");
                    await Task.Delay(200, CancellationToken.None);
                    continue;
                }

                // 접속 하나는 따로 처리한다. (다음 접속을 계속 받을 수 있게)
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken));
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken listenToken)
        {
            using (client)
            {
                string ip = NetIo.RemoteIp(client);
                CancellationTokenSource? sessionCts = null;

                try
                {
                    client.NoDelay = true;
                    NetworkStream stream = client.GetStream();

                    // ── 1) 인사 확인 (5초 안에) ──
                    using (var helloCts = CancellationTokenSource.CreateLinkedTokenSource(listenToken))
                    {
                        helloCts.CancelAfter(TimeSpan.FromSeconds(5));

                        byte[] hello = new byte[5];
                        await stream.ReadExactlyAsync(hello, helloCts.Token);

                        if (!hello.AsSpan(0, 4).SequenceEqual(VideoProtocol.Magic) ||
                            hello[4] != VideoProtocol.Version)
                        {
                            Console.WriteLine($"화면 보기 요청 무시 (모르는 형식): {ip}");
                            return;
                        }
                    }

                    // ── 2) 허락 확인 ──
                    if (!Allowed(ip))
                    {
                        Console.WriteLine($"화면 보기 거절: {ip}");

                        await NetIo.WriteStatusAsync(
                            stream,
                            ok: false,
                            "이 PC 를 조종 중인 기기만 화면을 볼 수 있습니다.",
                            listenToken);

                        return;
                    }

                    // 한 명만 본다. 이전에 보던 쪽은 끊는다.
                    sessionCts = CancellationTokenSource.CreateLinkedTokenSource(listenToken);

                    CancellationTokenSource? previous;

                    lock (_lock)
                    {
                        previous = _activeSession;
                        _activeSession = sessionCts;
                    }

                    try { previous?.Cancel(); } catch { }

                    await NetIo.WriteStatusAsync(stream, ok: true, string.Empty, sessionCts.Token);

                    Console.WriteLine($"화면 보내기 시작: {ip}");

                    // ── 3) 보는 쪽이 끊는지 지켜본다 ──
                    // 보는 쪽은 인사 뒤에 아무것도 보내지 않는다. 읽기가 끝나면 = 끊었다.
                    CancellationTokenSource watchCts = sessionCts;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            byte[] one = new byte[1];
                            await stream.ReadAsync(one, watchCts.Token);
                        }
                        catch
                        {
                        }
                        finally
                        {
                            try { watchCts.Cancel(); } catch { }
                        }
                    });

                    // ── 4) 화면 보내기 (전용 스레드) ──
                    client.SendTimeout = (int)VideoProtocol.SendTimeout.TotalMilliseconds;

                    CancellationToken sessionToken = sessionCts.Token;

                    // 그만두면 줄을 닫아서 막혀 있던 Write 를 풀어 준다.
                    using (sessionToken.Register(() => { try { client.Close(); } catch { } }))
                    {
                        await Task.Factory.StartNew(
                            () => StreamScreen(stream, ip, sessionToken),
                            CancellationToken.None,
                            TaskCreationOptions.LongRunning,
                            TaskScheduler.Default);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 그만 봄 / 프로그램 종료
                }
                catch (Exception ex) when (sessionCts?.IsCancellationRequested == true)
                {
                    // 그만둬서 줄을 닫았더니 Write 가 예외로 풀린 경우. 정상 종료.
                    _ = ex;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"화면 보내기 중단 ({ip}): {NetIo.Describe(ex)}");
                }
                finally
                {
                    if (sessionCts != null)
                    {
                        lock (_lock)
                        {
                            if (_activeSession == sessionCts)
                                _activeSession = null;
                        }

                        sessionCts.Dispose();
                    }

                    Console.WriteLine($"화면 보내기 끝: {ip}");
                }
            }
        }

        // CanView 를 부른다. 설정이 안 돼 있거나 예외가 나면 거절한다. (안전한 쪽)
        private bool Allowed(string ip)
        {
            Func<string, bool>? check = CanView;

            if (check == null)
                return false;

            try
            {
                return check(ip);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CanView 오류: {ex.Message}");
                return false;
            }
        }


        // ── 캡처 → 압축 → 전송 ────────────────────────────────

        // GDI 화면 DC 는 만든 스레드에서 쓰고 놓아야 하므로
        // 이 반복은 전용 스레드 하나에서 동기로 돈다. (await 로 스레드가 바뀌지 않게)
        //
        // 그만둘 때는 HandleClientAsync 가 줄을 닫아서 막혀 있던 Write 를 풀어 준다.
        //
        // 허락(CanView)은 연결할 때만이 아니라 1초마다 다시 확인한다.
        // 조종이 끝났는데 보는 쪽이 줄을 안 끊는 경우(버그·조작된 프로그램)에도
        // 화면이 계속 나가지 않게 하기 위해서다.
        private void StreamScreen(NetworkStream stream, string ip, CancellationToken cancellationToken)
        {
            using var capture = new GdiScreenCapture();
            var encoder = new TileEncoder();

            byte[] header = new byte[17];

            // 바뀐 것 없는 장이 몇 번 이어졌는가
            int unchangedFrames = 0;
            byte[] keepAlive = { VideoProtocol.TypeKeepAlive };

            long lastSent = Stopwatch.GetTimestamp();
            long lastAllowedCheck = Stopwatch.GetTimestamp();

            while (!cancellationToken.IsCancellationRequested)
            {
                long frameStart = Stopwatch.GetTimestamp();

                // 1초마다 허락 다시 확인. 아니면 멈춘다. (줄은 HandleClientAsync 가 닫는다)
                if (Stopwatch.GetElapsedTime(lastAllowedCheck) >= AllowedRecheckInterval)
                {
                    lastAllowedCheck = Stopwatch.GetTimestamp();

                    if (!Allowed(ip))
                    {
                        Console.WriteLine($"화면 보내기 멈춤 (더 이상 허락되지 않음): {ip}");
                        return;
                    }
                }

                // 캡처 (보안 데스크톱 등으로 못 찍으면 이번 장은 건너뛴다)
                if (capture.Capture())
                {
                    // 캡처 메모리를 복사하지 않고 바로 비교한다. (바뀐 칸만 꺼내 감)
                    EncodedFrame? encoded =
                        encoder.Encode(capture.Pixels, capture.Width, capture.Height);

                    unchangedFrames = encoded == null
                        ? unchangedFrames + 1
                        : 0;

                    if (encoded is EncodedFrame frame)
                    {
                        header[0] = VideoProtocol.TypeFrame;
                        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), frame.Width);
                        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), frame.Height);
                        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(9), frame.RawLength);
                        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(13), frame.Length);

                        // 쓰기가 오래 막히면 SendTimeout 으로 예외가 난다. (끊긴 것으로 본다)
                        stream.Write(header, 0, header.Length);
                        stream.Write(frame.Data, 0, frame.Length);

                        lastSent = Stopwatch.GetTimestamp();
                    }
                }

                // 바뀐 화면이 없어도 1초마다 살아있다고 알린다.
                if (Stopwatch.GetElapsedTime(lastSent) >= VideoProtocol.KeepAliveInterval)
                {
                    stream.Write(keepAlive, 0, keepAlive.Length);
                    lastSent = Stopwatch.GetTimestamp();
                }

                // 초당 MaxFramesPerSecond 장을 넘지 않게 쉰다. (그만두면 바로 깬다)
                // 화면이 한동안 가만히 있었으면 IdleFramesPerSecond 로 낮춘다.
                int fps = unchangedFrames >= IdleAfterFrames
                    ? Math.Clamp(IdleFramesPerSecond, 1, 60)
                    : Math.Clamp(MaxFramesPerSecond, 1, 60);
                TimeSpan interval = TimeSpan.FromSeconds(1.0 / fps);
                TimeSpan remaining = interval - Stopwatch.GetElapsedTime(frameStart);

                if (remaining > TimeSpan.Zero)
                    cancellationToken.WaitHandle.WaitOne(remaining);
            }
        }
    }
}
