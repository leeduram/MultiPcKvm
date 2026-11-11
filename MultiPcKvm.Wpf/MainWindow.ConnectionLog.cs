using MultiPcKvm.App.Storage;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  MainWindow — 연결 기록 (connection_log) (6주차)
    //
    //  연결될 때 기록을 "연결 중" 으로 만들고, 끊길 때 "종료" 로 바꾼다.
    //  SQL 은 DeviceRepository 에만 있다. 여기서는 언제 부를지만 정한다.
    //
    //    조종하는 쪽   : ConnectTo 성공          → BeginOutgoingLog
    //                   Disconnect              → EndOutgoingLog (이유: 이 PC 에서 끊음 / 상대가 끊음 / ...)
    //    조종당하는 쪽 : 상대 코드·열쇠 승인     → BeginIncomingLog
    //                   _incomingPaired 가 내려감 → Tick 의 CheckIncomingLog 가 종료
    //    프로그램 끝   : Cleanup                 → EndConnectionLogs("프로그램 종료")
    //    프로그램 켬   : OpenStore               → 비정상 종료로 남은 "연결 중" 정리
    //
    //  기록이 실패해도(DB 오류 등) 연결 자체는 그대로 둔다. 진단 기록에만 남긴다.
    //  모든 함수는 화면 담당 스레드에서만 부른다. (DeviceRepository 의 전제)
    // ─────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        // 내가 조종 중인 연결의 기록 번호와 그 기기
        private long? _outLogId;
        private Guid _outLogDevice;

        // 상대가 나를 조종 중인 연결의 기록 번호와 그 기기
        private long? _inLogId;
        private Guid _inLogDevice;

        // 조종당하는 쪽이 끊긴 이유. 네트워크 스레드(Unpair 받음)가 넣고 Tick 이 읽는다.
        // 비어 있으면 "연결 끊김" (줄이 끊김 등) 으로 적는다.
        private volatile string? _inEndReason;

        // 조종당하는 쪽에서 받아 실행한 입력 수. (네트워크 스레드에서 센다)
        private long _inputsReceived;


        // ── 조종하는 쪽 ───────────────────────────────────────

        private void BeginOutgoingLog(DeviceItem item)
        {
            // 혹시 앞 기록이 남아 있으면 먼저 닫는다. (한 번에 하나만 연결 중)
            EndOutgoingLog("다시 연결함");

            _outLogId = StartLog(item);
            _outLogDevice = item.DeviceId;
        }

        // 조종을 멈추기 직전에 부른다. (평균 지연·보낸 수를 세션이 살아 있을 때 읽어야 한다)
        private void EndOutgoingLog(string reason)
        {
            if (_outLogId is not long logId)
                return;

            _outLogId = null;

            double? latency = null;
            long sent = 0;

            if (_session != null)
            {
                // AvgRttMs 는 아직 못 쟀으면 -1 이다. 그때는 "모름(null)" 으로 저장한다.
                double rtt = _session.Monitor.AvgRttMs;
                latency = rtt < 0 ? null : rtt;
                sent = _session.MessagesSent;
            }

            EndLog(logId, _outLogDevice, reason, latency, sent);
        }


        // ── 조종당하는 쪽 ─────────────────────────────────────

        private void BeginIncomingLog(DeviceItem item)
        {
            EndIncomingLog("다시 연결함");

            _inEndReason = null;
            Interlocked.Exchange(ref _inputsReceived, 0);

            _inLogId = StartLog(item);
            _inLogDevice = item.DeviceId;
        }

        private void EndIncomingLog(string reason)
        {
            if (_inLogId is not long logId)
                return;

            _inLogId = null;

            // 조종당하는 쪽은 지연을 재지 않는다. (재는 건 조종하는 쪽의 ConnectionMonitor)
            EndLog(logId, _inLogDevice, reason, null, Interlocked.Read(ref _inputsReceived));
        }

        // Tick 에서 부른다. 조종당하던 연결이 끝났으면 기록을 닫는다.
        //
        // _incomingPaired 는 여러 곳(상대가 끊음, 줄이 끊김, 내가 끊음)에서 내려가고
        // 그중 일부는 네트워크 스레드다. 그 자리마다 DB 를 부르지 않고 여기 한 곳에서 처리한다.
        private void CheckIncomingLog()
        {
            if (_inLogId != null && !_incomingPaired)
                EndIncomingLog(_inEndReason ?? "연결 끊김");
        }

        // 프로그램을 끝낼 때 열려 있는 기록을 모두 닫는다.
        private void EndConnectionLogs(string reason)
        {
            EndOutgoingLog(reason);
            EndIncomingLog(reason);
        }


        // ── DeviceRepository 부르기 ───────────────────────────

        private long? StartLog(DeviceItem item)
        {
            if (_store == null)
                return null;

            long? logId = _store.StartConnection(item.DeviceId);

            DiagLog.Write(logId.HasValue
                ? $"연결 기록 시작 | {item.Name} | 기록 {logId}"
                : $"연결 기록 못 남김 | {item.Name} | {(int)_store.LastResult} {_store.LastMessage}");

            return logId;
        }

        private void EndLog(long logId, Guid deviceId, string reason, double? latency, long count)
        {
            if (_store == null)
                return;

            StoreResult result = _store.EndConnection(logId, deviceId, reason, latency, count);

            DiagLog.Write($"연결 기록 종료 | 기록 {logId} | {(int)result} {_store.LastMessage} | " +
                          $"지연 {(latency.HasValue ? $"{latency.Value:F1}ms" : "모름")} · 메시지 {count}");

            // 목록의 풍선 도움말(연결 횟수·평균 지연)을 새 값으로 바꾼다.
            if (result == StoreResult.Ok)
                RefreshDetailText(deviceId);
        }

        // 기기 한 대의 통합 조회 결과로 풍선 도움말을 고친다.
        private void RefreshDetailText(Guid deviceId)
        {
            if (_store == null)
                return;

            DeviceItem? item = _devices.FirstOrDefault(d => d.DeviceId == deviceId);

            if (item == null)
                return;

            DeviceDetail? detail = _store.GetDeviceDetail(deviceId, recent: 1);

            if (detail != null)
                item.DetailText = DescribeDetail(detail);
        }

        // 통합 조회 결과 → 목록에 마우스를 올렸을 때 보이는 글자
        private static string DescribeDetail(DeviceDetail detail)
        {
            if (detail.ConnectionCount == 0)
                return "연결 기록 없음";

            string text = $"연결 {detail.ConnectionCount}회";

            if (detail.LastConnectedAt is DateTime last)
                text += $" · 마지막 연결 {last.ToLocalTime():M/d HH:mm}";

            if (detail.AvgLatencyMs is double latency)
                text += $" · 평균 지연 {latency:F1}ms";

            return text;
        }
    }
}
