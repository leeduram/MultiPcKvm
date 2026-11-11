using Microsoft.Data.Sqlite;

namespace MultiPcKvm.App.Storage
{
    // ─────────────────────────────────────────────────────────────
    //  신뢰 기기 CRUD 시연 (4주차 스프린트 1)
    //
    //  App 프로젝트를 실행해서 [3] 을 고르면 나온다.
    //
    //  실제 DB 파일(%AppData%\MultiPcKvm\kvm.db)에 시연용 기기 한 대를
    //  만들고 → 읽고 → 고치고 → 지운다. 단계마다 멈추므로
    //  옆에 DB Browser for SQLite 를 띄워두고 [새로 고침] 하면서
    //  레코드가 실제로 바뀌는 걸 같이 볼 수 있다.
    //
    //  일부러 실패하는 경우(중복, 잘못된 값, 없는 기기)도 같이 보여준다.
    //  성공만 보여주면 오류 처리가 제대로 되는지 알 수 없다.
    //
    //  시연용 기기는 마지막에 지우므로 실제 저장된 기기에는 영향이 없다.
    // ─────────────────────────────────────────────────────────────
    public static class DeviceStoreDemo
    {
        // 시연용 기기 ID. 실제 PC 와 겹치지 않도록 눈에 띄는 값으로 고정한다.
        private static readonly Guid DemoId =
            Guid.Parse("de300000-0000-4000-8000-000000000001");

        // pause = false 면 멈추지 않고 끝까지 돈다. (자동 테스트용)
        public static void Run(string? dbPath = null, bool pause = true)
        {
            var db = new KvmDatabase(dbPath ?? KvmDatabase.DefaultPath);
            var repo = new DeviceRepository(db);

            Title("신뢰 기기 CRUD 시연");
            Console.WriteLine($"  DB 파일: {db.FilePath}");
            Console.WriteLine("  (DB Browser for SQLite 로 이 파일을 열어두면 단계마다 바뀌는 걸 볼 수 있습니다)");


            // ── Step 1. 스키마 반영 확인 ────────────────────────
            Step("Step 1. 테이블 만들기 + 스키마 확인", pause);

            db.EnsureCreated();
            ShowSchema(db);

            // 이전 시연이 중간에 끊겼다면 남아 있을 수 있으므로 먼저 치운다.
            repo.Delete(DemoId);


            // ── C. Create ───────────────────────────────────────
            Step("C. Create — 새 기기 저장", pause);

            var demo = new TrustedDevice
            {
                DeviceId = DemoId,
                DeviceName = "시연용-PC",
                LastIp = "192.168.0.99",
                IsTrusted = true,
                PairedAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
            };

            Result("새 기기 저장", repo.Create(demo), repo);
            Result("같은 기기를 또 저장 (실패해야 정상)", repo.Create(demo), repo);

            var noName = new TrustedDevice
            {
                DeviceId = Guid.NewGuid(),
                DeviceName = "   ",
                PairedAt = DateTime.UtcNow,
            };
            Result("이름이 빈 기기 (실패해야 정상)", repo.Create(noName), repo);

            var badIp = new TrustedDevice
            {
                DeviceId = Guid.NewGuid(),
                DeviceName = "잘못된-IP",
                LastIp = "999.1.2",
                PairedAt = DateTime.UtcNow,
            };
            Result("IP 형식이 틀린 기기 (실패해야 정상)", repo.Create(badIp), repo);


            // ── R. Read ─────────────────────────────────────────
            Step("R. Read — 목록 조회 / 한 대 조회", pause);

            ShowList(repo.GetAll());

            TrustedDevice? found = repo.Find(DemoId);
            Result("시연용 기기 한 대 조회", found != null ? StoreResult.Ok : StoreResult.NotFound, repo);
            if (found != null) ShowOne(found);

            TrustedDevice? missing = repo.Find(Guid.NewGuid());
            Result("없는 기기 조회 (실패해야 정상)",
                missing != null ? StoreResult.Ok : StoreResult.NotFound, repo);


            // ── U. Update ───────────────────────────────────────
            Step("U. Update — 이름과 IP 바꾸기", pause);

            demo.DeviceName = "시연용-PC-이름변경";
            demo.LastIp = "192.168.0.123";
            demo.LastSeenAt = DateTime.UtcNow;

            Result("이름·IP 수정", repo.Update(demo), repo);

            TrustedDevice? afterUpdate = repo.Find(DemoId);
            if (afterUpdate != null) ShowOne(afterUpdate);

            var ghost = new TrustedDevice
            {
                DeviceId = Guid.NewGuid(),
                DeviceName = "없는-PC",
                PairedAt = DateTime.UtcNow,
            };
            Result("없는 기기 수정 (실패해야 정상)", repo.Update(ghost), repo);


            // ── 제약 조건 확인 ──────────────────────────────────
            Step("제약 조건 — DB 가 직접 막는지 확인", pause);

            // 1) CHECK: is_trusted 는 0 또는 1 만 된다.
            //    Repository 를 거치면 bool 이라 애초에 2 가 못 들어가므로 SQL 로 직접 넣어본다.
            TrySql(db, "is_trusted = 2 넣기 (CHECK 제약으로 거부돼야 정상)",
                "UPDATE device SET is_trusted = 2 WHERE device_id = $id;");

            // 2) FOREIGN KEY: 없는 기기의 화면 배치는 못 넣는다.
            TrySql(db, "없는 기기의 화면 배치 넣기 (외래키 제약으로 거부돼야 정상)",
                "INSERT INTO screen_layout (device_id, position, screen_width, screen_height, updated_at) " +
                "VALUES ('00000000-0000-0000-0000-00000000dead', 'RIGHT', 1920, 1080, '2026-01-01T00:00:00.000Z');");

            // 3) CASCADE 준비: 시연용 기기에 화면 배치 한 줄을 달아둔다.
            TrySql(db, "시연용 기기의 화면 배치 넣기 (성공해야 정상)",
                "INSERT INTO screen_layout (device_id, position, screen_width, screen_height, updated_at) " +
                "VALUES ($id, 'RIGHT', 2880, 1800, '2026-01-01T00:00:00.000Z');");

            Console.WriteLine($"    → 시연용 기기의 화면 배치: {CountLayouts(db)}줄");


            // ── D. Delete ───────────────────────────────────────
            Step("D. Delete — 기기 삭제 (화면 배치도 같이 지워져야 함)", pause);

            Result("시연용 기기 삭제", repo.Delete(DemoId), repo);
            Console.WriteLine($"    → 시연용 기기의 화면 배치: {CountLayouts(db)}줄 (0 이면 CASCADE 정상)");

            Result("한 번 더 삭제 (실패해야 정상)", repo.Delete(DemoId), repo);

            Title("시연 끝");
            ShowList(repo.GetAll());
        }


