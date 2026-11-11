using System.Globalization;
using Microsoft.Data.Sqlite;
using MultiPcKvm.App.Storage;

// ─────────────────────────────────────────────────────────────
//  시연용 연결 기록 넣기 · 지우기 (8주차)
//
//  연결 기록 창(WPF)에서 페이지 넘기기 · 검색 · 필터를 보여주려면 기록이 10개보다 많아야 한다.
//  실제로 수십 번 연결하는 대신, 실제 DB(%AppData%\MultiPcKvm\kvm.db)에 시연용 기록을 넣는다.
//
//    Seed()   : 가짜 기기 2대("시연용PC", "시연용노트북") + 연결 기록 30개를 넣는다
//    Remove() : 가짜 기기 2대를 지운다 → 기록도 ON DELETE CASCADE 로 함께 지워진다
//
//  진짜 기기·기록은 건드리지 않는다. 가짜 기기는 아래 고정 ID 로만 찾고 지운다.
//  가짜 기기는 "신뢰 안 함"(is_trusted = 0)으로 넣어서 메인 창의 기기 목록에는 뜨지 않는다.
//  (메인 창은 신뢰한 기기만 띄운다. MainWindow.Store.cs) 연결 기록 창에만 보인다.
// ─────────────────────────────────────────────────────────────
internal static class DemoData
{
    private static readonly (Guid Id, string Name, string Ip)[] Devices =
    {
        (Guid.Parse("de800000-0000-4000-8000-000000000001"), "시연용PC", "192.168.0.201"),
        (Guid.Parse("de800000-0000-4000-8000-000000000002"), "시연용노트북", "192.168.0.202"),
    };

    private static readonly string[] Reasons = { "이 PC 에서 끊음", "상대가 끊음", "연결 끊김", "다시 연결 실패" };

    private const int Count = 30;

    public static void Seed()
    {
        var db = new KvmDatabase(KvmDatabase.DefaultPath);
        db.EnsureCreated();
        var repo = new DeviceRepository(db);

        // 이미 넣은 적이 있으면 지우고 새로 넣는다. (여러 번 눌러도 30개로 유지)
        foreach (var d in Devices)
            repo.Delete(d.Id);

        foreach (var d in Devices)
        {
            StoreResult r = repo.Create(new TrustedDevice
            {
                DeviceId = d.Id,
                DeviceName = d.Name,
                LastIp = d.Ip,
                IsTrusted = false,              // 메인 창 기기 목록에는 안 뜨게
                PairedAt = DateTime.UtcNow.AddDays(-10),
            });

            if (r != StoreResult.Created)
            {
                Console.WriteLine($"  시연용 기기를 만들지 못했습니다: [{(int)r}] {repo.LastMessage}");
                return;
            }
        }

        // 연결 시각을 지난 며칠에 걸쳐 흩어 놓으려고 SQL 로 직접 넣는다.
        // (StartConnection 은 "지금" 시각만 쓴다)
        // 마지막 2개는 "연결 중"(끊긴 시각 없음)으로 둔다.
        //   ※ 메인 프로그램을 켤 때 남아 있는 "연결 중" 기록은 "비정상 종료" 로 정리된다. (6주차 기능)
        //     그래서 "연결 중" 탭을 보이려면 메인 프로그램을 켠 뒤에 넣어야 한다.
        DateTime start = DateTime.UtcNow.AddDays(-7);

        using SqliteConnection connection = db.Open();
        using SqliteTransaction tx = connection.BeginTransaction();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO connection_log (device_id, connected_at, disconnected_at, avg_latency_ms, message_count, disconnect_reason)
VALUES ($dev, $at, $end, $lat, $msg, $reason);";

        for (int i = 0; i < Count; i++)
        {
            var d = Devices[i % 3 == 2 ? 1 : 0];                 // 3개 중 1개는 노트북
            DateTime at = start.AddHours(i * 5.5);
            bool open = i >= Count - 2;

            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$dev", d.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$at", ToDb(at));
            cmd.Parameters.AddWithValue("$end", open ? DBNull.Value : (object)ToDb(at.AddMinutes(10 + i * 3)));
            cmd.Parameters.AddWithValue("$lat", open || i % 7 == 0 ? DBNull.Value : (object)Math.Round(1.2 + i % 9 * 0.35, 2));
            cmd.Parameters.AddWithValue("$msg", open ? 0 : 150 + i * 37 % 900);
            cmd.Parameters.AddWithValue("$reason", open ? DBNull.Value : (object)Reasons[i % 4]);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();

        Console.WriteLine($"  시연용 기기 2대 · 연결 기록 {Count}개를 넣었습니다. (연결 중 2개 포함)");
        Console.WriteLine($"  DB: {db.FilePath}");
        Console.WriteLine("  메인 프로그램의 [연결 기록] 창에서 확인하세요. 다 찍은 뒤에는 3번으로 지우세요.");
    }

    public static void Remove()
    {
        var db = new KvmDatabase(KvmDatabase.DefaultPath);

        if (!File.Exists(db.FilePath))
        {
            Console.WriteLine("  DB 파일이 없습니다. 지울 것이 없습니다.");
            return;
        }

        var repo = new DeviceRepository(db);
        int removed = 0;

        foreach (var d in Devices)
        {
            if (repo.Delete(d.Id) == StoreResult.Ok)
                removed++;
        }

        Console.WriteLine(removed == 0
            ? "  시연용 기록이 없습니다. (이미 지웠거나 넣은 적 없음)"
            : $"  시연용 기기 {removed}대와 그 연결 기록을 지웠습니다. 진짜 기기·기록은 그대로입니다.");
    }

    private static string ToDb(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
