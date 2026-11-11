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
    //  MainWindow — 화면 배치
    //
    //  [배치] 버튼 → 배치 창 → DB 저장, 연결할 때 배치·상대 화면 크기 적용.
    //  계산은 MultiPcKvm.App 의 ScreenLayout 이 한다.
    //
    //  MainWindow 는 할 일이 많아서 주제별로 파일을 나눴다. (partial = 한 클래스를 여러 파일에 나눠 쓰기)
    //  필드(공용 상태)·생성자·시작·Tick 은 MainWindow.xaml.cs 에 있다.
    // ─────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        // 대기 중에 상대가 알려준 상대 화면 크기. 0 이면 아직 모름.
        // 네트워크 스레드가 쓰고 화면 스레드가 읽는다. (int 하나씩이라 찢어져 읽힐 일은 없다)
        private volatile int _peerScreenWidth;
        private volatile int _peerScreenHeight;

        // 화면 배치를 DB 에서 매번 읽지 않도록 들고 있는다. (키: 기기 ID)
        // 저장된 배치가 없는 기기는 null 로 기억한다. (없다는 것도 매번 확인하지 않게)
        private readonly Dictionary<Guid, SavedLayout?> _layoutCache = new();

        // ── 화면 배치 ─────────────────────────────────────────
        //
        //   [배치] 버튼      LayoutButton_Click → 배치 창 → DB 저장 (+ 연결 중이면 바로 적용)
        //   연결할 때        LoadPlacement / KnownRemoteSize → InputRouter 에 넣음
        //   크기를 받으면     PersistRemoteSize (Tick, 2초마다) → DB 에 크기 저장
        //
        // 계산은 전부 MultiPcKvm.App 의 ScreenLayout 이 한다.

        // DB 에 저장된 배치 (기억해 둔 것 우선). 없으면 null.
        private SavedLayout? GetSavedLayout(Guid deviceId)
        {
            if (_store == null)
                return null;

            if (_layoutCache.TryGetValue(deviceId, out SavedLayout? cached))
                return cached;

            SavedLayout? saved = null;

            try
            {
                saved = _store.GetLayout(deviceId);
            }
            catch (Exception ex)
            {
                // 읽기 실패해도 연결은 되어야 한다. 기본 배치로 간다.
                DiagLog.Write($"배치 읽기 실패 | {ex.Message}");
            }

            _layoutCache[deviceId] = saved;
            return saved;
        }

        // 저장된 배치. 없으면 null → 처음 위치(내 화면 왼쪽, 가운데 맞춤)를 쓴다.
        //
        // 처음 위치를 여기서 계산하지 않고 null 로 넘기는 이유:
        //   가운데 맞춤은 상대 화면 크기에 따라 달라진다. 연결 직후에는 아직 크기를 모를 수 있다.
        //   null 이면 InputRouter 가 실제 크기를 받는 순간 다시 가운데로 맞춘다.
        private ScreenPlacement? LoadPlacement(DeviceItem item) =>
            GetSavedLayout(item.DeviceId)?.Placement;

        // 상대 화면 크기로 쓸 값과, 그게 실제 값인지.
        //   1) 지금 대기 상태로 이어진 상대가 방금 알려준 크기 (가장 최신)
        //   2) DB 에 저장해 둔 크기 (지난번 연결 때 받은 값)
        //   3) 둘 다 없으면 내 화면과 같다고 짐작
        private (int Width, int Height, bool Known) KnownRemoteSize(DeviceItem item)
        {
            if (_peerItem != null && _peerItem.DeviceId == item.DeviceId &&
                _peerScreenWidth > 0 && _peerScreenHeight > 0)
            {
                return (_peerScreenWidth, _peerScreenHeight, true);
            }

            SavedLayout? saved = GetSavedLayout(item.DeviceId);

            if (saved != null && saved.ScreenWidth > 0 && saved.ScreenHeight > 0)
                return (saved.ScreenWidth, saved.ScreenHeight, true);

            return (_myWidth, _myHeight, false);
        }

        // [배치] 버튼
        private void LayoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not DeviceItem item)
                return;

            if (_store == null)
            {
                ShowHint("저장소를 열지 못해서 배치를 저장할 수 없습니다.", 5);
                return;
            }

            // 지금 이 기기를 조종 중인가. 그렇다면 연결에서 받은 실제 크기·배치를 쓴다.
            bool controllingIt = _session != null && _connectedItem == item;

            (int width, int height, bool known) =
                controllingIt && _session!.Router.RemoteSizeKnown
                    ? (_session.Router.RemoteWidth, _session.Router.RemoteHeight, true)
                    : KnownRemoteSize(item);

            ScreenPlacement? current = controllingIt
                ? _session!.Router.Placement
                : LoadPlacement(item);

            var window = new LayoutWindow(
                item.Name, _myWidth, _myHeight, width, height, known, current)
            {
                Owner = this
            };

            if (window.ShowDialog() != true)
                return;

            var layout = new SavedLayout
            {
                DeviceId = item.DeviceId,
                Placement = window.Result,
                ScreenWidth = width,
                ScreenHeight = height,
            };

            StoreResult result = _store.SaveLayout(layout);

            DiagLog.Write($"배치 저장 | {item.Name} | {DescribePlacement(window.Result)} | 결과 {result}");

            if (result != StoreResult.Ok && result != StoreResult.Created)
            {
                ShowHint("배치를 저장하지 못했습니다: " + _store.LastMessage, 6);
                return;
            }

            _layoutCache[item.DeviceId] = layout;

            // 지금 조종 중이면 바로 적용한다. 다시 연결할 필요 없다.
            // (커서가 상대 화면에 가 있으면 다음에 넘어갈 때부터 새 배치로 간다)
            if (_session != null && _connectedItem == item)
            {
                _session.Router.Placement = window.Result;
                ShowHint("배치를 바꿨습니다. " + CrossHint(window.Result.Side), 6);
            }
            else
            {
                ShowHint($"{item.Name} 의 화면 배치를 저장했습니다. 다음 연결부터 적용됩니다.", 5);
            }
        }

        // 상대 화면 크기를 새로 알게 됐으면 DB 에 저장한다. (Tick 에서 2초마다)
        //
        // 다음에 배치 창을 열 때 연결 전이라도 실제 비율로 그리고,
        // 다음에 연결할 때 처음부터 맞는 크기로 시작하려는 것이다.
        // 저장된 크기와 같으면 아무것도 안 한다. (DB 에 2초마다 쓰지 않도록)
        private void PersistRemoteSize()
        {
            if (_store == null)
                return;

            DeviceItem? target = _connectedItem ?? _peerItem;

            if (target == null || !target.IsSaved)
                return;

            int width, height;

            if (_session != null && _connectedItem == target && _session.Router.RemoteSizeKnown)
            {
                width = _session.Router.RemoteWidth;
                height = _session.Router.RemoteHeight;
            }
            else if (_peerItem == target && _peerScreenWidth > 0 && _peerScreenHeight > 0)
            {
                width = _peerScreenWidth;
                height = _peerScreenHeight;
            }
            else
            {
                return;     // 아직 실제 크기를 모른다
            }

            SavedLayout? saved = GetSavedLayout(target.DeviceId);

            if (saved != null && saved.ScreenWidth == width && saved.ScreenHeight == height)
                return;

            var layout = new SavedLayout
            {
                DeviceId = target.DeviceId,
                // 배치를 정한 적이 없으면 처음 위치(왼쪽, 가운데)를 실제 크기로 계산해 저장한다.
                Placement = saved?.Placement ??
                    ScreenLayout.DefaultPlacement(_myWidth, _myHeight, width, height),
                ScreenWidth = width,
                ScreenHeight = height,
            };

            try
            {
                StoreResult result = _store.SaveLayout(layout);

                if (result == StoreResult.Ok || result == StoreResult.Created)
                {
                    _layoutCache[target.DeviceId] = layout;
                    DiagLog.Write($"상대 화면 크기 저장 | {target.Name} | {width}×{height}");
                }
            }
            catch (Exception ex)
            {
                DiagLog.Write($"상대 화면 크기 저장 실패 | {ex.Message}");
            }
        }

        // 넘어가는 방법 안내. 예) "마우스를 화면 오른쪽 끝까지 밀면 ..."
        private static string CrossHint(RemotePosition side) =>
            $"마우스를 화면 {ScreenLayout.SideName(side)} 끝까지 밀면 상대 PC 로 넘어갑니다. " +
            $"돌아오려면 상대 화면의 {ScreenLayout.SideName(ScreenLayout.Opposite(side))} 끝까지 미세요.";

        // 기록 파일용 한 줄. 예) "오른쪽 +300"
        private static string DescribePlacement(ScreenPlacement p) =>
            $"{ScreenLayout.SideName(p.Side)} {(p.Offset >= 0 ? "+" : "")}{p.Offset}";
    }
}
