using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;

namespace MultiPcKvm.App.Storage
{
    // ─────────────────────────────────────────────────────────────
    //  신뢰 기기(device 테이블) 의 CRUD.
    //
    //    Create  Create(device)      새 기기 저장              → 201 / 409 / 400
    //    Read    GetAll()            저장된 기기 전부           → 목록
    //    Read    Find(id)            기기 한 대                → 있으면 기기, 없으면 null(404)
    //    Update  Update(device)      이름·IP·신뢰·확인시각 수정  → 200 / 404 / 400
    //    Delete  Delete(id)          기기 삭제                 → 200 / 404
    //
    //  그리고 위 넷을 조합한 편의 기능 하나:
    //    SavePaired(id, 이름, ip)    연결 코드를 통과한 기기를 저장한다.
    //                               처음이면 Create, 이미 있으면 Update.
    //
    //  화면 배치 (screen_layout 테이블)
    //    GetLayout(id)               저장된 배치. 없으면 null
    //    SaveLayout(layout)          배치 저장. 처음이면 201, 고쳤으면 200
    //
    //  저장된 기기 열쇠 (device 테이블의 token_out, token_in)
    //    GetTokenOut / SetTokenOut   이 기기에 연결할 때 낼 열쇠
    //    GetTokenIn  / SetTokenIn    이 기기가 나에게 연결할 때 낼 열쇠
    //
    //  프로그램 설정 (app_setting 테이블, 이름 → 값)
    //    GetSetting(이름)            없으면 null
    //    SetSetting(이름, 값)        없으면 넣고 있으면 고친다. null 이면 지운다
    //
    //  연결 기록 (connection_log 테이블) — 6주차. 지우지 않고 "상태" 를 바꾼다
    //    StartConnection(id)         연결 시작 기록                → 기록 번호 (201) / 400 / 404
    //    EndConnection(번호, id, ..) 연결 중 → 종료 로 바꾼다       → 200 / 400 / 403 / 404 / 409
    //    CloseDanglingConnections()  비정상 종료로 남은 "연결 중" 을 정리 → 정리한 개수
    //
    //  통합 조회 (device + screen_layout + connection_log JOIN) — 6주차
    //    GetDeviceDetail(id)         기기 한 대 + 배치 + 연결 통계 + 최근 기록 → 200 / 404
    //    GetAllDetails()             모든 기기 + 배치 + 연결 통계를 쿼리 1번에 (N+1 없음)
    //
    //  연결 기록 목록 (필터·검색·정렬·페이지) — 7주차, DeviceRepository.History.cs
    //    GetConnectionHistory(조건)  조건에 맞는 기록 한 페이지 + 전체 개수 → 200 / 400 / 500
    //
    //  DB 자체가 고장 났을 때(파일 잠김·손상 등) 쓰기 작업은 예외 대신 500(Error) 을 돌려준다.
    //
    //  SQL 을 이 파일 밖으로 내보내지 않는다.
    //  화면(MainWindow)은 TrustedDevice 와 StoreResult 만 알고, SQL 은 모른다.
    //  나중에 저장 방식을 바꿔도 이 파일만 고치면 된다.
    //
    //  ※ 스레드: 화면 담당 스레드에서만 부른다는 전제다.
    //            LastMessage 가 공유 상태라 여러 스레드에서 동시에 쓰면 섞인다.
    // ─────────────────────────────────────────────────────────────
    public partial class DeviceRepository
    {
        private readonly KvmDatabase _db;

        // 마지막 작업이 왜 그렇게 끝났는지 한 줄 설명.
        // 화면 안내 문구와 시연 출력에 쓴다.
        public string LastMessage { get; private set; } = "";

        // 마지막 작업의 결과 코드. (값을 돌려주는 조회 함수도 결과 코드를 알 수 있게)
        // 예) GetDeviceDetail 이 null 이면 이걸 보고 404 인지 500 인지 구분한다.
        public StoreResult LastResult { get; private set; } = StoreResult.Ok;

        public DeviceRepository(KvmDatabase db)
        {
            _db = db;
        }


        // ═════════════════════════════════════════════════════
        //  C — Create
        // ═════════════════════════════════════════════════════

        public StoreResult Create(TrustedDevice device) =>
            Guarded("기기 저장", () => CreateCore(device));

