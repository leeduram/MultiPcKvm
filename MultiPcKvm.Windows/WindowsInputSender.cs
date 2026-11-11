using System.Runtime.InteropServices;
using MultiPcKvm.Common;

namespace MultiPcKvm.Windows
{
    // 네트워크로 받은 메시지대로 이 PC에서 실제 마우스/키보드 입력을 만들어낸다.
    // Common 의 IInputSender 설계도를 따른다.
    //
    // 예: 상대가 "마우스를 (500, 400) 으로 옮겨라" 는 메시지를 보내면
    //     이 클래스가 실제로 커서를 그 자리로 옮긴다.
    public class WindowsInputSender : IInputSender
    {
        // 메시지 하나를 받아서 종류에 맞게 처리한다.
        public void Apply(InputMessage message)
        {
            // switch = message.Type 값에 따라 갈라진다.
            //          if 를 여러 번 쓰는 것과 같지만 종류가 많을 때 더 읽기 쉽다.
            switch (message.Type)
            {
                case MessageType.MouseMove:
                    // 마우스 이동일 때는 Value1 이 X, Value2 가 Y 좌표다.
                    // (이 약속은 MessageType.cs 에 표로 적어뒀다)
                    Win32.SetCursorPos(message.Value1, message.Value2);
                    break;

                case MessageType.MouseButton:
                    // 버튼일 때는 Value1 이 버튼 번호, Value2 가 누름(1)/뗌(0) 이다.
                    // Value2 == 1 은 "Value2 가 1이면 true, 아니면 false" 라는 뜻.
                    ApplyMouseButton(message.Value1, message.Value2 == 1);
                    break;

                case MessageType.KeyDown:
                    // 키일 때는 Value1 이 키 번호(가상 키 코드)다.
                    ApplyKey(message.Value1, isDown: true);
                    break;

                case MessageType.KeyUp:
                    ApplyKey(message.Value1, isDown: false);
                    break;

                case MessageType.MouseWheel:
                    // 휠일 때는 Value1 이 굴린 양(한 칸 120), Value2 가 0 세로 / 1 가로다.
                    ApplyWheel(message.Value1, horizontal: message.Value2 == 1);
                    break;
            }
        }

        // ─────────────────────────────────────────────────────────
        //  마우스 버튼
        // ─────────────────────────────────────────────────────────

        // 마우스 버튼 하나를 누르거나 뗀다.
        //
        // private = 이 클래스 안에서만 쓰는 함수.
        //           바깥에서는 Apply 만 쓰면 되므로 감춰둔다.
        private void ApplyMouseButton(int button, bool isDown)
        {
            // ── 1단계: 버튼 번호와 누름/뗌 을 Windows 가 아는 값으로 바꾼다 ──
            //
            // switch 식(expression) 이라고 하며, 아래는 이런 뜻이다:
            //   button 이 1이면 → isDown 이 참이면 LEFTDOWN, 아니면 LEFTUP
            //   button 이 2이면 → ...
            //   _ 는 "그 외 전부" 를 뜻한다.
            uint flag = button switch
            {
                1 => isDown ? Win32.MOUSEEVENTF_LEFTDOWN   : Win32.MOUSEEVENTF_LEFTUP,
                2 => isDown ? Win32.MOUSEEVENTF_RIGHTDOWN  : Win32.MOUSEEVENTF_RIGHTUP,
                3 => isDown ? Win32.MOUSEEVENTF_MIDDLEDOWN : Win32.MOUSEEVENTF_MIDDLEUP,
                _ => 0
            };

            // 모르는 버튼 번호가 왔으면 아무것도 하지 않는다.
            // 네트워크로 들어온 값이라 이상한 숫자가 올 수도 있으므로 확인한다.
            if (flag == 0)
                return;

            // ── 2단계: 보낼 입력 하나를 만든다 ──
            //
            // dx, dy 를 0 으로 두는 이유:
            //   커서는 이미 SetCursorPos 로 원하는 자리에 옮겨놓았다.
            //   그래서 "지금 있는 그 자리에서 눌러라" 는 뜻이 된다.
            Win32.INPUT[] inputs = new Win32.INPUT[1];

            inputs[0].type = Win32.INPUT_MOUSE;   // 이건 마우스 입력이다
            inputs[0].u.mi = new Win32.MOUSEINPUT
            {
                dx = 0,
                dy = 0,
                mouseData = 0,
                dwFlags = flag,          // 위에서 정한 누름/뗌 값
                time = 0,
                dwExtraInfo = IntPtr.Zero
            };

            // ── 3단계: Windows 에 밀어 넣는다 ──
            //
            // Marshal.SizeOf 로 크기를 구하는 이유:
            //   32비트와 64비트에서 구조체 크기가 달라진다.
            //   숫자를 직접 적어놓으면 한쪽 환경에서 조용히 실패한다.
            //   그래서 "지금 이 환경에서의 크기" 를 그때그때 계산해서 넘긴다.
            Win32.SendInput(1, inputs, Marshal.SizeOf<Win32.INPUT>());
        }

