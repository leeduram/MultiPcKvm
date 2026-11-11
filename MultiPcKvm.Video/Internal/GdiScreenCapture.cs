using System.Runtime.InteropServices;

namespace MultiPcKvm.Video.Internal
{
    // ─────────────────────────────────────────────────────────────
    //  주 모니터 화면을 BGRA 32비트로 캡처한다. (GDI BitBlt)
    //
    //  왜 GDI 인가 (1차):
    //    Windows Graphics Capture 는 Direct3D 와 WinRT 연동이 필요해서
    //    외부 라이브러리 없이 만들기 어렵다. GDI 는 Win32 함수 몇 개로 끝나고,
    //    LAN 에서 초당 10~15장 정도는 충분히 나온다.
    //
    //  좌표·크기:
    //    WPF 프로그램이 Per-Monitor DPI 를 켜 두었으므로(app.manifest)
    //    여기서 얻는 크기는 실제 픽셀이다. (ScreenInfo 로 보내는 크기와 같다)
    //
    //  캡처한 화면은 Pixels(캡처 메모리 그대로)로 읽는다. byte[] 로 복사하지 않는다.
    //  (예전에는 매 장 8MB 를 복사했다. 이제 인코더가 바뀐 칸만 꺼내 간다)
    //
    //  마우스 커서는 찍히지 않는다.
    //    보는 쪽에서는 자기 커서가 창 위에 보이므로 오히려 커서가 두 개로 보이지 않아 좋다.
    //
    //  잠금 화면·UAC 확인 창(보안 데스크톱)은 Windows 가 캡처를 막는다.
    //    그동안은 Capture 가 false 를 돌려주고, 풀리면 다시 찍힌다.
    //
    //  스레드: 한 스레드에서만 쓴다. (ScreenSharer 의 보내기 루프)
    // ─────────────────────────────────────────────────────────────
    internal sealed class GdiScreenCapture : IDisposable
    {
        private IntPtr _screenDc;
        private IntPtr _memoryDc;
        private IntPtr _bitmap;
        private IntPtr _oldBitmap;
        private IntPtr _bits;

        public int Width { get; private set; }
        public int Height { get; private set; }

        // 한 줄 바이트 수 (32비트라 항상 Width × 4)
        public int Stride => Width * 4;

        /// <summary>
        /// 반투명 창(일부 툴팁·프로그램)까지 찍는 CAPTUREBLT 를 쓸지.
        ///
        /// 기본은 끔. CAPTUREBLT 는 느리고 마우스 커서가 깜빡이는 것으로 알려져 있다.
        /// Windows 8 이후는 화면 합성(DWM) 덕분에 없어도 대부분 그대로 찍힌다.
        /// 빠지는 창이 보이면 켠다.
        /// </summary>
        public bool IncludeLayeredWindows { get; set; }

        /// <summary>
        /// 마지막으로 캡처한 화면 (BGRA, 위에서 아래로, 줄 길이 = Stride).
        /// 다음 Capture 를 부르기 전까지만 유효하다. 같은 스레드에서만 읽는다.
        /// </summary>
        public unsafe ReadOnlySpan<byte> Pixels =>
            _bits == IntPtr.Zero
                ? ReadOnlySpan<byte>.Empty
                : new ReadOnlySpan<byte>((void*)_bits, Stride * Height);

        // 화면 한 장을 찍는다. 결과는 Pixels 로 읽는다.
        // 캡처할 수 없으면(보안 데스크톱 등) false.
        public bool Capture()
        {
            int width = GetSystemMetrics(SM_CXSCREEN);
            int height = GetSystemMetrics(SM_CYSCREEN);

            if (width <= 0 || height <= 0)
                return false;

            // 해상도가 바뀌었으면 GDI 자원을 다시 만든다.
            if (width != Width || height != Height || _bitmap == IntPtr.Zero)
            {
                Release();

                if (!Create(width, height))
                    return false;
            }

            uint rop = IncludeLayeredWindows
                ? SRCCOPY | CAPTUREBLT
                : SRCCOPY;

            if (!BitBlt(_memoryDc, 0, 0, Width, Height, _screenDc, 0, 0, rop))
                return false;

            // GDI 가 비트맵에 다 쓴 뒤에 읽어야 한다.
            GdiFlush();

            return true;
        }

        private bool Create(int width, int height)
        {
            _screenDc = GetDC(IntPtr.Zero);

            if (_screenDc == IntPtr.Zero)
                return false;

            _memoryDc = CreateCompatibleDC(_screenDc);

            if (_memoryDc == IntPtr.Zero)
            {
                Release();
                return false;
            }

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,     // 음수 = 위에서 아래로 (WPF 와 같은 줄 순서)
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };

            _bitmap = CreateDIBSection(
                _memoryDc,
                ref info,
                DIB_RGB_COLORS,
                out _bits,
                IntPtr.Zero,
                0);

            if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                Release();
                return false;
            }

            _oldBitmap = SelectObject(_memoryDc, _bitmap);

            Width = width;
            Height = height;

            return true;
        }

        private void Release()
        {
            if (_memoryDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
                SelectObject(_memoryDc, _oldBitmap);

            if (_bitmap != IntPtr.Zero)
                DeleteObject(_bitmap);

            if (_memoryDc != IntPtr.Zero)
                DeleteDC(_memoryDc);

            if (_screenDc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, _screenDc);

            _screenDc = IntPtr.Zero;
            _memoryDc = IntPtr.Zero;
            _bitmap = IntPtr.Zero;
            _oldBitmap = IntPtr.Zero;
            _bits = IntPtr.Zero;

            Width = 0;
            Height = 0;
        }

        public void Dispose() => Release();


        // ── Win32 ────────────────────────────────────────────

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private const uint SRCCOPY = 0x00CC0020;

        // 반투명 창(툴팁, 일부 프로그램)도 같이 찍는다.
        private const uint CAPTUREBLT = 0x40000000;

        private const uint BI_RGB = 0;
        private const uint DIB_RGB_COLORS = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;      // BI_RGB 32비트에서는 쓰지 않는다
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BITMAPINFO info,
            uint usage,
            out IntPtr bits,
            IntPtr section,
            uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(
            IntPtr destDc, int x, int y, int width, int height,
            IntPtr srcDc, int srcX, int srcY, uint rop);

        [DllImport("gdi32.dll")]
        private static extern bool GdiFlush();
    }
}
