using MultiPcKvm.Common;

namespace MultiPcKvm.App
{
    // 커서가 지금 어느 PC에 있는가
    public enum CursorLocation
    {
        Local,      // 내 PC
        Remote      // 상대 PC
    }

    // 상대 PC 화면이 내 화면의 어느 쪽에 붙어 있는가
    public enum RemotePosition
    {
        Left,
        Right,
        Top,
        Bottom
    }

    // ─────────────────────────────────────────────────────────────
    //  입력을 어디로 보낼지 결정한다. 이 프로젝트의 두뇌에 해당한다.
    //
    //  여기까지 만든 조각들을 이어붙이는 자리다.
    //    가로채기(Capture) → [ 여기 ] → 상대 PC로 전송
    //                          ↓
    //                    삼키기 켜고 끄기
    //                    내 커서 위치 되돌리기
    //
    //  ─── 판단을 한쪽에서만 하는 이유 ───
    //
    //  "커서가 지금 어느 PC에 있는가" 를 두 PC가 각자 판단하면,
    //  통신이 한 번만 어긋나도 양쪽 생각이 달라진다.
    //  "둘 다 내 차례다" 또는 "둘 다 상대 차례다" 가 되면 복구가 어렵다.
    //
    //  그래서 판단은 보내는 쪽에서만 한다.
    //  받는 쪽은 전달받은 좌표대로 실행하기만 하고 아무 판단도 하지 않는다.
    // ─────────────────────────────────────────────────────────────
    public class InputRouter
    {
        private readonly IInputCapture _capture;
        private readonly IInputSender _localSender;

        // ── 화면 정보 ─────────────────────────────────────────

        // 내 화면 크기
        public int LocalWidth { get; set; } = 1920;
        public int LocalHeight { get; set; } = 1080;

        // 상대 화면 크기
        public int RemoteWidth { get; set; } = 1920;
        public int RemoteHeight { get; set; } = 1080;

        // ── 화면 보기 창을 쓰는 동안 ─────────────────────────
        //
        // KeyboardToRemote: 커서가 내 PC 에 있어도 키보드는 상대에게 보낸다.
        //                   (화면 보기 창이 선택돼 있는 동안. 마우스는 창이 직접 보낸다)
        // EdgeCrossingPaused: 화면 끝으로 밀어도 넘어가지 않는다.
        //                   (창을 화면 끝까지 키워 쓰면 창 안에서 움직이다 넘어가 버리므로)
        public volatile bool KeyboardToRemote;
        public volatile bool EdgeCrossingPaused;

        // 상대 화면 크기를 상대에게서 실제로 받았는가. (ScreenInfo, KvmSession 이 켠다)
        // false 면 위 크기는 짐작한 값이다. 화면(WPF)이 이걸 보고 저장할지 정한다.
        public bool RemoteSizeKnown { get; set; }

        // 상대 화면이 내 화면 옆 어디에 붙어 있는가 (쪽 + 밀린 양). ScreenLayout.cs 참고.
        //
        // null 이면 "아직 정하지 않음" → 처음 위치(왼쪽, 가운데 맞춤)를 쓴다.
        //   처음 위치는 상대 화면 크기에 따라 달라진다. null 로 두면
        //   연결 직후 실제 크기를 받았을 때 저절로 다시 가운데로 맞춰진다.
        //
        // 화면 배치 창에서 바꾸면 연결 중에도 바로 적용된다.
        // 입력 처리 스레드가 읽는 중에 화면 스레드가 바꿀 수 있으므로
        // 값 두 개를 따로 두지 않고 객체 하나를 통째로 바꿔 끼운다.
        public ScreenPlacement? Placement { get; set; }

        // 예전 코드(콘솔 시연 Program.cs)와 맞추려고 남겨둔 것.
        // 쪽만 바꾸고 밀린 양은 그대로 둔다.
        public RemotePosition RemoteAt
        {
            get => EffectivePlacement.Side;
            set => Placement = EffectivePlacement with { Side = value };
        }

