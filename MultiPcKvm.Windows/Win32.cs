using System.Runtime.InteropServices;

namespace MultiPcKvm.Windows
{
    // Windows 가 제공하는 기능(Win32 API)을 C#에서 쓰기 위한 선언 모음.
    //
    // 마우스 조작 같은 건 C#에 기본으로 없다.
    // Windows 안에 이미 들어있는 user32.dll 이라는 파일에 그 기능이 있고,
    // 여기에 "그 함수를 쓰겠다" 고 선언해두면 C#에서 부를 수 있게 된다.
    //
    // 필요한 게 생길 때마다 여기에 하나씩 추가한다.
    internal static class Win32
    {
        // ── 마우스 커서 ────────────────────────────────────────

        // 커서를 지정한 화면 좌표로 즉시 옮긴다.
        // 화면 왼쪽 위 구석이 (0, 0) 이고, 오른쪽·아래로 갈수록 숫자가 커진다.
        //
        // DllImport = "user32.dll 안에 있는 SetCursorPos 함수를 쓰겠다"
        // extern    = "이 함수의 내용은 여기 없다. 밖에 있다"
        //             그래서 중괄호 { } 없이 세미콜론으로 끝난다.
        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);


        // 지금 커서가 어디 있는지 알아낸다.
        // 삼키기를 시작할 때 "기준점" 을 기억해두는 데 쓴다.
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        // 화면 좌표 한 점
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        // 화면 크기 같은 시스템 정보를 얻는다.
        // 무엇을 물어볼지는 아래 번호로 지정한다.
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        public const int SM_CXSCREEN = 0;   // 주 모니터 가로 픽셀 수
        public const int SM_CYSCREEN = 1;   // 주 모니터 세로 픽셀 수


        // ── 입력 주입 ──────────────────────────────────────────

        // 마우스 클릭이나 키 입력을 "만들어서" Windows 에 밀어 넣는다.
        // 이 함수를 통과한 입력은 진짜 마우스·키보드에서 온 것과 똑같이 취급된다.
        //
        // 커서 이동과 달리 왜 이렇게 복잡한가:
        //   이동은 "위치"만 바꾸면 끝이지만, 클릭은 Windows 가
        //   "지금 커서 밑에 있는 창이 뭐지? 거기에 전달해야겠다" 를 판단해야 한다.
        //   그래서 좌표 두 개가 아니라 구조체로 정보를 담아서 넘긴다.
        //
        // nInputs : 몇 개를 보낼 건지
        // pInputs : 보낼 입력들이 담긴 배열
        // cbSize  : INPUT 구조체 하나의 크기 (Windows 가 버전 확인용으로 쓴다)
        // 반환값  : 실제로 처리된 개수. 0이면 실패한 것이다.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);


        // ── SendInput 에 넘길 데이터 모양 ──────────────────────
        //
        // struct = 값 여러 개를 하나로 묶은 것. class 와 비슷하지만 더 단순하다.
        // 아래 구조체들은 우리가 마음대로 만든 게 아니라,
        // Windows 가 "이 모양으로 담아서 줘" 라고 정해놓은 규격이다.
        // 그래서 필드 이름과 순서를 바꾸면 안 된다.

