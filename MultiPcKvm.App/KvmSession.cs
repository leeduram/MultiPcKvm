using System.Collections.Concurrent;
using System.Diagnostics;
using MultiPcKvm.Common;
using MultiPcKvm.Network.Connection;

namespace MultiPcKvm.App
{
    // ─────────────────────────────────────────────────────────────
    //  지금까지 만든 부품들을 하나로 잇는다.
    //
    //  각 부품은 서로를 모른다. 알 필요도 없다.
    //  이 클래스가 가운데 서서 중계한다.
    //
    //      WindowsInputCapture ─┐                  ┌─ WindowsInputSender
    //                           │                  │
    //                      [ KvmSession ]──────────┘
    //                           │
    //      InputRouter ─────────┤
    //      ConnectionMonitor ───┤
    //      PeerConnection ──────┘
    //
    //  이렇게 하면 부품 하나를 바꿔도 나머지는 그대로 쓸 수 있다.
    //  예를 들어 나중에 인터넷 연결(WebRTC)을 추가해도
    //  Capture 와 Router 는 손댈 필요가 없다.
    // ─────────────────────────────────────────────────────────────
    public class KvmSession : IDisposable
    {
        private readonly IInputCapture _capture;
        private readonly PeerConnection _peer;

        // 받은 입력을 실행하는 쪽.
        // SafeInputSender 로 감싸서 "지금 눌려 있는 것" 을 같이 기억하게 한다.
        // 연결이 끊기면 이 목록을 보고 전부 떼준다.
        private readonly SafeInputSender _sender;

        // 상대 PC 에 눌러놓은 것들.
        //
        // 위의 _sender 와 방향이 반대다.
        //   _sender      : 내가 "받아서 실행한" 것  → 내 PC 에 눌려 있음
        //   여기 목록     : 내가 "보낸" 것          → 상대 PC 에 눌려 있음
        //
        // 커서를 내 PC 로 되찾아올 때 이 목록을 비워줘야 한다.
        // 안 그러면: Shift 누른 채 커서를 가져오면, 그 뒤의 Shift 뗌은
        // 내 PC 로 가고 상대 PC 는 Shift 가 눌린 채로 남는다.
        private readonly HashSet<int> _remoteKeys = new HashSet<int>();
        private readonly HashSet<int> _remoteButtons = new HashSet<int>();
        private readonly object _remoteLock = new object();

        // 바깥에서 설정을 바꿀 수 있도록 공개한다.
        // (화면 배치, 연결 상태 확인 등)
        public InputRouter Router { get; }
        public ConnectionMonitor Monitor { get; }

        // 얼마나 자주 "살아있냐" 를 물어볼지
        public int HeartbeatIntervalMs { get; set; } = 200;


        // ── 보내기 대기 통 ────────────────────────────────────
        //
        // 입력이 잡힌 자리에서 바로 네트워크로 보내면 안 된다.
        // 그 자리가 결국 후킹 함수 안이고, Windows 는 후킹 함수가
        // 0.3초 안에 안 끝나면 경고 없이 후킹을 해제해버린다.
        //
        // 그래서 통에 던져넣기만 하고 즉시 돌아온다.
        // 실제 전송은 아래 SendLoop 가 따로 맡는다.
        //
        // 덤으로 순서도 보장된다. 통에서 하나씩 꺼내 보내므로
        // 넣은 순서 그대로 나간다.
        // 클릭·키처럼 하나도 잃으면 안 되는 메시지는 여기에 줄을 세운다.
        private readonly BlockingCollection<Outgoing> _outbox =
            new BlockingCollection<Outgoing>();

        // 줄에 선 메시지 하나.
        //   ViaTcp = true : 종류와 상관없이 TCP 로 보낸다. (클릭 직전의 "커서 위치", 아래 설명)
        private readonly record struct Outgoing(InputMessage Message, bool ViaTcp);

        // 줄에 넣었지만 아직 다 보내지 못한 개수. (끝낼 때 "떼주세요" 가 나갈 때까지 기다리는 데 쓴다)
        private int _pendingReliable;