        // 지금 실제로 쓰는 배치. (화면 안내 문구에도 쓴다)
        //
        //   · 정하지 않았으면 처음 위치
        //   · 저장해 둔 배치는 예전 상대 화면 크기 기준이라, 연결 후 실제 크기를 받으면
        //     범위를 벗어날 수 있다. 쓸 때마다 잘라서 항상 맞닿아 있게 한다.
        public ScreenPlacement EffectivePlacement
        {
            get
            {
                ScreenPlacement? chosen = Placement;

                return chosen == null
                    ? ScreenLayout.DefaultPlacement(LocalWidth, LocalHeight, RemoteWidth, RemoteHeight)
                    : ScreenLayout.Clamp(chosen, LocalWidth, LocalHeight, RemoteWidth, RemoteHeight);
            }
        }

        private ScreenPlacement Effective() => EffectivePlacement;


        // ── 현재 상태 ─────────────────────────────────────────

        public CursorLocation Location { get; private set; } = CursorLocation.Local;

        // 상대 화면에서 커서가 있다고 계산된 위치.
        // 이동량을 계속 더해서 유지한다.
        public int RemoteX { get; private set; }
        public int RemoteY { get; private set; }


        // ── 바깥으로 내보내는 것 ──────────────────────────────

        // 상대 PC에 보낼 메시지. 네트워크 담당이 이걸 받아서 보내면 된다.
        public event Action<InputMessage>? SendToRemote;

        // 커서가 넘어가거나 돌아올 때 알려준다. (화면 표시용)
        public event Action<CursorLocation, CursorLocation, string>? LocationChanged;


        public InputRouter(IInputCapture capture, IInputSender localSender)
        {
            _capture = capture;
            _localSender = localSender;

            // 가로챈 입력을 전부 이쪽으로 받는다.
            _capture.InputCaptured += OnInput;
        }


        // ── 입력이 들어올 때마다 불린다 ───────────────────────

        private void OnInput(InputMessage msg)
        {
            switch (msg.Type)
            {
                case MessageType.MouseMove:
                    // 화면 좌표가 들어왔다는 건 내 PC에서 커서가 움직였다는 뜻이다.
                    // 경계에 닿았는지만 본다.
                    if (Location == CursorLocation.Local)
                        CheckEdge(msg.Value1, msg.Value2);
                    break;

                case MessageType.MouseMoveDelta:
                    // 이동량이 들어왔다는 건 삼키는 중이라는 뜻이다.
                    if (Location == CursorLocation.Remote)
                        MoveOnRemote(msg.Value1, msg.Value2);
                    break;

                default:
                    // 버튼·휠·키는 판단할 게 없다.
                    // 커서가 상대에게 있으면 그대로 넘기고, 내 PC에 있으면 아무것도 안 한다.
                    // (내 PC에 있을 때는 삼키지 않으므로 Windows 가 알아서 처리한다)
                    //
                    // 단, 화면 보기 창을 쓰는 중(KeyboardToRemote)이면 키는 커서 위치와 상관없이 보낸다.
                    bool isKey = msg.Type == MessageType.KeyDown || msg.Type == MessageType.KeyUp;

                    if (Location == CursorLocation.Remote || (KeyboardToRemote && isKey))
                        SendToRemote?.Invoke(msg);
                    break;
            }
        }


        // ── 내 PC → 상대 PC ───────────────────────────────────