        // 입력 하나를 나타낸다.
        // type 이 0이면 "이건 마우스 입력", 1이면 "이건 키보드 입력" 이라는 뜻.
        //
        // LayoutKind.Sequential = "적어놓은 순서 그대로 메모리에 배치해라".
        //   C#은 평소에 성능을 위해 순서를 바꿔도 되는데,
        //   Windows 에 넘길 데이터는 순서가 정확해야 하므로 못 바꾸게 막는다.
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        // 마우스 정보와 키보드 정보가 "같은 자리"를 나눠 쓴다.
        //
        // FieldOffset(0) 이 두 개인 건 오타가 아니다.
        // "둘 다 0번 자리에서 시작한다" = 둘 중 하나만 쓴다는 뜻이다.
        // 위의 type 값이 뭐냐에 따라 Windows 가 어느 쪽으로 읽을지 결정한다.
        // (마우스 입력이면서 동시에 키보드 입력일 수는 없으니 자리를 아끼는 것)
        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        // 마우스 입력 정보
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;              // 가로 이동량 (또는 절대 좌표)
            public int dy;              // 세로 이동량 (또는 절대 좌표)
            public uint mouseData;      // 휠을 굴릴 때만 쓴다. 클릭할 땐 0
            public uint dwFlags;        // 무슨 동작인지 (아래 상수 중 하나)
            public uint time;           // 0으로 두면 Windows 가 알아서 채운다
            public IntPtr dwExtraInfo;  // 추가 정보. 안 쓰면 0
        }

        // 키보드 입력 정보
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;          // 가상 키 코드 (어느 키인지. 아래 설명 참고)
            public ushort wScan;        // 키보드 하드웨어 상의 위치 번호. 지금은 안 쓴다
            public uint dwFlags;        // 누름/뗌, 확장 키 여부
            public uint time;           // 0으로 두면 Windows 가 알아서 채운다
            public IntPtr dwExtraInfo;  // 추가 정보. 안 쓰면 0
        }


        // ── 상수 ───────────────────────────────────────────────
        //
        // const = 절대 안 바뀌는 값에 이름을 붙여둔 것.
        // 0x0002 같은 숫자를 코드에 직접 쓰면 나중에 무슨 뜻인지 알 수 없으므로
        // 이름을 붙여서 쓴다. 이 숫자들은 Windows 가 정해놓은 값이다.

        public const uint INPUT_MOUSE    = 0;    // "이 입력은 마우스다"
        public const uint INPUT_KEYBOARD = 1;    // "이 입력은 키보드다"

        // 마우스 버튼을 누른다 / 뗀다
        // DOWN 과 UP 이 따로 있는 이유: 드래그 때문이다.
        // 누른 채로 움직여야 하므로 "눌렀다 뗐다" 를 하나로 묶으면 안 된다.
        public const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;   // 왼쪽 누름
        public const uint MOUSEEVENTF_LEFTUP     = 0x0004;   // 왼쪽 뗌
        public const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;   // 오른쪽 누름
        public const uint MOUSEEVENTF_RIGHTUP    = 0x0010;   // 오른쪽 뗌
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;   // 가운데(휠) 누름
        public const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;   // 가운데(휠) 뗌
        public const uint MOUSEEVENTF_WHEEL      = 0x0800;   // 세로 휠 (mouseData 에 굴린 양)
        public const uint MOUSEEVENTF_HWHEEL     = 0x1000;   // 가로 휠 (mouseData 에 굴린 양)

        // 키보드
        //
        // 마우스와 달리 "누름" 에 해당하는 값이 따로 없다.
        // 아무 표시도 안 하면 누름이고, KEYUP 을 붙이면 뗌이다.
        public const uint KEYEVENTF_KEYUP = 0x0002;    // 키를 뗌

        // 확장 키 표시.
        //
        // 옛날 키보드에는 방향키가 없었고 숫자패드로 방향을 조작했다.
        // 나중에 방향키가 추가되면서 번호가 모자라서, 숫자패드와 같은 번호를 쓰되
        // "이건 확장 키야" 라는 표시를 하나 더 붙이는 식으로 구분하게 됐다.
        //
        // 이 표시를 안 붙이면 → 방향키를 눌렀는데 숫자패드의 2, 4, 6, 8 이 입력된다.
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;


        // ═══════════════════════════════════════════════════════
        //  여기서부터는 입력을 "가로채는" 쪽 (후킹)
        //
        //  위쪽은 우리가 Windows 에게 시키는 것이었고,
        //  아래쪽은 Windows 가 우리에게 알려주게 만드는 것이다.
        // ═══════════════════════════════════════════════════════

        // Windows 가 입력을 알려줄 때 부를 함수의 "모양" 을 미리 정해둔 것.
        //
        // delegate = "함수를 담는 변수의 타입".
        //   int 변수에 숫자를 담듯이, HookProc 변수에는 함수를 담을 수 있다.
        //   Windows 에게 "입력 생기면 이 함수 좀 불러줘" 하고 함수를 건네줘야 해서 필요하다.
        //
        // nCode  : 0보다 작으면 우리가 건드리지 말고 그냥 넘기라는 뜻
        // wParam : 무슨 일인지 (키를 눌렀나, 뗐나)
        // lParam : 자세한 정보가 들어있는 메모리 주소
        // 반환값 : 0을 돌려주면 입력이 그대로 진행되고,
        //          1을 돌려주면 입력이 여기서 막힌다 (= 삼키기)
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        // 후킹을 건다. "앞으로 키보드(또는 마우스) 입력이 생기면 이 함수를 불러줘"
        //
        // idHook    : 무슨 입력을 잡을지 (WH_KEYBOARD_LL / WH_MOUSE_LL)
        // lpfn      : 알림을 받을 함수
        // hMod      : 이 프로그램의 식별자
        // dwThreadId: 0이면 "모든 프로그램의 입력을 다 잡겠다"
        // 반환값    : 후킹 식별자. 0이면 실패다.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        // 후킹을 푼다. 프로그램 끝낼 때 반드시 해줘야 한다.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        // "나는 볼 일 다 봤으니 다음 프로그램에게 넘겨라".
        //
        // 후킹은 여러 프로그램이 동시에 걸 수 있어서 줄을 서 있다.
        // 이걸 안 부르면 뒤에 선 프로그램들이 입력을 못 받는다.
        // (= 다른 프로그램의 단축키가 갑자기 안 먹게 된다)
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        // 이 프로그램 자신의 식별자를 얻는다. SetWindowsHookEx 에 넘겨줄 용도.
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        public const int WH_KEYBOARD_LL = 13;   // 키보드를 잡겠다
        public const int WH_MOUSE_LL    = 14;   // 마우스를 잡겠다

        // 후킹 함수의 wParam 으로 들어오는 값 = 무슨 일이 일어났는지
        public const int WM_KEYDOWN    = 0x0100;   // 키를 눌렀다
        public const int WM_KEYUP      = 0x0101;   // 키를 뗐다
        public const int WM_SYSKEYDOWN = 0x0104;   // Alt 와 같이 누른 키 (별도로 온다)
        public const int WM_SYSKEYUP   = 0x0105;

        // 키보드 후킹에서 lParam 이 가리키는 곳에 들어있는 정보
        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;         // 어느 키인지 (가상 키 코드)
            public uint scanCode;       // 키보드 하드웨어 상의 위치
            public uint flags;          // 확장 키 여부 등
            public uint time;
            public IntPtr dwExtraInfo;
        }


        // 마우스 후킹에서 wParam 으로 들어오는 값 = 무슨 일이 일어났는지
        public const int WM_MOUSEMOVE   = 0x0200;   // 마우스가 움직였다
        public const int WM_LBUTTONDOWN = 0x0201;   // 왼쪽 눌림
        public const int WM_LBUTTONUP   = 0x0202;   // 왼쪽 뗌
        public const int WM_RBUTTONDOWN = 0x0204;   // 오른쪽 눌림
        public const int WM_RBUTTONUP   = 0x0205;   // 오른쪽 뗌
        public const int WM_MBUTTONDOWN = 0x0207;   // 가운데 눌림
        public const int WM_MBUTTONUP   = 0x0208;   // 가운데 뗌
        public const int WM_MOUSEWHEEL  = 0x020A;   // 세로 휠 (mouseData 위쪽 16비트에 굴린 양)
        public const int WM_MOUSEHWHEEL = 0x020E;   // 가로 휠 (마찬가지)

        // 마우스 후킹에서 lParam 이 가리키는 곳에 들어있는 정보
        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public int pt_x;            // 화면 기준 X 좌표
            public int pt_y;            // 화면 기준 Y 좌표
            public uint mouseData;      // 휠을 굴렸을 때만 의미가 있다
            public uint flags;          // 아래 LLMHF_INJECTED 등이 들어온다
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // "이 입력은 진짜 마우스가 아니라 프로그램이 만들어낸 것" 이라는 표시.
        //
        // 우리도 SendInput 으로 입력을 만들어내므로, 그게 다시 우리 후킹에 잡힌다.
        // 걸러내지 않으면 "내가 만든 입력을 내가 다시 잡아서 또 보내는" 무한 반복이 된다.
        public const uint LLMHF_INJECTED = 0x00000001;

        // 키보드 쪽의 같은 표시.
        // 마우스(0x01)와 값이 다르니 섞어 쓰면 안 된다.
        public const uint LLKHF_INJECTED = 0x00000010;


        // ── 메시지 루프 ────────────────────────────────────────
        //
        // 후킹은 "메시지 루프" 가 돌고 있는 스레드에서만 알림이 온다.
        // 메시지 루프란 "할 일 없니? 있으면 처리하고, 없으면 기다린다" 를
        // 무한 반복하는 것이다. Windows 프로그램의 심장 같은 것이다.
        //
        // 콘솔 프로그램에는 이게 없어서 직접 만들어줘야 한다.

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        // 할 일이 생길 때까지 기다렸다가 하나 꺼내온다.
        // 반환값이 0이면 "이제 그만해라" 는 뜻이라 루프를 빠져나가야 한다.
        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        // 다른 스레드에게 메시지를 보낸다.
        // 후킹 스레드를 끝낼 때 "그만해라(WM_QUIT)" 를 보내는 데 쓴다.
        [DllImport("user32.dll")]
        public static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        // 지금 이 코드가 돌고 있는 스레드의 번호를 얻는다.
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        public const uint WM_QUIT = 0x0012;   // "그만해라"
    }
}