        // ── 마우스 이동은 줄 세우지 않고 "최신 것 하나" 만 들고 있는다 ──
        //
        // 왜 다르게 다루는가:
        //   마우스가 1000Hz 면 초당 1000개가 쏟아진다.
        //   네트워크가 그만큼 못 빼내면 줄이 계속 길어지고,
        //   커서가 "지금 내 손 위치" 가 아니라 몇 초 전 기록을 재생하게 된다.
        //   손을 멈췄는데도 커서가 계속 움직이는 게 이 증상이다.
        //
        //   이동 좌표는 절대 좌표라서 중간 것을 버려도 괜찮다.
        //   마지막 좌표 하나면 커서가 정확한 자리에 간다.
        //   반대로 클릭과 키는 하나만 빠져도 망가지므로 전부 보낸다.
        private InputMessage _latestMove;
        private bool _hasLatestMove;
        private readonly object _moveLock = new object();

        // 가장 최근에 받은 이동. (이미 보냈든 아니든)
        //
        // ── 클릭이 엉뚱한 자리에 찍히는 것 막기 ──
        //   이동은 빠른 길(UDP), 클릭·휠은 확실한 길(TCP)로 따로 간다.
        //   두 길은 도착 순서가 보장되지 않는다. 받는 쪽도 두 길을 따로 처리한다.
        //   그래서 마우스를 빨리 옮기고 바로 누르면, 클릭이 이동보다 먼저 도착해서
        //   옮기기 전 자리에 찍힐 수 있다.
        //
        //   막는 방법: 클릭·휠을 보낼 때 바로 앞에 "지금 커서 위치" 를 같은 TCP 줄로 한 번 더 보낸다.
        //   같은 줄 안에서는 순서가 보장되므로, 상대는 반드시 그 자리로 옮긴 뒤에 누른다.
        //   클릭 하나에 작은 메시지 하나가 더 붙을 뿐이라 부담은 없다.
        private InputMessage _lastMoveSeen;
        private bool _hasLastMoveSeen;

        // 보낼 게 생겼다고 알리는 신호
        private readonly ManualResetEventSlim _wake =
            new ManualResetEventSlim(false);

        // 통계 (합치기가 실제로 동작하는지 보려고)
        private long _movesSent;
        private long _movesDropped;

        public long MovesSent => Interlocked.Read(ref _movesSent);
        public long MovesDropped => Interlocked.Read(ref _movesDropped);

        // 클릭·키·휠 등 이동이 아닌 메시지를 보낸 수. (6주차: 연결 기록의 message_count 용)
        private long _reliableSent;

        // 이 연결에서 상대에게 보낸 입력 메시지 전체 수 (이동 + 클릭·키·휠)
        public long MessagesSent => Interlocked.Read(ref _movesSent) + Interlocked.Read(ref _reliableSent);

        // 전송이 실패한 횟수와 마지막 오류.
        //
        // 예전에는 Console.WriteLine 으로 찍었는데,
        // 창이 있는 프로그램에는 콘솔이 없어서 아무 데도 안 보였다.
        // 그래서 화면에 띄울 수 있도록 값으로 들고 있는다.
        private long _sendFailures;
        public long SendFailures => Interlocked.Read(ref _sendFailures);

        public string LastSendError { get; private set; } = "";

        private Task? _sendLoop;
        private Task? _heartbeatLoop;
        private CancellationTokenSource? _cts;


        // ── 하트비트 추적 ─────────────────────────────────────

        // 다음에 보낼 질문 번호
        private int _nextPingId;

        // 보낸 질문들: 번호 → 보낸 시각
        //
        // 시각을 여기에 보관하는 이유:
        //   두 PC의 시계가 다를 수 있으므로 메시지에 시각을 담으면 안 된다.
        //   번호만 주고받고, 계산은 내 PC 안에서 한다.
        private readonly ConcurrentDictionary<int, long> _pingSentAt =
            new ConcurrentDictionary<int, long>();