        private StoreResult CreateCore(TrustedDevice device)
        {
            // 1) 값 검사 (400)
            //    DB 에 보내기 전에 걸러야 사용자에게 알아듣기 쉬운 이유를 줄 수 있다.
            //    DB 가 내는 오류는 "CHECK constraint failed" 같은 말이라 그대로 보여주기 어렵다.
            string? problem = Validate(device);

            if (problem != null)
                return Fail(StoreResult.Invalid, problem);

            using SqliteConnection connection = _db.Open();

            // 2) 이미 있는지 확인 (409)
            //
            // 같은 ID 로 또 만들려고 하면 PRIMARY KEY 제약 때문에 DB 가 어차피 거부한다.
            // 그래도 먼저 확인하는 이유: "이미 있다" 는 오류가 아니라 흔한 상황이라서
            // 예외로 처리하지 않고 결과값으로 돌려주고 싶다.
            if (Exists(connection, device.DeviceId))
            {
                return Fail(StoreResult.Conflict,
                    $"이미 저장된 기기입니다: {device.DeviceName}");
            }

            // 3) 넣기 (201)
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText = @"
INSERT INTO device (device_id, device_name, last_ip, is_trusted, paired_at, last_seen_at)
VALUES ($id, $name, $ip, $trusted, $pairedAt, $lastSeenAt);";

            // 값은 절대 SQL 문자열에 이어 붙이지 않고 $이름 자리로 넘긴다.
            //
            // PC 이름에 ' 가 들어 있으면(예: Tom's PC) 문자열을 이어 붙인 SQL 은 깨진다.
            // 악의적인 이름이면 다른 SQL 이 실행될 수도 있다. (SQL 인젝션)
            BindAll(command, device);

            try
            {
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqlitePrimaryKey ||
                                             ex.SqliteExtendedErrorCode == SqliteUnique)
            {
                // 2) 의 확인과 지금 INSERT 사이에 다른 쪽이 같은 기기를 먼저 넣었다. (경쟁)
                //
                // 예) 두 PC 가 거의 동시에 연결해서 "저장" 이 두 번 겹친 경우.
                // 이것도 "이미 있다" 와 같은 뜻이므로 400 이 아니라 409 로 돌려준다. (6주차 보완)
                return Fail(StoreResult.Conflict,
                    $"이미 저장된 기기입니다(동시에 저장됨): {device.DeviceName}");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
            {
                // Validate 가 놓친 제약 위반. (예: 앞으로 CHECK 를 추가했는데 Validate 를 안 고친 경우)
                // 마지막 방어선은 DB 다.
                return Fail(StoreResult.Invalid, "DB 제약 조건 위반: " + ex.Message);
            }

            return Done(StoreResult.Created, $"저장했습니다: {device.DeviceName}");
        }


        // ═════════════════════════════════════════════════════
        //  R — Read (목록)
        // ═════════════════════════════════════════════════════

        // 저장된 기기 전부. 최근에 연결한 순서.
        public List<TrustedDevice> GetAll()
        {
            using SqliteConnection connection = _db.Open();
            using SqliteCommand command = connection.CreateCommand();

            // 정렬: 한 번도 연결 안 된 기기(last_seen_at 이 NULL)는 맨 뒤,
            //       나머지는 최근 것부터, 같으면 이름순.
            //
            // (last_seen_at IS NULL) 은 NULL 이면 1, 아니면 0 이 되므로
            // 오름차순으로 정렬하면 NULL 인 줄이 뒤로 간다.
            // 날짜를 ISO 8601 문자열로 저장했기 때문에 문자열 정렬이 곧 시간 정렬이다.
            command.CommandText = SelectColumns + @"
ORDER BY (last_seen_at IS NULL), last_seen_at DESC, device_name;";

            var result = new List<TrustedDevice>();

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
                result.Add(ReadRow(reader));

            Done(StoreResult.Ok, $"{result.Count}대 조회");
            return result;
        }


        // ═════════════════════════════════════════════════════
        //  R — Read (한 대)
        // ═════════════════════════════════════════════════════

        // 없으면 null. (404 에 해당)
        public TrustedDevice? Find(Guid deviceId)
        {
            using SqliteConnection connection = _db.Open();
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText = SelectColumns + " WHERE device_id = $id;";
            command.Parameters.AddWithValue("$id", ToDb(deviceId));

            using SqliteDataReader reader = command.ExecuteReader();

            if (!reader.Read())
            {
                Fail(StoreResult.NotFound, "저장되지 않은 기기입니다.");
                return null;
            }

            Done(StoreResult.Ok, "조회 성공");
            return ReadRow(reader);
        }


        // ═════════════════════════════════════════════════════
        //  U — Update
        // ═════════════════════════════════════════════════════

        // 이름·IP·신뢰 여부·마지막 확인 시각을 고친다.
        //
        // paired_at(처음 연결한 날)은 고치지 않는다.
        // 한 번 정해지면 바뀌면 안 되는 기록이라, 실수로 덮어쓰지 못하게
        // UPDATE 문에서 아예 뺐다.
        public StoreResult Update(TrustedDevice device) =>
            Guarded("기기 수정", () => UpdateCore(device));

        private StoreResult UpdateCore(TrustedDevice device)
        {
            string? problem = Validate(device);

            if (problem != null)
                return Fail(StoreResult.Invalid, problem);

            using SqliteConnection connection = _db.Open();
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText = @"
UPDATE device
   SET device_name  = $name,
       last_ip      = $ip,
       is_trusted   = $trusted,
       last_seen_at = $lastSeenAt
 WHERE device_id    = $id;";

            BindAll(command, device);

            int changed;

            try
            {
                // ExecuteNonQuery 는 "몇 줄이 바뀌었는지" 를 돌려준다.
                changed = command.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
            {
                return Fail(StoreResult.Invalid, "DB 제약 조건 위반: " + ex.Message);
            }

            // 0줄이면 그런 기기가 없었다는 뜻이다.
            // SQL 의 UPDATE 는 대상이 없어도 오류를 내지 않으므로 여기서 직접 확인해야 한다.
            if (changed == 0)
                return Fail(StoreResult.NotFound, "수정할 기기가 없습니다.");

            return Done(StoreResult.Ok, $"수정했습니다: {device.DeviceName}");
        }


        // ═════════════════════════════════════════════════════
        //  D — Delete
        // ═════════════════════════════════════════════════════

        // 기기를 지운다.
        //
        // 이 기기의 화면 배치(screen_layout)와 연결 이력(connection_log)도
        // DB 가 ON DELETE CASCADE 로 함께 지운다. 여기서 따로 지울 필요가 없다.
        // (KvmDatabase.Open 에서 외래키 검사를 켜뒀기 때문에 동작한다)
        public StoreResult Delete(Guid deviceId) =>
            Guarded("기기 삭제", () => DeleteCore(deviceId));

        private StoreResult DeleteCore(Guid deviceId)
        {
            using SqliteConnection connection = _db.Open();
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "DELETE FROM device WHERE device_id = $id;";
            command.Parameters.AddWithValue("$id", ToDb(deviceId));

            int changed = command.ExecuteNonQuery();

            if (changed == 0)
                return Fail(StoreResult.NotFound, "삭제할 기기가 없습니다.");

            return Done(StoreResult.Ok, "삭제했습니다.");
        }


        // ═════════════════════════════════════════════════════
        //  편의 기능 — 연결 코드를 통과한 기기 저장
        // ═════════════════════════════════════════════════════

        // 처음 보는 기기면 새로 만들고(201), 이미 있으면 정보를 갱신한다(200).
        //
        // 위의 CRUD 를 조합해서 만든다. SQL 을 새로 쓰지 않는다.
        // 그래야 값 검사·오류 처리가 한 곳(Create/Update)에만 있게 된다.
        public StoreResult SavePaired(Guid deviceId, string deviceName, string? ip) =>
            Guarded("기기 저장", () => SavePairedCore(deviceId, deviceName, ip));

        private StoreResult SavePairedCore(Guid deviceId, string deviceName, string? ip)
        {
            DateTime now = DateTime.UtcNow;

            TrustedDevice? existing = Find(deviceId);

            if (existing == null)
            {
                return Create(new TrustedDevice
                {
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    LastIp = ip,
                    IsTrusted = true,
                    PairedAt = now,
                    LastSeenAt = now,
                });
            }

            existing.DeviceName = deviceName;
            existing.LastIp = ip;
            existing.IsTrusted = true;
            existing.LastSeenAt = now;

            return Update(existing);
        }


        // ═════════════════════════════════════════════════════
        //  프로그램 설정 (app_setting)
        // ═════════════════════════════════════════════════════
        //
        // 이름과 값을 글자로 저장하는 단순한 표다.
        // 예) "close_to_tray" → "1",  "last_target" → "aa848e36-...."
        // 어떤 이름을 쓰는지는 화면(MainWindow) 쪽 AppSettingKeys 에 모아 두었다.

        public string? GetSetting(string key)
        {
            using SqliteConnection connection = _db.Open();
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "SELECT setting_value FROM app_setting WHERE setting_key = $key;";
            command.Parameters.AddWithValue("$key", key);

            return command.ExecuteScalar() as string;
        }

        // value 가 null 이면 그 설정을 지운다. (기본값으로 돌아간다)
        public StoreResult SetSetting(string key, string? value) =>
            Guarded("설정 저장", () => SetSettingCore(key, value));

        private StoreResult SetSettingCore(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(key))
                return Fail(StoreResult.Invalid, "설정 이름이 비어 있습니다.");

            using SqliteConnection connection = _db.Open();
            using SqliteCommand command = connection.CreateCommand();

            if (value == null)
            {
                command.CommandText = "DELETE FROM app_setting WHERE setting_key = $key;";
                command.Parameters.AddWithValue("$key", key);
                command.ExecuteNonQuery();
                return Done(StoreResult.Ok, $"설정을 지웠습니다: {key}");
            }

            // 있으면 고치고 없으면 넣는다. (setting_key 가 PRIMARY KEY 라 ON CONFLICT 로 한 번에 된다)
            command.CommandText = @"
INSERT INTO app_setting (setting_key, setting_value, updated_at)
VALUES ($key, $value, $now)
ON CONFLICT (setting_key) DO UPDATE
   SET setting_value = excluded.setting_value,
       updated_at    = excluded.updated_at;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
            command.ExecuteNonQuery();

            return Done(StoreResult.Ok, $"설정을 저장했습니다: {key}");
        }


        // ═════════════════════════════════════════════════════
        //  저장된 기기 열쇠 (코드 없이 연결)
        // ═════════════════════════════════════════════════════
        //
        // 열쇠 = 64비트 무작위 수. 코드로 한 번 연결을 승인한 쪽이 만들어서 상대에게 준다.
        //
        //   token_out : 내가 그 기기에 연결할 때 낸다. (그 기기가 만들어 준 것)
        //   token_in  : 그 기기가 나에게 연결할 때 내야 하는 값. (내가 만들어 준 것)
        //
        // 기기를 지우면(Delete) 줄째로 지워지므로 열쇠도 같이 사라진다.
        // 그러면 다음에는 다시 코드를 입력해야 한다.

        public long? GetTokenOut(Guid deviceId) => GetToken(deviceId, "token_out");

        public long? GetTokenIn(Guid deviceId) => GetToken(deviceId, "token_in");

        // null 을 넣으면 열쇠를 지운다. (다음부터 코드 입력)
        public StoreResult SetTokenOut(Guid deviceId, long? token) => SetToken(deviceId, "token_out", token);

        public StoreResult SetTokenIn(Guid deviceId, long? token) => SetToken(deviceId, "token_in", token);

        // column 은 위 네 함수가 넘기는 고정 글자뿐이다. (사용자 입력이 SQL 에 들어가지 않는다)
        private long? GetToken(Guid deviceId, string column)
        {
            using SqliteConnection connection = _db.Open();
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText = $"SELECT {column} FROM device WHERE device_id = $id;";
            command.Parameters.AddWithValue("$id", ToDb(deviceId));

            object? value = command.ExecuteScalar();

            // 줄이 없으면 null, 줄은 있는데 칸이 비었으면 DBNull
            if (value == null || value is DBNull)
                return null;

            return Convert.ToInt64(value);
        }

        private StoreResult SetToken(Guid deviceId, string column, long? token) =>
            Guarded("열쇠 저장", () => SetTokenCore(deviceId, column, token));

        private StoreResult SetTokenCore(Guid deviceId, string column, long? token)
        {
            using SqliteConnection connection = _db.Open();
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText = $"UPDATE device SET {column} = $token WHERE device_id = $id;";
            command.Parameters.AddWithValue("$id", ToDb(deviceId));
            command.Parameters.AddWithValue("$token", token.HasValue ? (object)token.Value : DBNull.Value);

            if (command.ExecuteNonQuery() == 0)
                return Fail(StoreResult.NotFound, "저장되지 않은 기기라 열쇠를 저장할 수 없습니다.");

            return Done(StoreResult.Ok, token.HasValue ? "열쇠를 저장했습니다." : "열쇠를 지웠습니다.");
        }


        // ═════════════════════════════════════════════════════
        //  화면 배치 (screen_layout)
        // ═════════════════════════════════════════════════════

        // 저장된 배치. 없으면 null. (처음 연결하는 기기 등)
        public SavedLayout? GetLayout(Guid deviceId)
        {
            using SqliteConnection connection = _db.Open();
            using SqliteCommand command = connection.CreateCommand();

            // 한 기기에 한 줄만 두지만(SaveLayout 이 고쳐 쓰므로),
            // 혹시 여러 줄이 있으면 가장 최근 것을 쓴다.
            command.CommandText = @"
SELECT position, edge_offset, screen_width, screen_height
  FROM screen_layout
 WHERE device_id = $id
 ORDER BY updated_at DESC
 LIMIT 1;";
            command.Parameters.AddWithValue("$id", ToDb(deviceId));

            using SqliteDataReader reader = command.ExecuteReader();

            if (!reader.Read())
            {
                Fail(StoreResult.NotFound, "저장된 배치가 없습니다.");
                return null;
            }

            Done(StoreResult.Ok, "배치 조회 성공");

            return new SavedLayout
            {
                DeviceId = deviceId,
                Placement = new ScreenPlacement(
                    ScreenLayout.FromDb(reader.GetString(0)),
                    (int)reader.GetInt64(1)),
                ScreenWidth = (int)reader.GetInt64(2),
                ScreenHeight = (int)reader.GetInt64(3),
            };
        }

        // 배치를 저장한다. 이미 있으면 고치고(200), 없으면 새로 넣는다(201).
        //
        // 저장된 기기(device 테이블)여야 한다. 아니면 404.
        // (screen_layout 은 device 를 외래키로 가리키므로 DB 도 거부한다)
        public StoreResult SaveLayout(SavedLayout layout) =>
            Guarded("배치 저장", () => SaveLayoutCore(layout));

        private StoreResult SaveLayoutCore(SavedLayout layout)
        {
            if (layout.DeviceId == Guid.Empty)
                return Fail(StoreResult.Invalid, "기기 ID 가 비어 있습니다.");

            if (layout.ScreenWidth <= 0 || layout.ScreenHeight <= 0)
                return Fail(StoreResult.Invalid,
                    $"화면 크기가 잘못됐습니다: {layout.ScreenWidth}×{layout.ScreenHeight}");

            using SqliteConnection connection = _db.Open();

            if (!Exists(connection, layout.DeviceId))
                return Fail(StoreResult.NotFound, "저장되지 않은 기기라 배치를 저장할 수 없습니다.");

            // 먼저 고쳐 보고, 고친 줄이 없으면 새로 넣는다.
            using (SqliteCommand update = connection.CreateCommand())
            {
                update.CommandText = @"
UPDATE screen_layout
   SET position      = $position,
       edge_offset   = $offset,
       screen_width  = $width,
       screen_height = $height,
       updated_at    = $now
 WHERE device_id     = $id;";
                BindLayout(update, layout);

                if (update.ExecuteNonQuery() > 0)
                    return Done(StoreResult.Ok, "배치를 고쳤습니다.");
            }

            using SqliteCommand insert = connection.CreateCommand();

            insert.CommandText = @"
INSERT INTO screen_layout (device_id, position, edge_offset, screen_width, screen_height, updated_at)
VALUES ($id, $position, $offset, $width, $height, $now);";
            BindLayout(insert, layout);

            try
            {
                insert.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
            {
                // 바로 위에서 기기가 있는 걸 확인했지만, 그 사이 지워졌을 수 있다.
                return Fail(StoreResult.NotFound, "DB 제약 조건 위반: " + ex.Message);
            }

            return Done(StoreResult.Created, "배치를 저장했습니다.");
        }

        private static void BindLayout(SqliteCommand command, SavedLayout layout)
        {
            command.Parameters.AddWithValue("$id", ToDb(layout.DeviceId));
            command.Parameters.AddWithValue("$position", ScreenLayout.ToDb(layout.Placement.Side));
            command.Parameters.AddWithValue("$offset", layout.Placement.Offset);
            command.Parameters.AddWithValue("$width", layout.ScreenWidth);
            command.Parameters.AddWithValue("$height", layout.ScreenHeight);
            command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
        }


        // ═════════════════════════════════════════════════════
        //  연결 기록 (connection_log) — 상태 변경 (6주차)
        // ═════════════════════════════════════════════════════
        //
        // 연결 한 번 = 기록 한 줄.
        //   연결되면       StartConnection  → "연결 중" 줄을 만든다 (disconnected_at 비움)
        //   끊기면         EndConnection    → 그 줄을 "종료" 로 바꾼다 (지우지 않는다)
        //
        // 지우지 않고 상태만 바꾸는 이유:
        //   "몇 번 연결했나, 평균 지연은 얼마였나, 왜 끊겼나" 를 나중에 볼 수 있어야 한다.
        //   기기를 지우면(Delete) ON DELETE CASCADE 로 그 기기의 기록도 함께 지워진다.

        // 연결 시작을 기록한다. 성공하면 기록 번호(log_id), 실패하면 null.
        //
        //   400 : 기기 ID 가 비어 있음
        //   404 : 저장되지 않은 기기 (기록은 저장된 기기에만 남긴다. 외래키도 막는다)
        //   201 : 만들었음
        //   500 : DB 오류
        public long? StartConnection(Guid deviceId)
        {
            long? logId = null;

            Guarded("연결 기록 시작", () =>
            {
                if (deviceId == Guid.Empty)
                    return Fail(StoreResult.Invalid, "기기 ID 가 비어 있습니다.");

                using SqliteConnection connection = _db.Open();

                if (!Exists(connection, deviceId))
                    return Fail(StoreResult.NotFound, "저장되지 않은 기기라 연결 기록을 남길 수 없습니다.");

                using SqliteCommand command = connection.CreateCommand();

                // RETURNING : 넣은 줄의 번호를 같은 문장에서 바로 돌려받는다.
                // (따로 last_insert_rowid() 를 묻지 않아도 된다)
                command.CommandText = @"
INSERT INTO connection_log (device_id, connected_at)
VALUES ($id, $now)
RETURNING log_id;";
                command.Parameters.AddWithValue("$id", ToDb(deviceId));
                command.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));

                logId = Convert.ToInt64(command.ExecuteScalar());

                return Done(StoreResult.Created, $"연결 기록 시작 (기록 {logId})");
            });

            return LastResult == StoreResult.Created ? logId : null;
        }

