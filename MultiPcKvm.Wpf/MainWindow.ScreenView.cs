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
    //  MainWindow — 화면 보기
    //
    //  조종하는 쪽: 연결된 기기 줄의 [화면] → 상대 화면 보기 창(ViewerWindow)
    //  조종당하는 쪽: 켤 때 화면 보내기(IScreenSharer)를 준비하고,
    //                 "지금 나를 조종 중인 PC" 가 요청할 때만 허락한다. (CanViewScreen)
    //
    //  영상 자체(캡처·인코딩·전송)는 김기령님 MultiPcKvm.Video 가 한다.
    //  약속은 MultiPcKvm.Common 의 ScreenShare.cs, 만드는 곳은 ScreenShareFactory.cs.
    //
    //  MainWindow 는 할 일이 많아서 주제별로 파일을 나눴다. (partial = 한 클래스를 여러 파일에 나눠 쓰기)
    //  필드(공용 상태)·생성자·시작·Tick 은 MainWindow.xaml.cs 에 있다.
    // ─────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        // 조종당하는 쪽: 내 화면 보내기. 영상 기능이 없으면 null.
        private IScreenSharer? _sharer;

        // 조종하는 쪽: 지금 열려 있는 화면 보기 창. (한 번에 하나)
        private ViewerWindow? _viewerWindow;

        // 지금 조종에 쓰는 가로채기. (ConnectTo 에서 KvmSession 을 만들 때 같이 넣는다)
        // 화면 보기 창이 선택돼 있는 동안 키보드만 삼켜서 상대에게 보내는 데 쓴다.
        private WindowsInputCapture? _capture;

        // 화면 보내기 준비. (StartServices 에서 부른다)
        private void StartScreenSharing()
        {
            _sharer = ScreenShareFactory.CreateSharer();

            if (_sharer == null)
            {
                DiagLog.Write("화면 보내기 | 영상 기능 없음 (ScreenShareFactory)");
                return;
            }

            // 누가 보여 달라고 하면 여기서 허락 여부를 정한다.
            _sharer.CanView = CanViewScreen;

            try
            {
                _sharer.StartListening();
                DiagLog.Write($"화면 보내기 준비 | 포트 {Constants.VideoPort}");
            }
            catch (Exception ex)
            {
                DiagLog.Write($"화면 보내기 준비 실패 | {ex.Message}");
                ShowHint("화면 보내기를 준비하지 못했습니다: " + ex.Message, 8);
                _sharer.Dispose();
                _sharer = null;
            }
        }

        // 화면 보내기 정리. (Cleanup 에서 부른다)
        private void StopScreenSharing()
        {
            try
            {
                _sharer?.StopListening();
                _sharer?.Dispose();
            }
            catch (Exception ex)
            {
                DiagLog.Write($"화면 보내기 정리 실패 | {ex.Message}");
            }

            _sharer = null;
        }

        // 마지막으로 기록한 화면 보기 허락 결과 (같은 결과면 다시 기록하지 않는다)
        private string? _lastViewDecision;

        // 내 화면을 보여줘도 되는가. (영상 쪽이 요청을 받을 때 부른다. 어느 스레드에서든 온다)
        //
        // "코드(또는 저장된 기기 열쇠)를 통과해서 지금 나를 조종 중인 PC" 일 때만 허락한다.
        // 이게 없으면 같은 공유기에 있는 누구나 내 화면을 볼 수 있다.
        //
        // IP 비교는 "지금 연결된 입력 줄(TCP 45000)의 상대 실제 주소" 로 한다. (PeerConnection.ConnectedIpAddress)
        // 검색에 잡힌 주소와 달리, PC 가 랜선·와이파이 두 주소를 가진 경우에도 정확하다.
        //
        // 영상 쪽은 보내는 동안 1초마다 이걸 다시 부른다. (조종이 끝나면 바로 멈추려고)
        // 그래서 기록은 결과가 바뀔 때만 남긴다. 매번 남기면 보는 동안 1초에 한 줄씩 쌓인다.
        private bool CanViewScreen(string ip)
        {
            PeerConnection? peer = _peer;
            string? controllerIp = peer?.ConnectedIpAddress;

            bool allowed =
                _incomingPaired &&
                peer != null && peer.IsTcpConnected &&
                controllerIp != null &&
                string.Equals(controllerIp, ip, StringComparison.OrdinalIgnoreCase);

            string decision = $"{ip} | {(allowed ? "허락" : "거절")} (조종 중인 PC {controllerIp ?? "없음"})";

            // 여러 스레드에서 올 수 있어서 바꿔 끼우기(Exchange)로 비교한다.
            if (Interlocked.Exchange(ref _lastViewDecision, decision) != decision)
                DiagLog.Write("화면 보기 허락 확인 | " + decision);

            return allowed;
        }

        // [화면] 버튼 (연결된 기기 줄에만 보인다)
        private void ViewScreen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not DeviceItem item)
                return;

            // 내가 이 기기를 조종 중일 때만 연다. (창 위 입력을 상대가 받아주는 조건이라)
            if (_session == null || _connectedItem != item || _peer == null)
            {
                ShowHint("먼저 이 기기에 연결하세요.", 4);
                return;
            }

            // 이미 열려 있으면 앞으로 가져오기만 한다.
            if (_viewerWindow != null)
            {
                _viewerWindow.Activate();
                return;
            }

            string ip = _peer.IpAddress;

            _viewerWindow = new ViewerWindow(
                item.Name,
                ip,
                ScreenShareFactory.CreateViewer(),
                send: SendViewerInputAsync,
                remoteSize: RemoteScreenSize,
                setKeyboardToRemote: SetViewerKeyboard,
                // 파일 버튼. (MainWindow.FileTransfer.cs)
                sendFile: () => SendFilesToPeer(_viewerWindow),
                requestFile: RequestFilesFromPeer);

            // 창 주인(Owner)은 두지 않는다. 메인 창을 트레이로 숨겨도 이 창은 남아 있어야 한다.
            _viewerWindow.Closed += (s, args) =>
            {
                _viewerWindow = null;
                _viewerLastMove = null;     // 다음 창에서 옛 위치로 클릭이 가지 않게
            };
            _viewerWindow.Show();
        }

        // 화면 보기 창의 마지막 이동. (클릭·휠 직전에 TCP 로 한 번 더 보내려고)
        private InputMessage? _viewerLastMove;

        // 화면 보기 창의 마우스·키보드를 상대에게 보낸다. (화면 스레드에서 불린다)
        //
        // 이동은 빠른 길(UDP), 클릭·휠은 확실한 길(TCP)로 따로 가서 도착 순서가 보장되지 않는다.
        // 창은 "이동 → 누르기" 순서로 보내지만, 누르기가 먼저 도착하면 옮기기 전 자리에 찍힌다.
        // 그래서 클릭·휠 바로 앞에 마지막 위치를 같은 TCP 줄로 한 번 더 보낸다. (같은 줄 안에서는 순서가 지켜진다)
        // 일반 조종(KvmSession)도 같은 방법을 쓴다.
        private async Task SendViewerInputAsync(InputMessage message)
        {
            PeerConnection? peer = _peer;

            if (peer == null)
                return;

            if (message.Type == MessageType.MouseMove)
            {
                _viewerLastMove = message;
                await peer.SendAsync(message);
                return;
            }

            if ((message.Type == MessageType.MouseButton || message.Type == MessageType.MouseWheel) &&
                _viewerLastMove is InputMessage position)
            {
                await peer.Tcp.SendAsync(position);
            }

            await peer.SendAsync(message);
        }

        // 상대 화면 해상도. 연결 직후 상대가 알려준 값(ScreenInfo), 모르면 지금까지 아는 값.
        private (int Width, int Height) RemoteScreenSize()
        {
            if (_session != null)
                return (_session.Router.RemoteWidth, _session.Router.RemoteHeight);

            return (_peerScreenWidth > 0 ? _peerScreenWidth : _myWidth,
                    _peerScreenHeight > 0 ? _peerScreenHeight : _myHeight);
        }

        // 화면 보기 창이 선택되면 켜고, 다른 창으로 가거나 닫히면 끈다.
        //
        // 켜져 있는 동안
        //   · 키보드: 후킹에서 삼키고 상대에게 보낸다. 그래서 Alt+Tab·Windows 키도 상대에게 간다.
        //            (창으로 받으면 Windows 가 먼저 가로채서 내 PC 에서 작업 전환·시작 메뉴가 열린다)
        //   · 화면 끝으로 밀어 넘어가기: 잠시 멈춘다. (창을 크게 띄워 쓰다가 넘어가 버리지 않게)
        //   · 마우스: 그대로 내 PC 에서 움직인다. 창 위의 움직임·클릭·휠은 창이 직접 보낸다.
        //
        // 끌 때 상대에 눌려 있는 키를 전부 뗀다. (누른 채 창을 떠나면 뗀 신호가 안 가므로)
        private void SetViewerKeyboard(bool on)
        {
            KvmSession? session = _session;
            WindowsInputCapture? capture = _capture;

            if (session == null || capture == null)
                return;

            session.Router.KeyboardToRemote = on;
            session.Router.EdgeCrossingPaused = on;
            capture.SwallowKeyboard = on;

            if (!on)
                session.ReleaseRemotePressed();

            DiagLog.Write($"화면 보기 키보드 | {(on ? "상대에게 보냄" : "내 PC 로 돌려놓음")}");
        }

        // 조종을 멈추면 화면 보기 창도 닫는다. (Disconnect 에서 부른다)
        // 창 위 입력을 상대가 더 이상 받아주지 않으므로 열어 둘 이유가 없다.
        private void CloseViewer()
        {
            // 창을 닫으면 Deactivated·Closed 에서 키보드도 돌려놓는다. 혹시 몰라 여기서도 끈다.
            if (_capture != null)
                _capture.SwallowKeyboard = false;

            _viewerWindow?.Close();
            _viewerWindow = null;
        }
    }
}
