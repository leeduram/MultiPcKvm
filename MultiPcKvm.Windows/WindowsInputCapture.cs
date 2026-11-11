using System.Runtime.InteropServices;
using MultiPcKvm.Common;

namespace MultiPcKvm.Windows
{
    // 내 PC의 마우스/키보드 움직임을 가로채서 메시지로 바꾼다.
    // Common 의 IInputCapture 설계도를 따른다.
    //
    // WindowsInputSender 와 정반대 역할이다.
    //   이 클래스           : 내 입력      → InputMessage
    //   WindowsInputSender : InputMessage → 이 PC의 입력
    //
    // ─────────────────────────────────────────────────────────
    //  진행 상황
    //   1단계        : 키보드만. 가로채서 알려주기만 하고 삼키지는 않음  ✔ 완료
    //   2단계        : 마우스 추가                                    ✔ 완료
    //   3단계        : 삼키기 — 키보드                              ✔ 완료
    //   4단계        : 마우스 삼키기 + 커서 묶어두기 + 이동량 계산    ✔ 완료
    //   5단계        : 자동 복구 — 신호가 끊기면 삼키기가 저절로 풀림  ✔ 완료
    //   6단계        : 커서 숨기기                                    ✔ 완료
    //                  이 클래스는 삼키기를 켜고 끌 때 알려주기만 한다. (SwallowInputChanged)
    //                  실제로 숨기는 건 WPF 의 CursorHider (투명 창이 필요해서)
    // ─────────────────────────────────────────────────────────
    public class WindowsInputCapture : IInputCapture
    {
        // 마우스가 움직이거나 키를 누를 때마다 발생한다.
        public event Action<InputMessage>? InputCaptured;

        // true 면 입력을 이 PC에 전달하지 않고 삼켜버린다.
        // 커서가 상대 PC로 넘어가 있는 동안 필요하다.
        //
        // ⚠️ 켜면 이 PC의 키보드가 완전히 안 먹는다.
        //    반드시 "자동으로 꺼지는 장치" 와 같이 써야 한다.
        //
        // volatile 을 붙인 이유:
        //   이 값을 쓰는 곳(메인 스레드)과 읽는 곳(후킹 스레드)이 다르다.
        //   volatile 이 없으면 각 스레드가 값을 자기 안에 캐싱해버려서,
        //   한쪽에서 false 로 바꿔도 다른 쪽은 계속 true 로 볼 수 있다.
        //   그러면 삼키기가 안 풀린다.
        private volatile bool _swallowInput;

        public bool SwallowInput
        {
            get => _swallowInput;
            set
            {
                // 꺼진 상태 → 켜기
                if (value && !_swallowInput)
                {
                    // 켜자마자 감시 시계를 0으로 맞춘다.
                    // 안 그러면 예전 시각이 남아 있어서 바로 풀려버린다.
                    KeepAlive();

                    // 1) 원래 커서 위치를 기억해둔다. 나중에 되돌려놔야 한다.
                    Win32.GetCursorPos(out Win32.POINT p);
                    _savedX = p.x;
                    _savedY = p.y;

                    // 2) 기준점은 화면 한가운데로 잡는다.
                    //
                    //    왜 지금 있는 자리가 아니라 한가운데인가:
                    //      커서가 화면 가장자리에 있으면 그 바깥쪽 이동량이 잘려나간다.
                    //      Windows 가 커서를 화면 밖으로 못 나가게 막기 때문이다.
                    //      한가운데에 두면 사방으로 여유가 생긴다.
                    _anchorX = Win32.GetSystemMetrics(Win32.SM_CXSCREEN) / 2;
                    _anchorY = Win32.GetSystemMetrics(Win32.SM_CYSCREEN) / 2;

                    // 3) 커서를 기준점으로 옮긴다.
                    //    ★ 삼키기를 켜기 "전에" 옮겨야 한다. 켠 뒤에 옮기면 그것마저 막힌다.
                    Win32.SetCursorPos(_anchorX, _anchorY);

                    _swallowInput = true;
                    RaiseSwallowInputChanged(true);
                    return;
                }

                // 켜진 상태 → 끄기
                if (!value && _swallowInput)
                {
                    // ★ 먼저 끄고 나서 옮겨야 한다. 켜진 채로 옮기면 막힌다.
                    _swallowInput = false;
                    Win32.SetCursorPos(_savedX, _savedY);
                    RaiseSwallowInputChanged(false);
                    return;
                }

                _swallowInput = value;
            }
        }