        // 연결을 "종료" 상태로 바꾼다. (지우지 않는다)
        //
        // deviceId : 이 기록의 주인이라고 주장하는 기기.
        //            기록의 실제 주인과 다르면 바꾸지 못하게 막는다. (403)
        //            다른 기기의 연결을 엉뚱하게 끝내 버리는 실수를 막는 권한 확인이다.
        //
        //   400 : 끊긴 이유가 비었거나, 평균 지연·메시지 수가 음수
        //   404 : 그런 기록이 없음
        //   403 : 다른 기기의 기록임
        //   409 : 이미 종료된 기록 (종료 → 종료 로 두 번 바꿀 수 없다)
        //   200 : 종료로 바꿨음
        //   500 : DB 오류
        public StoreResult EndConnection(
            long logId,
            Guid deviceId,
            string reason,
            double? avgLatencyMs,
            long messageCount) =>
            Guarded("연결 기록 종료", () =>
            {
                // 1) 값 검사 (400)
                if (string.IsNullOrWhiteSpace(reason))
                    return Fail(StoreResult.Invalid, "끊긴 이유가 비어 있습니다.");

                if (avgLatencyMs is < 0 || messageCount < 0)
                    return Fail(StoreResult.Invalid, "평균 지연이나 메시지 수가 음수입니다.");

                using SqliteConnection connection = _db.Open();

                // 2) 기록을 찾아 주인과 상태를 확인한다 (404 → 403 → 409)
                Guid owner;
                bool alreadyEnded;

                using (SqliteCommand find = connection.CreateCommand())
                {
                    find.CommandText = @"
SELECT device_id, disconnected_at IS NOT NULL
  FROM connection_log
 WHERE log_id = $logId;";
                    find.Parameters.AddWithValue("$logId", logId);

                    using SqliteDataReader reader = find.ExecuteReader();

                    if (!reader.Read())
                        return Fail(StoreResult.NotFound, $"연결 기록 {logId} 이(가) 없습니다.");

                    owner = Guid.Parse(reader.GetString(0));
                    alreadyEnded = reader.GetInt64(1) == 1;
                }

                if (owner != deviceId)
                    return Fail(StoreResult.Forbidden, $"연결 기록 {logId} 은(는) 다른 기기의 기록이라 바꿀 수 없습니다.");

                if (alreadyEnded)
                    return Fail(StoreResult.Conflict, $"연결 기록 {logId} 은(는) 이미 종료됐습니다.");

                // 3) 상태 변경 (200)
                //
                // WHERE 에 "disconnected_at IS NULL" 을 한 번 더 건다.
                // 2) 와 3) 사이에 다른 쪽이 먼저 종료했으면 바뀐 줄이 0 이 되고, 그때도 409 로 돌려준다.
                // (확인과 변경 사이의 틈을 DB 가 막아 준다)
                using SqliteCommand update = connection.CreateCommand();

                update.CommandText = @"
UPDATE connection_log
   SET disconnected_at   = $now,
       disconnect_reason = $reason,
       avg_latency_ms    = $latency,
       message_count     = $count
 WHERE log_id = $logId
   AND device_id = $id
   AND disconnected_at IS NULL;";
                update.Parameters.AddWithValue("$now", ToDb(DateTime.UtcNow));
                update.Parameters.AddWithValue("$reason", reason.Trim());
                update.Parameters.AddWithValue("$latency",
                    avgLatencyMs.HasValue ? (object)Math.Round(avgLatencyMs.Value, 2) : DBNull.Value);
                update.Parameters.AddWithValue("$count", messageCount);
                update.Parameters.AddWithValue("$logId", logId);
                update.Parameters.AddWithValue("$id", ToDb(deviceId));

                if (update.ExecuteNonQuery() == 0)
                    return Fail(StoreResult.Conflict, $"연결 기록 {logId} 은(는) 방금 다른 곳에서 종료됐습니다.");

                return Done(StoreResult.Ok, $"연결 기록 {logId} 종료: {reason.Trim()}");
            });