        // 커서가 화면 경계에 닿았는지 확인한다.
        //
        // 상대 화면과 "맞닿은 구간" 에서만 넘어간다.
        // 예) 상대 화면을 오른쪽에 300 픽셀 내려 붙였으면,
        //     내 화면 오른쪽 끝 중 y 가 300 보다 위인 곳은 벽이다. 넘어가지 않는다.
        // Windows 에서 모니터 두 개를 엇갈리게 놓았을 때와 같다.
        private void CheckEdge(int x, int y)
        {
            // 화면 보기 창을 쓰는 동안은 넘어가지 않는다.
            if (EdgeCrossingPaused)
                return;

            ScreenPlacement p = Effective();

            bool atEdge = p.Side switch
            {
                RemotePosition.Right  => x >= LocalWidth - 1,
                RemotePosition.Left   => x <= 0,
                RemotePosition.Top    => y <= 0,
                RemotePosition.Bottom => y >= LocalHeight - 1,
                _ => false
            };

            if (!atEdge)
                return;

            // 붙은 변을 따라 상대 화면 안에 들어가는 자리인가
            bool vertical = p.Side == RemotePosition.Right || p.Side == RemotePosition.Left;

            int along = vertical ? y - p.Offset : x - p.Offset;
            int remoteLength = vertical ? RemoteHeight : RemoteWidth;

            if (along < 0 || along > remoteLength - 1)
                return;     // 맞닿지 않은 구간 → 벽

            GoToRemote(x, y, p);
        }

        // 커서를 상대 PC로 넘긴다.
        private void GoToRemote(int x, int y, ScreenPlacement p)
        {
            // ★ 좌표는 비율로 늘리거나 줄이지 않고 픽셀 그대로 이어 붙인다.
            //
            //   배치 창에서 놓은 모양 그대로 이어져야 하기 때문이다.
            //   상대를 오른쪽에 300 픽셀 내려 붙였으면,
            //   내 화면 y = 500 에서 넘어가면 상대 화면 y = 200 으로 들어간다. (500 - 300)
            //   Windows 에서 모니터 두 개를 이어 쓸 때와 같은 방식이다.
            //
            //   (예전에는 변 전체를 비율로 맞췄다. 그러면 배치를 엇갈리게 놓은 의미가 없어진다)
            switch (p.Side)
            {
                case RemotePosition.Right:
                    RemoteX = 0;                              // 상대 화면 왼쪽 끝으로 들어간다
                    RemoteY = y - p.Offset;
                    break;

                case RemotePosition.Left:
                    RemoteX = RemoteWidth - 1;                // 상대 화면 오른쪽 끝으로 들어간다
                    RemoteY = y - p.Offset;
                    break;

                case RemotePosition.Top:
                    RemoteX = x - p.Offset;
                    RemoteY = RemoteHeight - 1;               // 상대 화면 아래쪽 끝으로 들어간다
                    break;

                case RemotePosition.Bottom:
                    RemoteX = x - p.Offset;
                    RemoteY = 0;                              // 상대 화면 위쪽 끝으로 들어간다
                    break;
            }

            // 혹시 모를 계산 오차로 화면 밖 좌표가 가지 않게 한 번 더 자른다.
            RemoteX = Math.Clamp(RemoteX, 0, RemoteWidth - 1);
            RemoteY = Math.Clamp(RemoteY, 0, RemoteHeight - 1);

            // 순서가 중요하다. 상태를 먼저 바꾸고 삼키기를 켠다.
            CursorLocation prev = Location;
            Location = CursorLocation.Remote;

            _capture.SwallowInput = true;

            LocationChanged?.Invoke(prev, Location,
                $"내 화면 ({x}, {y}) → 상대 화면 ({RemoteX}, {RemoteY})");

            // 상대에게 첫 좌표를 알려준다.
            SendToRemote?.Invoke(new InputMessage
            {
                Type = MessageType.MouseMove,
                Value1 = RemoteX,
                Value2 = RemoteY
            });
        }


        // ── 상대 PC 위에서 움직이는 중 ────────────────────────