        // 마우스 삼키기를 켜거나 껐을 때 알려준다. (true = 켬)
        //
        // WPF 가 이걸 받아서 내 PC 의 커서를 숨기고 보인다. (CursorHider)
        // 상대 PC 로 넘어갈 때, 돌아올 때, 자동 복구로 풀릴 때, 끝낼 때 모두 여기로 온다.
        //
        // ★ 후킹 스레드에서 불릴 수 있다. 받는 쪽은 오래 걸리는 일을 하면 안 된다.
        //   (WPF 는 Dispatcher.BeginInvoke 로 화면 스레드에 넘기기만 한다)
        public event Action<bool>? SwallowInputChanged;

        private void RaiseSwallowInputChanged(bool on)
        {
            try
            {
                SwallowInputChanged?.Invoke(on);
            }
            catch
            {
                // 알림 받는 쪽 오류로 삼키기가 꼬이면 안 된다.
            }
        }

        // 삼키는 동안 커서를 묶어두는 자리 (주 모니터 한가운데, 실제 픽셀).
        // 커서를 숨기는 투명 창을 이 자리에 띄운다.
        public int AnchorX => _anchorX;
        public int AnchorY => _anchorY;

        // ── 키보드만 삼키기 (화면 보기 창) ────────────────────
        //
        // 화면 보기 창을 쓰는 동안에는 마우스는 내 PC 에서 그대로 쓰고(창 위에서 움직여야 하니까)
        // 키보드만 상대에게 보낸다.
        //
        // 창으로 받지 않고 여기서 받는 이유:
        //   Alt+Tab, Windows 키 같은 것은 Windows 가 먼저 가로채서 창까지 오지 않는다.
        //   그러면 상대에게 못 보내고 내 PC 에서 작업 전환·시작 메뉴가 열린다.
        //   후킹은 Windows 보다 먼저 받으므로 막고 넘길 수 있다.
        //   (Ctrl+Alt+Del 만은 Windows 보안 기능이라 후킹으로도 못 막는다)
        //
        // 마우스 삼키기와 같은 자동 복구가 걸린다. 연결이 끊겨 신호가 2초 없으면 풀린다.
        // (키보드가 계속 막혀 있으면 사용자가 아무것도 못 하므로)
        private volatile bool _swallowKeyboard;

        public bool SwallowKeyboard
        {
            get => _swallowKeyboard;
            set
            {
                // 켜자마자 자동 복구가 풀지 않게 감시 시계를 0으로 맞춘다.
                if (value && !_swallowKeyboard)
                    KeepAlive();

                _swallowKeyboard = value;
            }
        }

        // 삼키는 동안 커서를 묶어둘 자리 (화면 한가운데).
        //
        // 마우스가 움직일 때마다 여기서 얼마나 벗어나려 했는지를 재고,
        // 곧바로 다시 이 자리로 되돌려놓는다.
        private volatile int _anchorX;
        private volatile int _anchorY;

        // 삼키기를 켜기 직전의 커서 위치. 풀 때 여기로 되돌린다.
        private volatile int _savedX;
        private volatile int _savedY;


        // ── 자동 복구 (죽은 사람 스위치) ──────────────────────

        // 마지막으로 "아직 정상입니다" 신호를 받은 시각.
        private long _lastKeepAlive;

        // 이 시간(밀리초) 동안 신호가 없으면 삼키기를 자동으로 푼다.
        // 2초 정도가 적당하다. 너무 짧으면 잠깐 버벅여도 풀리고,
        // 너무 길면 고장났을 때 그만큼 오래 입력을 못 쓴다.
        public int WatchdogTimeoutMs { get; set; } = 2000;

        // 자동으로 풀렸을 때 알려준다. (화면에 경고를 띄우는 용도)
        public event Action? SwallowAutoReleased;

