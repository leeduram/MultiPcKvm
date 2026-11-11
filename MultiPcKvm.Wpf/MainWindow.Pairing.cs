using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;    // Path, File, FileInfo (진단 기록 파일). WPF 프로젝트는 System.IO 를 자동으로 넣어주지 않는다
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MultiPcKvm.App;
using MultiPcKvm.App.Storage;
using MultiPcKvm.Common;
using MultiPcKvm.Network.Configuration;
using MultiPcKvm.Network.Connection;
using MultiPcKvm.Network.Discovery;
using MultiPcKvm.Windows;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  MainWindow — 대기 상태 · 메시지 받기 · 연결 승인
    //
    //  상대가 걸어올 수 있게 듣고 있다가(EnsureStandby) 받은 메시지를 처리한다. (OnPeerMessage)
    //  연결 요청(코드·저장된 기기 열쇠)을 확인하고 답한다. (HandleIncomingPair)
    //  Hello(내 기기 ID)·열쇠·화면 크기·Pong 을 보내는 함수도 여기 있다.
    //
    //  MainWindow 는 할 일이 많아서 주제별로 파일을 나눴다. (partial = 한 클래스를 여러 파일에 나눠 쓰기)
    //  필드(공용 상태)·생성자·시작·Tick 은 MainWindow.xaml.cs 에 있다.
    // ─────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        // ── 저장된 기기 열쇠 (코드 없이 연결) ──
        //
        // 상대가 Hello 로 알려준 기기 ID. 앞쪽 절반이 먼저 오고, 뒤쪽 절반이 오면 완성된다.
        // 네트워크 스레드가 쓰고 읽는다. 화면 스레드도 읽으므로 잠금을 건다.
        private readonly object _helloLock = new();
        private long? _helloHigh;
        private Guid? _claimedPeerId;

        // 내가 코드를 보낸 상대. 이 상대가 보내준 열쇠만 받는다.
        // (아무 데서나 온 열쇠를 저장하지 않도록. 화면 스레드에서만 쓴다)
        private Guid? _expectGrantFrom;

        // 열쇠가 기기 저장보다 먼저 도착했을 때 잠시 들고 있는다. (저장 직후 넣는다)
        private (Guid Id, long Token)? _pendingGrant;

        // ── 대기 상태 ─────────────────────────────────────────

        // 듣기를 시작한다. 후킹은 걸지 않는다.
        private void EnsureStandby(DeviceItem item)
        {
            if (_peer != null)
                return;

            try
            {
                _peer = new PeerConnection(
                    new NetworkConfig(), item.DeviceId, item.Name, item.IpAddress);

                _peerItem = item;

                _receiver = new SafeInputSender(new WindowsInputSender());

                _peer.MessageReceived += OnPeerMessage;
                _peer.StartListening();

                // UDP 소켓이 열렸는지 확인한다.
                //
                // 마우스 이동만 UDP 로 가고 나머지는 TCP 로 간다.
                // 그래서 UDP 만 실패하면 "연결은 되는데 마우스만 안 움직이는"
                // 헷갈리는 증상이 난다. 키보드와 클릭은 멀쩡하게 먹는다.
                //
                // 가장 흔한 원인은 다른 프로그램이 45000 포트를 잡고 있는 것이다.
                // (예: 예전에 띄워둔 콘솔 테스트 프로그램이 아직 살아있음)
                if (!_peer.IsUdpReady)
                {
                    ShowHint(
                        "UDP 준비에 실패했습니다. 45000 포트를 다른 프로그램이 " +
                        "쓰고 있을 수 있습니다. 이 상태로는 마우스 이동이 전달되지 않습니다.", 15);
                }
            }
            catch (Exception ex)
            {
                ShowHint("대기 상태로 전환하지 못했습니다: " + ex.Message, 10);
                _peer = null;
                _peerItem = null;
            }
        }

        // 대기 상태에서 메시지를 받았을 때.
        //
        // 주의: 네트워크 스레드에서 불린다. 화면을 직접 건드리면 안 된다.
        private void OnPeerMessage(InputMessage message)
        {
            // ── 짝 맺기는 조종 중이어도 여기서 처리한다 ──
            //
            // KvmSession 은 코드를 모른다. 코드는 창이 들고 있다.
            // 그리고 아래 "조종 중이면 나가기" 보다 먼저 와야 하는 이유가 둘 있다.
            //   · 재연결: 조종 중에 줄이 끊겨 다시 붙을 때 답을 받아야 한다.
            //   · 거절:   내가 조종 중인데 상대가 나에게 코드를 보내오면
            //            "안 된다" 고 답해줘야 한다. (한 방향만 허용)
            //            답을 안 하면 상대는 3초 동안 멍하니 기다린다.
            if (message.Type == MessageType.PairResult)
            {
                _pairWaiter?.TrySetResult(message.Value1);
                return;
            }

            if (message.Type == MessageType.PairRequest)
            {
                HandleIncomingPair(code: message.Value1, token: null);
                return;
            }

            // 코드 대신 저장된 기기 열쇠로 연결하려고 한다.
            if (message.Type == MessageType.PairTokenRequest)
            {
                HandleIncomingPair(code: null, token: PairToken.JoinLong(message.Value1, message.Value2));
                return;
            }

            // 연결하려는 쪽이 "나는 이 기기다" 를 알려준다. (두 번에 나눠서 온다)
            if (message.Type == MessageType.HelloIdHigh)
            {
                lock (_helloLock)
                {
                    _helloHigh = PairToken.JoinLong(message.Value1, message.Value2);
                    _claimedPeerId = null;
                }
                return;
            }

            if (message.Type == MessageType.HelloIdLow)
            {
                lock (_helloLock)
                {
                    if (_helloHigh.HasValue)
                    {
                        _claimedPeerId = PairToken.JoinGuid(_helloHigh.Value, PairToken.JoinLong(message.Value1, message.Value2));
                        _helloHigh = null;
                    }
                }
                return;
            }

            // 코드로 연결이 승인된 직후, 상대가 "다음부터는 이 열쇠를 써" 를 보내왔다.
            if (message.Type == MessageType.PairTokenGrant)
            {
                long token = PairToken.JoinLong(message.Value1, message.Value2);
                Dispatcher.InvokeAsync(() => StoreGrantedToken(token));
                return;
            }

            if (message.Type == MessageType.Unpair)
            {
                DiagLog.Write("상대가 연결을 끊음 (Unpair 받음)");

                // 상대가 "이제 그만 조종할게" 라고 알려왔다.
                //
                // 이걸 안 지우면 상대가 끊은 뒤에도 "조종당하는 중" 으로 남아서
                // 내 [연결] 버튼이 계속 잠겨 있다.
                //
                // 끊긴 이유를 먼저 적어 둔다. 연결 기록은 Tick 이 닫는다. (CheckIncomingLog, 6주차)
                _inEndReason = "상대가 끊음";
                _incomingPaired = false;

                // 상대가 눌러놓고 간 키가 있으면 떼준다.
                _receiver?.ReleaseAll();

                // 화면을 건드리므로 화면 담당 스레드로 넘긴다.
                //
                // Invoke 가 아니라 InvokeAsync 를 쓰는 이유:
                //   Invoke 는 화면 쪽 일이 끝날 때까지 이 네트워크 스레드를 붙잡는다.
                //   아래 Disconnect 는 조종을 멈추느라 1초 가까이 걸릴 수 있어서
                //   그동안 다음 메시지를 못 받게 된다. 맡기기만 하고 바로 돌아간다.
                Dispatcher.InvokeAsync(() =>
                {
                    // 한 방향만 허용하므로 보통은 여기서 내가 조종 중일 일이 없다.
                    // 그래도 양쪽이 거의 같은 순간에 [연결] 을 눌러서
                    // 둘 다 조종 중이 되는 틈이 있을 수 있다. 그때는 같이 멈춘다.
                    //
                    // notifyPeer: false — 상대는 이미 끊었으니 다시 알릴 필요 없다.
                    //                    알리면 서로 "끊어" 를 주고받으며 핑퐁이 된다.
                    if (_session != null)
                        Disconnect(notifyPeer: false, reason: "상대가 끊음");

                    ShowHint($"{_peerItem?.Name} 이(가) 연결을 끊었습니다.");
                });
                return;
            }

            // 내가 조종하는 중이면 KvmSession 이 알아서 다 처리한다.
            // 여기서 또 처리하면 입력이 두 번 실행된다.
            if (_session != null)
                return;

            switch (message.Type)
            {
                case MessageType.Ping:
                    // "살아있냐?" → 받은 번호 그대로 돌려준다.
                    _ = ReplyPongAsync(message.Value1);
                    return;

                case MessageType.Pong:
                    return;

                case MessageType.ScreenInfo:
                    // 상대가 자기 화면 크기를 알려줬다.
                    // 나도 내 것을 알려준다. 그래야 상대가 좌표를 맞게 환산한다.
                    //
                    // 대기 상태에는 KvmSession 이 없어서 주기적으로 보낼 주체가 없다.
                    // 그래서 받을 때마다 답하는 방식으로 대신한다.
                    _ = SendMyScreenInfoAsync();

                    // 받은 크기는 기억해 둔다. 화면 배치 창에 상대 화면을 실제 비율로 그리고,
                    // 나중에 내가 이 기기를 조종할 때 처음부터 맞는 크기로 시작하려는 것이다.
                    // (DB 저장은 화면 담당 스레드인 Tick 에서 한다)
                    if (message.Value1 > 0 && message.Value2 > 0)
                    {
                        _peerScreenWidth = message.Value1;
                        _peerScreenHeight = message.Value2;
                    }
                    return;

                case MessageType.FileRequest:
                    // 조종하는 PC 가 화면 보기 창의 [파일 받기] 를 눌렀다.
                    // 내 화면에 "보낼 파일 고르기" 창을 띄운다. (MainWindow.FileTransfer.cs)
                    // 코드(또는 열쇠)를 통과한 상대일 때만. 입력으로 실행하지 않고 여기서 끝낸다.
                    if (_incomingPaired)
                        Dispatcher.InvokeAsync(() => _ = HandleFileRequestAsync());
                    return;

                default:
                    // 코드를 통과해서 조종이 허락된 동안에만 입력을 실행한다.
                    //
                    // 조종당하는 쪽에서 [연결 끊기] 를 누르면 _incomingPaired 가 바로 false 가 된다.
                    // 상대가 끊었다는 신호(Unpair)를 받기 전에 보낸 입력이 조금 더 올 수 있는데,
                    // 그것까지 여기서 버린다. 누른 순간부터 내 PC 는 더 이상 움직이지 않는다.
                    // (예전에는 이 검사가 없어서 코드 확인 없이 들어온 입력도 실행됐다)
                    if (!_incomingPaired)
                        return;

                    _lastInputAt = DateTime.Now;
                    Interlocked.Increment(ref _inputsReceived);   // 연결 기록의 메시지 수 (6주차)
                    _receiver?.Apply(message);
                    return;
            }
        }

        // 연결 요청(코드 또는 저장된 기기 열쇠)을 확인하고 답한다.
        //
        // code, token 중 하나만 들어온다.
        // 주의: 네트워크 스레드에서 불린다. 화면·DB 는 Dispatcher 로 넘긴다.
        private void HandleIncomingPair(int? code, long? token)
        {
            // Hello 로 알려준 상대 ID. 예전 버전이 보낸 요청이면 없다(null).
            Guid? claimed;

            lock (_helloLock)
                claimed = _claimedPeerId;

            int result;

            // ── 한 방향만 허용한다 ──
            //
            // 내가 이미 조종 중이거나, 지금 막 상대에게 연결을 거는 중이면 거절한다.
            // 코드·열쇠가 맞아도 거절한다.
            //
            // 두 번째 경우(거는 중)는 양쪽이 거의 같은 순간에 [연결] 을 누른 상황이다.
            // 둘 다 거절당하니 한쪽이 다시 누르면 된다.
            // 둘 다 받아주면 서로 조종하는 상태가 되고, 그건 빼기로 했다.
            if (_session != null || _pairWaiter != null)
                result = PairBusy;
            else if (code.HasValue)
                result = code.Value == _myCode ? PairOk : PairWrongCode;
            else
                // 열쇠 비교는 DB 를 읽으므로 화면 담당 스레드에서 한다.
                result = Dispatcher.Invoke(() => IsValidTokenIn(claimed, token!.Value))
                    ? PairOk
                    : PairBadToken;

            _ = SendPairResultAsync(result);

            DiagLog.Write($"{(code.HasValue ? "코드" : "열쇠")} 요청 받음 | 보낸 기기 {claimed?.ToString() ?? "모름"} | " +
                          $"결과 {result} (1 승인, 0 코드 틀림, 2 지금 안 됨, 3 열쇠 틀림)");

            // 승인했으면 "상대가 나에게 붙어 있다" 고 기억해둔다.
            // 이게 켜져 있는 동안 내 [연결] 버튼은 잠긴다.
            if (result == PairOk)
                _incomingPaired = true;

            // 화면 갱신은 화면 담당 스레드에서 해야 한다.
            Dispatcher.Invoke(() =>
            {
                if (result == PairOk)
                {
                    // 누가 연결했는지 Hello 로 알았으면 그 기기를 상대로 삼는다.
                    //
                    // 예전에는 대기 상태가 향하던 기기(처음 검색된 기기)를 상대로 여겼다.
                    // 기기가 둘뿐이면 같지만, 셋 이상이면 엉뚱한 이름이 뜨고 엉뚱한 기기가 저장됐다.
                    DeviceItem? who = claimed.HasValue
                        ? _devices.FirstOrDefault(d => d.DeviceId == claimed.Value)
                        : null;

                    if (who != null && who != _peerItem)
                    {
                        DiagLog.Write($"상대 기기 바로잡음 | {_peerItem?.Name} → {who.Name}");
                        _peerItem = who;
                    }

                    // 상대가 내 코드를 맞혔으면 나도 상대를 신뢰 기기로 저장한다.
                    // (DB 는 화면 담당 스레드에서만 쓰기로 했으므로 여기 안에서 부른다)
                    if (_peerItem != null)
                    {
                        SaveTrustedDevice(_peerItem);

                        // 연결 기록을 "연결 중" 으로 남긴다. (6주차, MainWindow.ConnectionLog.cs)
                        BeginIncomingLog(_peerItem);
                    }

                    // 코드로 승인했으면, 다음부터 코드 없이 들어올 열쇠를 만들어 준다.
                    // 누가 보냈는지 모르면(예전 버전) 만들지 않는다. 엉뚱한 기기에 저장될 수 있다.
                    if (code.HasValue && who != null && who.IsSaved)
                        GrantToken(who);

                    // 이제 나는 조종당하는 쪽이다. 다음에 켰을 때 내가 먼저 걸지 않게 "마지막 기기" 를 지운다.
                    // (조종하는 쪽이 자동으로 걸어온다. 둘 다 걸면 서로 거절된다)
                    WriteSetting(SettingKeys.LastTarget, null);
                    CancelAutoConnect("다른 기기가 이 PC 에 연결함");
                }

                // 내가 코드 입력 창을 띄워둔 사이에 상대가 먼저 연결했다.
                // 먼저 연결한 쪽이 이기므로 내 입력 창은 닫는다.
                // (DialogResult 를 넣으면 창이 닫히고 ShowDialog 가 false 를 돌려준다)
                if (result == PairOk && _pairDialog != null)
                {
                    DiagLog.Write("코드 입력 창 닫음 | 상대가 먼저 연결함");
                    _pairDialog.DialogResult = false;
                    _pairDialog = null;
                }

                ShowHint(result switch
                {
                    PairOk when token.HasValue => $"{_peerItem?.Name} 이(가) 이 PC 에 연결했습니다. (저장된 기기)",
                    PairOk => $"{_peerItem?.Name} 이(가) 이 PC 에 연결했습니다.",
                    PairBusy => $"{_peerItem?.Name} 이(가) 연결하려 했지만, 이 PC 가 조종 중이라 거절했습니다.",
                    PairBadToken => "저장되지 않은 기기가 코드 없이 연결하려 해서 거절했습니다.",
                    _ => "잘못된 코드로 연결 시도가 있었습니다."
                });
            });
        }

        // 상대가 낸 열쇠가 내가 그 기기에 만들어 준 열쇠와 같은가. (화면 스레드에서)
        private bool IsValidTokenIn(Guid? claimed, long token)
        {
            if (_store == null || claimed == null)
                return false;

            try
            {
                long? saved = _store.GetTokenIn(claimed.Value);
                return saved.HasValue && saved.Value == token;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"열쇠 확인 실패 | {ex.Message}");
                return false;
            }
        }

        // 코드로 승인한 기기에게 열쇠를 만들어 주고 보낸다. (화면 스레드에서)
        //
        // 이전 열쇠가 있으면 새 것으로 바뀐다. 상대도 새 것으로 덮어쓴다.
        private void GrantToken(DeviceItem who)
        {
            if (_store == null)
                return;

            long token = PairToken.NewToken();

            StoreResult result = _store.SetTokenIn(who.DeviceId, token);

            DiagLog.Write($"열쇠 만들어 줌 | {who.Name} | 결과 {result}");

            if (result == StoreResult.Ok)
                _ = SendPairMessageAsync(MessageType.PairTokenGrant, token);
        }

        // 상대가 보내준 열쇠를 저장한다. (화면 스레드에서)
        //
        // 내가 코드를 보낸 상대(_expectGrantFrom)가 보낸 것만 받는다.
        private void StoreGrantedToken(long token)
        {
            Guid? from = _expectGrantFrom;

            if (from == null || _peerItem == null || _peerItem.DeviceId != from.Value || _store == null)
            {
                DiagLog.Write("열쇠 받음 | 기다리던 열쇠가 아니라 버림");
                return;
            }

            StoreResult result = _store.SetTokenOut(from.Value, token);

            if (result == StoreResult.NotFound)
            {
                // 아직 이 기기를 저장하기 전이다. 저장 직후 넣는다. (ConnectTo)
                _pendingGrant = (from.Value, token);
                return;
            }

            _expectGrantFrom = null;
            DiagLog.Write($"열쇠 받음 | {_peerItem.Name} | 다음부터 코드 없이 연결 | 결과 {result}");
        }

        // 열쇠 만들기, 64비트·기기 ID 나누고 합치기는 MultiPcKvm.App 의 PairToken 에 있다.
        // (양쪽 PC 가 같은 규칙을 써야 하므로 한 곳에 모아 두고 시험했다)

        // 64비트 값을 담은 메시지를 보낸다. (Hello, 열쇠)
        //
        // PeerConnection 이 종류를 보고 알아서 TCP 로 보낸다.
        // (12~15 번은 팀원이 PeerConnection 의 TCP 목록에 추가해 줬다.
        //  그 전에는 목록에 없어서 _peer.Tcp.SendAsync 로 우회해 보냈다)
        private async Task SendPairMessageAsync(MessageType type, long value)
        {
            try
            {
                if (_peer == null)
                    return;

                (int high, int low) = PairToken.SplitLong(value);

                await _peer.SendAsync(new InputMessage
                {
                    Type = type,
                    Value1 = high,
                    Value2 = low
                });
            }
            catch
            {
                // 못 보내면 상대가 답을 못 하고, 보낸 쪽은 시간 초과로 처리한다.
            }
        }

        // "나는 이 기기다" 를 보낸다. 연결 요청(코드·열쇠) 바로 앞에 보낸다.
        private async Task SendHelloAsync()
        {
            (long high, long low) = PairToken.SplitGuid(DeviceIdentity.DeviceId);

            await SendPairMessageAsync(MessageType.HelloIdHigh, high);
            await SendPairMessageAsync(MessageType.HelloIdLow, low);
        }

        // PairResult 의 Value1 에 담는 값. (MessageType.cs 설명 참고)
        private const int PairWrongCode = 0;
        private const int PairOk = 1;
        private const int PairBusy = 2;
        private const int PairBadToken = 3;     // 저장된 기기 열쇠가 맞지 않음 → 코드로 다시

        // 답이 안 왔을 때. 네트워크로 오가는 값은 아니다.
        private const int PairNoAnswer = -1;

        private async Task SendPairResultAsync(int result)
        {
            try
            {
                if (_peer != null)
                {
                    // PeerConnection 이 종류를 보고 알아서 TCP 로 보낸다.
                    await _peer.SendAsync(new InputMessage
                    {
                        Type = MessageType.PairResult,
                        Value1 = result
                    });
                }
            }
            catch
            {
                // 답장이 실패하면 상대가 시간 초과로 처리한다.
            }
        }

        private async Task SendMyScreenInfoAsync()
        {
            try
            {
                if (_peer != null)
                {
                    // PeerConnection 이 종류를 보고 알아서 TCP 로 보낸다.
                    await _peer.SendAsync(new InputMessage
                    {
                        Type = MessageType.ScreenInfo,
                        Value1 = _myWidth,
                        Value2 = _myHeight
                    });
                }
            }
            catch
            {
                // 상대가 주기적으로 다시 물어보므로 실패해도 넘어간다.
            }
        }

        private async Task ReplyPongAsync(int pingId)
        {
            try
            {
                if (_peer != null)
                {
                    await _peer.SendAsync(new InputMessage
                    {
                        Type = MessageType.Pong,
                        Value1 = pingId
                    });
                }
            }
            catch
            {
                // 답장 실패는 상대가 시간 초과로 알아서 처리한다.
            }
        }
    }
}
