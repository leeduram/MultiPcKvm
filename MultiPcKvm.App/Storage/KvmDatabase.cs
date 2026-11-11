using Microsoft.Data.Sqlite;

namespace MultiPcKvm.App.Storage
{
    // ─────────────────────────────────────────────────────────────
    //  이 PC 의 로컬 DB. SQLite 파일 하나다.
    //
    //  왜 SQLite 인가 (3주차 기술 명세서 1장)
    //    · 서버 프로그램이 필요 없다. 파일 하나가 곧 DB 다.
    //    · 사용자가 따로 설치할 게 없다. 라이브러리가 프로그램에 같이 들어간다.
    //    · 그래도 외래키·CHECK 같은 제약을 DB 가 직접 지켜준다.
    //
    //  파일 위치: %AppData%\MultiPcKvm\kvm.db
    //    김기령님 DeviceIdentity 의 device-id.txt 와 같은 폴더다.
    //    Program Files 에 두면 안 되는 이유: 일반 권한으로는 거기 쓰기가 막힌다.
    //
    //  이 클래스가 하는 일은 두 가지뿐이다.
    //    1) EnsureCreated : 처음 켤 때 테이블을 만든다 (이미 있으면 그대로 둔다)
    //    2) Open          : 연결을 하나 열어서 돌려준다
    //  실제로 읽고 쓰는 건 DeviceRepository 가 한다.
    // ─────────────────────────────────────────────────────────────
    public class KvmDatabase
    {
        public static string DefaultPath { get; } =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MultiPcKvm",
                "kvm.db");

        // 스키마 버전.
        //
        // 테이블 구조를 바꾸는 날이 오면 이 숫자를 올리고,
        // EnsureCreated 에서 "예전 버전이면 이렇게 고쳐라" 를 추가한다.
        // DB 파일 안에는 PRAGMA user_version 으로 기록된다.
        //
        // 버전 기록
        //   1 : 3주차 DDL
        //   2 : screen_layout 에 edge_offset 추가 (화면 배치를 칸이 아니라 자유 위치로)
        //   3 : device 에 token_out, token_in 추가 (저장된 기기는 코드 없이 연결)
        //   4 : connection_log 에 연결 시각 색인 추가 (7주차 연결 기록 목록을 최신순으로 나눠 읽기)
        //       색인만 더한 것이라 예전 DB 도 아래 DDL(IF NOT EXISTS)이 켤 때 알아서 만든다.
        public const int SchemaVersion = 4;

        public string FilePath { get; }

        private readonly string _connectionString;

        public KvmDatabase(string filePath)
        {
            FilePath = filePath;

            // 연결 문자열은 직접 이어 붙이지 않고 빌더로 만든다.
            // 경로에 공백이나 ; 가 들어 있어도 알아서 따옴표를 쳐준다.
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = filePath,
                Mode = SqliteOpenMode.ReadWriteCreate,   // 파일이 없으면 만든다
            }.ToString();
        }


        // ── 연결 열기 ─────────────────────────────────────────

        // 연결을 하나 열어서 돌려준다. 쓰고 나면 반드시 닫아야 한다. (using)
        //
        // 매번 새로 여는 게 느리지 않은가?
        //   Microsoft.Data.Sqlite 는 닫은 연결을 버리지 않고 모아뒀다가
        //   다음에 다시 쓴다(연결 풀). 그래서 짧게 열고 닫는 편이 오히려 안전하다.
        public SqliteConnection Open()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // ★ 외래키 검사를 켠다. 연결을 열 때마다 해야 한다.
            //
            // SQLite 는 옛날 프로그램과의 호환 때문에 외래키 검사가 기본으로 꺼져 있다.
            // 그리고 이 설정은 파일에 저장되지 않고 "연결마다" 따로 적용된다.
            // 이걸 빼먹으면 DDL 에 적은 ON DELETE CASCADE 가 조용히 무시된다.
            // (기기를 지워도 그 기기의 화면 배치·연결 이력이 고아로 남는다)
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys = ON;";
                command.ExecuteNonQuery();
            }

            return connection;
        }


        // ── 테이블 만들기 ─────────────────────────────────────

        // 3주차 기술 명세서 3.3 의 DDL 을 그대로 실행한다.
        //
        // 달라진 점은 "IF NOT EXISTS" 하나뿐이다.
        // 프로그램은 켤 때마다 이걸 부르는데, 두 번째부터는
        // 이미 테이블이 있으므로 아무 일도 일어나지 않아야 한다.
        public void EnsureCreated()
        {
            // 폴더가 없으면 SQLite 가 파일을 못 만든다. 먼저 만든다.
            string? folder = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            using SqliteConnection connection = Open();

            // 테이블 4개를 한 묶음(트랜잭션)으로 만든다.
            // 중간에 하나가 실패하면 전부 없던 일이 된다.
            // 반쯤 만들어진 DB 가 남으면 다음 실행 때 원인을 찾기 어렵다.
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Ddl;
                command.ExecuteNonQuery();
            }

            // 버전 1 로 만들어진 DB 를 버전 2 로 올린다.
            //
            // CREATE TABLE IF NOT EXISTS 는 테이블이 이미 있으면 아무것도 안 한다.
            // 그래서 예전에 만든 DB 에는 새 칸(edge_offset)이 저절로 생기지 않는다.
            // 칸이 없을 때만 추가한다. (새로 만든 DB 에는 위 DDL 로 이미 있다)
            AddColumnIfMissing(connection, transaction, "screen_layout", "edge_offset",
                "INTEGER NOT NULL DEFAULT 0");

            // 버전 2 이하 → 3 : 저장된 기기 열쇠 칸
            AddColumnIfMissing(connection, transaction, "device", "token_out", "INTEGER");
            AddColumnIfMissing(connection, transaction, "device", "token_in", "INTEGER");

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"PRAGMA user_version = {SchemaVersion};";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        // 칸이 없을 때만 추가한다.
        //
        // table, column, type 은 이 파일 안에 적은 고정 글자만 넘긴다. (사용자 입력이 아님)
        // ALTER TABLE 은 $이름 자리를 쓸 수 없어서 글자를 이어 붙인다.
        private static void AddColumnIfMissing(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string table,
            string column,
            string type)
        {
            if (HasColumn(connection, transaction, table, column))
                return;

            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
            command.ExecuteNonQuery();
        }

        // 테이블에 그 칸이 있는가. (PRAGMA table_info 는 칸마다 한 줄을 돌려준다. 1번째 값이 이름)
        private static bool HasColumn(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string table,
            string column)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({table});";

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }


        // 3주차 DDL + IF NOT EXISTS
        //
        // 실제로 읽고 쓰는 건 device 와 screen_layout(화면 배치) 이다.
        // 나머지 둘도 미리 만드는 이유:
        //   device 를 지울 때 ON DELETE CASCADE 로 함께 지워져야 하는데,
        //   그 동작을 지금 확인해두려면 테이블이 있어야 한다.
        public const string Ddl = @"
