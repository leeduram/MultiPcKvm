using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MultiPcKvm.Network.Discovery;

namespace MultiPcKvm.Video.Internal
{
    // ─────────────────────────────────────────────────────────────
    //  영상·파일 전송이 같이 쓰는 네트워크 도우미.
    //
    //  모든 숫자는 리틀 엔디언. (InputMessage 와 같은 규칙)
    //  글자는 [길이 2바이트][UTF-8] 로 보낸다.
    // ─────────────────────────────────────────────────────────────
    internal static class NetIo
    {
        // 상대에게 TCP 로 연결한다.
        //
        // 169.254 어댑터가 여럿인 경우(랜선 직결 + Npcap 등)
        // 검색 때 상대가 들어온 어댑터로 나가도록 LocalRoutes 로 내 주소를 지정한다.
        // 그 외에는 Windows 가 고른다.
        public static async Task<TcpClient> ConnectAsync(
            string ip,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            int socketBufferSize = 0)
        {
            IPAddress.TryParse(ip, out IPAddress? remote);

            IPAddress? local =
                LocalRoutes.ResolveLocalAddress(remote);

            TcpClient client = local != null
                ? new TcpClient(new IPEndPoint(local, 0))
                : new TcpClient(AddressFamily.InterNetwork);

            client.NoDelay = true;

            // 큰 데이터를 보낼 때(파일 전송) 버퍼를 키운다. 연결 전에 정해야 효과가 있다.
            if (socketBufferSize > 0)
            {
                client.SendBufferSize = socketBufferSize;
                client.ReceiveBufferSize = socketBufferSize;
            }

            try
            {
                using var timeoutCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                timeoutCts.CancelAfter(timeout);

                try
                {
                    await client.ConnectAsync(ip, port, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"{ip}:{port} 연결 시간 초과");
                }

                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        // 정확히 buffer 만큼 읽는다. 상대가 중간에 끊으면 EndOfStreamException.
        public static async Task ReadExactAsync(
            Stream stream,
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
        }

        public static async Task<byte> ReadByteAsync(
            Stream stream,
            byte[] scratch,
            CancellationToken cancellationToken)
        {
            await stream.ReadExactlyAsync(scratch.AsMemory(0, 1), cancellationToken);
            return scratch[0];
        }

        public static async Task<int> ReadInt32Async(
            Stream stream,
            byte[] scratch,
            CancellationToken cancellationToken)
        {
            await stream.ReadExactlyAsync(scratch.AsMemory(0, 4), cancellationToken);
            return BinaryPrimitives.ReadInt32LittleEndian(scratch);
        }

        public static async Task<long> ReadInt64Async(
            Stream stream,
            byte[] scratch,
            CancellationToken cancellationToken)
        {
            await stream.ReadExactlyAsync(scratch.AsMemory(0, 8), cancellationToken);
            return BinaryPrimitives.ReadInt64LittleEndian(scratch);
        }

        // [길이 2바이트][UTF-8]. 길이가 maxBytes 를 넘으면 잘못된 데이터로 본다.
        public static async Task<string> ReadStringAsync(
            Stream stream,
            int maxBytes,
            CancellationToken cancellationToken)
        {
            byte[] lengthBytes = new byte[2];
            await stream.ReadExactlyAsync(lengthBytes, cancellationToken);

            int length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);

            if (length > maxBytes)
                throw new InvalidDataException($"글자 길이가 너무 깁니다 ({length} 바이트)");

            if (length == 0)
                return string.Empty;

            byte[] data = new byte[length];
            await stream.ReadExactlyAsync(data, cancellationToken);

            return Encoding.UTF8.GetString(data);
        }

        public static byte[] EncodeString(string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text ?? string.Empty);

            // 2바이트 길이에 들어가도록 자른다. (UTF-8 글자 중간에서 잘릴 수 있지만 표시용이라 괜찮다)
            int length = Math.Min(utf8.Length, ushort.MaxValue);

            byte[] result = new byte[2 + length];
            BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)length);
            Buffer.BlockCopy(utf8, 0, result, 2, length);

            return result;
        }

        // [상태 1바이트][이유 글자] 보내기. (1 = 좋음, 0 = 거절·실패)
        public static async Task WriteStatusAsync(
            Stream stream,
            bool ok,
            string reason,
            CancellationToken cancellationToken)
        {
            byte[] text = EncodeString(reason);
            byte[] packet = new byte[1 + text.Length];

            packet[0] = ok ? (byte)1 : (byte)0;
            Buffer.BlockCopy(text, 0, packet, 1, text.Length);

            await stream.WriteAsync(packet, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        public static async Task<(bool Ok, string Reason)> ReadStatusAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            byte[] scratch = new byte[1];
            byte status = await ReadByteAsync(stream, scratch, cancellationToken);
            string reason = await ReadStringAsync(stream, 4096, cancellationToken);

            return (status == 1, reason);
        }

        // 연결된 상대의 IP (IPv4 로 정리)
        public static string RemoteIp(TcpClient client)
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint endPoint)
                return string.Empty;

            IPAddress address = endPoint.Address.IsIPv4MappedToIPv6
                ? endPoint.Address.MapToIPv4()
                : endPoint.Address;

            return address.ToString();
        }

        // 예외를 사람이 읽을 이유로
        public static string Describe(Exception ex) => ex switch
        {
            TimeoutException => "응답이 없습니다 (시간 초과)",
            EndOfStreamException => "상대가 연결을 끊었습니다",
            SocketException se when se.SocketErrorCode == SocketError.ConnectionRefused
                => "상대가 받을 준비가 안 되어 있습니다 (프로그램이 꺼졌거나 방화벽)",
            SocketException se => $"네트워크 오류 ({se.SocketErrorCode})",
            IOException io when io.InnerException is SocketException se
                => $"네트워크 오류 ({se.SocketErrorCode})",
            UnauthorizedAccessException => "파일에 접근할 권한이 없습니다",
            InvalidDataException => "받은 데이터가 올바르지 않습니다",
            _ => ex.Message
        };
    }
}