        // ── 출력 도우미 ───────────────────────────────────────

        private static void Title(string text)
        {
            Console.WriteLine();
            Console.WriteLine("════════════════════════════════════════════════");
            Console.WriteLine($"  {text}");
            Console.WriteLine("════════════════════════════════════════════════");
        }

        private static void Step(string text, bool pause)
        {
            if (pause)
            {
                Console.WriteLine();
                Console.Write("  (엔터를 누르면 다음 단계)");
                Console.ReadLine();
            }

            Console.WriteLine();
            Console.WriteLine($"── {text} ──");
        }

        private static void Result(string what, StoreResult code, DeviceRepository repo)
        {
            // 예: [201 Created ] 새 기기 저장 — 저장했습니다: 시연용-PC
            Console.WriteLine($"  [{(int)code} {code,-8}] {what} — {repo.LastMessage}");
        }

        private static void ShowList(List<TrustedDevice> list)
        {
            Console.WriteLine($"  저장된 기기 {list.Count}대");

            foreach (TrustedDevice d in list)
            {
                string seen = d.LastSeenAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-";
                Console.WriteLine($"    · {d.DeviceName,-20} {d.LastIp ?? "-",-15} 신뢰={(d.IsTrusted ? 1 : 0)}  마지막 연결 {seen}");
            }
        }

        private static void ShowOne(TrustedDevice d)
        {
            Console.WriteLine($"    device_id    = {d.DeviceId}");
            Console.WriteLine($"    device_name  = {d.DeviceName}");
            Console.WriteLine($"    last_ip      = {d.LastIp ?? "NULL"}");
            Console.WriteLine($"    is_trusted   = {(d.IsTrusted ? 1 : 0)}");
            Console.WriteLine($"    paired_at    = {d.PairedAt:yyyy-MM-dd HH:mm:ss} (UTC)");
            Console.WriteLine($"    last_seen_at = {(d.LastSeenAt.HasValue ? d.LastSeenAt.Value.ToString("yyyy-MM-dd HH:mm:ss") + " (UTC)" : "NULL")}");
        }

        // 테이블 목록과 device 테이블의 컬럼·외래키 설정을 보여준다.
        // 3주차 DDL 이 의도대로 들어갔는지 눈으로 확인하는 용도다.
        private static void ShowSchema(KvmDatabase db)
        {
            using SqliteConnection connection = db.Open();

            Console.WriteLine("  테이블:");
            Query(connection,
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;",
                r => $"    · {r.GetString(0)}");

            Console.WriteLine("  device 컬럼 (이름 / 타입 / NOT NULL / 기본값 / PK):");
            Query(connection, "PRAGMA table_info(device);",
                r => $"    · {r.GetString(1),-13} {r.GetString(2),-8} notnull={r.GetInt64(3)}  " +
                     $"default={(r.IsDBNull(4) ? "-" : r.GetString(4))}  pk={r.GetInt64(5)}");

            Console.WriteLine("  screen_layout 외래키:");
            Query(connection, "PRAGMA foreign_key_list(screen_layout);",
                r => $"    · {r.GetString(3)} → {r.GetString(2)}.{r.GetString(4)}  ON DELETE {r.GetString(6)}");

            Console.WriteLine("  외래키 검사:");
            Query(connection, "PRAGMA foreign_keys;",
                r => $"    · {(r.GetInt64(0) == 1 ? "켜짐" : "꺼짐 (CASCADE 가 동작하지 않음!)")}");

            Console.WriteLine("  스키마 버전:");
            Query(connection, "PRAGMA user_version;", r => $"    · {r.GetInt64(0)}");
        }

        private static void Query(SqliteConnection connection, string sql, Func<SqliteDataReader, string> line)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
                Console.WriteLine(line(reader));
        }

        // SQL 을 직접 실행해보고, 성공했는지 제약에 걸렸는지 보여준다.
        private static void TrySql(KvmDatabase db, string what, string sql)
        {
            using SqliteConnection connection = db.Open();
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", DemoId.ToString("D"));

            try
            {
                int changed = command.ExecuteNonQuery();
                Console.WriteLine($"  [성공] {what} — {changed}줄");
            }
            catch (SqliteException ex)
            {
                Console.WriteLine($"  [거부] {what} — {ex.Message}");
            }
        }

        private static long CountLayouts(KvmDatabase db)
        {
            using SqliteConnection connection = db.Open();
            using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "SELECT COUNT(*) FROM screen_layout WHERE device_id = $id;";
            command.Parameters.AddWithValue("$id", DemoId.ToString("D"));

            return Convert.ToInt64(command.ExecuteScalar());
        }
    }
}