        // 프로그램이 갑자기 꺼져서(강제 종료·정전) "연결 중" 으로 남은 기록을 정리한다.
        // 켤 때 한 번 부른다. 정리한 개수를 돌려준다. (DB 오류면 0)
        //
        // 끊긴 시각은 정확히 모르므로 시작 시각과 같게 둔다. (연결 시간이 0 으로 보인다)
        // 지금 시각으로 넣으면 꺼져 있던 시간까지 연결된 것처럼 부풀려진다.
        public int CloseDanglingConnections(string reason)
        {
            int closed = 0;

            Guarded("남은 연결 기록 정리", () =>
            {
                using SqliteConnection connection = _db.Open();
                using SqliteCommand command = connection.CreateCommand();

                command.CommandText = @"
UPDATE connection_log
   SET disconnected_at   = connected_at,
       disconnect_reason = $reason
 WHERE disconnected_at IS NULL;";
                command.Parameters.AddWithValue("$reason", reason);

                closed = command.ExecuteNonQuery();
                return Done(StoreResult.Ok, $"남은 연결 기록 {closed}건 정리");
            });

            return closed;
        }


        // ═════════════════════════════════════════════════════
        //  통합 조회 (device + screen_layout + connection_log) — 6주차
        // ═════════════════════════════════════════════════════
        //
        // 기기 정보, 화면 배치, 연결 통계를 따로 묻지 않고 JOIN 한 번으로 모은다.
        //
        //   device  d
        //     LEFT JOIN screen_layout l   ← 기기마다 가장 최근 배치 한 줄 (없으면 빈칸)
        //     LEFT JOIN (connection_log 를 기기별로 미리 집계한 표) c
        //
        // LEFT JOIN 인 이유: 배치나 연결 기록이 아직 없는 기기도 목록에서 빠지면 안 된다.
        //                   (그냥 JOIN 이면 짝이 없는 기기는 결과에서 사라진다)
        //
        // connection_log 를 먼저 GROUP BY 로 묶어 두고 붙이는 이유:
        //   기록 줄을 그대로 붙이면 기기 한 대가 기록 수만큼 여러 줄로 불어난다.
        //   기기당 한 줄로 줄여 놓은 뒤 붙여야 결과도 기기당 한 줄이다.
        private const string DetailSelect = @"
SELECT d.device_id, d.device_name, d.last_ip, d.is_trusted, d.paired_at, d.last_seen_at,
       l.position, l.edge_offset, l.screen_width, l.screen_height,
       IFNULL(c.connection_count, 0), c.last_connected_at, c.avg_latency_ms, IFNULL(c.total_messages, 0)
  FROM device d
  LEFT JOIN screen_layout l
         ON l.layout_id = (SELECT l2.layout_id
                             FROM screen_layout l2
                            WHERE l2.device_id = d.device_id
                            ORDER BY l2.updated_at DESC, l2.layout_id DESC
                            LIMIT 1)
  LEFT JOIN (SELECT device_id,
                    COUNT(*)            AS connection_count,
                    MAX(connected_at)   AS last_connected_at,
                    AVG(avg_latency_ms) AS avg_latency_ms,
                    SUM(message_count)  AS total_messages
               FROM connection_log
              GROUP BY device_id) c
         ON c.device_id = d.device_id";

