namespace MultiPcKvm.App.Storage
{
    // ─────────────────────────────────────────────────────────────
    //  연결 기록 한 줄. DB 의 connection_log 테이블 한 줄과 대응한다. (6주차)
    //
    //  컬럼 ↔ 속성
    //    log_id            INTEGER ↔ LogId
    //    device_id         TEXT    ↔ DeviceId
    //    connected_at      TEXT    ↔ ConnectedAt      (UTC)
    //    disconnected_at   TEXT    ↔ DisconnectedAt   (UTC, 아직 연결 중이면 null)
    //    avg_latency_ms    REAL    ↔ AvgLatencyMs     (모르면 null)
    //    message_count     INTEGER ↔ MessageCount     (보낸·받은 입력 수)
    //    disconnect_reason TEXT    ↔ DisconnectReason (아직 연결 중이면 null)
    //
    //  상태는 두 가지뿐이다.
    //    연결 중 : disconnected_at 이 비어 있음
    //    종료    : disconnected_at 이 채워짐   (한 번 종료되면 다시 바꿀 수 없다 → 409)
    // ─────────────────────────────────────────────────────────────
    public class ConnectionRecord
    {
        public long LogId { get; set; }

        public Guid DeviceId { get; set; }

        public DateTime ConnectedAt { get; set; }

        public DateTime? DisconnectedAt { get; set; }

        public double? AvgLatencyMs { get; set; }

        public long MessageCount { get; set; }

        public string? DisconnectReason { get; set; }

        // 아직 끝나지 않은 연결인가
        public bool IsOpen => DisconnectedAt == null;
    }


    // ─────────────────────────────────────────────────────────────
    //  기기 한 대를 "한 번에" 모아 본 결과. (6주차 통합 조회)
    //
    //    device 테이블         → Device
    //    screen_layout 테이블  → Layout                     (없으면 null)
    //    connection_log 테이블 → ConnectionCount, LastConnectedAt,
    //                            AvgLatencyMs, TotalMessages (집계)
    //                          → RecentConnections          (최근 기록 몇 줄, 한 대 조회 때만)
    //
    //  왜 따로따로 조회하지 않고 모으는가:
    //    기기가 10대면 "기기 목록 1번 + 기기마다 배치 1번 + 기기마다 기록 1번" = 21번 묻게 된다. (N+1 문제)
    //    JOIN 으로 묶으면 기기가 몇 대든 1번에 끝난다. (DeviceRepository.GetAllDetails)
    // ─────────────────────────────────────────────────────────────
    public class DeviceDetail
    {
        public TrustedDevice Device { get; set; } = new();

        public SavedLayout? Layout { get; set; }

        // 지금까지 연결한 횟수 (연결 중인 것 포함)
        public int ConnectionCount { get; set; }

        public DateTime? LastConnectedAt { get; set; }

        // 기록마다 잰 평균 지연의 평균. 잰 기록이 하나도 없으면 null
        public double? AvgLatencyMs { get; set; }

        public long TotalMessages { get; set; }

        // 최근 연결 기록 (최근 것부터). 목록 조회(GetAllDetails)에서는 비워 둔다.
        public List<ConnectionRecord> RecentConnections { get; set; } = new();
    }
}
