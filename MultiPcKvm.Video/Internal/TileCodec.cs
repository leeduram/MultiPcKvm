using MultiPcKvm.Common;
using System.Buffers.Binary;
using System.IO.Compression;

namespace MultiPcKvm.Video.Internal
{
    // ─────────────────────────────────────────────────────────────
    //  화면 압축 (1차): "바뀐 칸만 + Deflate"
    //
    //  화면을 64×64 픽셀 칸으로 나눈다. 직전 화면과 비교해서 바뀐 칸만 모아
    //  Deflate(zip 과 같은 방식)로 압축해 보낸다.
    //
    //    · 가만히 있는 화면     → 보낼 것이 없다 (0 바이트)
    //    · 글자 몇 개 입력      → 칸 몇 개 (수 KB)
    //    · 창을 끌거나 동영상   → 칸이 많아진다 (수백 KB ~ 수 MB)
    //
    //  무손실이라 글자가 뭉개지지 않는다. 대신 동영상처럼 전체가 계속 바뀌면 크다.
    //  (그런 경우를 위해 다음 단계에서 H.264 로 바꿀 예정. 이 파일만 갈아 끼우면 된다)
    //
    //  압축 전 데이터 모양 (리틀 엔디언):
    //    [칸 개수 4]
    //    칸마다: [x 2][y 2][가로 2][세로 2][픽셀: 가로×4 바이트 × 세로 줄]
    //
    //  픽셀은 BGRA 순서 4바이트. 네 번째 바이트는 WPF 가 Bgr32 로 그려서 보지 않는다.
    //  (예전에는 255 로 채웠는데, Common 약속이 Bgr32 로 바뀌어 필요 없어졌다)
    // ─────────────────────────────────────────────────────────────
    internal static class TileCodec
    {
        public const int TileSize = 64;

        // 너무 큰 값은 잘못된 데이터로 본다.
        public const int MaxDimension = 16384;
    }


    // 보내는 쪽
    internal sealed class TileEncoder
    {
        // 직전에 보낸 화면. 바뀐 칸만 여기에 덮어쓴다. (매 장 통째 복사하지 않음)
        private byte[] _previous = Array.Empty<byte>();
        private int _previousWidth;
        private int _previousHeight;
        private bool _hasPrevious;

        private readonly MemoryStream _raw = new MemoryStream();
        private readonly MemoryStream _compressed = new MemoryStream();

        // 다음 화면은 바뀐 칸만이 아니라 전체를 보낸다.
        public void ForceFullFrame() => _hasPrevious = false;

