using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MultiPcKvm.Common;
using MultiPcKvm.Video.Internal;

namespace MultiPcKvm.Video
{
    // ─────────────────────────────────────────────────────────────
    //  파일 하나 보내기 / 받기  (IFileTransfer, FileTransfer.cs 약속)
    //
    //  양쪽 PC 에 하나씩 있고, 보내기와 받기를 둘 다 한다. 포트 45003 (TCP).
    //
    //  ─── 줄 위에서 주고받는 모양 (리틀 엔디언) ───
    //
    //    보내는 쪽 → 받는 쪽 : [M K F T][버전 1][파일 이름 (길이 2 + UTF-8)][크기 8]
    //    받는 쪽 → 보내는 쪽 : [1 받음 / 0 거절][이유]
    //        · CanTransfer(보낸 쪽 IP) 가 false → 거절 (파일 이름도 읽기 전에 끊는다)
    //        · ChooseSavePath 가 null → 거절
    //    보내는 쪽 → 받는 쪽 : [파일 내용 크기만큼][SHA-256 32]
    //    받는 쪽 → 보내는 쪽 : [1 저장함 / 0 실패][이유]
    //
    //  ─── 받는 쪽이 지키는 것 ───
    //    · 임시 파일(경로.xxxxxxxx.part)에 받다가, 다 받고 SHA-256 이 맞으면 진짜 이름으로 바꾼다.
    //    · 실패·취소·끊김이면 임시 파일을 지우고 Failed(파일 이름, 이유).
    //    · ChooseSavePath 가 준 경로에만 저장한다. (보낸 쪽 이름은 그대로 넘기고, 거르는 건 WPF 가 함)
    //
    //  ─── 보내는 쪽 ───
    //    · 64KB 씩 나눠 보낸다. (큰 파일도 메모리에 통째로 올리지 않는다)
    //    · 받는 쪽이 저장까지 끝냈다고 답해야 true.
    //    · 취소 토큰이 취소되면 줄을 끊고 OperationCanceledException.
    //
    //  ─── 끊김 감지 ───
    //    읽기·쓰기가 30초 넘게 멈추면 끊긴 것으로 본다. (상대 PC 가 갑자기 꺼진 경우 등)
    // ─────────────────────────────────────────────────────────────
    public sealed class FileTransferEngine : IFileTransfer
    {
        private static readonly byte[] Magic = { (byte)'M', (byte)'K', (byte)'F', (byte)'T' };
        private const byte Version = 1;

        // 한 번에 읽고 보내는 조각 크기. (64KB 는 유선 1Gbps 에서 왕복이 너무 잦았다)
        private const int ChunkSize = 1024 * 1024;

        // 소켓 버퍼. 크면 상대 응답을 기다리지 않고 더 많이 흘려보낼 수 있다.
        private const int SocketBufferSize = 4 * 1024 * 1024;
        private const int MaxFileNameBytes = 1024;

        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DecisionTimeout = TimeSpan.FromSeconds(60);

        // 진행 알림 간격
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

        private readonly object _lock = new object();

        private TcpListener? _listener;
        private CancellationTokenSource? _listenCts;

        public Func<string, bool>? CanTransfer { get; set; }
        public Func<IncomingFile, string?>? ChooseSavePath { get; set; }

        public event Action<IncomingFile, string>? FileReceived;
        public event Action<FileProgress>? Progress;
        public event Action<string, string>? Failed;


        // ═════════════════════════════════════════════════════
        //  받기
        // ═════════════════════════════════════════════════════

        public void StartListening()
        {
            lock (_lock)
            {
                if (_listener != null)
                    return;

                var listener = new TcpListener(IPAddress.Any, Constants.FileTransferPort);

                // 받는 쪽 버퍼는 연결 전에 정해야 큰 창(window scaling)이 잡힌다.
                // 대기 소켓에 정해 두면 받아들인 연결이 물려받는다.
                listener.Server.ReceiveBufferSize = SocketBufferSize;

                // 실패하면 예외를 그대로 던진다.
                listener.Start();

                _listener = listener;
                _listenCts = new CancellationTokenSource();

                _ = AcceptLoopAsync(listener, _listenCts.Token);
            }

            Console.WriteLine($"파일 받기 대기 시작: {Constants.FileTransferPort}");
        }