        // 기기 한 대 + 배치 + 연결 통계 + 최근 기록 recent 줄.
        //
        //   400 : recent 가 1~50 밖
        //   404 : 저장되지 않은 기기 (null)
        //   200 : 성공
        //   500 : DB 오류 (null)
        // null 일 때 어느 쪽인지는 LastResult 로 구분한다.
        //
        // 쿼리는 기록 수와 상관없이 항상 2번이다. (통합 1번 + 최근 기록 1번)
        public DeviceDetail? GetDeviceDetail(Guid deviceId, int recent = 5)
        {
            DeviceDetail? detail = null;

            Guarded("기기 상세 조회", () =>
            {
                if (recent < 1 || recent > 50)
                    return Fail(StoreResult.Invalid, "최근 기록 개수는 1~50 이어야 합니다.");

                using SqliteConnection connection = _db.Open();

                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = DetailSelect + " WHERE d.device_id = $id;";
                    command.Parameters.AddWithValue("$id", ToDb(deviceId));

                    using SqliteDataReader reader = command.ExecuteReader();

                    if (!reader.Read())
                        return Fail(StoreResult.NotFound, "저장되지 않은 기기입니다.");

                    detail = ReadDetail(reader);
                }

                using (SqliteCommand command = connection.CreateCommand())
                {
                    // idx_connection_log_device_time (device_id, connected_at DESC) 색인을 그대로 탄다.
                    command.CommandText = @"
SELECT log_id, device_id, connected_at, disconnected_at, avg_latency_ms, message_count, disconnect_reason
  FROM connection_log
 WHERE device_id = $id
 ORDER BY connected_at DESC, log_id DESC
 LIMIT $limit;";
                    command.Parameters.AddWithValue("$id", ToDb(deviceId));
                    command.Parameters.AddWithValue("$limit", recent);

                    using SqliteDataReader reader = command.ExecuteReader();

                    while (reader.Read())
                        detail.RecentConnections.Add(ReadConnection(reader));
                }

                return Done(StoreResult.Ok, "상세 조회 성공");
            });