-- 신뢰 기기 : 페어링을 통과한 PC 목록
--   token_out : 이 기기에 연결할 때 코드 대신 낼 열쇠 (이 기기가 만들어 준 것). 버전 3.
--   token_in  : 이 기기가 나에게 연결할 때 낼 열쇠 (내가 만들어 준 것). 버전 3.
--               없으면(NULL) 코드를 입력해야 한다.
CREATE TABLE IF NOT EXISTS device (
    device_id     TEXT    NOT NULL,
    device_name   TEXT    NOT NULL,
    last_ip       TEXT,
    is_trusted    INTEGER NOT NULL DEFAULT 0,
    paired_at     TEXT    NOT NULL,
    last_seen_at  TEXT,
    token_out     INTEGER,
    token_in      INTEGER,
    CONSTRAINT device_pk PRIMARY KEY ( device_id ),
    CONSTRAINT device_is_trusted_ck CHECK ( is_trusted IN (0, 1) )
);

-- 화면 배치 : 각 기기가 내 PC 기준 어느 방향에 있는지
--   edge_offset : 붙은 변을 따라 밀린 양 (픽셀). 버전 2 에서 추가.
--                 오른쪽/왼쪽이면 세로로, 위/아래면 가로로 밀린 양.
CREATE TABLE IF NOT EXISTS screen_layout (
    layout_id     INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id     TEXT    NOT NULL,
    position      TEXT    NOT NULL,
    screen_width  INTEGER NOT NULL,
    screen_height INTEGER NOT NULL,
    dpi_scale     INTEGER NOT NULL DEFAULT 100,
    updated_at    TEXT    NOT NULL,
    edge_offset   INTEGER NOT NULL DEFAULT 0,
    CONSTRAINT screen_layout_position_ck
        CHECK ( position IN ('LEFT', 'RIGHT', 'TOP', 'BOTTOM') ),
    CONSTRAINT screen_layout_device_fk FOREIGN KEY ( device_id )
        REFERENCES device ( device_id ) ON DELETE CASCADE
);

-- 연결 이력 : 진단 및 통계용
CREATE TABLE IF NOT EXISTS connection_log (
    log_id            INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id         TEXT    NOT NULL,
    connected_at      TEXT    NOT NULL,
    disconnected_at   TEXT,
    avg_latency_ms    REAL,
    message_count     INTEGER NOT NULL DEFAULT 0,
    disconnect_reason TEXT,
    CONSTRAINT connection_log_device_fk FOREIGN KEY ( device_id )
        REFERENCES device ( device_id ) ON DELETE CASCADE
);

-- 프로그램 설정 : 포트, 자동 실행, 스트리밍 품질 등
CREATE TABLE IF NOT EXISTS app_setting (
    setting_key   TEXT NOT NULL,
    setting_value TEXT NOT NULL,
    updated_at    TEXT NOT NULL,
    CONSTRAINT app_setting_pk PRIMARY KEY ( setting_key )
);

CREATE INDEX IF NOT EXISTS idx_screen_layout_device
    ON screen_layout ( device_id );

CREATE INDEX IF NOT EXISTS idx_connection_log_device_time
    ON connection_log ( device_id, connected_at DESC );

-- 버전 4 : 모든 기기의 기록을 최신순으로 한 페이지씩 읽을 때 (기기 조건이 없을 때) 쓴다
CREATE INDEX IF NOT EXISTS idx_connection_log_time
    ON connection_log ( connected_at DESC );
";
    }
}
