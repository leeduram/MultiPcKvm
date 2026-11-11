using System.Buffers.Binary;
using System.Net.Sockets;
using MultiPcKvm.Common;
using MultiPcKvm.Video.Internal;

namespace MultiPcKvm.Video
{
    // ─────────────────────────────────────────────────────────────
    //  조종하는 쪽(A): 상대 화면 받기  (IScreenViewer, ScreenShare.cs 약속)
    //
    //  StartAsync(B 의 IP)
    //    → 45002 로 연결 → 인사 → B 가 허락하면 true
    //    → 이후 화면이 올 때마다 FrameReceived(VideoFrame, BGRA 32비트)
    //  Stop() 하면 줄을 끊는다. B 는 그걸 보고 보내기를 멈춘다.
    //
    //  FrameReceived 로 넘기는 배열은 계속 다시 쓴다. (약속: 이벤트 안에서 WPF 가 복사를 끝낸다)
    //  DirtyRects 에 이번에 바뀐 칸들을 넣어 준다. (목록도 다음 장에 다시 쓴다)
    //  B 가 거절했거나, 끊겼거나, 6초 넘게 아무것도 안 오면 Stopped(이유) 를 한 번 부르고 끝낸다.
    //  Stop() 으로 직접 멈춘 경우에는 Stopped 를 부르지 않는다.
    // ─────────────────────────────────────────────────────────────
    public sealed class ScreenViewer : IScreenViewer
    {
        private readonly object _lock = new object();

        private TcpClient? _client;
        private CancellationTokenSource? _cts;

        // Stopped 를 이미 불렀는가 (0/1)
        private int _stoppedRaised;

        // Stop() 으로 직접 멈췄는가
        private volatile bool _stopRequested;

        public event Action<VideoFrame>? FrameReceived;
        public event Action<string>? Stopped;


        // ── 시작 ─────────────────────────────────────────────

        public async Task<bool> StartAsync(string ip)
        {
            // 이미 받고 있었다면 정리하고 새로 시작한다.
            Stop();

            var cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            TcpClient? client = null;

            lock (_lock)
            {
                _cts = cts;
                _stopRequested = false;
                _stoppedRaised = 0;
            }

            try
            {
                client = await NetIo.ConnectAsync(
                    ip,
                    Constants.VideoPort,
                    TimeSpan.FromSeconds(5),
                    token);

                NetworkStream stream = client.GetStream();

                // 인사
                byte[] hello = new byte[5];
                VideoProtocol.Magic.CopyTo(hello, 0);
                hello[4] = VideoProtocol.Version;

                await stream.WriteAsync(hello, token);

                // 허락 기다리기 (10초)
                (bool ok, string reason) result;

                using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    waitCts.CancelAfter(TimeSpan.FromSeconds(10));

                    try
                    {
                        result = await NetIo.ReadStatusAsync(stream, waitCts.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new TimeoutException("화면 보기 허락 응답이 없습니다");
                    }
                }

                if (!result.ok)
                {
                    client.Dispose();

                    RaiseStopped(string.IsNullOrEmpty(result.reason)
                        ? "상대가 화면 보기를 거절했습니다."
                        : result.reason);

                    return false;
                }

                lock (_lock)
                {
                    // 기다리는 사이에 Stop() 이 불렸으면 그만둔다.
                    if (_stopRequested || _cts != cts)
                    {
                        client.Dispose();
                        return false;
                    }

                    _client = client;
                }

                Console.WriteLine($"화면 받기 시작: {ip}");

                TcpClient connected = client;

                _ = Task.Run(() => ReceiveLoopAsync(connected, stream, token));

                return true;
            }
            catch (Exception) when (_stopRequested)
            {
                // 기다리는 사이에 Stop() 이 불렸다. (Stopped 는 부르지 않는다)
                client?.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                client?.Dispose();

                RaiseStopped("화면 보기 연결 실패: " + NetIo.Describe(ex));

                return false;
            }
        }


        // ── 받기 ─────────────────────────────────────────────

        private async Task ReceiveLoopAsync(
            TcpClient client,
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var decoder = new TileDecoder();

            byte[] type = new byte[1];
            byte[] header = new byte[16];
            byte[] data = new byte[64 * 1024];

            // 읽을 때마다 "6초 안에 와야 함" 시계를 다시 맞춘다.
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            string reason = "화면 보기가 끝났습니다.";

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    readCts.CancelAfter(VideoProtocol.ReceiveTimeout);

                    await stream.ReadExactlyAsync(type, readCts.Token);

                    if (type[0] == VideoProtocol.TypeKeepAlive)
                        continue;

                    if (type[0] != VideoProtocol.TypeFrame)
                        throw new InvalidDataException($"모르는 메시지 종류 {type[0]}");

                    await stream.ReadExactlyAsync(header, readCts.Token);

                    int width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0));
                    int height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                    int rawLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));

                    if (length <= 0 || length > VideoProtocol.MaxCompressedLength)
                        throw new InvalidDataException($"화면 데이터 크기가 올바르지 않습니다 ({length})");

                    if (data.Length < length)
                        data = new byte[Math.Max(length, data.Length * 2)];

                    await stream.ReadExactlyAsync(data.AsMemory(0, length), readCts.Token);

                    decoder.Apply(width, height, rawLength, data, length);

                    // Stop 이후에는 화면을 넘기지 않는다.
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        // 바뀐 칸 목록도 같이 넘긴다. (WPF 는 그 부분만 복사·다시 그림)
                        // 첫 장이나 절반 넘게 바뀐 장은 null = 전체.
                        FrameReceived?.Invoke(
                            new VideoFrame(
                                decoder.Width,
                                decoder.Height,
                                decoder.Stride,
                                decoder.Frame,
                                decoder.DirtyRects));
                    }
                    catch (Exception ex)
                    {
                        // 그리는 쪽 오류로 받기가 멈추지 않게 한다.
                        Console.WriteLine($"화면 그리기 오류: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                reason = "상대 화면이 6초 넘게 오지 않아 끊었습니다.";
            }
            catch (OperationCanceledException)
            {
                // Stop()
            }
            catch (Exception ex)
            {
                reason = "화면 보기가 끊겼습니다: " + NetIo.Describe(ex);
            }
            finally
            {
                try { client.Dispose(); } catch { }

                lock (_lock)
                {
                    if (_client == client)
                        _client = null;
                }
            }

            if (!_stopRequested)
            {
                Console.WriteLine($"화면 받기 끝: {reason}");
                RaiseStopped(reason);
            }
        }


        // ── 멈추기 ───────────────────────────────────────────

        public void Stop()
        {
            TcpClient? client;
            CancellationTokenSource? cts;

            lock (_lock)
            {
                _stopRequested = true;

                client = _client;
                cts = _cts;

                _client = null;
                _cts = null;
            }

            try { cts?.Cancel(); } catch { }

            // 줄을 끊으면 상대(B)가 보내기를 멈춘다.
            try { client?.Dispose(); } catch { }

            cts?.Dispose();
        }

        public void Dispose() => Stop();


        // Stopped 는 한 번만 부른다.
        private void RaiseStopped(string reason)
        {
            if (Interlocked.Exchange(ref _stoppedRaised, 1) != 0)
                return;

            try
            {
                Stopped?.Invoke(reason);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Stopped 처리 오류: {ex.Message}");
            }
        }
    }
}