            return LastResult == StoreResult.Ok ? detail : null;
        }

        // 저장된 기기 전부 + 배치 + 연결 통계. 쿼리 1번. (기기 수 N 과 상관없음 → N+1 없음)
        // DB 오류면 빈 목록 (LastResult = 500)
        public List<DeviceDetail> GetAllDetails()
        {
            var result = new List<DeviceDetail>();

            Guarded("기기 목록 통합 조회", () =>
            {
                using SqliteConnection connection = _db.Open();
                using SqliteCommand command = connection.CreateCommand();

                // 정렬은 GetAll 과 같다. (최근 연결 순, 연결 안 된 기기는 뒤로)
                command.CommandText = DetailSelect + @"
 ORDER BY (d.last_seen_at IS NULL), d.last_seen_at DESC, d.device_name;";

                using SqliteDataReader reader = command.ExecuteReader();

                while (reader.Read())
                    result.Add(ReadDetail(reader));

                return Done(StoreResult.Ok, $"{result.Count}대 통합 조회");
            });

            return result;
        }

        // DetailSelect 한 줄 → DeviceDetail
        // 0~5 는 device, 6~9 는 screen_layout, 10~13 은 connection_log 집계. (DetailSelect 의 순서)
        private static DeviceDetail ReadDetail(SqliteDataReader reader)
        {
            var detail = new DeviceDetail
            {
                // 앞 6칸은 SelectColumns 와 순서가 같아서 ReadRow 를 그대로 쓴다.
                Device = ReadRow(reader),
                ConnectionCount = (int)reader.GetInt64(10),
                LastConnectedAt = reader.IsDBNull(11) ? null : FromDb(reader.GetString(11)),
                AvgLatencyMs = reader.IsDBNull(12) ? null : Math.Round(reader.GetDouble(12), 2),
                TotalMessages = reader.GetInt64(13),
            };

            // LEFT JOIN 에서 짝이 없으면 배치 칸이 전부 NULL 이다.
            if (!reader.IsDBNull(6))
            {
                detail.Layout = new SavedLayout
                {
                    DeviceId = detail.Device.DeviceId,
                    Placement = new ScreenPlacement(
                        ScreenLayout.FromDb(reader.GetString(6)),
                        (int)reader.GetInt64(7)),
                    ScreenWidth = (int)reader.GetInt64(8),
                    ScreenHeight = (int)reader.GetInt64(9),
                };
            }

            return detail;
        }