        public void StopListening()
        {
            TcpListener? listener;
            CancellationTokenSource? cts;

            lock (_lock)
            {
                listener = _listener;
                cts = _listenCts;

                _listener = null;
                _listenCts = null;
            }

            // 받던 파일들도 이 토큰으로 멈춘다. (임시 파일은 각자 지운다)
            try { cts?.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }

            cts?.Dispose();
        }

        public void Dispose() => StopListening();

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

                    Console.WriteLine($"파일 받기 접속 오류: {ex.Message}");
                    await Task.Delay(200, CancellationToken.None);
                    continue;
                }

                // 동시에 여러 개 와도 된다. 하나씩 따로 받는다.
                _ = Task.Run(() => ReceiveOneAsync(client, cancellationToken));
            }
        }

        private async Task ReceiveOneAsync(TcpClient client, CancellationToken listenToken)
        {
            string fromIp = NetIo.RemoteIp(client);
            string fileName = "";
            string? tempPath = null;
            bool done = false;

            using (client)
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(listenToken))
            using (cts.Token.Register(() => { try { client.Close(); } catch { } }))
            {
                try
                {
                    client.NoDelay = true;
                    client.ReceiveBufferSize = SocketBufferSize;
                    NetworkStream stream = client.GetStream();

                    // ── 1) 허락 확인 (데이터를 받기 전에) ──
                    if (!Ask(() => CanTransfer?.Invoke(fromIp) ?? false))
                    {
                        Console.WriteLine($"파일 받기 거절 (허락 안 된 상대): {fromIp}");

                        await NetIo.WriteStatusAsync(
                            stream,
                            ok: false,
                            "상대 PC 가 이 PC 와 연결된 상태가 아니라서 받지 않습니다.",
                            cts.Token);

                        return;
                    }

                    // ── 2) 파일 정보 ──
                    cts.CancelAfter(IdleTimeout);

                    byte[] scratch = new byte[8];
                    byte[] magic = new byte[5];
                    await stream.ReadExactlyAsync(magic, cts.Token);

                    if (!magic.AsSpan(0, 4).SequenceEqual(Magic) || magic[4] != Version)
                    {
                        Console.WriteLine($"파일 받기 무시 (모르는 형식): {fromIp}");
                        return;
                    }

                    fileName = await NetIo.ReadStringAsync(stream, MaxFileNameBytes, cts.Token);
                    long size = await NetIo.ReadInt64Async(stream, scratch, cts.Token);

                    if (size < 0)
                        throw new InvalidDataException("파일 크기가 올바르지 않습니다");

                    var incoming = new IncomingFile(fromIp, fileName, size);

                    // ── 3) 저장 위치 ──
                    string? savePath = null;
                    Ask(() => { savePath = ChooseSavePath?.Invoke(incoming); return true; });

                    if (string.IsNullOrWhiteSpace(savePath))
                    {
                        Console.WriteLine($"파일 받기 거절 (저장 위치 없음): {fileName}");

                        await NetIo.WriteStatusAsync(stream, ok: false, "받는 쪽이 저장을 거절했습니다.", cts.Token);
                        return;
                    }

                    string finalPath = savePath;

                    string? folder = Path.GetDirectoryName(finalPath);
                    if (!string.IsNullOrEmpty(folder))
                        Directory.CreateDirectory(folder);

                    tempPath = finalPath + "." + Guid.NewGuid().ToString("N")[..8] + ".part";

                    await NetIo.WriteStatusAsync(stream, ok: true, string.Empty, cts.Token);

                    // ── 4) 내용 받기 ──
                    //
                    // 버퍼 두 개를 번갈아 쓴다.
                    // 하나를 디스크에 쓰는 동안 다른 하나로 네트워크에서 다음 조각을 받는다.
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[ChunkSize];
                    byte[] spare = new byte[ChunkSize];
                    Task pendingWrite = Task.CompletedTask;
                    long received = 0;
                    var throttle = new ProgressThrottle(size);

                    ReportProgress(fileName, 0, size, sending: false);

                    await using (var file = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        ChunkSize,
                        useAsync: true))
                    {
                        try
                        {
                            while (received < size)
                            {
                                int want = (int)Math.Min(buffer.Length, size - received);

                                // 조각을 꽉 채워 받는다. (작게 여러 번 쓰지 않도록)
                                int count = 0;

                                while (count < want)
                                {
                                    cts.CancelAfter(IdleTimeout);
                                    int n = await stream.ReadAsync(buffer.AsMemory(count, want - count), cts.Token);

                                    if (n == 0)
                                        throw new EndOfStreamException();

                                    count += n;
                                }

                                // 앞 조각 디스크 쓰기가 끝나야 그 버퍼를 다시 쓸 수 있다.
                                await pendingWrite;

                                hash.AppendData(buffer, 0, count);
                                pendingWrite = file.WriteAsync(buffer.AsMemory(0, count), cts.Token).AsTask();

                                (buffer, spare) = (spare, buffer);
                                received += count;

                                if (throttle.ShouldReport(received))
                                    ReportProgress(fileName, received, size, sending: false);
                            }

                            await pendingWrite;
                        }
                        catch
                        {
                            // 쓰던 것이 남아 있으면 끝날 때까지 기다린 뒤 파일을 닫는다. (예외는 무시)
                            try { await pendingWrite; } catch { }
                            throw;
                        }

                        await file.FlushAsync(cts.Token);
                    }

                    // ── 5) 확인값 비교 ──
                    byte[] expected = new byte[32];
                    cts.CancelAfter(IdleTimeout);
                    await stream.ReadExactlyAsync(expected, cts.Token);

                    if (!hash.GetHashAndReset().AsSpan().SequenceEqual(expected))
                        throw new InvalidDataException("받은 내용이 보낸 내용과 다릅니다 (확인값 불일치)");

                    // ── 6) 진짜 이름으로 ──
                    File.Move(tempPath, finalPath, overwrite: false);
                    tempPath = null;
                    done = true;

                    ReportProgress(fileName, size, size, sending: false);

                    try
                    {
                        await NetIo.WriteStatusAsync(stream, ok: true, string.Empty, cts.Token);
                    }
                    catch
                    {
                        // 저장은 끝났다. 답을 못 보내도 받은 파일은 그대로 둔다.
                    }

                    Console.WriteLine($"파일 받음: {fileName} → {finalPath}");

                    Raise(() => FileReceived?.Invoke(incoming, finalPath));
                }
                catch (Exception ex)
                {
                    // 시간 초과로 cts 가 취소되면 줄이 닫혀서 IOException 으로 오기도 한다.
                    string reason =
                        listenToken.IsCancellationRequested ? "프로그램이 받기를 멈췄습니다."
                        : cts.IsCancellationRequested ? "상대가 30초 넘게 응답하지 않습니다."
                        : NetIo.Describe(ex);

                    if (!done && fileName.Length > 0)
                    {
                        // 상대에게도 실패를 알려 본다. (끊긴 줄이면 실패해도 괜찮다)
                        try
                        {
                            await NetIo.WriteStatusAsync(
                                client.GetStream(), ok: false, reason, CancellationToken.None)
                                .WaitAsync(TimeSpan.FromSeconds(2));
                        }
                        catch
                        {
                        }

                        Console.WriteLine($"파일 받기 실패: {fileName} ({reason})");
                        Raise(() => Failed?.Invoke(fileName, reason));
                    }
                }
                finally
                {
                    // 덜 받은 파일은 지운다.
                    if (tempPath != null)
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }
            }
        }


        // ═════════════════════════════════════════════════════
        //  보내기
        // ═════════════════════════════════════════════════════

        public async Task<bool> SendFileAsync(string ip, string localPath, CancellationToken cancellationToken)
        {
            string fileName = Path.GetFileName(localPath);

            FileStream file;

            try
            {
                file = new FileStream(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    // 다른 프로그램이 쓰기로 열어 둔 파일(열린 엑셀, 로그 파일 등)도 보낼 수 있게 한다.
                    // 보내는 도중 파일이 짧아지면 아래 읽기에서 "파일이 짧아졌습니다" 로 실패 처리된다.
                    FileShare.ReadWrite | FileShare.Delete,
                    ChunkSize,
                    useAsync: true);
            }
            catch (Exception ex)
            {
                RaiseFailed(fileName, "파일을 열 수 없습니다: " + NetIo.Describe(ex));
                return false;
            }

            await using (file)
            {
                long size = file.Length;

                TcpClient client;

                try
                {
                    client = await NetIo.ConnectAsync(
                        ip,
                        Constants.FileTransferPort,
                        TimeSpan.FromSeconds(5),
                        cancellationToken,
                        SocketBufferSize);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RaiseFailed(fileName, "상대 PC 에 연결하지 못했습니다: " + NetIo.Describe(ex));
                    return false;
                }

                using (client)
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                using (cts.Token.Register(() => { try { client.Close(); } catch { } }))
                {
                    try
                    {
                        NetworkStream stream = client.GetStream();

                        // ── 1) 파일 정보 ──
                        byte[] name = NetIo.EncodeString(fileName);
                        byte[] header = new byte[5 + name.Length + 8];

                        Magic.CopyTo(header, 0);
                        header[4] = Version;
                        Buffer.BlockCopy(name, 0, header, 5, name.Length);
                        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(5 + name.Length), size);

                        cts.CancelAfter(IdleTimeout);
                        await stream.WriteAsync(header, cts.Token);

                        // ── 2) 받을지 답 기다리기 ──
                        // (거절이면 이름을 받기 전에 바로 답이 온다. 저장 위치 묻기에 시간이 걸릴 수 있어 넉넉히)
                        cts.CancelAfter(DecisionTimeout);
                        var (accepted, reason) = await NetIo.ReadStatusAsync(stream, cts.Token);

                        if (!accepted)
                        {
                            RaiseFailed(fileName, string.IsNullOrEmpty(reason) ? "상대가 받지 않았습니다." : reason);
                            return false;
                        }

                        // ── 3) 내용 보내기 ──
                        //
                        // 버퍼 두 개를 번갈아 쓴다.
                        // 지금 조각을 네트워크로 보내는 동안 다음 조각을 디스크에서 미리 읽는다.
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        byte[] buffer = new byte[ChunkSize];
                        byte[] spare = new byte[ChunkSize];
                        long sent = 0;
                        var throttle = new ProgressThrottle(size);

                        ReportProgress(fileName, 0, size, sending: true);

                        Task<int> pendingRead = size > 0
                            ? ReadChunkAsync(file, buffer, size, cts.Token)
                            : Task.FromResult(0);

                        try
                        {
                            while (sent < size)
                            {
                                int count = await pendingRead;

                                if (count == 0)
                                    throw new IOException("보내는 도중 파일이 짧아졌습니다 (다른 프로그램이 파일을 바꿈)");

                                byte[] current = buffer;
                                (buffer, spare) = (spare, buffer);

                                // 다음 조각 미리 읽기 시작
                                long remaining = size - sent - count;
                                pendingRead = remaining > 0
                                    ? ReadChunkAsync(file, buffer, remaining, cts.Token)
                                    : Task.FromResult(0);

                                hash.AppendData(current, 0, count);

                                cts.CancelAfter(IdleTimeout);
                                await stream.WriteAsync(current.AsMemory(0, count), cts.Token);

                                sent += count;

                                if (throttle.ShouldReport(sent))
                                    ReportProgress(fileName, sent, size, sending: true);
                            }
                        }
                        catch
                        {
                            // 미리 읽던 것이 끝날 때까지 기다린 뒤 파일을 닫는다. (예외는 무시)
                            try { await pendingRead; } catch { }
                            throw;
                        }

                        cts.CancelAfter(IdleTimeout);
                        await stream.WriteAsync(hash.GetHashAndReset(), cts.Token);
                        await stream.FlushAsync(cts.Token);

                        // ── 4) 저장까지 끝났는지 ──
                        cts.CancelAfter(DecisionTimeout);
                        var (saved, failReason) = await NetIo.ReadStatusAsync(stream, cts.Token);

                        if (!saved)
                        {
                            RaiseFailed(fileName, string.IsNullOrEmpty(failReason) ? "상대가 저장하지 못했습니다." : failReason);
                            return false;
                        }

                        ReportProgress(fileName, size, size, sending: true);

                        Console.WriteLine($"파일 보냄: {fileName} → {ip}");

                        return true;
                    }
                    catch (Exception) when (cancellationToken.IsCancellationRequested)
                    {
                        // 취소 (연결이 끊겨서 WPF 가 취소한 경우 포함)
                        throw new OperationCanceledException(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        // 시간 초과로 cts 가 취소되면 줄이 닫혀서 IOException 으로 오기도 한다.
                        string reason = cts.IsCancellationRequested
                            ? "상대가 응답하지 않습니다 (시간 초과)."
                            : NetIo.Describe(ex);

                        RaiseFailed(fileName, reason);
                        return false;
                    }
                }
            }
        }


        // ═════════════════════════════════════════════════════
        //  공통
        // ═════════════════════════════════════════════════════

        // 파일에서 조각 하나를 꽉 채워 읽는다. (끝에 닿으면 남은 만큼)
        private static async Task<int> ReadChunkAsync(
            FileStream file,
            byte[] buffer,
            long remaining,
            CancellationToken cancellationToken)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int count = 0;

            while (count < want)
            {
                int n = await file.ReadAsync(buffer.AsMemory(count, want - count), cancellationToken);

                if (n == 0)
                    break;

                count += n;
            }

            return count;
        }

        // WPF 함수(CanTransfer / ChooseSavePath)를 부른다. 예외가 나면 거절로 본다.
        private static bool Ask(Func<bool> call)
        {
            try
            {
                return call();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"파일 전송 확인 함수 오류: {ex.Message}");
                return false;
            }
        }

        private void ReportProgress(string fileName, long done, long total, bool sending)
        {
            Raise(() => Progress?.Invoke(new FileProgress(fileName, done, total, sending)));
        }

        private void RaiseFailed(string fileName, string reason)
        {
            Console.WriteLine($"파일 보내기 실패: {fileName} ({reason})");
            Raise(() => Failed?.Invoke(fileName, reason));
        }

        // 이벤트 처리 쪽 오류로 전송이 멈추지 않게 한다.
        private static void Raise(Action raise)
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"파일 전송 이벤트 처리 오류: {ex.Message}");
            }
        }

        // 1% 이상 늘었거나 0.2초가 지났을 때만 알린다.
        private sealed class ProgressThrottle
        {
            private readonly long _total;
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            private long _lastDone;

            public ProgressThrottle(long total)
            {
                _total = total;
            }

            public bool ShouldReport(long done)
            {
                bool percent = _total > 0 && (done - _lastDone) * 100 >= _total;
                bool time = _watch.Elapsed >= ProgressInterval;

                if (!percent && !time)
                    return false;

                _lastDone = done;
                _watch.Restart();
                return true;
            }
        }
    }
}