        public KvmSession(
            IInputCapture capture,
            IInputSender sender,
            PeerConnection peer)
        {
            _capture = capture;
            _peer = peer;

            _sender = new SafeInputSender(sender);

            Router = new InputRouter(capture, _sender);
            Monitor = new ConnectionMonitor();
        }


        // ── 시작 / 종료 ───────────────────────────────────────

        public void Start()
        {
            _cts = new CancellationTokenSource();

            // ── 선 1: 판단 결과를 통에 넣는다 ──
            Router.SendToRemote += OnSendToRemote;

            // ── 선 2: 받은 메시지를 실행한다 ──
            _peer.MessageReceived += OnPeerMessage;

            // ── 연결 상태가 바뀌면 알림 ──
            Monitor.StateChanged += OnLinkStateChanged;

            // ── 커서가 내 PC 로 돌아오면 상대에 눌러놓은 걸 풀어준다 ──
            Router.LocationChanged += OnCursorLocationChanged;

            _capture.Start();

            _sendLoop = Task.Run(() => SendLoopAsync(_cts.Token));
            _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_cts.Token));

            // 연결됨 상태로 시작한다. 첫 응답이 오면 정상으로 바뀐다.
            Monitor.OnConnected();
        }

        public void Stop()
        {
            // 순서가 중요하다.
            // 삼키기부터 풀어야 입력이 막힌 채로 남지 않는다.
            // (ForceComeBack 안에서 상대에 눌러놓은 것도 같이 풀린다)
            Router.ForceComeBack();

            // 내 PC 에 눌린 채 남은 것도 정리한다.
            _sender.ReleaseAll();

            // 방금 넣은 "떼주세요" 메시지가 실제로 나갈 때까지 기다린다. (최대 0.15초)
            //
            // 예전에는 무조건 0.15초를 잤다. 그동안 화면이 멈춰서 [연결 끊기] 가 굼떴다.
            // 보통 몇 밀리초면 다 나가므로, 다 나가면 바로 넘어간다.
            // 줄이 끊긴 상태라 안 나가면 예전처럼 0.15초에서 포기한다.
            var drain = System.Diagnostics.Stopwatch.StartNew();

            while (Volatile.Read(ref _pendingReliable) > 0 && drain.ElapsedMilliseconds < 150)
                Thread.Sleep(1);

            _cts?.Cancel();

            // 잠들어 있는 전송 루프를 깨워서 취소를 알아채게 한다.
            _wake.Set();

            try
            {
                _outbox.CompleteAdding();
            }
            catch
            {
            }

            Router.SendToRemote -= OnSendToRemote;
            Router.LocationChanged -= OnCursorLocationChanged;
            _peer.MessageReceived -= OnPeerMessage;
            Monitor.StateChanged -= OnLinkStateChanged;

            _capture.Stop();

            // 루프가 정리될 시간을 잠깐 준다.
            try { _sendLoop?.Wait(1000); } catch { }
            try { _heartbeatLoop?.Wait(1000); } catch { }

            _cts?.Dispose();
            _cts = null;
        }

        public void Dispose() => Stop();


        // ── 선 1: 보내기 ──────────────────────────────────────

        // ★ 이 함수는 후킹 함수 안에서 실행된다.
        //   절대 오래 걸리는 일을 하면 안 된다. 통에 넣고 바로 나간다.
        private void OnSendToRemote(InputMessage message)
        {
            try
            {
                if (message.Type == MessageType.MouseMove)
                {
                    // 이동은 덮어쓴다. 아직 안 보낸 게 있었다면 그건 버려진다.
                    lock (_moveLock)
                    {
                        if (_hasLatestMove)
                            Interlocked.Increment(ref _movesDropped);

                        _latestMove = message;
                        _hasLatestMove = true;

                        _lastMoveSeen = message;
                        _hasLastMoveSeen = true;
                    }
                }
                else
                {
                    // 상대 PC 에 뭘 눌러놨는지 기억해둔다.
                    TrackRemotePressed(message);

                    // 클릭·휠이면 바로 앞에 지금 커서 위치를 TCP 로 먼저 보낸다. (위 _lastMoveSeen 설명)
                    if (message.Type == MessageType.MouseButton ||
                        message.Type == MessageType.MouseWheel)
                    {
                        InputMessage position = default;
                        bool hasPosition = false;

                        lock (_moveLock)
                        {
                            if (_hasLastMoveSeen)
                            {
                                position = _lastMoveSeen;
                                hasPosition = true;

                                // 이 위치는 지금 TCP 로 보내므로, 아직 안 보낸 같은 이동은 따로 보낼 필요가 없다.
                                _hasLatestMove = false;
                            }
                        }

                        if (hasPosition)
                            AddReliable(new Outgoing(position, ViaTcp: true));
                    }

                    // 클릭·키는 줄을 세운다. 하나도 버리면 안 된다.
                    AddReliable(new Outgoing(message, ViaTcp: false));
                }

                _wake.Set();
            }
            catch (InvalidOperationException)
            {
                // 종료 중이라 통이 닫힌 경우. 무시해도 된다.
            }
        }

        // 실제로 보내는 쪽.
        private async Task SendLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 보낼 게 생길 때까지 잠들어 있는다. CPU 를 쓰지 않는다.
                    _wake.Wait(token);
                    _wake.Reset();

                    // ── 1) 클릭·키 먼저 전부 비운다 ──
                    //
                    // 이동보다 먼저 보내는 이유:
                    //   "버튼 누름" 이 늦어지면 클릭이 씹힌 것처럼 느껴진다.
                    //   이동은 조금 늦어도 티가 덜 난다.
                    while (_outbox.TryTake(out Outgoing reliable))
                    {
                        await SendOneAsync(reliable.Message, reliable.ViaTcp);
                        Interlocked.Decrement(ref _pendingReliable);
                        Interlocked.Increment(ref _reliableSent);
                    }

                    // ── 2) 마우스 이동은 최신 것 하나만 ──
                    InputMessage move = default;
                    bool hasMove = false;

                    lock (_moveLock)
                    {
                        if (_hasLatestMove)
                        {
                            move = _latestMove;
                            _hasLatestMove = false;
                            hasMove = true;
                        }
                    }

                    if (hasMove)
                    {
                        await SendOneAsync(move);
                        Interlocked.Increment(ref _movesSent);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 정상 종료
            }
        }

        // ── 상대 PC 에 눌러놓은 것 관리 ─────────────────────────

        private void TrackRemotePressed(InputMessage message)
        {
            lock (_remoteLock)
            {
                switch (message.Type)
                {
                    case MessageType.KeyDown:
                        _remoteKeys.Add(message.Value1);
                        break;

                    case MessageType.KeyUp:
                        _remoteKeys.Remove(message.Value1);
                        break;

                    case MessageType.MouseButton:
                        if (message.Value2 == 1)
                            _remoteButtons.Add(message.Value1);
                        else
                            _remoteButtons.Remove(message.Value1);
                        break;
                }
            }
        }

        // 커서가 내 PC 로 돌아왔다.
        // 상대에 눌러놓은 게 남아 있으면 "떼주세요" 를 보낸다.
        private void OnCursorLocationChanged(
            CursorLocation prev,
            CursorLocation next,
            string reason)
        {
            if (next != CursorLocation.Local)
                return;

            ReleaseRemotePressed();
        }

        // 상대에 눌러놓은 키·버튼을 전부 뗀다.
        //
        // 커서가 내 PC 로 돌아올 때, 그리고 화면 보기 창에서 다른 창으로 옮길 때 부른다.
        // (화면 보기 창에서는 키가 커서 위치와 상관없이 상대에게 가므로, 창을 떠날 때 떼 줘야 한다)
        public void ReleaseRemotePressed()
        {
            List<int> keys;
            List<int> buttons;

            lock (_remoteLock)
            {
                if (_remoteKeys.Count == 0 && _remoteButtons.Count == 0)
                    return;

                keys = new List<int>(_remoteKeys);
                buttons = new List<int>(_remoteButtons);

                _remoteKeys.Clear();
                _remoteButtons.Clear();
            }

            // 통에 직접 넣는다.
            // OnSendToRemote 를 부르면 TrackRemotePressed 가 다시 불려서
            // 방금 비운 목록에 또 쌓이므로 그렇게 하면 안 된다.
            foreach (int key in keys)
            {
                Enqueue(new InputMessage
                {
                    Type = MessageType.KeyUp,
                    Value1 = key
                });
            }

            foreach (int button in buttons)
            {
                Enqueue(new InputMessage
                {
                    Type = MessageType.MouseButton,
                    Value1 = button,
                    Value2 = 0
                });
            }

            Console.WriteLine();
            Console.WriteLine(
                $"  [안전] 상대 PC 에 눌려 있던 키 {keys.Count}개, " +
                $"버튼 {buttons.Count}개를 떼줬습니다.");
        }

        private void Enqueue(InputMessage message)
        {
            try
            {
                AddReliable(new Outgoing(message, ViaTcp: false));
                _wake.Set();
            }
            catch (InvalidOperationException)
            {
            }
        }


        // 줄에 넣는다. (보낼 때까지 남은 개수도 센다)
        // 줄이 닫혔으면(끝내는 중) InvalidOperationException 이 날 수 있다. 부르는 쪽이 받는다.
        private void AddReliable(Outgoing item)
        {
            if (_outbox.IsAddingCompleted)
                return;

            Interlocked.Increment(ref _pendingReliable);

            try
            {
                _outbox.Add(item);
            }
            catch
            {
                Interlocked.Decrement(ref _pendingReliable);
                throw;
            }
        }

        // viaTcp: true 면 종류와 상관없이 TCP 로 보낸다. (클릭 직전 커서 위치)
        //         false 면 PeerConnection 이 종류를 보고 고른다. (이동은 UDP, 나머지는 TCP)
        private async Task SendOneAsync(InputMessage message, bool viaTcp = false)
        {
            try
            {
                if (viaTcp)
                    await _peer.Tcp.SendAsync(message);
                else
                    await _peer.SendAsync(message);
            }
            catch (Exception ex)
            {
                // 전송 하나가 실패해도 루프는 계속 돌아야 한다.
                // 연결이 정말 끊겼는지는 하트비트가 판단한다.
                Interlocked.Increment(ref _sendFailures);
                LastSendError = ex.Message;
            }
        }


        // ── 선 2: 받기 ────────────────────────────────────────

        private void OnPeerMessage(InputMessage message)
        {
            switch (message.Type)
            {
                case MessageType.Ping:
                    // "살아있냐?" → 받은 번호 그대로 돌려준다.
                    //
                    // 여기는 후킹 함수 안이 아니므로 바로 보내도 된다.
                    // _ = 는 "결과를 기다리지 않는다" 는 뜻이다.
                    _ = ReplyPongAsync(message.Value1);
                    return;

                case MessageType.Pong:
                    // 답이 왔다. 보낸 시각을 꺼내서 왕복 시간을 계산한다.
                    if (_pingSentAt.TryRemove(message.Value1, out long sentAt))
                    {
                        double rttMs = ElapsedMs(
                            sentAt,
                            Stopwatch.GetTimestamp());

                        Monitor.OnPongReceived(rttMs);
                    }
                    return;

                case MessageType.ScreenInfo:
                    // 상대가 자기 화면 크기를 알려줬다.
                    //
                    // 이 값으로 경계를 넘을 때의 좌표 환산이 정확해진다.
                    // 이게 틀리면 커서가 상대 화면 밖으로 나가려 하거나,
                    // 화면 일부에 도달하지 못한다.
                    if (message.Value1 > 0 && message.Value2 > 0)
                    {
                        Router.RemoteWidth = message.Value1;
                        Router.RemoteHeight = message.Value2;

                        // 짐작한 값이 아니라 실제 값이라고 표시한다.
                        // 화면(MainWindow)이 이걸 보고 화면 배치 DB 에 크기를 저장한다.
                        Router.RemoteSizeKnown = true;
                    }
                    return;

                case MessageType.PairRequest:
                case MessageType.PairResult:
                case MessageType.Unpair:
                case MessageType.HelloIdHigh:
                case MessageType.HelloIdLow:
                case MessageType.PairTokenRequest:
                case MessageType.PairTokenGrant:
                case MessageType.FileRequest:
                    // 짝 맺기(코드·열쇠 확인)와 파일 받기 요청은 창 쪽에서 처리한다.
                    //
                    // 여기서 안 걸러주면 아래 default 로 떨어져서
                    // 코드 숫자가 키 번호로 둔갑해 실제로 눌려버린다.
                    return;

                default:
                    // 진짜 입력이다. 이 PC에서 실행한다.
                    //
                    // 받는 쪽은 아무 판단도 하지 않는다.
                    // 판단은 전부 보내는 쪽에서 끝냈다.
                    _sender.Apply(message);
                    return;
            }
        }

        private async Task ReplyPongAsync(int pingId)
        {
            try
            {
                // 하트비트는 PeerConnection 이 알아서 TCP로 보낸다.
                // 유실되면 "죽었다" 고 오판하게 되므로 반드시 도착해야 하고,
                // 그래서 UDP가 아니라 신뢰 채널을 쓴다.
                await _peer.SendAsync(new InputMessage
                {
                    Type = MessageType.Pong,
                    Value1 = pingId
                });
            }
            catch
            {
                // 답장 실패는 상대가 알아서 시간 초과로 처리한다.
            }
        }


        // ── 선 3, 4: 하트비트와 자동 복구 ─────────────────────

        private async Task HeartbeatLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HeartbeatIntervalMs, token);

                    if (_peer.IsTcpConnected)
                    {
                        await SendPingAsync();
                        CheckPingTimeouts();

                        // 몇 초마다 내 화면 크기를 알려준다.
                        //
                        // 한 번만 보내면 그 메시지가 유실됐을 때 영영 모른다.
                        // 주기적으로 보내면 저절로 복구되고,
                        // 중간에 해상도를 바꿔도 따라간다.
                        _screenInfoCount++;

                        if (_screenInfoCount >= 10)     // 0.2초 × 10 = 2초
                        {
                            _screenInfoCount = 0;
                            await SendScreenInfoAsync();
                        }
                    }
                    else
                    {
                        // 소켓이 이미 끊긴 걸 확인했다. 추측할 필요가 없다.
                        Monitor.OnHardFailure("TCP 연결 없음");
                    }

                    // ★ 끊긴 동안에는 커서를 계속 내 PC 로 붙잡아둔다.
                    //
                    // OnLinkStateChanged 는 "상태가 바뀌는 순간" 딱 한 번만 불린다.
                    // 그래서 끊긴 걸 알고 커서를 되찾아온 뒤에도,
                    // 사용자가 마우스를 다시 오른쪽 끝까지 밀면
                    // 경계 판정은 그대로 살아 있어서 또 상대 쪽으로 넘어가버린다.
                    //
                    // 그러면 내 화면에서도 커서가 사라지고 상대 화면도 안 움직이는,
                    // 제일 답답한 상태가 된다. 매 주기 되돌려서 그걸 막는다.
                    if (Monitor.State == LinkState.Lost &&
                        Router.Location == CursorLocation.Remote)
                    {
                        Router.ForceComeBack();
                    }

                    // ★ 선 4 — 자동 복구의 핵심
                    //
                    // 연결이 살아있을 때만 "아직 정상입니다" 신호를 보낸다.
                    // 끊겼으면 아무것도 안 한다.
                    // 그러면 2초 뒤 WindowsInputCapture 가 알아서 삼키기를 푼다.
                    //
                    // 이 한 줄이 "고장을 감지" 가 아니라
                    // "정상임을 증명" 하는 구조를 만든다.
                    if (Monitor.CanKeepSwallowing)
                        _capture.KeepAlive();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"하트비트 오류: {ex.Message}");
                }
            }
        }

        // 10 으로 시작하는 이유:
        //   연결되자마자 첫 하트비트에서 바로 한 번 보내려는 것이다.
        //   0 으로 시작하면 2초 동안 상대 화면 크기를 모르는 채로 지낸다.
        private int _screenInfoCount = 10;

        // 내 화면 크기를 상대에게 알려준다.
        private async Task SendScreenInfoAsync()
        {
            try
            {
                // 신뢰 채널(TCP)로 보낸다.
                //
                // PeerConnection 이 종류를 보고 알아서 TCP 로 보낸다.
                await _peer.SendAsync(new InputMessage
                {
                    Type = MessageType.ScreenInfo,
                    Value1 = Router.LocalWidth,
                    Value2 = Router.LocalHeight
                });
            }
            catch
            {
                // 다음 주기에 다시 보내므로 실패해도 그냥 넘어간다.
            }
        }

        private async Task SendPingAsync()
        {
            int id = Interlocked.Increment(ref _nextPingId);

            _pingSentAt[id] = Stopwatch.GetTimestamp();

            try
            {
                await _peer.SendAsync(new InputMessage
                {
                    Type = MessageType.Ping,
                    Value1 = id
                });
            }
            catch
            {
                // 보내기 자체가 실패했다. 답이 올 리 없으므로 기록을 지운다.
                _pingSentAt.TryRemove(id, out _);
                Monitor.OnPingTimedOut();
            }
        }

        // 답이 안 온 질문들을 확인한다.
        private void CheckPingTimeouts()
        {
            long now = Stopwatch.GetTimestamp();

            // 기다릴 시간은 고정값이 아니다.
            // ConnectionMonitor 가 평소 응답 속도에 맞춰 계산해준다.
            int timeoutMs = Monitor.TimeoutMs;

            foreach (var pair in _pingSentAt)
            {
                if (ElapsedMs(pair.Value, now) <= timeoutMs)
                    continue;

                // 시간 초과. 목록에서 빼고 Monitor 에 알린다.
                //
                // TryRemove 로 확인하는 이유:
                //   바로 이 순간에 답이 도착해서 다른 스레드가 먼저
                //   꺼내갔을 수 있다. 그러면 시간 초과로 세면 안 된다.
                if (_pingSentAt.TryRemove(pair.Key, out _))
                    Monitor.OnPingTimedOut();
            }
        }

        private void OnLinkStateChanged(
            LinkState prev,
            LinkState next,
            string reason)
        {
            Console.WriteLine($"[연결] {prev} → {next}   ({reason})");

            if (next == LinkState.Lost)
            {
                // 커서를 즉시 내 PC로 되찾아온다.
                //
                // 이걸 안 해도 2초 뒤 감시 스레드가 삼키기를 풀지만,
                // 여기서 미리 알았으니 기다릴 이유가 없다.
                Router.ForceComeBack();

                // 내 PC 에 눌린 채 남은 것도 떼준다.
                //
                // 이게 핵심이다. 상대가 전원이 나가서 사라졌다면
                // "떼주세요" 메시지를 보내줄 사람이 아무도 없다.
                // 받은 쪽이 스스로 복구해야 한다.
                _sender.ReleaseAll();
            }
        }


        // ── 도우미 ────────────────────────────────────────────

        // Stopwatch 눈금 차이를 밀리초로 바꾼다.
        //
        // DateTime 대신 Stopwatch 를 쓰는 이유:
        //   DateTime 은 사용자가 시계를 바꾸거나 시간 동기화가 일어나면
        //   값이 튄다. Stopwatch 는 그런 영향을 받지 않는다.
        private static double ElapsedMs(long startTicks, long nowTicks)
        {
            return (nowTicks - startTicks) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