        private static ConnectionRecord ReadConnection(SqliteDataReader reader)
        {
            return new ConnectionRecord
            {
                LogId = reader.GetInt64(0),
                DeviceId = Guid.Parse(reader.GetString(1)),
                ConnectedAt = FromDb(reader.GetString(2)),
                DisconnectedAt = reader.IsDBNull(3) ? null : FromDb(reader.GetString(3)),
                AvgLatencyMs = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                MessageCount = reader.GetInt64(5),
                DisconnectReason = reader.IsDBNull(6) ? null : reader.GetString(6),
            };
        }


        // ═════════════════════════════════════════════════════
        //  도우미
        // ═════════════════════════════════════════════════════

        // SQLite 오류 번호 19 = 제약 조건 위반 (PRIMARY KEY, CHECK, NOT NULL, FOREIGN KEY 전부)
        private const int SqliteConstraint = 19;

        // 19 를 더 자세히 나눈 번호. (SqliteExtendedErrorCode)
        //   1555 = PRIMARY KEY 중복,  2067 = UNIQUE 중복
        private const int SqlitePrimaryKey = 1555;
        private const int SqliteUnique = 2067;

        private const string SelectColumns = @"
SELECT device_id, device_name, last_ip, is_trusted, paired_at, last_seen_at
  FROM device";

        // 값 검사. 문제가 있으면 이유를, 없으면 null 을 돌려준다.
        private static string? Validate(TrustedDevice device)
        {
            if (device.DeviceId == Guid.Empty)
                return "기기 ID 가 비어 있습니다.";

            if (string.IsNullOrWhiteSpace(device.DeviceName))
                return "기기 이름이 비어 있습니다.";

            // Windows 컴퓨터 이름은 최대 15자지만, 나중에 사용자가 별명을 붙일 수 있게 여유를 둔다.
            if (device.DeviceName.Length > 100)
                return "기기 이름이 너무 깁니다. (100자 이하)";

            // IP 는 없어도 되지만, 있으면 제대로 된 주소여야 한다.
            if (!string.IsNullOrEmpty(device.LastIp) && !IsValidIp(device.LastIp))
                return $"IP 주소 형식이 아닙니다: {device.LastIp}";

            if (device.PairedAt == default)
                return "처음 연결한 시각(paired_at)이 비어 있습니다.";

            return null;
        }

        // IP 주소가 맞는가. (6주차 보완)
        //
        // IPAddress.TryParse 만 쓰면 "1" 이나 "10.1" 같은 줄임 표기도 통과한다.
        // ("1" → 0.0.0.1 로 바뀐다) 사용자가 잘못 넣은 값이 그대로 저장되므로,
        // IPv4 로 읽혔으면 점으로 나뉜 숫자 4칸(각 1~3자리)인지 한 번 더 본다.
        // IPv6 는 TryParse 결과를 그대로 믿는다.
        private static bool IsValidIp(string text)
        {
            if (!IPAddress.TryParse(text, out IPAddress? address))
                return false;

            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return true;

            string[] parts = text.Split('.');

            return parts.Length == 4 &&
                   parts.All(p => p.Length is >= 1 and <= 3 && p.All(char.IsAsciiDigit));
        }

