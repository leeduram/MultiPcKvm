using System.Runtime.InteropServices;   // Marshal.Copy (판의 메모리에 직접 복사)
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiPcKvm.Common;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  상대 PC 화면 보기 창 (조종하는 쪽)
    //
    //  하는 일 두 가지
    //    1. 영상(IScreenViewer)이 보내주는 화면을 그린다.
    //    2. 창 위의 마우스·키보드를 상대에게 보낸다. (기존 입력 줄, 영상과 상관없음)
    //
    //  영상 기능이 아직 없으면(viewer == null) 1 은 안내 문구만 띄우고, 2 는 그대로 동작한다.
    //
    //  ─── 마우스 좌표 ───
    //    창에 그려진 화면 안에서의 비율(0~1)을 구한 뒤, 상대 "화면 해상도" 를 곱한다.
    //    영상 크기를 곱하지 않는 이유: 영상은 대역폭 때문에 줄여서 올 수 있다.
    //    상대 PC 의 커서는 실제 화면 좌표로 움직여야 한다.
    //
    //  ─── 휠 ───
    //    세로 휠은 WPF 가 알려준다(MouseWheel). 가로 휠은 WPF 에 이벤트가 없어서
    //    창 메시지(WM_MOUSEHWHEEL)를 직접 받는다. 휠 누르기는 가운데 버튼(3)이다.
    //
    //  ─── 키보드 ───
    //    이 창이 선택돼 있는 동안 키보드는 후킹에서 받아 상대에게 보낸다. (MainWindow.SetViewerKeyboard)
    //    창으로 받으면 Alt+Tab, Windows 키를 Windows 가 먼저 가로채서 내 PC 에서 작업 전환·시작 메뉴가 열린다.
    //    후킹은 Windows 보다 먼저 받으므로 막고 넘길 수 있다.
    //    ※ Ctrl+Alt+Del 만은 Windows 보안 기능이라 어떤 프로그램도 가로채거나 대신 누를 수 없다.
    //    ※ 키보드가 상대에게 가는 동안 Alt+Tab 도 상대에게 가므로, 이 창에서 빠져나오려면 마우스로 다른 곳을 누른다.
    //
    //  ─── 전체 화면 · 파일 ───
    //    위쪽 줄 [전체 화면] [파일 보내기] [파일 받기]. 파일 일은 MainWindow.FileTransfer.cs 가 한다.
    //    전체 화면에서는 위쪽 줄을 숨기고, 마우스를 화면 맨 위 끝에 대면 가운데에 막대가 나온다.
    //    키보드는 전부 상대에게 가므로 F11·Esc 같은 키로는 나올 수 없다. 막대의 [창 모드] 로만 나온다.
    //
    //  ─── 눌린 채로 남는 것 막기 ───
    //    버튼을 누른 채 다른 창으로 가면 뗀 신호가 안 간다. 상대 PC 에는 계속 눌린 걸로 남는다.
    //    그래서 이 창이 비활성화되거나 닫히면 누르고 있던 버튼을 전부 뗀다. (키는 MainWindow 쪽에서 뗀다)
    // ─────────────────────────────────────────────────────────────
    public partial class ViewerWindow : Window
    {
        private readonly IScreenViewer? _viewer;
        private readonly string _ip;

        // 입력 보내기. (MainWindow 가 넘겨준다. 지금 연결된 상대에게 보낸다)
        private readonly Func<InputMessage, Task> _send;

        // 상대 화면 해상도. (연결 중에 ScreenInfo 로 바뀔 수 있어서 매번 물어본다)
        private readonly Func<(int Width, int Height)> _remoteSize;

        // 키보드를 상대에게 보낼지 켜고 끈다. (이 창이 선택되면 켜고, 떠나면 끈다)
        private readonly Action<bool> _setKeyboardToRemote;

        // 그리는 판. 영상 크기가 바뀔 때만 새로 만든다.
        //
        // Bgr32 로 그린다. (네 번째 바이트를 보지 않는다)
        // Bgra32 는 투명도를 봐서, 영상 쪽이 픽셀마다 255 를 채워 넣어야 했다. 그 일을 없앤다.
        private WriteableBitmap? _bitmap;

        // ── 받은 화면을 들고 있는 곳 ──
        //
        // 상대 화면 전체를 이 배열 하나에 계속 고쳐 쓴다. (한 줄 = 가로 × 4 바이트)
        // 예전에는 화면이 올 때마다 8MB 배열에 통째로 복사하고, 그리는 동안 오면 새 8MB 를 만들었다.
        // 큰 배열을 자꾸 만들면 메모리 정리(GC)가 무겁게 돌아 화면·입력이 잠깐씩 끊긴다.
        //
        // 영상 쪽이 바뀐 곳(DirtyRects)을 알려주면 그 부분만 복사하고 그 부분만 다시 그린다.
        // 안 알려주면 전체를 복사하고 전체를 그린다. (ScreenShare.cs 약속)
        private byte[]? _screen;
        private int _screenWidth;
        private int _screenHeight;

        // 아직 창에 안 그린 바뀐 곳. 그리기 전에 화면이 여러 장 오면 계속 모은다. (중간 장을 건너뛰어도 빠지는 곳이 없다)
        private readonly List<FrameRect> _dirty = new();
        private bool _dirtyAll;

        // 바뀐 곳이 이보다 많으면 하나하나 그리지 않고 전체를 그린다. (그쪽이 더 빠르다)
        private const int MaxDirtyRects = 512;

        // 위 것들을 지키는 자물쇠. (화면은 네트워크 스레드에서 오고, 그리기는 화면 스레드에서 한다)
        private readonly object _frameLock = new();

        // 그리기를 이미 예약했는가. (예약은 한 번만. 그릴 때 그때까지 모인 걸 다 그린다)
        private bool _renderQueued;

        // 지금 누르고 있는 마우스 버튼 (창을 떠날 때 떼 주려고)
        private readonly HashSet<int> _heldButtons = new();

        // 닫혔는가. (닫힌 뒤 늦게 도착한 화면은 버린다)
        private bool _closed;

        // [파일 보내기] [파일 받기] 를 누르면 부른다. (MainWindow 가 넘겨준다)
        private readonly Action _sendFile;
        private readonly Action _requestFile;

        // 전체 화면인가, 그리고 들어가기 전 창 상태 (나올 때 되돌린다)
        private bool _fullScreen;
        private WindowState _stateBeforeFullScreen;

        // 전체 화면 막대를 잠시 뒤 숨기는 타이머
        private readonly DispatcherTimer _barHideTimer = new();

        public ViewerWindow(
            string peerName,
            string ip,
            IScreenViewer? viewer,
            Func<InputMessage, Task> send,
            Func<(int Width, int Height)> remoteSize,
            Action<bool> setKeyboardToRemote,
            Action sendFile,
            Action requestFile)
        {
            InitializeComponent();

            Title = $"화면 보기 — {peerName}";

            _ip = ip;
            _viewer = viewer;
            _send = send;
            _remoteSize = remoteSize;
            _setKeyboardToRemote = setKeyboardToRemote;
            _sendFile = sendFile;
            _requestFile = requestFile;

            _barHideTimer.Tick += BarHideTimer_Tick;

            // 전체 화면에서 마우스가 화면 맨 위 끝에 닿았는지 본다. (상대 화면 위든 검은 띠 위든)
            PreviewMouseMove += Window_PreviewMouseMove;

            Loaded += async (s, e) => await StartAsync();
        }


        // ── 영상 시작·멈춤 ────────────────────────────────────

        private async Task StartAsync()
        {
            if (_viewer == null)
            {
                StatusText.Text = "영상 기능이 아직 연결되지 않았습니다.\n" +
                                  "(팀원 영상 기능이 들어오면 여기에 상대 화면이 나옵니다)";
                return;
            }

            _viewer.FrameReceived += OnFrameReceived;
            _viewer.Stopped += OnStopped;

            DiagLog.Write($"화면 보기 시작 | {_ip}");

            bool ok;

            try
            {
                ok = await _viewer.StartAsync(_ip);
            }
            catch (Exception ex)
            {
                ok = false;
                StatusText.Text = "화면을 받지 못했습니다: " + ex.Message;
            }

            if (!ok && _bitmap == null && !_closed)
            {
                StatusText.Visibility = Visibility.Visible;

                if (StatusText.Text == "화면을 받는 중...")
                    StatusText.Text = "화면을 받지 못했습니다. 상대 PC 가 화면 보내기를 지원하는지 확인하세요.";
            }
        }

        // 영상 쪽에서 멈췄다고 알려왔다. (어느 스레드에서든 온다)
        private void OnStopped(string reason)
        {
            DiagLog.Write($"화면 보기 멈춤 | {reason}");

            Dispatcher.BeginInvoke(() =>
            {
                if (_closed)
                    return;

                StatusText.Text = "화면이 멈췄습니다: " + reason;
                StatusText.Visibility = Visibility.Visible;
            });
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            _closed = true;
            _barHideTimer.Stop();

            ReleaseAll();
            _setKeyboardToRemote(false);

            if (_viewer != null)
            {
                _viewer.FrameReceived -= OnFrameReceived;
                _viewer.Stopped -= OnStopped;

                try
                {
                    _viewer.Stop();
                    _viewer.Dispose();
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"화면 보기 정리 실패 | {ex.Message}");
                }
            }

            DiagLog.Write("화면 보기 닫음");
        }


        // ── 화면 그리기 ───────────────────────────────────────

        // 영상 쪽에서 화면 한 장이 왔다. (어느 스레드에서든 온다)
        //
        // 약속(ScreenShare.cs): 이 함수가 돌아오면 영상 쪽은 같은 배열을 다시 쓸 수 있다.
        // 그래서 여기서 바로 내 배열(_screen)에 복사해 둔다. 바뀐 곳만.
        private void OnFrameReceived(VideoFrame frame)
        {
            int width = frame.Width;
            int height = frame.Height;

            if (width <= 0 || height <= 0 || frame.Stride < width * 4 ||
                frame.Pixels.Length < (long)frame.Stride * height)
            {
                return;
            }

            bool queue;

            lock (_frameLock)
            {
                if (_closed)
                    return;

                if (_screen == null || _screenWidth != width || _screenHeight != height)
                {
                    // 처음이거나 해상도가 바뀌었다. 새로 만들고 전체를 복사한다.
                    _screen = new byte[width * height * 4];
                    _screenWidth = width;
                    _screenHeight = height;

                    CopyFromFrame(frame, 0, 0, width, height);
                    MarkAllDirty();
                }
                else if (frame.DirtyRects == null)
                {
                    // 바뀐 곳을 안 알려줬다 → 전체가 바뀐 것으로 본다.
                    CopyFromFrame(frame, 0, 0, width, height);
                    MarkAllDirty();
                }
                else
                {
                    foreach (FrameRect r in frame.DirtyRects)
                    {
                        // 화면 밖으로 나간 부분은 잘라낸다. (잘못된 값이 와도 배열 밖을 건드리지 않게)
                        int x = Math.Max(0, r.X);
                        int y = Math.Max(0, r.Y);
                        int right = Math.Min(width, r.X + r.Width);
                        int bottom = Math.Min(height, r.Y + r.Height);

                        if (right <= x || bottom <= y)
                            continue;

                        CopyFromFrame(frame, x, y, right - x, bottom - y);

                        if (_dirtyAll)
                            continue;

                        if (_dirty.Count >= MaxDirtyRects)
                            MarkAllDirty();
                        else
                            _dirty.Add(new FrameRect(x, y, right - x, bottom - y));
                    }
                }

                queue = !_renderQueued;
                _renderQueued = true;
            }

            // 이미 그리기가 예약돼 있으면 또 예약하지 않는다. (그때 모인 걸 다 그린다)
            if (queue)
                Dispatcher.BeginInvoke(RenderPending);
        }

        private void MarkAllDirty()
        {
            _dirtyAll = true;
            _dirty.Clear();
        }

        // 받은 화면의 (x, y, w, h) 부분을 내 배열로 복사한다. (_frameLock 안에서 부른다)
        private void CopyFromFrame(VideoFrame frame, int x, int y, int w, int h)
        {
            byte[] screen = _screen!;
            int dstStride = _screenWidth * 4;
            int rowBytes = w * 4;

            // 전체 줄을 통째로 복사할 수 있으면 한 번에 한다.
            if (x == 0 && w == _screenWidth && frame.Stride == dstStride)
            {
                Buffer.BlockCopy(frame.Pixels, y * dstStride, screen, y * dstStride, h * dstStride);
                return;
            }

            for (int row = 0; row < h; row++)
            {
                Buffer.BlockCopy(
                    frame.Pixels, (y + row) * frame.Stride + x * 4,
                    screen, (y + row) * dstStride + x * 4,
                    rowBytes);
            }
        }

        // 화면 스레드에서: 모인 바뀐 곳을 창에 그린다.
        private void RenderPending()
        {
            lock (_frameLock)
            {
                _renderQueued = false;

                if (_closed || _screen == null)
                    return;

                int width = _screenWidth;
                int height = _screenHeight;
                int stride = width * 4;

                // 크기가 바뀌었으면(처음 포함) 판을 새로 만들고 전체를 그린다.
                if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
                {
                    _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                    ScreenImage.Source = _bitmap;
                    MarkAllDirty();
                }

                if (!_dirtyAll && _dirty.Count == 0)
                    return;

                // 판의 메모리에 직접 복사하고, 바뀐 곳만 "다시 그려라" 고 표시한다.
                // (WPF 는 표시한 곳만 그래픽 카드로 다시 올린다)
                _bitmap.Lock();

                try
                {
                    IntPtr back = _bitmap.BackBuffer;
                    int backStride = _bitmap.BackBufferStride;

                    if (_dirtyAll)
                    {
                        if (backStride == stride)
                        {
                            Marshal.Copy(_screen, 0, back, stride * height);
                        }
                        else
                        {
                            for (int row = 0; row < height; row++)
                                Marshal.Copy(_screen, row * stride, back + row * backStride, stride);
                        }

                        _bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                    }
                    else
                    {
                        foreach (FrameRect r in _dirty)
                        {
                            int rowBytes = r.Width * 4;

                            for (int row = 0; row < r.Height; row++)
                            {
                                int y = r.Y + row;
                                Marshal.Copy(_screen, y * stride + r.X * 4, back + y * backStride + r.X * 4, rowBytes);
                            }

                            _bitmap.AddDirtyRect(new Int32Rect(r.X, r.Y, r.Width, r.Height));
                        }
                    }
                }
                finally
                {
                    _bitmap.Unlock();
                }

                _dirty.Clear();
                _dirtyAll = false;
            }

            StatusText.Visibility = Visibility.Collapsed;
        }


        // ── 마우스 ────────────────────────────────────────────

        // 창 위의 위치 → 상대 화면 좌표. 그려진 화면 밖이면 null.
        private (int X, int Y)? ToRemote(MouseEventArgs e)
        {
            double w = ScreenImage.ActualWidth;
            double h = ScreenImage.ActualHeight;

            if (w <= 0 || h <= 0)
                return null;    // 아직 화면이 없다

            Point p = e.GetPosition(ScreenImage);

            double rx = p.X / w;
            double ry = p.Y / h;

            if (rx < 0 || rx > 1 || ry < 0 || ry > 1)
                return null;

            (int remoteW, int remoteH) = _remoteSize();

            int x = (int)Math.Round(rx * (remoteW - 1));
            int y = (int)Math.Round(ry * (remoteH - 1));

            return (Math.Clamp(x, 0, remoteW - 1), Math.Clamp(y, 0, remoteH - 1));
        }

        private void ScreenImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (ToRemote(e) is (int x, int y))
                Send(new InputMessage { Type = MessageType.MouseMove, Value1 = x, Value2 = y });
        }

        private void ScreenImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            int button = ButtonNumber(e.ChangedButton);

            if (button == 0 || ToRemote(e) is not (int x, int y))
                return;

            // 누르기 전에 커서를 그 자리로 옮긴다. (누르는 곳이 정확하도록)
            Send(new InputMessage { Type = MessageType.MouseMove, Value1 = x, Value2 = y });
            Send(new InputMessage { Type = MessageType.MouseButton, Value1 = button, Value2 = 1 });

            _heldButtons.Add(button);

            // 누른 채 창 밖으로 끌어도 뗄 때 신호를 받도록 마우스를 붙잡는다.
            ScreenImage.CaptureMouse();
            e.Handled = true;
        }

        private void ScreenImage_MouseUp(object sender, MouseButtonEventArgs e)
        {
            int button = ButtonNumber(e.ChangedButton);

            if (button == 0)
                return;

            Send(new InputMessage { Type = MessageType.MouseButton, Value1 = button, Value2 = 0 });

            _heldButtons.Remove(button);

            if (_heldButtons.Count == 0)
                ScreenImage.ReleaseMouseCapture();

            e.Handled = true;
        }

        // WPF 버튼 → 우리 버튼 번호 (MessageType.MouseButton 약속: 1 왼쪽, 2 오른쪽, 3 가운데)
        private static int ButtonNumber(MouseButton button) => button switch
        {
            MouseButton.Left => 1,
            MouseButton.Right => 2,
            MouseButton.Middle => 3,
            _ => 0,
        };


        // ── 휠 ────────────────────────────────────────────────

        // 세로 휠. e.Delta 는 Windows 값 그대로다. (한 칸 120, 위로 +)
        private void ScreenImage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ToRemote(e) is not (int x, int y))
                return;

            // 휠은 커서 아래 창이 받으므로 먼저 그 자리로 옮긴다.
            Send(new InputMessage { Type = MessageType.MouseMove, Value1 = x, Value2 = y });
            Send(new InputMessage { Type = MessageType.MouseWheel, Value1 = e.Delta, Value2 = 0 });

            e.Handled = true;
        }

        // 가로 휠. WPF 에 이벤트가 없어서 창 메시지를 직접 받는다.
        private const int WM_MOUSEHWHEEL = 0x020E;

        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_MOUSEHWHEEL)
                return IntPtr.Zero;

            // 굴린 양은 wParam 의 위쪽 16비트 (부호 있음)
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);

            // 지금 마우스가 화면 위에 있을 때만 보낸다.
            Point p = Mouse.GetPosition(ScreenImage);
            double w = ScreenImage.ActualWidth;
            double h = ScreenImage.ActualHeight;

            if (delta != 0 && w > 0 && h > 0 && p.X >= 0 && p.Y >= 0 && p.X <= w && p.Y <= h)
            {
                (int remoteW, int remoteH) = _remoteSize();

                int x = Math.Clamp((int)Math.Round(p.X / w * (remoteW - 1)), 0, remoteW - 1);
                int y = Math.Clamp((int)Math.Round(p.Y / h * (remoteH - 1)), 0, remoteH - 1);

                Send(new InputMessage { Type = MessageType.MouseMove, Value1 = x, Value2 = y });
                Send(new InputMessage { Type = MessageType.MouseWheel, Value1 = delta, Value2 = 1 });

                handled = true;
            }

            return IntPtr.Zero;
        }


        // ── 위쪽 줄 · 전체 화면 막대 버튼 ─────────────────────

        private void SendFile_Click(object sender, RoutedEventArgs e) => _sendFile();

        private void RequestFile_Click(object sender, RoutedEventArgs e) => _requestFile();

        // [전체 화면] ↔ [창 모드]
        private void FullScreen_Click(object sender, RoutedEventArgs e)
        {
            if (_fullScreen)
                LeaveFullScreen();
            else
                EnterFullScreen();
        }

        // 테두리 없이 지금 모니터 전체를 덮는다. 작업 표시줄도 덮도록 맨 위(Topmost)로 둔다.
        //
        // 최대화 상태에서 테두리를 없애면 작업 표시줄 자리가 남는다.
        // 그래서 보통 상태로 돌렸다가 테두리를 없앤 뒤 다시 최대화한다.
        private void EnterFullScreen()
        {
            _stateBeforeFullScreen = WindowState;
            _fullScreen = true;

            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            WindowState = WindowState.Maximized;

            TopBar.Visibility = Visibility.Collapsed;

            // 어떻게 나오는지 알 수 있게 막대를 잠깐 보여준다.
            ShowBar(TimeSpan.FromSeconds(3));
        }

        private void LeaveFullScreen()
        {
            _fullScreen = false;

            _barHideTimer.Stop();
            FullScreenBar.Visibility = Visibility.Collapsed;
            TopBar.Visibility = Visibility.Visible;

            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Topmost = false;
            WindowState = _stateBeforeFullScreen;
        }

        // 전체 화면 막대를 보여주고, hideAfter 뒤에 숨긴다. (그때 마우스가 막대 위에 있으면 숨기지 않는다)
        private void ShowBar(TimeSpan hideAfter)
        {
            if (!_fullScreen)
                return;

            FullScreenBar.Visibility = Visibility.Visible;

            _barHideTimer.Stop();
            _barHideTimer.Interval = hideAfter;
            _barHideTimer.Start();
        }

        private void BarHideTimer_Tick(object? sender, EventArgs e)
        {
            // 막대 위에 마우스가 있으면 계속 둔다. 떠날 때 MouseLeave 가 다시 타이머를 건다.
            if (FullScreenBar.IsMouseOver)
                return;

            _barHideTimer.Stop();
            FullScreenBar.Visibility = Visibility.Collapsed;
        }

        // 마우스가 화면 맨 위 끝(2 픽셀 이내)에 닿으면 막대를 꺼낸다.
        private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_fullScreen && e.GetPosition(this).Y <= 2)
                ShowBar(TimeSpan.FromSeconds(2));
        }

        private void FullScreenBar_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_fullScreen)
                ShowBar(TimeSpan.FromSeconds(0.8));
        }

        // 파일 전송 상태를 보여준다. (MainWindow 가 부른다. 화면 스레드에서만)
        // 전체 화면이면 막대에 띄우고 막대를 잠깐 꺼낸다. (보내는 중에는 계속 갱신되므로 계속 보인다)
        public void ShowTransferStatus(string text)
        {
            if (_closed)
                return;

            TransferText.Text = text;
            TransferText.ToolTip = text;    // 길어서 잘리면 마우스를 올려 전체를 본다

            BarTransferText.Text = text;
            BarTransferText.Visibility = Visibility.Visible;

            if (_fullScreen)
                ShowBar(TimeSpan.FromSeconds(4));
        }


        // ── 키보드 (이 창이 선택된 동안 상대에게) ──────────────

        private void Window_Activated(object? sender, EventArgs e) => _setKeyboardToRemote(true);


        // ── 눌린 채로 남는 것 막기 ────────────────────────────

        // 다른 창으로 가면 누르고 있던 버튼을 떼고, 키보드를 내 PC 로 돌려놓는다.
        // (상대에 눌려 있던 키는 MainWindow.SetViewerKeyboard 가 뗀다)
        private void Window_Deactivated(object? sender, EventArgs e)
        {
            ReleaseAll();
            _setKeyboardToRemote(false);
        }

        private void ReleaseAll()
        {
            foreach (int button in _heldButtons)
                Send(new InputMessage { Type = MessageType.MouseButton, Value1 = button, Value2 = 0 });

            _heldButtons.Clear();
        }

        // 보내기. 실패해도 창은 계속 쓸 수 있게 기록만 남긴다.
        private async void Send(InputMessage message)
        {
            try
            {
                await _send(message);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"화면 보기 입력 전송 실패 | {message.Type} | {ex.Message}");
            }
        }
    }
}
