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
    //  MainWindow — 연결 · 끊기 · 다시 잇기 · 자동 연결
    //
    //  [연결]/[연결 끊기] 버튼, 코드 입력, 연결 요청 보내기(ConnectTo).
    //  줄이 끊겼을 때 다시 잇기(ReconnectAsync), 조종 멈추기(Disconnect), 대기 상태 내리기(TearDownPeer).
    //  켜자마자 마지막으로 조종한 기기에 자동 연결(TryAutoConnect).
    //
    //  MainWindow 는 할 일이 많아서 주제별로 파일을 나눴다. (partial = 한 클래스를 여러 파일에 나눠 쓰기)
    //  필드(공용 상태)·생성자·시작·Tick 은 MainWindow.xaml.cs 에 있다.
    // ─────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        // ── 연결 / 연결 끊기 ──────────────────────────────────

        private async void DeviceAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not DeviceItem item)
                return;

            // 사용자가 직접 눌렀으니 자동 연결은 그만둔다. (사용자 뜻이 먼저)
            CancelAutoConnect("사용자가 직접 [연결]/[연결 끊기] 를 누름");

            // 자동 연결이 진행 중이면 겹치지 않게 막는다.
            if (_autoConnecting)
            {
                ShowHint("자동 연결 중입니다. 잠시만 기다려 주세요.", 3);
                return;
            }

            if (item.IsConnected)
            {
                Disconnect();
                return;
            }

            // 조종당하는 쪽에서 [연결 끊기]
            if (item.IsControllingMe)
            {
                DisconnectIncoming();
                return;
            }

            // 재연결이 돌고 있는 중에 누르면 서로 꼬인다. 잠깐 막는다.
            if (_reconnecting)
            {
                ShowHint("다시 잇는 중입니다. 잠시만 기다려 주세요.", 3);
                return;
            }

            // 꺼져 있는 기기(회색)는 연결하지 않는다.
            // 버튼은 잠겨 있지만, 잠기기 직전(0.3초 틈)에 눌렸을 수 있어서 한 번 더 막는다.
            if (!item.IsOnline)
            {
                ShowHint($"{item.Name} 이(가) 아직 검색되지 않았습니다. " +
                         "상대 프로그램이 켜져 있고 같은 네트워크인지 확인하세요.", 5);
                return;
            }

            // 누가 나를 조종 중이면 연결하지 않는다. (한 방향만)
            //
            // 버튼은 이미 잠겨 있지만, 잠기기 직전(0.3초 틈)에 눌렸을 수 있어서
            // 여기서 한 번 더 막는다.
            if (IsBeingControlled())
            {
                ShowHint("상대가 이 PC 를 조종하고 있어 연결할 수 없습니다.", 4);
                return;
            }

            // 내가 다른 기기를 조종 중이면 연결하지 않는다.
            //
            // 예전에는 지금 조종을 멈추고 이 기기로 갈아탔다.
            // 실수로 누르면 조종이 끊기므로 막았다. 버튼도 잠겨 있지만(Tick),
            // 잠기기 직전(0.3초 틈)에 눌렸을 수 있어서 여기서 한 번 더 막는다.
            if (_session != null)
            {
                ShowHint("다른 기기와 연결 중입니다. 먼저 [연결 끊기] 를 누르세요.", 4);
                return;
            }

            await ConnectTo(item);
        }

        // 저장된 기기 열쇠. 없으면 null → 코드를 입력받는다.
        private long? GetTokenOut(DeviceItem item)
        {
            if (_store == null || !item.IsSaved)
                return null;

            try
            {
                return _store.GetTokenOut(item.DeviceId);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"열쇠 읽기 실패 | {ex.Message}");
                return null;
            }
        }

        // 상대 화면에 뜬 코드를 입력받는다. 취소했거나 그사이 다른 연결이 먼저 됐으면 null.
        private int? AskCode(DeviceItem item)
        {
            var dialog = new PairCodeWindow(item.Name) { Owner = this };

            // 입력하는 사이에 다른 기기가 먼저 이 PC 에 연결하면
            // HandleIncomingPair 에서 이 창을 닫는다. 그래서 들고 있는다.
            _pairDialog = dialog;

            bool? entered;

            try
            {
                entered = dialog.ShowDialog();
            }
            finally
            {
                _pairDialog = null;
            }

            if (entered != true)
                return null;

            // 창이 닫힌 뒤 한 번 더 확인한다. (먼저 연결한 쪽이 이긴다)
            //
            // [확인] 을 누른 것과 거의 같은 순간에 다른 기기가 먼저 연결했을 수 있다.
            // 그대로 진행하면, 다른 기기로 가려던 경우 지금 이 PC 를 조종 중인 줄을 끊어버린다.
            if (IsBeingControlled() || _session != null)
            {
                DiagLog.Write($"연결 취소 | {item.Name} | 코드 입력 중에 다른 연결이 먼저 됨");
                ShowHint("다른 기기가 먼저 연결해서 연결하지 않았습니다.", 5);
                return null;
            }

            return dialog.Code;
        }

        // interactive: 사용자가 [연결] 을 누른 경우 true.
        //              false(켜자마자 자동 연결)면 코드 창을 띄우지 않는다. 열쇠로 안 되면 그냥 그만둔다.
        private async Task ConnectTo(DeviceItem item, bool interactive = true)
        {
            // ── 1) 무엇으로 확인받을지 정한다 ──
            //
            // 저장된 기기 열쇠가 있으면 코드 없이 바로 연결한다.
            // 없으면(처음 연결하는 기기, 열쇠를 지운 기기) 상대 화면의 코드를 입력받는다.
            long? token = GetTokenOut(item);
            int code = -1;

            if (token == null && !interactive)
                return;

            if (token == null)
            {
                int? entered = AskCode(item);

                if (entered == null)
                    return;

                code = entered.Value;
            }

            item.StatusText = "연결 중...";

            try
            {
                // 대기 상태가 다른 기기를 향하고 있으면 그쪽을 정리하고 새로 만든다.
                //
                // IP 도 비교한다. 다른 기기가 나에게 연결했다가 끊고 간 뒤라면,
                // 대기 상태는 그 기기로 바로잡혀 있어도(HandleIncomingPair)
                // 줄은 처음 만들 때의 IP 를 향하고 있을 수 있다. 그러면 마우스 이동(UDP)이 엉뚱한 곳으로 간다.
                // (여기는 내가 조종당하는 중에는 오지 않으므로, 상대가 쓰는 줄을 끊을 일은 없다)
                if (_peer != null && _peerItem != null &&
                    (_peerItem.DeviceId != item.DeviceId || _peer.IpAddress != item.IpAddress))
                {
                    TearDownPeer();
                }

                EnsureStandby(item);

                if (_peer == null)
                    return;

                // ★ 이미 TCP 가 이어져 있으면 다시 걸지 않는다. ★
                //
                // 여기가 "연결 끊기 버튼인데 상태는 끊김" 의 원인이었다.
                //
                // TcpTransport.ConnectAsync() 는 맨 앞에서 Disconnect() 를 부른다.
                // 그래서 상대가 이미 나에게 붙어 있는데 내가 또 걸면:
                //
                //   1. 내 쪽: 상대가 만들어둔 줄을 내가 먼저 끊는다.
                //   2. 새 줄을 상대에게 건다.
                //   3. 상대 쪽 Accept: "이미 연결이 있다" 며 새 줄을 거부한다.
                //      (1:1 이라 두 번째 연결을 안 받는 구조다)
                //   4. 그런데 그 "이미 있는 연결" 은 1번에서 죽은 줄이다.
                //
                //   결과: 양쪽 다 연결이 없어진다. 다시 붙는 코드도 없어서
                //         Ping 도 키도 전부 "TCP 연결이 되어 있지 않습니다" 로 실패한다.
                //
                // TCP 한 줄에는 방향이 없다. 누가 걸었든 양쪽 다 보낼 수 있다.
                // 그러니 있는 줄을 그대로 쓰면 된다.
                //
                // ※ 지금은 팀원 TcpTransport 도 "이미 연결돼 있으면 안 끊음" 으로
                //    바뀌어서 이 검사가 없어도 줄이 죽지 않는다. 이중 안전장치로 둔다.
                if (!_peer.IsTcpConnected)
                {
                    DiagLog.Write($"연결 시도 | {item.Name} {_peer.IpAddress}:{item.TcpPort}");

                    bool ok = await _peer.ConnectAsync();

                    DiagLog.Write($"연결 결과 | {(ok ? "성공" : "실패")}");

                    if (!ok)
                    {
                        item.StatusText = item.IpAddress + " · 연결 실패";

                        ShowHint(
                            "연결에 실패했습니다. 방화벽에서 45000 포트를 " +
                            "차단하고 있지 않은지 확인하세요.", 8);
                        return;
                    }
                }

                // ── 2) 나를 알리고, 열쇠 또는 코드를 보내 승인을 기다린다 ──
                //
                // Hello(내 기기 ID)를 먼저 보낸다. 상대는 이걸로 누가 연결하는지 알고,
                // 열쇠가 맞는지 확인하고, 코드로 승인하면 나에게 줄 열쇠를 만든다.
                await SendHelloAsync();

                int result = PairNoAnswer;

                if (token != null)
                {
                    item.StatusText = "저장된 기기 확인 중...";

                    result = await RequestTokenPairAsync(token.Value);

                    DiagLog.Write($"열쇠 확인 결과 | {result} (1 승인, 2 지금 안 됨, 3 열쇠 틀림, -1 응답 없음)");

                    // 열쇠로 안 되면 코드로 다시 한다.
                    //   3 (열쇠 틀림)    : 상대가 나를 저장된 기기에서 지웠거나, 상대 DB 가 새로 만들어졌다.
                    //                    이 열쇠는 다시 써도 안 되므로 버린다.
                    //   -1 (답 없음)     : 줄은 살아 있는데 답이 없으면 상대가 예전 버전(열쇠를 모름)이다.
                    //                    줄까지 끊겼으면 상대가 다른 기기와 연결 중이라 받지 않은 것이므로 코드로 가지 않는다.
                    bool badToken = result == PairBadToken;
                    bool oldVersion = result == PairNoAnswer && _peer != null && _peer.IsTcpConnected;

                    if (badToken || oldVersion)
                    {
                        if (badToken)
                        {
                            _store?.SetTokenOut(item.DeviceId, null);
                            DiagLog.Write($"열쇠 버림 | {item.Name} | 상대가 받지 않음");
                        }

                        // 자동 연결이면 코드 창을 띄우지 않고 그만둔다. (사용자가 없을 수 있다)
                        if (!interactive)
                        {
                            if (_peer != null && !_incomingPaired)
                                _peer.Disconnect();

                            if (badToken)
                                CancelAutoConnect($"{item.Name} 이(가) 열쇠를 받지 않음 → [연결] 을 눌러 코드 입력 필요");

                            item.StatusText = item.IpAddress;
                            return;
                        }

                        ShowHint(badToken
                            ? $"{item.Name} 이(가) 이 PC 를 저장된 기기에서 지웠습니다. 코드를 입력하세요."
                            : $"{item.Name} 에 코드 없이 연결하지 못했습니다. 코드를 입력하세요.", 8);

                        int? entered = AskCode(item);

                        if (entered == null || _peer == null)
                        {
                            // 코드 입력을 취소했다. 상대가 나에게 쓰는 줄이 아니면 정리한다.
                            if (_peer != null && !_incomingPaired)
                                _peer.Disconnect();

                            item.StatusText = item.IpAddress;
                            return;
                        }

                        code = entered.Value;
                        token = null;

                        // 코드를 입력하는 사이 줄이 끊겼을 수 있다.
                        if (!_peer.IsTcpConnected && !await _peer.ConnectAsync())
                        {
                            item.StatusText = item.IpAddress + " · 연결 실패";
                            ShowHint("연결에 실패했습니다. 상대 프로그램이 켜져 있는지 확인하세요.", 8);
                            return;
                        }

                        await SendHelloAsync();
                    }
                }

                if (token == null)
                {
                    item.StatusText = "코드 확인 중...";

                    // 코드로 승인되면 상대가 열쇠를 보내준다. 이 기기에서 온 것만 받는다.
                    _expectGrantFrom = item.DeviceId;

                    result = await RequestPairAsync(code);

                    DiagLog.Write($"코드 확인 결과 | {result} (1 승인, 0 코드 틀림, 2 지금 안 됨, -1 응답 없음)");
                }

                // 기다리는 사이 대기 상태가 정리됐으면(다른 일로 TearDownPeer) 더 진행하지 않는다.
                if (_peer == null)
                {
                    _expectGrantFrom = null;
                    item.StatusText = item.IpAddress;
                    return;
                }

                if (result != PairOk)
                {
                    // 거절 이유마다 안내를 다르게 한다.
                    // 전부 "코드가 틀렸다" 로 보여주면 진짜 이유를 못 찾는다.
                    // 답이 없을 때, 줄이 이미 끊겨 있는지 본다.
                    //
                    // 상대가 다른 기기와 연결 중이면 상대 쪽(TcpTransport)이 내 줄을 받자마자 끊는다.
                    // 그래서 코드를 보내도 답이 안 오고, 줄은 끊겨 있다.
                    // 상대 프로그램이 꺼져 있으면 위의 연결 단계에서 이미 실패하므로 여기까지 오지 않는다.
                    bool droppedByPeer = result == PairNoAnswer && !_peer.IsTcpConnected;

                    (string status, string hint) = result switch
                    {
                        PairNoAnswer when droppedByPeer => (
                            "지금 연결할 수 없음",
                            $"{item.Name} 이(가) 연결을 받지 않았습니다. " +
                            "다른 기기와 연결 중일 수 있습니다. 잠시 후 다시 시도하세요."),

                        PairBusy => (
                            "지금 연결할 수 없음",
                            // Busy 는 두 경우다. 상대가 다른 기기와 연결 중이거나,
                            // 상대도 같은 순간 나에게 연결을 거는 중이었거나(동시에 Enter).
                            // 둘 다 맞는 문구로 쓴다.
                            $"{item.Name} 이(가) 지금 다른 연결 중이라 연결할 수 없습니다. " +
                            "잠시 후 다시 시도하세요."),

                        PairNoAnswer => (
                            "응답 없음",
                            "상대 PC 가 응답하지 않습니다. 상대 프로그램이 켜져 있는지 확인하세요."),

                        _ => (
                            "코드가 맞지 않음",
                            "코드가 맞지 않습니다. 상대 PC 화면에 표시된 4자리 숫자를 확인하세요.")
                    };

                    // 코드가 틀렸거나 응답이 없을 때만 줄을 끊는다.
                    //
                    // "지금 안 됨(Busy)" 일 때 끊으면 안 되는 이유:
                    //   상대도 막 나에게 연결을 거는 중일 수 있다.
                    //   그 줄을 끊으면 상대 쪽 시도까지 같이 망가진다.
                    //
                    // 상대가 나에게 걸어온 줄(_incomingPaired)도 끊으면 안 된다.
                    // 그건 상대가 쓰고 있는 줄이다.
                    if (result != PairBusy && !_incomingPaired)
                        _peer.Disconnect();

                    _expectGrantFrom = null;

                    item.StatusText = item.IpAddress + " · " + status;
                    ShowHint(hint, 8);
                    return;
                }

                // ── 3) 승인됐다. 조종을 시작한다 ──
                //
                // 통과한 코드(또는 열쇠)를 기억해둔다. 중간에 줄이 끊기면
                // 사용자에게 다시 묻지 않고 이걸로 조용히 다시 붙는다.
                _lastAuth = token != null
                    ? new PairAuth(IsToken: true, token.Value)
                    : new PairAuth(IsToken: false, code);

                // 신뢰 기기로 저장한다. 다음에 켜면 목록에 미리 떠 있다. (C 또는 U)
                SaveTrustedDevice(item);

                // 다음에 켜면 이 기기에 자동으로 연결한다. (TryAutoConnect)
                WriteSetting(SettingKeys.LastTarget, item.DeviceId.ToString());

                // 열쇠가 기기 저장보다 먼저 도착했으면 지금 넣는다.
                if (_pendingGrant is { } pending && pending.Id == item.DeviceId && _store != null)
                {
                    _store.SetTokenOut(pending.Id, pending.Token);
                    _pendingGrant = null;
                    _expectGrantFrom = null;
                    DiagLog.Write($"열쇠 받음 | {item.Name} | 다음부터 코드 없이 연결");
                }

                // 가로채기는 따로 들고 있는다. 화면 보기 창에서 키보드만 삼킬 때 쓴다. (MainWindow.ScreenView.cs)
                _capture = new WindowsInputCapture();

                // 커서가 상대 PC 로 넘어가면(삼키기 켜짐) 내 PC 커서를 숨기고, 돌아오면 다시 보인다.
                // 알림은 후킹 스레드에서 올 수 있어서 화면 스레드로 넘기기만 한다. (후킹 함수는 짧아야 한다)
                WindowsInputCapture capture = _capture;
                capture.SwallowInputChanged += on => Dispatcher.BeginInvoke(() => UpdateCursorHider(capture));

                _session = new KvmSession(
                    _capture,
                    new WindowsInputSender(),
                    _peer);

                _session.Router.LocalWidth = _myWidth;
                _session.Router.LocalHeight = _myHeight;

                // 상대 화면 크기와 배치.
                //
                // 크기: 실제 크기는 연결 직후 상대가 보내준다(ScreenInfo → KvmSession).
                //       그 전까지 쓸 값을 정한다. 아는 값이 있으면 그걸, 없으면 내 크기로 짐작.
                // 배치: [배치] 창에서 저장한 자리. 없으면 내 화면 왼쪽, 가운데 맞춤.
                (int remoteWidth, int remoteHeight, bool sizeKnown) = KnownRemoteSize(item);

                _session.Router.RemoteWidth = remoteWidth;
                _session.Router.RemoteHeight = remoteHeight;
                _session.Router.RemoteSizeKnown = sizeKnown;
                _session.Router.Placement = LoadPlacement(item);

                DiagLog.Write($"배치 적용 | {item.Name} | {DescribePlacement(_session.Router.EffectivePlacement)}{(_session.Router.Placement == null ? " (처음 위치)" : "")} | 상대 크기 {remoteWidth}×{remoteHeight}{(sizeKnown ? "" : " (짐작)")}");

                _session.Start();

                _connectedItem = item;
                item.IsConnected = true;

                // 연결 기록을 "연결 중" 으로 남긴다. (6주차, MainWindow.ConnectionLog.cs)
                BeginOutgoingLog(item);

                HintText.Text = CrossHint(_session.Router.EffectivePlacement.Side) + " " +
                    "넘어가 있는 동안에는 이 PC 의 키보드·마우스가 동작하지 않습니다.";
            }
            catch (Exception ex)
            {
                item.StatusText = item.IpAddress + " · 오류";
                ShowHint("오류: " + ex.Message, 8);
                Disconnect();
            }
        }

        // ── 끊긴 줄 다시 잇기 ────────────────────────────────
        //
        // 조종은 그대로 두고 줄만 다시 잇는다.
        // 조종 자체를 끊어버리면 사용자가 처음부터 다시 해야 한다.
        private async Task ReconnectAsync()
        {
            if (_peer == null || _session == null || _lastAuth == null)
                return;

            _reconnecting = true;

            try
            {
                ShowHint("연결이 끊겼습니다. 다시 잇는 중...", 3);
                DiagLog.Write($"줄 끊김 → 다시 잇기 시도 | {_peer?.IpAddress}");

                // 조금씩 다르게 기다렸다 건다.
                //
                // 양쪽이 동시에 걸면 줄이 또 죽는다.
                // (아래 ConnectTo 의 긴 주석 참고 — 실험에서 20번 중 20번 죽었다)
                // 기다리는 시간을 매번 다르게 하면 한쪽이 먼저 붙고,
                // 나머지 한쪽은 아래 IsTcpConnected 검사에서 그냥 그 줄을 쓴다.
                await Task.Delay(Random.Shared.Next(0, 700));

                if (_peer == null || _session == null)
                    return;

                // 그사이 상대가 나에게 걸어왔으면 그 줄을 쓴다.
                if (!_peer.IsTcpConnected)
                {
                    if (!await _peer.ConnectAsync())
                        return;     // 2초 뒤 Tick 이 다시 부른다
                }

                // 줄만 이어서는 안 된다. 상대는 코드(또는 열쇠)로 확인한 뒤에야
                // 나를 "조종 중인 상대" 로 인정한다.
                PairAuth? auth = _lastAuth;

                if (auth == null)
                    return;

                await SendHelloAsync();

                int result = auth.IsToken
                    ? await RequestTokenPairAsync(auth.Value)
                    : await RequestPairAsync((int)auth.Value);

                // 답이 없으면 포기하지 않는다. 2초 뒤 Tick 이 다시 부른다.
                // (예전에는 이것도 "코드가 바뀌었다" 로 처리해서
                //  잠깐 늦게 답했을 뿐인데 조종이 끊겼다)
                if (result == PairNoAnswer)
                    return;

                if (result != PairOk)
                {
                    // 자동으로는 더 붙을 수 없다. 조종을 멈추고 사용자에게 넘긴다.
                    Disconnect(reason: "다시 연결 실패");

                    // 열쇠가 거절됐으면 그 열쇠는 버린다. 다음 [연결] 에서 코드를 묻게 된다.
                    if (result == PairBadToken && _peerItem != null)
                        _store?.SetTokenOut(_peerItem.DeviceId, null);

                    ShowHint(result switch
                    {
                        // 그사이 상대가 이 PC 를 반대로 조종하기 시작했다.
                        PairBusy => "상대 PC 가 지금 다른 쪽을 조종하고 있어 다시 연결하지 못했습니다.",

                        // 상대가 이 PC 를 저장된 기기에서 지웠다.
                        PairBadToken => "상대 PC 가 이 PC 를 저장된 기기에서 지웠습니다. " +
                                        "[연결] 을 다시 눌러 코드를 입력하세요.",

                        // 상대가 프로그램을 다시 켜면 코드가 새로 뽑힌다.
                        _ => "상대 PC 의 연결 코드가 바뀌었습니다. " +
                             "[연결] 을 다시 눌러 새 코드를 입력하세요."
                    }, 10);
                    return;
                }

                // 연결 상태 판정을 처음부터 다시 시작한다.
                // 안 하면 끊겨 있던 동안 쌓인 실패 기록 때문에
                // 한동안 "끊김" 으로 남아서 커서가 안 넘어간다.
                _session?.Monitor.OnConnected();

                ShowHint("다시 연결됐습니다.", 3);
                DiagLog.Write("다시 잇기 성공");
            }
            catch (Exception ex)
            {
                ShowHint("다시 잇기 실패: " + ex.Message, 5);
            }
            finally
            {
                _reconnecting = false;
            }
        }

        // 코드를 보내고 답을 기다린다.
        //
        // 돌려주는 값: PairOk / PairWrongCode / PairBusy / PairNoAnswer
        private Task<int> RequestPairAsync(int code) =>
            RequestPairCoreAsync(peer => peer.SendAsync(new InputMessage
            {
                // PeerConnection 이 종류를 보고 알아서 TCP 로 보낸다.
                Type = MessageType.PairRequest,
                Value1 = code
            }));

        // 저장된 기기 열쇠를 보내고 답을 기다린다.
        //
        // 돌려주는 값: PairOk / PairBusy / PairBadToken / PairNoAnswer
        private Task<int> RequestTokenPairAsync(long token)
        {
            (int high, int low) = PairToken.SplitLong(token);

            // PeerConnection 이 종류를 보고 알아서 TCP 로 보낸다. (SendPairMessageAsync 설명 참고)
            return RequestPairCoreAsync(peer => peer.SendAsync(new InputMessage
            {
                Type = MessageType.PairTokenRequest,
                Value1 = high,
                Value2 = low
            }));
        }

        // 요청 하나를 보내고 PairResult 를 기다린다. (코드·열쇠 공통)
        private async Task<int> RequestPairCoreAsync(Func<PeerConnection, Task> send)
        {
            if (_peer == null)
                return PairNoAnswer;

            _pairWaiter = new TaskCompletionSource<int>();

            try
            {
                await send(_peer);
            }
            catch
            {
                _pairWaiter = null;
                return PairNoAnswer;
            }

            // 3초 안에 답이 안 오면 "답 없음" 으로 본다.
            //
            // Task.WhenAny = "둘 중 먼저 끝나는 쪽을 기다린다".
            //   답이 먼저 오면 그 답, 시간이 먼저 지나면 시간 초과.
            Task finished = await Task.WhenAny(
                _pairWaiter.Task,
                Task.Delay(3000));

            int result = finished == _pairWaiter.Task
                ? _pairWaiter.Task.Result
                : PairNoAnswer;

            _pairWaiter = null;

            return result;
        }


        // 조종만 멈춘다. 대기 상태는 유지해서 상대가 걸어올 수 있게 둔다.
        //
        // notifyPeer: 상대에게 "그만 조종할게" 를 보낼지.
        //   내가 끊을 때는 보낸다. 상대가 끊어서 따라 끊을 때는 안 보낸다.
        //
        // reason: 연결 기록(connection_log)에 남길 끊긴 이유. (6주차)
        private void Disconnect(bool notifyPeer = true, string reason = "이 PC 에서 끊음")
        {
            bool wasControlling = _session != null;

            if (wasControlling)
                DiagLog.Write($"조종 멈춤 | 상대에게 알림 {(notifyPeer ? "보냄" : "안 보냄(상대가 먼저 끊음)")}");

            // 연결 기록을 "종료" 로 바꾼다. 세션을 멈추기 전에 해야 평균 지연·보낸 수를 읽을 수 있다.
            EndOutgoingLog(reason);

            // KvmSession.Stop() 이 삼키기를 풀고 눌린 키를 정리한다.
            _session?.Stop();
            _session = null;

            // 커서를 숨겨 두었으면 바로 다시 보이게 한다. (삼키기가 풀리며 알림도 오지만 기다리지 않는다)
            _cursorHider?.Hide();

            // 화면 보기 창도 닫는다. (창 위 입력을 상대가 더 이상 받아주지 않는다)
            CloseViewer();

            // 일부러 끊었으니 자동으로 다시 붙으면 안 된다.
            _lastAuth = null;

            // 상대에게 알린다.
            //
            // TCP 줄은 끊지 않는다. 줄을 끊으면 상대가 "사고로 끊겼다" 고 보고
            // 자동으로 다시 붙어버린다. 그리고 상대가 나를 조종하는 줄이기도 하다.
            if (wasControlling && notifyPeer)
            {
                _ = SendUnpairAsync();

                // 한 방향이라 보통은 이미 false 다.
                // 양쪽이 거의 동시에 연결돼서 둘 다 조종 중이 된 틈이 있었다면,
                // 상대도 이 신호를 받고 멈추므로 여기서 같이 정리한다.
                // 안 지우면 내 [연결] 버튼이 잠긴 채로 남는다.
                _incomingPaired = false;
            }

            if (_connectedItem != null)
            {
                _connectedItem.IsConnected = false;
                _connectedItem.StatusText = _connectedItem.IpAddress;
                _connectedItem = null;
            }

            HintText.Text = "연결할 기기의 [연결] 을 누르세요.";
        }

        // 조종당하는 쪽에서 끊는다.
        //
        // 조종하는 쪽의 Disconnect 와 같은 신호(Unpair)를 보낸다.
        // 상대는 Unpair 를 받으면 조종을 멈추고, 자동으로 다시 붙지도 않는다.
        // (OnPeerMessage 의 Unpair 처리 → Disconnect(notifyPeer: false) → _lastAuth = null)
        //
        // 신호가 늦게 도착해도 괜찮다. 여기서 _incomingPaired 를 먼저 내리므로
        // 그사이 들어오는 입력은 OnPeerMessage 에서 버려진다.
        private void DisconnectIncoming()
        {
            DiagLog.Write($"조종당하는 쪽에서 끊음 | {_peerItem?.Name}");

            // 1) 입력 받기를 바로 멈춘다.
            _incomingPaired = false;

            // 연결 기록을 "종료" 로 바꾼다. (6주차)
            EndIncomingLog("이 PC 에서 끊음");

            // 2) 상대가 눌러놓은 키·마우스 버튼이 있으면 떼준다.
            //    안 하면 Shift 등이 눌린 채로 남을 수 있다.
            _receiver?.ReleaseAll();

            // 3) "최근 2초 안에 입력을 받았다" 기록도 지운다.
            //    안 지우면 끊은 뒤 2초 동안 버튼이 잠겨 있다. (IsBeingControlled)
            _lastInputAt = DateTime.MinValue;

            // 4) 상대에게 알린다. TCP 줄은 끊지 않는다. (Disconnect 와 같은 이유)
            _ = SendUnpairAsync();

            if (_peerItem != null)
                _peerItem.IsControllingMe = false;

            ShowHint("연결을 끊었습니다.", 4);
        }

        private async Task SendUnpairAsync()
        {
            try
            {
                if (_peer != null)
                {
                    // PeerConnection 이 종류를 보고 알아서 TCP 로 보낸다.
                    await _peer.SendAsync(new InputMessage
                    {
                        Type = MessageType.Unpair
                    });
                }
            }
            catch
            {
                // 못 보내도 괜찮다. 상대는 줄이 끊기면 알아서 정리한다.
            }
        }

        // 대기 상태까지 완전히 내린다.
        private void TearDownPeer()
        {
            Disconnect(reason: "연결 정리");

            _receiver?.ReleaseAll();
            _receiver = null;

            if (_peer != null)
                _peer.MessageReceived -= OnPeerMessage;

            _peer?.Disconnect();
            _peer?.StopListening();
            _peer = null;
            _peerItem = null;

            // 다른 기기와 대기 상태를 새로 만들 것이므로, 앞 상대의 화면 크기는 잊는다.
            _peerScreenWidth = 0;
            _peerScreenHeight = 0;
        }


        // ── 내 PC 커서 숨기기 ──────────────────────────────────
        //
        // 커서가 상대 PC 로 넘어가 있는 동안 내 PC 의 커서는 주 모니터 한가운데에 묶여 있다.
        // 그게 화면에 멈춰 보이지 않게 숨긴다. 방법은 CursorHider.cs 설명 참고.

        private CursorHider? _cursorHider;

        // 지금 상태에 맞춰 숨기거나 보인다. (화면 스레드)
        //
        // 알림이 늦게 도착할 수 있으므로, 받은 값이 아니라 "지금" 삼키는 중인지를 다시 본다.
        // 끝난 세션의 늦은 알림이 지금 세션의 커서를 숨기지 않게, 지금 쓰는 가로채기인지도 확인한다.
        private void UpdateCursorHider(WindowsInputCapture capture)
        {
            bool hide = capture == _capture && _session != null && capture.SwallowInput;

            if (hide)
            {
                _cursorHider ??= new CursorHider();
                _cursorHider.Show(capture.AnchorX, capture.AnchorY);
            }
            else if (_capture == null || !_capture.SwallowInput)
            {
                _cursorHider?.Hide();
            }
        }


        // ── 켜자마자 마지막 기기에 자동 연결 ───────────────────
        //
        // 마지막으로 내가 "조종한" 기기를 기억해 두었다가, 다음에 켰을 때
        // 그 기기가 검색에 잡히면 코드 없이(저장된 기기 열쇠로) 자동으로 연결한다.
        //
        // 조종당하던 쪽은 따로 할 일이 없다. 조종하던 쪽이 자동으로 걸어오면
        // 열쇠를 확인하고 받아준다. 그래서 두 PC 가 어느 순서로 켜지든 같은 방향으로 다시 이어진다.
        //
        // 그래서 "마지막 기기" 는 조종을 시작할 때 적고, 남이 나를 조종하기 시작하면 지운다.
        // (안 지우면 예전에 내가 조종했던 기기와, 지금 나를 조종하던 기기가
        //  켜자마자 서로에게 동시에 걸어서 둘 다 거절된다)
        //
        // 이번 실행에서 사용자가 [연결]·[연결 끊기] 를 직접 누르면 자동 연결은 그만둔다.

        private Guid? _autoTarget;
        private bool _autoConnectPending;
        private bool _autoConnecting;
        private int _autoTries;
        private DateTime _autoNextTry = DateTime.MinValue;

        // 자동 연결을 몇 번까지 해 볼지. 상대가 켜져 있는데도 안 되면(다른 기기와 연결 중 등) 그만둔다.
        private const int AutoConnectMaxTries = 10;

        // Tick 에서 0.3초마다 부른다. 조건이 맞을 때만 실제로 연결을 건다.
        private void TryAutoConnect()
        {
            if (!_autoConnectPending || _autoConnecting || DateTime.Now < _autoNextTry)
                return;

            // 이미 무언가와 연결돼 있거나, 사용자가 코드 창을 띄워 둔 동안은 하지 않는다.
            if (_session != null || IsBeingControlled() || _reconnecting || _pairDialog != null)
                return;

            DeviceItem? item = _devices.FirstOrDefault(d =>
                d.DeviceId == _autoTarget && d.IsOnline && d.IsSaved && !d.IsLocked);

            if (item == null)
                return;     // 아직 상대가 안 켜졌거나 검색 전이다. 잡힐 때까지 기다린다.

            // 열쇠가 없으면 코드를 물어야 하는데, 자동 연결은 창을 띄우지 않는다.
            if (GetTokenOut(item) == null)
            {
                _autoConnectPending = false;
                DiagLog.Write($"자동 연결 안 함 | {item.Name} | 저장된 기기 열쇠 없음 (코드로 한 번 연결해야 함)");
                return;
            }

            _autoConnecting = true;
            _autoTries++;
            _autoNextTry = DateTime.Now.AddSeconds(10);

            DiagLog.Write($"자동 연결 시도 {_autoTries}/{AutoConnectMaxTries} | {item.Name}");
            ShowHint($"마지막으로 연결한 {item.Name} 에 자동으로 연결하는 중...", 4);

            _ = AutoConnectAsync(item);
        }

        private async Task AutoConnectAsync(DeviceItem item)
        {
            try
            {
                await ConnectTo(item, interactive: false);
            }
            finally
            {
                _autoConnecting = false;

                if (_session != null && _connectedItem == item)
                {
                    _autoConnectPending = false;
                    DiagLog.Write($"자동 연결 성공 | {item.Name}");
                }
                else if (_autoTries >= AutoConnectMaxTries)
                {
                    _autoConnectPending = false;
                    DiagLog.Write($"자동 연결 그만둠 | {item.Name} | {AutoConnectMaxTries}번 실패");
                }
            }
        }

        // 사용자가 직접 연결·끊기를 했으면 자동 연결은 그만둔다.
        private void CancelAutoConnect(string why)
        {
            if (!_autoConnectPending)
                return;

            _autoConnectPending = false;
            DiagLog.Write($"자동 연결 취소 | {why}");
        }
    }
}