        private static bool Exists(SqliteConnection connection, Guid deviceId)
        {
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "SELECT COUNT(*) FROM device WHERE device_id = $id;";
            command.Parameters.AddWithValue("$id", ToDb(deviceId));

            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }

        // TrustedDevice → SQL 자리($이름)에 값 채우기
        private static void BindAll(SqliteCommand command, TrustedDevice device)
        {
            command.Parameters.AddWithValue("$id", ToDb(device.DeviceId));
            command.Parameters.AddWithValue("$name", device.DeviceName.Trim());

            // C# 의 null 은 DB 로 그대로 못 보낸다. DBNull.Value 로 바꿔야 NULL 이 들어간다.
            //
            // (object) 를 붙이는 이유: ? : 의 양쪽 타입이 DBNull 과 string 으로 달라서
            // 그대로 쓰면 컴파일러가 결과 타입을 못 정한다. 둘 다 object 로 맞춰준다.
            command.Parameters.AddWithValue("$ip",
                string.IsNullOrEmpty(device.LastIp) ? DBNull.Value : (object)device.LastIp);

            // SQLite 에는 참/거짓 타입이 없어서 0/1 정수로 저장한다. (CHECK 로 0, 1 만 허용)
            command.Parameters.AddWithValue("$trusted", device.IsTrusted ? 1 : 0);

            command.Parameters.AddWithValue("$pairedAt", ToDb(device.PairedAt));
            // 여기도 위의 $ip 와 같은 이유로 (object) 를 붙인다.
            command.Parameters.AddWithValue("$lastSeenAt",
                device.LastSeenAt.HasValue ? (object)ToDb(device.LastSeenAt.Value) : DBNull.Value);
        }

        // DB 한 줄 → TrustedDevice
        //
        // 번호(0, 1, 2 ...)는 SelectColumns 의 컬럼 순서와 같다.
        private static TrustedDevice ReadRow(SqliteDataReader reader)
        {
            return new TrustedDevice
            {
                DeviceId = Guid.Parse(reader.GetString(0)),
                DeviceName = reader.GetString(1),
                LastIp = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsTrusted = reader.GetInt64(3) == 1,
                PairedAt = FromDb(reader.GetString(4)),
                LastSeenAt = reader.IsDBNull(5) ? null : FromDb(reader.GetString(5)),
            };
        }

        // ── 저장 형식 ─────────────────────────────────────────
        //
        // Guid     → 소문자 36자 문자열 (예: 3f2b...-....)
        // DateTime → UTC ISO 8601 (예: 2026-09-28T06:52:10.123Z)
        //
        // 형식을 고정하는 이유:
        //   SQLite 는 문자열 비교로 정렬한다. 형식이 섞이면 시간 순서가 틀어진다.
        //   컴퓨터의 날짜 표시 설정(한국식/미국식)에 따라 형식이 달라지지 않도록
        //   InvariantCulture 를 쓴다.

        private static string ToDb(Guid id) => id.ToString("D");

        private static string ToDb(DateTime time) =>
            time.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        private static DateTime FromDb(string text) =>
            DateTime.Parse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        private StoreResult Done(StoreResult code, string message)
        {
            LastMessage = message;
            LastResult = code;
            return code;
        }

        private StoreResult Fail(StoreResult code, string message)
        {
            LastMessage = message;
            LastResult = code;
            return code;
        }

        // DB 자체가 고장 났을 때의 안전망. (6주차 보완)
        //
        // 파일이 다른 프로그램에 잠겼거나, 손상됐거나, 디스크가 꽉 찼으면
        // SqliteException 이 난다. 예전에는 그대로 화면까지 올라가서 프로그램이 멈출 수 있었다.
        // 이제는 500(Error) 으로 바꿔 돌려준다. 화면은 다른 실패와 똑같이 안내만 띄우면 된다.
        //
        // 제약 조건 위반(400·404·409)은 각 함수 안에서 먼저 잡으므로 여기까지 오지 않는다.
        private StoreResult Guarded(string what, Func<StoreResult> body)
        {
            try
            {
                return body();
            }
            catch (SqliteException ex)
            {
                return Fail(StoreResult.Error, $"{what} 중 DB 오류: {DescribeSqliteError(ex)} (SQLite {ex.SqliteErrorCode})");
            }
        }

        // 자주 나는 SQLite 오류를 사람이 알아듣는 한글로 바꾼다. (8주차 리뷰 반영)
        // 화면에 그대로 보여주는 글자라 "database is locked" 같은 영어가 나오지 않게 한다.
        // 목록에 없는 오류는 원래 영어 문구를 그대로 둔다. (원인 추적용)
        private static string DescribeSqliteError(SqliteException ex) => ex.SqliteErrorCode switch
        {
            5 or 6 => "다른 곳에서 DB 를 쓰는 중이라 잠겨 있습니다. 잠시 뒤 다시 시도하세요",
            8 => "DB 파일에 쓸 수 없습니다 (읽기 전용)",
            10 => "DB 파일을 읽거나 쓰는 중 디스크 오류가 났습니다",
            11 or 26 => "DB 파일이 손상됐거나 DB 파일이 아닙니다",
            13 => "디스크 공간이 부족합니다",
            14 => "DB 파일을 열 수 없습니다 (폴더·권한 확인)",
            _ => ex.Message,
        };
    }
}