        // ─────────────────────────────────────────────────────────
        //  마우스 휠
        // ─────────────────────────────────────────────────────────

        // 휠을 굴린다. 지금 커서가 있는 자리의 창이 받는다. (Windows 와 같다)
        private void ApplyWheel(int delta, bool horizontal)
        {
            // 0 이면 굴린 게 없다. 너무 큰 값은 네트워크로 들어온 이상한 값이므로 무시한다.
            // (한 번에 수십 칸 = 수천 이상은 실제 마우스에서 나오지 않는다)
            if (delta == 0 || Math.Abs(delta) > 120 * 50)
                return;

            Win32.INPUT[] inputs = new Win32.INPUT[1];
            inputs[0].type = Win32.INPUT_MOUSE;
            inputs[0].u.mi = new Win32.MOUSEINPUT
            {
                dx = 0,
                dy = 0,

                // mouseData 는 부호 없는 칸이지만, 음수(아래로)는 그대로 비트를 넘기면 된다.
                // unchecked = 음수를 부호 없는 수로 바꿀 때 오류 내지 말고 비트 그대로 바꿔라.
                mouseData = unchecked((uint)delta),
                dwFlags = horizontal ? Win32.MOUSEEVENTF_HWHEEL : Win32.MOUSEEVENTF_WHEEL,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            };

            Win32.SendInput(1, inputs, Marshal.SizeOf<Win32.INPUT>());
        }

        // ─────────────────────────────────────────────────────────
        //  키보드
        // ─────────────────────────────────────────────────────────

        // 키 하나를 누르거나 뗀다.
        //
        // virtualKey = "가상 키 코드". 키보드의 각 키에 Windows 가 붙여둔 번호다.
        //   예) A 키 = 0x41, Enter = 0x0D, Shift = 0x10
        //   "가상" 인 이유: 실제 키보드의 물리적 위치와 상관없이,
        //   한글 키보드든 영문 키보드든 A 자리는 항상 0x41 이기 때문이다.
        private void ApplyKey(int virtualKey, bool isDown)
        {
            // 가상 키 코드는 1 ~ 254 범위다. 벗어나면 잘못된 값이므로 무시한다.
            // 네트워크로 들어온 값이라 확인이 필요하다.
            if (virtualKey <= 0 || virtualKey > 254)
                return;

            uint flags = 0;

            // 아무 표시도 안 하면 "누름" 이다. 뗄 때만 표시를 붙인다.
            if (!isDown)
                flags |= Win32.KEYEVENTF_KEYUP;

            // 방향키 같은 확장 키는 표시를 하나 더 붙여야 한다.
            //
            // |= 는 "기존 값에 이 표시를 더한다" 는 뜻이다.
            // 표시들이 서로 다른 자리를 쓰도록 만들어져 있어서
            // 이렇게 여러 개를 한 숫자에 겹쳐 담을 수 있다.
            if (IsExtendedKey(virtualKey))
                flags |= Win32.KEYEVENTF_EXTENDEDKEY;

            Win32.INPUT[] inputs = new Win32.INPUT[1];

            inputs[0].type = Win32.INPUT_KEYBOARD;   // 이건 키보드 입력이다
            inputs[0].u.ki = new Win32.KEYBDINPUT
            {
                wVk = (ushort)virtualKey,   // 어느 키인지
                wScan = 0,                  // 지금은 안 쓴다
                dwFlags = flags,            // 위에서 정한 누름/뗌 + 확장 키 표시
                time = 0,
                dwExtraInfo = IntPtr.Zero
            };

            Win32.SendInput(1, inputs, Marshal.SizeOf<Win32.INPUT>());
        }

        // 이 키가 "확장 키" 인지 알려준다.
        //
        // 확장 키가 뭔지는 Win32.KEYEVENTF_EXTENDEDKEY 설명에 적어뒀다.
        // 짧게: 이 목록에 있는 키들은 표시를 안 붙이면 숫자패드 키로 입력된다.
        //
        // static = 이 클래스의 어떤 값도 안 쓰고, 넘겨받은 것만 보고 답하는 함수.
        //          그래서 객체를 안 만들어도 쓸 수 있다.
        private static bool IsExtendedKey(int virtualKey)
        {
            switch (virtualKey)
            {
                case 0x21:  // Page Up
                case 0x22:  // Page Down
                case 0x23:  // End
                case 0x24:  // Home
                case 0x25:  // ←
                case 0x26:  // ↑
                case 0x27:  // →
                case 0x28:  // ↓
                case 0x2D:  // Insert
                case 0x2E:  // Delete
                case 0x5B:  // 왼쪽 Windows 키
                case 0x5C:  // 오른쪽 Windows 키
                case 0x6F:  // 숫자패드 나누기( / )
                case 0xA3:  // 오른쪽 Ctrl
                case 0xA5:  // 오른쪽 Alt (한글 키보드의 한자 키 자리)
                    return true;

                default:
                    return false;
            }
        }
    }
}
