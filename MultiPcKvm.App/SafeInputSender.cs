using MultiPcKvm.Common;

namespace MultiPcKvm.App
{
    // ─────────────────────────────────────────────────────────────
    //  입력을 실행하면서 "지금 눌려 있는 것" 을 같이 기억해둔다.
    //
    //  ─── 왜 필요한가 ───
    //
    //  키는 누름과 뗌이 짝이다. 둘 중 하나만 도착하면 망가진다.
    //
    //    Shift 누름 도착  →  상대 PC: Shift 눌린 상태
    //    [ 여기서 연결이 끊김 ]
    //    Shift 뗌 못 도착  →  상대 PC: Shift 가 영원히 눌린 상태
    //
    //  마우스 버튼은 그 PC에서 한 번 클릭하면 풀린다.
    //  그런데 키는 그 PC에서 직접 그 키를 눌렀다 떼기 전까지 안 풀린다.
    //  Ctrl 이나 Alt 가 걸리면 그 PC로 사실상 아무것도 못 한다.
    //
    //  ─── 어떻게 막는가 ───
    //
    //  실행할 때마다 목록을 갱신해두고, 문제가 생기면 목록에 있는 걸
    //  전부 떼준다. 보내는 쪽이 이미 사라졌어도 받는 쪽이 스스로 복구한다.
    //
    //  이 클래스는 진짜 실행기(WindowsInputSender)를 감싸기만 한다.
    //  감싼 쪽은 자기가 감싸였는지 모른다.
    // ─────────────────────────────────────────────────────────────
    public class SafeInputSender : IInputSender
    {
        private readonly IInputSender _inner;

        // 지금 눌려 있다고 기억하는 것들
        private readonly HashSet<int> _pressedKeys = new HashSet<int>();
        private readonly HashSet<int> _pressedButtons = new HashSet<int>();

        // 실행하는 쪽(네트워크 수신 스레드)과 풀어주는 쪽(감시 스레드)이
        // 서로 다른 스레드라서 자물쇠가 필요하다.
        private readonly object _lock = new object();

        public SafeInputSender(IInputSender inner)
        {
            _inner = inner;
        }

        // 지금 눌려 있는 것의 개수 (화면에 표시용)
        public int PressedCount
        {
            get
            {
                lock (_lock)
                    return _pressedKeys.Count + _pressedButtons.Count;
            }
        }

        public void Apply(InputMessage message)
        {
            // 먼저 기록하고
            lock (_lock)
            {
                switch (message.Type)
                {
                    case MessageType.KeyDown:
                        _pressedKeys.Add(message.Value1);
                        break;

                    case MessageType.KeyUp:
                        _pressedKeys.Remove(message.Value1);
                        break;

                    case MessageType.MouseButton:
                        if (message.Value2 == 1)
                            _pressedButtons.Add(message.Value1);
                        else
                            _pressedButtons.Remove(message.Value1);
                        break;
                }
            }

            // 그 다음 실제로 실행한다.
            _inner.Apply(message);
        }

        // 눌려 있다고 기억하는 걸 전부 떼준다.
        //
        // 언제 부르나:
        //   · 연결이 끊겼을 때
        //   · 프로그램을 끝낼 때
        //
        // 여러 번 불러도 안전하다. 목록이 비어 있으면 아무 일도 안 한다.
        public void ReleaseAll()
        {
            List<int> keys;
            List<int> buttons;

            // 자물쇠 안에서는 목록만 꺼내고 바로 나온다.
            // 실제 실행은 자물쇠 밖에서 한다. (오래 잡고 있으면 안 된다)
            lock (_lock)
            {
                if (_pressedKeys.Count == 0 && _pressedButtons.Count == 0)
                    return;

                keys = new List<int>(_pressedKeys);
                buttons = new List<int>(_pressedButtons);

                _pressedKeys.Clear();
                _pressedButtons.Clear();
            }

            foreach (int key in keys)
            {
                _inner.Apply(new InputMessage
                {
                    Type = MessageType.KeyUp,
                    Value1 = key
                });
            }

            foreach (int button in buttons)
            {
                _inner.Apply(new InputMessage
                {
                    Type = MessageType.MouseButton,
                    Value1 = button,
                    Value2 = 0        // 0 = 뗌
                });
            }

            Console.WriteLine();
            Console.WriteLine(
                $"  [안전] 눌린 채 남아 있던 키 {keys.Count}개, " +
                $"버튼 {buttons.Count}개를 떼줬습니다.");
        }
    }
}
