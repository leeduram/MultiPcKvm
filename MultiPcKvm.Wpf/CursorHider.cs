using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  내 PC 커서 숨기기 (커서가 상대 PC 로 넘어가 있는 동안)
    //
    //  커서가 상대 PC 로 넘어가면, 내 PC 의 커서는 주 모니터 한가운데에 묶여 있다.
    //  (WindowsInputCapture 가 이동량을 재려고 그 자리에 붙잡아 둔다)
    //  그대로 두면 내 화면 한가운데에 커서가 멈춰 보여서 헷갈린다. 그래서 숨긴다.
    //
    //  ─── 숨기는 방법: 커서 밑에 "보이지 않는 작은 창" 을 띄운다 ───
    //    Windows 는 커서 바로 밑에 있는 창이 정한 모양으로 커서를 그린다.
    //    커서가 묶인 자리에 작은 창을 띄우고, 그 창의 커서 모양을 "없음" 으로 해 두면 커서가 사라진다.
    //    창은 거의 완전히 투명해서(불투명도 1/255) 눈에 보이지 않는다.
    //    (완전히 투명하면 Windows 가 창이 없는 것으로 보고 밑의 창에 커서 모양을 묻는다. 그래서 1/255)
    //
    //  왜 Windows 전체 커서를 바꾸지(SetSystemCursor) 않았나:
    //    그 방법은 프로그램이 갑자기 죽으면 커서가 영영 안 보이는 채로 남는다. (로그아웃해야 돌아옴)
    //    창 방식은 프로그램이 죽으면 창도 같이 사라져서 커서가 저절로 돌아온다.
    //
    //  창은 눌러도 활성화되지 않고(포커스를 뺏지 않음), 작업 표시줄·Alt+Tab 에도 안 나온다.
    //  커서가 넘어가 있는 동안 마우스 입력은 어차피 상대에게 가므로 이 창이 클릭을 받을 일도 없다.
    //
    //  화면 스레드에서만 쓴다.
    // ─────────────────────────────────────────────────────────────
    internal sealed class CursorHider
    {
        // 창 크기 (실제 픽셀). 커서가 조금 흔들려도 창 밖으로 안 나가게 넉넉히.
        private const int SizePx = 64;

        private Window? _window;
        private IntPtr _hwnd;

        public bool IsShown { get; private set; }

        // (x, y) 자리에 커서를 숨긴다. 좌표는 실제 픽셀. (WindowsInputCapture.AnchorX/Y)
        public void Show(int x, int y)
        {
            EnsureWindow();

            if (_hwnd == IntPtr.Zero)
                return;

            // 커서 자리를 가운데로 두고 맨 위에 띄운다. 활성화하지 않는다.
            //
            // WPF 의 Left/Top 은 화면 배율이 적용된 단위라 실제 픽셀과 다르다.
            // 그래서 Windows 함수로 실제 픽셀 위치에 직접 놓는다.
            SetWindowPos(_hwnd, HWND_TOPMOST,
                x - SizePx / 2, y - SizePx / 2, SizePx, SizePx,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);

            IsShown = true;

            // 커서 모양을 바로 없앤다.
            // 창이 커서 밑에 나타나면 Windows 가 이 창에 커서 모양을 다시 묻는데(그때 Cursor=None 이 적용됨),
            // 그 전에 한 번 직접 지워서 잠깐이라도 보이지 않게 한다.
            SetCursor(IntPtr.Zero);
        }

        public void Hide()
        {
            if (!IsShown || _hwnd == IntPtr.Zero)
                return;

            ShowWindow(_hwnd, SW_HIDE);
            IsShown = false;

            // 커서는 WindowsInputCapture 가 원래 자리로 옮기면서 그 자리 창의 모양으로 다시 그려진다.
        }

        public void Close()
        {
            IsShown = false;
            _window?.Close();
            _window = null;
            _hwnd = IntPtr.Zero;
        }

        private void EnsureWindow()
        {
            if (_window != null)
                return;

            _window = new Window
            {
                Title = "MultiPcKvm 커서 숨김",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,

                // 불투명도 1/255. 눈에는 안 보이지만 Windows 는 "여기 창이 있다" 고 본다. (위 설명)
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),

                Cursor = Cursors.None,      // 이 창 위에서는 커서를 그리지 않는다
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,      // 띄울 때 포커스를 뺏지 않는다
                Width = 1,
                Height = 1,
                Left = -32000,              // 처음 만들 때는 화면 밖에 둔다
                Top = -32000,
            };

            // 창 손잡이만 만들고(아직 안 보임) 스타일을 바꾼다.
            _hwnd = new WindowInteropHelper(_window).EnsureHandle();

            if (_hwnd == IntPtr.Zero)
                return;

            // 눌러도 활성화 안 됨 + 작업 표시줄·Alt+Tab 에 안 나옴
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));

            // WPF 가 내용을 그리도록 한 번 보여준 뒤 바로 숨긴다. (투명 창은 WPF 가 그려야 그 1/255 가 생긴다)
            _window.Show();
            ShowWindow(_hwnd, SW_HIDE);
        }


        // ── Win32 ────────────────────────────────────────────

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const int SW_HIDE = 0;

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const long WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }
}