        // 감시 전용 스레드
        private Thread? _watchdogThread;
        private volatile bool _watchdogRunning;

        // 감시 스레드를 바로 깨워서 끝내는 신호.
        // 예전에는 0.2초씩 자고 있어서, 끝낼 때(연결 끊기) 최대 0.2초를 기다렸다.
        private readonly ManualResetEventSlim _watchdogStop = new ManualResetEventSlim(false);

        // "아직 정상입니다" 신호.
        // 삼키기를 켜둔 동안 주기적으로 불러줘야 한다.
        public void KeepAlive()
        {
            // Interlocked.Exchange = 여러 스레드가 동시에 건드려도 안전하게 값을 바꾼다.
            // Environment.TickCount64 = 컴퓨터가 켜진 뒤 지난 밀리초.
            //   DateTime 대신 이걸 쓰는 이유: 사용자가 시계를 바꿔도 영향을 안 받는다.
            Interlocked.Exchange(ref _lastKeepAlive, Environment.TickCount64);
        }

        // 감시 스레드가 하는 일
        private void WatchdogMain()
        {
            while (_watchdogRunning)
            {
                // 0.2초 기다린다. 그 사이 Stop() 이 신호를 주면 바로 깬다.
                if (_watchdogStop.Wait(200))
                    break;

                // 삼키는 중이 아니면 감시할 게 없다.
                if (!_swallowInput && !_swallowKeyboard)
                    continue;

                long last = Interlocked.Read(ref _lastKeepAlive);
                long elapsed = Environment.TickCount64 - last;

                if (elapsed > WatchdogTimeoutMs)
                {
                    // 신호가 끊겼다. 이유는 알 필요 없다. 그냥 푼다.
                    SwallowInput = false;
                    SwallowKeyboard = false;

                    Console.WriteLine();
                    Console.WriteLine($"  [자동 복구] {elapsed}ms 동안 신호 없음 → 삼키기를 해제했습니다.");

                    SwallowAutoReleased?.Invoke();
                }
            }
        }


        // ── 내부에서 쓰는 것들 ────────────────────────────────

        // 후킹 전용 스레드.
        // UI 스레드나 메인 스레드에 걸면 안 되고, 이 일만 하는 스레드가 따로 필요하다.
        private Thread? _hookThread;

        // 그 스레드의 번호. 나중에 "그만해라" 메시지를 보낼 때 필요하다.
        private uint _hookThreadId;

        // 후킹 식별자. 풀 때 필요하다.
        // 키보드와 마우스는 별개의 후킹이라 따로 걸고 따로 푼다.
        private IntPtr _keyboardHook = IntPtr.Zero;
        private IntPtr _mouseHook = IntPtr.Zero;

        // ★★★ 중요 ★★★
        // Windows 에게 넘겨준 함수를 여기에 보관한다.
        //
        // 왜 이게 필요한가:
        //   C#은 "아무도 안 쓰는 것" 을 알아서 메모리에서 치운다(가비지 컬렉션).
        //   그런데 C#은 Windows 쪽에서 이 함수를 쓰고 있다는 걸 모른다.
        //   그래서 변수에 담아두지 않으면 "안 쓰네?" 하고 치워버리고,
        //   나중에 Windows 가 없어진 함수를 부르려다 프로그램이 죽는다.
        //
        //   문제는 바로 안 죽고 몇 분 뒤에 죽는다는 것이다.
        //   원인 찾기가 극도로 어려워지므로 반드시 필드에 담아둔다.
        private Win32.HookProc? _keyboardProc;
        private Win32.HookProc? _mouseProc;

        // Start() 가 "후킹 준비 끝났다" 를 기다리는 데 쓴다.
        // 이게 없으면 후킹이 걸리기도 전에 Start() 가 끝나버린다.
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);


        // ── 시작 / 종료 ───────────────────────────────────────

