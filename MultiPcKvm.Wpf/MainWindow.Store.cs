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
    //  MainWindow — 저장된 기기 (DB)
    //
    //  켤 때 저장된 기기를 목록에 띄우고, 연결 코드를 통과한 기기를 저장·수정·삭제한다.
    //  SQL 은 MultiPcKvm.App 의 DeviceRepository 에만 있다. 여기서는 결과만 화면에 반영한다.
    //
    //  MainWindow 는 할 일이 많아서 주제별로 파일을 나눴다. (partial = 한 클래스를 여러 파일에 나눠 쓰기)
    //  필드(공용 상태)·생성자·시작·Tick 은 MainWindow.xaml.cs 에 있다.
    // ─────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        // ── 저장된 기기 (DB) ──────────────────────────────────
        //
        // 여기 있는 함수들이 화면과 DeviceRepository 를 잇는다.
        // SQL 은 전부 DeviceRepository 안에 있고, 여기서는 결과만 받아서 화면에 반영한다.
        //
        //   켤 때           OpenStore          → R (통합 목록: 기기 + 배치 + 연결 통계, 6주차)
        //   연결 코드 통과   SaveTrustedDevice  → C 또는 U
        //   이름·IP 변경    UpdateSavedDevice  → R (한 대) + U
        //   [삭제] 버튼     DeleteDevice_Click → D

        // DB 를 열고 저장된 기기를 목록에 "오프라인" 으로 띄운다.
        private void OpenStore()
        {
            try
            {
                var db = new KvmDatabase(KvmDatabase.DefaultPath);
                db.EnsureCreated();
                _store = new DeviceRepository(db);

                // 지난번에 프로그램이 갑자기 꺼져서 "연결 중" 으로 남은 연결 기록을 닫는다. (6주차)
                int dangling = _store.CloseDanglingConnections("프로그램이 비정상 종료됨");

                if (dangling > 0)
                    DiagLog.Write($"남은 연결 기록 {dangling}건 정리 (지난번 비정상 종료)");

                // 기기 + 화면 배치 + 연결 통계를 JOIN 한 번으로 읽는다. (6주차)
                //
                // 예전에는 기기 목록을 읽고, 연결할 때 기기마다 배치를 또 읽었다. (기기 수만큼 더 묻기)
                // 이제 켤 때 한 번에 읽어서 배치는 _layoutCache 에 미리 넣어 둔다.
                List<DeviceDetail> savedList = _store.GetAllDetails();

                if (_store.LastResult != StoreResult.Ok)
                    throw new InvalidOperationException(_store.LastMessage);

                DiagLog.Write($"저장된 기기 {savedList.Count}대 불러옴 (기기·배치·연결 기록 통합 조회 1번)");

                foreach (DeviceDetail detail in savedList)
                {
                    TrustedDevice saved = detail.Device;

                    DiagLog.Write($"  저장됨 | {saved.DeviceName} {saved.LastIp} {saved.DeviceId} 신뢰={(saved.IsTrusted ? 1 : 0)} | {DescribeDetail(detail)}");

                    // 배치가 없는 기기도 null 로 넣어 둔다. ("없다" 도 다시 묻지 않게. GetSavedLayout 참고)
                    _layoutCache[saved.DeviceId] = detail.Layout;

                    // 신뢰가 취소된 기기(is_trusted = 0)는 띄우지 않는다.
                    if (!saved.IsTrusted)
                        continue;

                    _devices.Add(new DeviceItem
                    {
                        DeviceId = saved.DeviceId,
                        Name = saved.DeviceName,
                        IpAddress = saved.LastIp ?? "",
                        TcpPort = Constants.InputPort,
                        IsSaved = true,
                        IsOnline = false,       // 검색에 잡히기 전까지는 꺼진 걸로 본다
                        LastSeen = DateTime.MinValue,
                        StatusText = OfflineText(saved.LastSeenAt),
                        DetailText = DescribeDetail(detail),
                    });
                }
            }
            catch (Exception ex)
            {
                // DB 가 없어도 연결 기능은 돌아가야 한다. 저장만 포기한다.
                _store = null;
                ShowHint("저장된 기기 목록을 열지 못했습니다: " + ex.Message, 8);
            }
        }

        // 연결 코드를 통과한 기기를 저장한다.
        //
        // 조종하는 쪽: 내가 코드를 넣어 통과했을 때
        // 조종당하는 쪽: 상대가 내 코드를 맞혔을 때
        // 둘 다 저장한다. 서로 한 번 확인한 사이이기 때문이다.
        private void SaveTrustedDevice(DeviceItem item)
        {
            if (_store == null)
                return;

            StoreResult result = _store.SavePaired(item.DeviceId, item.Name, item.IpAddress);

            if (result == StoreResult.Created || result == StoreResult.Ok)
                item.IsSaved = true;
            else
                ShowHint("기기를 저장하지 못했습니다: " + _store.LastMessage, 5);
        }

        // 저장된 기기의 이름·IP 가 바뀌었을 때 DB 를 고친다.
        private void UpdateSavedDevice(DeviceItem item)
        {
            if (_store == null)
                return;

            // 먼저 DB 의 현재 값을 읽어온다. (R)
            // 바뀐 칸만 고치고 나머지(처음 연결한 날 등)는 그대로 두기 위해서다.
            TrustedDevice? saved = _store.Find(item.DeviceId);

            if (saved == null)
                return;         // 그사이 삭제됐다

            saved.DeviceName = item.Name;
            saved.LastIp = item.IpAddress;

            _store.Update(saved);
        }

        // [삭제] 버튼
        private void DeleteDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not DeviceItem item)
                return;

            if (_store == null)
                return;

            // 연결 중인 기기는 못 지운다.
            // 지우는 순간 상대가 "저장 안 된 기기" 가 되는데, 연결은 살아 있어서 헷갈린다.
            //
            // "연결 중" 은 조종이 실제로 걸려 있을 때만이다. (IsLinkBusy)
            //   · 내가 조종 중 (_session)
            //   · 상대가 나를 조종 중 (_incomingPaired)
            //
            // 예전에는 TCP 줄이 살아 있는지(IsTcpConnected)로 봤는데, 그게 버그였다.
            // [연결 끊기] 는 조종만 멈추고 TCP 줄은 일부러 남겨둔다. (Disconnect 참고)
            // 그래서 끊은 뒤에도 "연결 중" 으로 판단돼서 삭제가 안 됐다.
            if (item.IsConnected || (item == _peerItem && IsLinkBusy()))
            {
                DiagLog.Write($"삭제 거절 | {item.Name} | 조종 중");
                ShowHint("연결 중인 기기는 삭제할 수 없습니다. 먼저 연결을 끊으세요.", 4);
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                $"{item.Name} 을(를) 저장된 기기에서 삭제할까요?\n다음에 연결할 때 코드를 다시 입력해야 합니다.",
                "기기 삭제",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;

            StoreResult result = _store.Delete(item.DeviceId);

            // 404 여도 화면에서는 지운다. 어차피 DB 에 없다는 뜻이다.
            if (result != StoreResult.Ok && result != StoreResult.NotFound)
            {
                ShowHint("삭제하지 못했습니다: " + _store.LastMessage, 5);
                return;
            }

            item.IsSaved = false;
            DiagLog.Write($"삭제함 | {item.Name} {item.DeviceId} | 결과 {result}");

            // 화면 배치도 DB 에서 같이 지워졌다. (ON DELETE CASCADE) 기억해 둔 것도 버린다.
            _layoutCache.Remove(item.DeviceId);

            // 마지막으로 연결한 기기였으면 자동 연결 대상에서도 뺀다.
            if (ReadSetting(SettingKeys.LastTarget) == item.DeviceId.ToString())
                WriteSetting(SettingKeys.LastTarget, null);

            if (_autoTarget == item.DeviceId)
                CancelAutoConnect("자동 연결 대상 기기를 삭제함");

            // 꺼져 있는 기기면 목록에서도 뺀다.
            // 켜져 있는 기기는 검색에 잡히고 있으니 "저장 안 된 기기" 로 남긴다.
            if (!item.IsOnline)
                _devices.Remove(item);

            ShowHint($"{item.Name} 을(를) 저장된 기기에서 삭제했습니다.", 4);
        }

        // 꺼져 있는 저장된 기기의 상태 글자
        private static string OfflineText(DateTime? lastSeenUtc) =>
            lastSeenUtc.HasValue
                ? $"오프라인 · 마지막 연결 {lastSeenUtc.Value.ToLocalTime():M/d HH:mm}"
                : "오프라인";
    }
}
