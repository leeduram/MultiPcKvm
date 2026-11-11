namespace MultiPcKvm.App.Storage
{
    // ─────────────────────────────────────────────────────────────
    //  신뢰 기기 한 대의 정보. DB 의 device 테이블 한 줄과 1:1 로 대응한다.
    //
    //  "신뢰 기기" = 한 번 연결 코드를 통과한 PC.
    //  다음에 프로그램을 켰을 때 목록에 미리 띄워두려고 저장한다.
    //
    //  컬럼 ↔ 속성 대응 (3주차 기술 명세서 3.2 테이블 정의서)
    //    device_id     TEXT     ↔ DeviceId    (Guid)
    //    device_name   TEXT     ↔ DeviceName  (string)
    //    last_ip       TEXT     ↔ LastIp      (string?, 없을 수 있음)
    //    is_trusted    INTEGER  ↔ IsTrusted   (bool, DB 에는 0/1)
    //    paired_at     TEXT     ↔ PairedAt    (DateTime, UTC)
    //    last_seen_at  TEXT     ↔ LastSeenAt  (DateTime?, UTC)
    //
    //  이름 규칙이 다른 이유:
    //    DB 는 SQL 관례대로 snake_case, C# 은 C# 관례대로 PascalCase 를 쓴다.
    //    둘을 이어주는 건 DeviceRepository 한 곳뿐이라 헷갈릴 일이 없다.
    //
    //  시각을 UTC 로 들고 있는 이유:
    //    두 PC 의 시간대 설정이 달라도, 여름시간이 바뀌어도 순서가 꼬이지 않는다.
    //    화면에 보여줄 때만 ToLocalTime() 으로 바꾼다.
    // ─────────────────────────────────────────────────────────────
    public class TrustedDevice
    {
        public Guid DeviceId { get; set; }

        public string DeviceName { get; set; } = "";

        public string? LastIp { get; set; }

        public bool IsTrusted { get; set; }

        public DateTime PairedAt { get; set; }

        public DateTime? LastSeenAt { get; set; }
    }


    // ─────────────────────────────────────────────────────────────
    //  저장소 작업의 결과.
    //
    //  숫자를 HTTP 상태 코드와 똑같이 맞춰뒀다.
    //  웹 API 를 써본 사람이면 숫자만 보고 바로 뜻을 알 수 있고,
    //  나중에 서버를 붙이게 되면 그대로 응답 코드로 쓸 수 있다.
    // ─────────────────────────────────────────────────────────────
    public enum StoreResult
    {
        Ok = 200,        // 조회·수정·삭제 성공
        Created = 201,   // 새로 만들어짐
        Invalid = 400,   // 값이 잘못됨 (빈 이름, 잘못된 IP 등)
        Forbidden = 403, // 권한 없음 (다른 기기의 연결 기록을 끝내려 함 등). 6주차 추가
        NotFound = 404,  // 그런 기기가 없음
        Conflict = 409,  // 이미 있는 기기를 또 만들려고 함 / 이미 끝난 연결을 또 끝내려 함
        Error = 500,     // DB 자체 문제 (파일 잠김·손상·디스크 꽉 참 등). 6주차 추가
    }
}