        public void Start()
        {
            // 이미 돌고 있으면 무시
            if (_hookThread != null)
                return;

            _ready.Reset();

            _hookThread = new Thread(HookThreadMain);

            // IsBackground = true 로 두면, 프로그램이 끝날 때
            // 이 스레드가 남아서 프로그램이 안 꺼지는 일을 막을 수 있다.
            _hookThread.IsBackground = true;
            _hookThread.Name = "InputHook";

            _hookThread.Start();

            // 후킹이 실제로 걸릴 때까지 최대 2초 기다린다.
            _ready.Wait(2000);

            // 감시 스레드도 같이 띄운다.
            _watchdogStop.Reset();
            _watchdogRunning = true;
            _watchdogThread = new Thread(WatchdogMain);
            _watchdogThread.IsBackground = true;
            _watchdogThread.Name = "InputWatchdog";
            _watchdogThread.Start();
        }

        public void Stop()
        {
            // ★ 무슨 일이 있어도 삼키기부터 푼다.
            //   이걸 안 하면 프로그램이 꺼진 뒤에도 잠시 입력이 막혀 있을 수 있다.
            SwallowInput = false;

            _watchdogRunning = false;
            _watchdogStop.Set();
            _watchdogThread?.Join(500);
            _watchdogThread = null;

            if (_hookThread == null)
                return;

            // 후킹 스레드의 메시지 루프에게 "그만해라" 를 보낸다.
            // 그러면 GetMessage 가 0을 돌려주고 루프가 끝난다.
            Win32.PostThreadMessage(_hookThreadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

            // 스레드가 정리를 마칠 때까지 최대 1초 기다린다.
            _hookThread.Join(1000);
            _hookThread = null;
        }


        // ── 후킹 스레드가 하는 일 ─────────────────────────────

        private void HookThreadMain()
        {
            // 내 스레드 번호를 기억해둔다. Stop() 에서 쓴다.
            _hookThreadId = Win32.GetCurrentThreadId();

            // 알림 받을 함수를 필드에 담는다. (위의 ★★★ 설명 참고)
            _keyboardProc = KeyboardHookProc;
            _mouseProc = MouseHookProc;

            IntPtr module = Win32.GetModuleHandle(null);

            // 후킹을 건다.
            //   WH_KEYBOARD_LL : 키보드를 잡겠다
            //   마지막 0       : 모든 프로그램의 입력을 다 잡겠다
            _keyboardHook = Win32.SetWindowsHookEx(
                Win32.WH_KEYBOARD_LL, _keyboardProc, module, 0);

            _mouseHook = Win32.SetWindowsHookEx(
                Win32.WH_MOUSE_LL, _mouseProc, module, 0);

            if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            {
                Console.WriteLine("후킹 실패. 오류 번호: " + Marshal.GetLastWin32Error());
                _ready.Set();
                return;
            }

            Console.WriteLine("키보드 · 마우스 후킹 시작됨.");

            // 준비 끝. Start() 가 이제 돌아가도 된다.
            _ready.Set();

            // ── 메시지 루프 ──
            // 이게 돌고 있어야 Windows 가 KeyboardHookProc 를 불러준다.
            // GetMessage 는 할 일이 없으면 여기서 잠들어 있으므로 CPU를 쓰지 않는다.
            while (Win32.GetMessage(out Win32.MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessage(ref msg);
            }

            // Stop() 이 WM_QUIT 을 보내면 위 루프를 빠져나와 여기로 온다.
            if (_keyboardHook != IntPtr.Zero)
            {
                Win32.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            if (_mouseHook != IntPtr.Zero)
            {
                Win32.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

            Console.WriteLine("후킹 해제됨.");
        }


        // ── Windows 가 키 입력을 알려줄 때 불리는 함수 ────────
        //
        // ★★★ 이 안에서는 오래 걸리는 일을 절대 하면 안 된다 ★★★
        //
        // 약 0.3초 안에 빠져나오지 않으면 Windows 가 경고 없이 후킹을 해제한다.
        // 프로그램은 멀쩡히 돌고 있는데 입력만 안 잡히는 상태가 되고,
        // 오류 메시지도 안 뜬다.
        //
        // 그래서 여기서 네트워크 전송이나 파일 저장을 하면 안 된다.
        // (지금은 화면에 한 줄 출력만 하므로 충분히 빠르다.
        //  나중에 네트워크를 붙일 때는 "통에 던져넣고 즉시 빠져나오는" 방식으로 바꾼다)
        private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // nCode 가 0보다 작으면 "건드리지 말고 그냥 넘겨라" 는 뜻이다.
            if (nCode >= 0)
            {
                // lParam 은 정보가 들어있는 메모리 주소다.
                // PtrToStructure 로 그 주소에 있는 내용을 우리가 읽을 수 있는 모양으로 꺼낸다.
                Win32.KBDLLHOOKSTRUCT data =
                    Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);

                // 상대가 보내서 내 PC 에 "만들어 넣은" 키인지 확인한다.
                //
                // 이런 키는 그냥 통과시킨다. 잡지도 않고 삼키지도 않는다.
                //   · 잡으면: 상대가 보낸 키를 내가 다시 상대에게 돌려보낸다
                //            → 서로 조종 중일 때 무한 반복
                //   · 삼키면: 상대가 누른 키가 내 PC 에서 안 먹는다
                bool injected = (data.flags & Win32.LLKHF_INJECTED) != 0;

                if (injected)
                    return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

                int what = (int)wParam;

                // 눌렀나 뗐나에 따라 메시지 종류를 정한다.
                // SYS 가 붙은 것은 Alt 와 같이 누른 경우인데, 우리에겐 똑같이 취급하면 된다.
                if (what == Win32.WM_KEYDOWN || what == Win32.WM_SYSKEYDOWN)
                {
                    InputCaptured?.Invoke(new InputMessage
                    {
                        Type = MessageType.KeyDown,
                        Value1 = (int)data.vkCode
                    });
                }
                else if (what == Win32.WM_KEYUP || what == Win32.WM_SYSKEYUP)
                {
                    InputCaptured?.Invoke(new InputMessage
                    {
                        Type = MessageType.KeyUp,
                        Value1 = (int)data.vkCode
                    });
                }
            }

            // ── 삼키기 ──
            //
            // 1을 돌려주면 입력이 여기서 죽는다. 내 PC에는 아무 일도 안 일어난다.
            // (커서가 상대 PC에 가 있는 동안 필요하다)
            //
            // nCode >= 0 일 때만 삼켜야 한다.
            // nCode 가 음수면 "건드리지 말라" 는 뜻이라 무조건 넘겨야 한다.
            if (nCode >= 0 && (SwallowInput || _swallowKeyboard))
                return (IntPtr)1;

            // 다음 프로그램에게 넘긴다.
            // 이걸 안 부르면 뒤에 선 다른 프로그램들이 입력을 못 받는다.
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }


        // ── Windows 가 마우스 입력을 알려줄 때 불리는 함수 ────
        //
        // 키보드보다 훨씬 자주 불린다. 마우스를 움직이는 동안에는
        // 1초에 수백 번 불릴 수 있으므로, 키보드보다 더 가벼워야 한다.
        private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                Win32.MSLLHOOKSTRUCT data =
                    Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);

                // 우리가 SendInput 으로 만들어낸 입력인지 확인한다.
                //
                // & 는 "이 표시가 켜져 있나?" 를 확인하는 연산이다.
                // 켜져 있으면 0이 아닌 값이 나온다.
                //
                // 걸러내지 않으면: 내가 만든 입력 → 내 후킹이 잡음 → 또 보냄 → 무한 반복
                bool injected = (data.flags & Win32.LLMHF_INJECTED) != 0;

                int what = injected ? 0 : (int)wParam;

                switch (what)
                {
                    case Win32.WM_MOUSEMOVE:
                        if (SwallowInput)
                        {
                            // 삼키는 중 — 커서는 기준점에 묶여 있다.
                            // 기준점에서 얼마나 벗어나려 했는지가 곧 이동량이다.
                            int dx = data.pt_x - _anchorX;
                            int dy = data.pt_y - _anchorY;

                            // 0, 0 이면 실제로 안 움직인 것이므로 보낼 필요가 없다.
                            // (아래 SetCursorPos 때문에 생기는 되돌림 신호도 여기서 걸러진다)
                            if (dx != 0 || dy != 0)
                            {
                                InputCaptured?.Invoke(new InputMessage
                                {
                                    Type = MessageType.MouseMoveDelta,
                                    Value1 = dx,
                                    Value2 = dy
                                });

                                // ★ 곧바로 기준점으로 되돌린다.
                                //
                                // 막기만 해서는 커서가 조금씩 밀려난다.
                                // 밀려나면 다음 번 계산이 전부 그만큼 틀어지므로
                                // 매번 제자리로 끌어다 놓는다.
                                //
                                // 후킹 함수 안에서 함수를 부르는 게 걸리지만,
                                // SetCursorPos 는 아주 빠른 함수라 0.3초 제한에 영향이 없다.
                                Win32.SetCursorPos(_anchorX, _anchorY);
                            }
                        }
                        else
                        {
                            // 평소 — 화면 좌표 그대로
                            InputCaptured?.Invoke(new InputMessage
                            {
                                Type = MessageType.MouseMove,
                                Value1 = data.pt_x,
                                Value2 = data.pt_y
                            });
                        }
                        break;

                    // 버튼은 여섯 가지 경우를 버튼 번호 + 누름/뗌 으로 바꾼다.
                    // (MessageType.MouseButton 의 약속대로)
                    case Win32.WM_LBUTTONDOWN: RaiseButton(1, true);  break;
                    case Win32.WM_LBUTTONUP:   RaiseButton(1, false); break;
                    case Win32.WM_RBUTTONDOWN: RaiseButton(2, true);  break;
                    case Win32.WM_RBUTTONUP:   RaiseButton(2, false); break;
                    case Win32.WM_MBUTTONDOWN: RaiseButton(3, true);  break;
                    case Win32.WM_MBUTTONUP:   RaiseButton(3, false); break;

                    // 휠. 굴린 양은 mouseData 의 위쪽 16비트에 부호 있는 값으로 들어 있다.
                    // (short) 로 바꿔야 아래로 굴린 음수(-120)가 제대로 나온다.
                    case Win32.WM_MOUSEWHEEL:  RaiseWheel((short)(data.mouseData >> 16), horizontal: false); break;
                    case Win32.WM_MOUSEHWHEEL: RaiseWheel((short)(data.mouseData >> 16), horizontal: true);  break;
                }

                // 삼키기 — 키보드와 같은 방식이다.
                //
                // 여기서 막기 때문에 커서가 실제로 움직이지 않고,
                // 그래서 위의 기준점이 계속 유효하다.
                //
                // ★ 단, 만들어진 입력(injected)은 막지 않는다.
                //
                // 예전에는 이것도 막았다. 그런데 내 PC 에서 만들어지는 입력은
                // 전부 "상대가 보내서 내가 대신 눌러준 것" 뿐이다.
                // 그걸 막으면 서로 조종할 때 이런 일이 생긴다:
                //
                //   A 커서가 B 에 가 있음 → A 는 삼키는 중
                //   B 가 A 를 조종 → 이동은 SetCursorPos 라 후킹을 안 거쳐서 됨
                //                  → 클릭은 SendInput 이라 A 의 후킹이 삼켜버림
                //
                // 그래서 "이동은 되는데 클릭이 안 먹는" 증상이 났다.
                if (SwallowInput && !injected)
                    return (IntPtr)1;
            }

            return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // 휠 메시지를 만들어서 알린다. (MessageType.MouseWheel 의 약속대로)
        private void RaiseWheel(int delta, bool horizontal)
        {
            if (delta == 0)
                return;

            InputCaptured?.Invoke(new InputMessage
            {
                Type = MessageType.MouseWheel,
                Value1 = delta,
                Value2 = horizontal ? 1 : 0
            });
        }

        // 버튼 메시지를 만들어서 알린다. 위에서 여섯 번 반복되는 걸 줄이려고 뺐다.
        private void RaiseButton(int button, bool isDown)
        {
            InputCaptured?.Invoke(new InputMessage
            {
                Type = MessageType.MouseButton,
                Value1 = button,
                Value2 = isDown ? 1 : 0
            });
        }
    }
}