        private void MoveOnRemote(int dx, int dy)
        {
            // ★ 이동량은 더한다. 덮어쓰면 움직인 거리가 사라진다.
            int nx = RemoteX + dx;
            int ny = RemoteY + dy;

            ScreenPlacement p = Effective();

            // 상대 화면 밖으로 나가려 하는가? 그렇다면 내 PC로 돌아올 때다.
            //
            // 들어간 쪽의 반대편이 아니라 "들어간 쪽" 으로 되돌아 나온다.
            // 상대가 오른쪽에 있으면 상대 화면 왼쪽 끝으로 나와야 내 화면 오른쪽으로 돌아온다.
            bool leaving = p.Side switch
            {
                RemotePosition.Right  => nx < 0,
                RemotePosition.Left   => nx > RemoteWidth - 1,
                RemotePosition.Top    => ny > RemoteHeight - 1,
                RemotePosition.Bottom => ny < 0,
                _ => false
            };

            // 단, 맞닿은 구간에서 나갈 때만 돌아온다. 나머지는 벽이다.
            //
            // 예) 상대를 오른쪽에 300 픽셀 내려 붙였고 상대 화면이 더 길면,
            //     상대 화면 왼쪽 끝 아래쪽 일부는 내 화면 옆이 아니다. 거기서는 안 넘어간다.
            if (leaving)
            {
                bool vertical = p.Side == RemotePosition.Right || p.Side == RemotePosition.Left;

                int along = vertical
                    ? Math.Clamp(ny, 0, RemoteHeight - 1) + p.Offset
                    : Math.Clamp(nx, 0, RemoteWidth - 1) + p.Offset;

                int localLength = vertical ? LocalHeight : LocalWidth;

                if (along >= 0 && along <= localLength - 1)
                {
                    ComeBack(nx, ny);
                    return;
                }
            }

            // 상대 화면 안에 머무르도록 자른다.
            RemoteX = Math.Clamp(nx, 0, RemoteWidth - 1);
            RemoteY = Math.Clamp(ny, 0, RemoteHeight - 1);

            SendToRemote?.Invoke(new InputMessage
            {
                Type = MessageType.MouseMove,
                Value1 = RemoteX,
                Value2 = RemoteY
            });
        }


        // ── 상대 PC → 내 PC ───────────────────────────────────

        // 커서를 내 PC로 되찾아온다.
        public void ComeBack(int lastRemoteX, int lastRemoteY)
        {
            if (Location == CursorLocation.Local)
                return;

            // 내 화면 어디로 돌아올지 계산한다. 넘어갈 때의 반대 과정이다.
            // (상대 좌표 + 밀린 양 = 내 좌표)
            ScreenPlacement p = Effective();

            int rx = Math.Clamp(lastRemoteX, 0, RemoteWidth - 1);
            int ry = Math.Clamp(lastRemoteY, 0, RemoteHeight - 1);

            int localX, localY;

            switch (p.Side)
            {
                case RemotePosition.Right:
                    localX = LocalWidth - 1;
                    localY = ry + p.Offset;
                    break;

                case RemotePosition.Left:
                    localX = 0;
                    localY = ry + p.Offset;
                    break;

                case RemotePosition.Top:
                    localX = rx + p.Offset;
                    localY = 0;
                    break;

                default: // Bottom
                    localX = rx + p.Offset;
                    localY = LocalHeight - 1;
                    break;
            }

            localX = Math.Clamp(localX, 0, LocalWidth - 1);
            localY = Math.Clamp(localY, 0, LocalHeight - 1);

            // ★ 삼키기를 먼저 끈다. 켜진 채로 커서를 옮기면 그 이동마저 막힌다.
            _capture.SwallowInput = false;

            CursorLocation prev = Location;
            Location = CursorLocation.Local;

            // 내 커서를 화면 경계에 갖다 놓는다.
            _localSender.Apply(new InputMessage
            {
                Type = MessageType.MouseMove,
                Value1 = localX,
                Value2 = localY
            });

            LocationChanged?.Invoke(prev, Location,
                $"상대 화면 ({lastRemoteX}, {lastRemoteY}) → 내 화면 ({localX}, {localY})");
        }

        // 연결이 끊겼을 때 바깥에서 부른다. 조건 없이 커서를 되찾아온다.
        public void ForceComeBack()
        {
            if (Location == CursorLocation.Remote)
                ComeBack(RemoteX, RemoteY);
        }
    }
}