        /// <summary>
        /// 직전 화면과 비교해 바뀐 칸을 압축한다. 바뀐 것이 없으면 null.
        ///
        /// current 는 캡처 메모리를 그대로 가리킨다. (GdiScreenCapture.Pixels)
        /// 칸마다 직전 화면과 비교하고, 바뀐 칸만 꺼내서
        ///   1) 보낼 데이터에 쓰고  2) 직전 화면에도 덮어쓴다.
        /// 그래서 화면이 가만히 있으면 비교만 하고 복사는 하지 않는다.
        /// </summary>
        public EncodedFrame? Encode(ReadOnlySpan<byte> current, int width, int height)
        {
            int stride = width * 4;
            int length = stride * height;

            if (current.Length < length)
                return null;

            bool full =
                !_hasPrevious ||
                width != _previousWidth ||
                height != _previousHeight ||
                _previous.Length != length;

            if (full)
            {
                // 해상도가 바뀌었거나 첫 장: 직전 화면을 통째로 맞춰 둔다.
                if (_previous.Length != length)
                    _previous = new byte[length];

                current.Slice(0, length).CopyTo(_previous);
            }

            _raw.SetLength(0);

            Span<byte> header = stackalloc byte[8];
            _raw.Write(header[..4]);    // 칸 개수 자리. 마지막에 채운다.

            int tileCount = 0;

            for (int ty = 0; ty < height; ty += TileCodec.TileSize)
            {
                int tileHeight = Math.Min(TileCodec.TileSize, height - ty);

                for (int tx = 0; tx < width; tx += TileCodec.TileSize)
                {
                    int tileWidth = Math.Min(TileCodec.TileSize, width - tx);
                    int rowBytes = tileWidth * 4;

                    if (!full && !TileChanged(current, _previous, stride, tx, ty, rowBytes, tileHeight))
                        continue;

                    BinaryPrimitives.WriteUInt16LittleEndian(header[0..], (ushort)tx);
                    BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)ty);
                    BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)tileWidth);
                    BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)tileHeight);
                    _raw.Write(header);

                    for (int row = 0; row < tileHeight; row++)
                    {
                        int offset = (ty + row) * stride + tx * 4;
                        ReadOnlySpan<byte> pixels = current.Slice(offset, rowBytes);

                        // 보낼 데이터에 쓰기
                        // (네 번째 바이트는 WPF 가 Bgr32 로 그려서 보지 않는다. 그대로 보낸다)
                        _raw.Write(pixels);

                        // 직전 화면 갱신 (첫 장은 위에서 통째로 맞췄다)
                        if (!full)
                            pixels.CopyTo(_previous.AsSpan(offset, rowBytes));
                    }

                    tileCount++;
                }
            }

            _previousWidth = width;
            _previousHeight = height;
            _hasPrevious = true;

            if (tileCount == 0)
                return null;

            byte[] raw = _raw.GetBuffer();
            BinaryPrimitives.WriteInt32LittleEndian(raw, tileCount);

            int rawLength = (int)_raw.Length;

            _compressed.SetLength(0);

            using (var deflate = new DeflateStream(_compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                deflate.Write(raw, 0, rawLength);
            }

            return new EncodedFrame(
                width,
                height,
                rawLength,
                _compressed.GetBuffer(),
                (int)_compressed.Length,
                tileCount);
        }

        private static bool TileChanged(
            ReadOnlySpan<byte> current,
            byte[] previous,
            int stride,
            int tx,
            int ty,
            int rowBytes,
            int tileHeight)
        {
            for (int row = 0; row < tileHeight; row++)
            {
                int offset = (ty + row) * stride + tx * 4;

                if (!current.Slice(offset, rowBytes).SequenceEqual(previous.AsSpan(offset, rowBytes)))
                    return true;
            }

            return false;
        }
    }


    // 압축된 화면 한 장. Data 는 Length 까지만 유효하다. (내부 버퍼를 다시 쓰므로 바로 보내야 한다)
    internal readonly record struct EncodedFrame(
        int Width,
        int Height,
        int RawLength,
        byte[] Data,
        int Length,
        int TileCount);


    // 받는 쪽
    internal sealed class TileDecoder
    {
        private byte[] _inflated = Array.Empty<byte>();

        public byte[] Frame { get; private set; } = Array.Empty<byte>();

        // 이번에 덮어쓴 곳들. null 이면 전체가 바뀐 것. (VideoFrame.DirtyRects 로 그대로 넘긴다)
        //
        // null 이 되는 경우
        //   · 해상도가 바뀐 첫 장
        //   · 바뀐 칸이 화면의 절반을 넘을 때 (그때는 WPF 가 통째로 복사하는 편이 빠르다)
        public IReadOnlyList<FrameRect>? DirtyRects { get; private set; }

        private readonly List<FrameRect> _dirty = new List<FrameRect>();

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Stride => Width * 4;

        /// <summary>
        /// 압축된 칸들을 풀어서 Frame 에 덮어 그린다.
        /// 잘못된 데이터면 InvalidDataException.
        /// </summary>
        public void Apply(int width, int height, int rawLength, byte[] compressed, int compressedLength)
        {
            if (width <= 0 || height <= 0 ||
                width > TileCodec.MaxDimension || height > TileCodec.MaxDimension)
            {
                throw new InvalidDataException($"화면 크기가 올바르지 않습니다 ({width}×{height})");
            }

            long maxRaw = 4L + (long)width * height * 4 + (long)(width / TileCodec.TileSize + 1) * (height / TileCodec.TileSize + 1) * 8;

            if (rawLength < 4 || rawLength > maxRaw)
                throw new InvalidDataException("압축 풀기 크기가 올바르지 않습니다");

            // 해상도가 바뀌었으면 새 화면 (첫 장은 전체 칸이 온다)
            bool resized = width != Width || height != Height;

            if (resized)
            {
                Width = width;
                Height = height;
                Frame = new byte[width * height * 4];
            }

            _dirty.Clear();
            long dirtyArea = 0;

            if (_inflated.Length < rawLength)
                _inflated = new byte[rawLength];

            using (var input = new MemoryStream(compressed, 0, compressedLength, writable: false))
            using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                inflate.ReadExactly(_inflated, 0, rawLength);
            }

            ReadOnlySpan<byte> raw = _inflated.AsSpan(0, rawLength);

            int tileCount = BinaryPrimitives.ReadInt32LittleEndian(raw);
            int position = 4;
            int stride = Stride;

            for (int t = 0; t < tileCount; t++)
            {
                if (position + 8 > raw.Length)
                    throw new InvalidDataException("칸 정보가 잘렸습니다");

                int tx = BinaryPrimitives.ReadUInt16LittleEndian(raw[position..]);
                int ty = BinaryPrimitives.ReadUInt16LittleEndian(raw[(position + 2)..]);
                int tw = BinaryPrimitives.ReadUInt16LittleEndian(raw[(position + 4)..]);
                int th = BinaryPrimitives.ReadUInt16LittleEndian(raw[(position + 6)..]);
                position += 8;

                if (tw <= 0 || th <= 0 || tx + tw > width || ty + th > height)
                    throw new InvalidDataException("칸 위치가 화면 밖입니다");

                int rowBytes = tw * 4;

                if (position + rowBytes * th > raw.Length)
                    throw new InvalidDataException("칸 픽셀이 잘렸습니다");

                for (int row = 0; row < th; row++)
                {
                    raw.Slice(position, rowBytes)
                       .CopyTo(Frame.AsSpan((ty + row) * stride + tx * 4, rowBytes));

                    position += rowBytes;
                }

                AddDirty(tx, ty, tw, th);
                dirtyArea += (long)tw * th;
            }

            // 첫 장이거나 절반 넘게 바뀌었으면 "전체" 로 알린다.
            DirtyRects =
                resized || dirtyArea * 2 > (long)width * height
                    ? null
                    : _dirty;
        }

        // 바뀐 칸을 목록에 넣는다.
        // 칸은 왼쪽→오른쪽, 위→아래 순서로 오므로, 바로 왼쪽 칸과 같은 줄이면 하나로 합친다.
        // (WPF 가 다시 그릴 네모 개수가 줄어든다)
        private void AddDirty(int x, int y, int width, int height)
        {
            if (_dirty.Count > 0)
            {
                FrameRect last = _dirty[^1];

                if (last.Y == y && last.Height == height && last.X + last.Width == x)
                {
                    _dirty[^1] = last with { Width = last.Width + width };
                    return;
                }
            }

            _dirty.Add(new FrameRect(x, y, width, height));
        }
    }
}
