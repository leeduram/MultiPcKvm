using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.Data.Sqlite;
using MultiPcKvm.App;
using MultiPcKvm.App.Storage;

// ─────────────────────────────────────────────────────────────
//  저장소(DeviceRepository) 검사 (6주차 · 7주차)
//
//  실행하면 아래를 차례로 확인하고, 항목마다 ✔ / ✘ 와 결과 코드(200·201·400·403·404·409·500)를 찍는다.
//
//    A. 4주차 리뷰 보완  : IPv4 줄임 표기 거부(400), 동시 저장 경쟁(409), DB 고장(500)
//    B. 상태 변경 API    : 연결 기록 시작(201) → 종료(200), 남의 기록(403), 없음(404), 두 번 종료(409), 잘못된 값(400)
//    C. 통합 조회 (JOIN) : 기기 + 배치 + 연결 통계를 한 번에(200), 없는 기기(404), N+1 비교
//    D. 그 밖            : 비정상 종료 정리, 기기 삭제 시 기록도 함께 삭제(CASCADE)
//
//  7주차 — 연결 기록 목록 (GetConnectionHistory)
//    E. 페이지 · 정렬    : 기본값(page=1, size=10, 최신순), 마지막·빈 페이지, 겹침 없음, 정렬 4종
//    F. 필터 · 검색      : 상태(open/closed), 키워드, 둘 다(AND), LIKE 특수 글자, SQL 인젝션 시도
//    G. 잘못된 조건      : page·size·sort·status·keyword 가 잘못되면 400, 빈 값은 기본값, DB 고장 500
//    H. 대량 데이터      : 연결 기록 10만 줄에서 첫·마지막 페이지, 복합 조건 시간
//
//  검사는 실제 DB(%AppData%\MultiPcKvm\kvm.db)를 건드리지 않는다. 임시 폴더에 시험용 DB 를 만들고 끝나면 지운다.
//  (실제 DB 를 쓰는 건 처음 메뉴의 2 · 3 번 "시연용 기록" 뿐이다)
//  실패한 항목 수를 종료 코드로 돌려준다. (0 이면 전부 통과)
// ─────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;   // 한글이 깨지지 않게

// ── 메뉴 (8주차) ─────────────────────────────────────────
// Enter 만 누르면 예전처럼 검사를 돈다.
// 2 · 3 은 실제 DB 에 시연용 연결 기록을 넣고 지운다. (연결 기록 창 화면을 찍을 때. DemoData.cs)
// 입력을 넣을 수 없는 환경(자동 실행)에서는 메뉴 없이 바로 검사한다.
if (!Console.IsInputRedirected)
{
    Console.WriteLine("  1) 검사 실행 (Enter)");
    Console.WriteLine("  2) 시연용 연결 기록 30개 넣기 (실제 DB)");
    Console.WriteLine("  3) 시연용 연결 기록 지우기 (실제 DB)");
    Console.Write("  번호: ");
    string? choice = Console.ReadLine()?.Trim();

    if (choice == "2") { DemoData.Seed(); return 0; }
    if (choice == "3") { DemoData.Remove(); return 0; }
    Console.WriteLine();
}

string folder = Path.Combine(Path.GetTempPath(), "MultiPcKvm-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(folder);
string dbPath = Path.Combine(folder, "test.db");

var db = new KvmDatabase(dbPath);
db.EnsureCreated();
var repo = new DeviceRepository(db);

int passed = 0, failed = 0;

// 시험용 기기 셋
var pcA = NewDevice("de600000-0000-4000-8000-00000000000a", "거실PC", "192.168.0.10");
var pcB = NewDevice("de600000-0000-4000-8000-00000000000b", "노트북", "192.168.0.11");
var pcC = NewDevice("de600000-0000-4000-8000-00000000000c", "새PC", null);   // 배치·기록 없는 기기

Console.WriteLine($"시험용 DB: {dbPath}");


// ═════════════════════════════════════════════════════════
Section("A. 4주차 리뷰 보완");
// ═════════════════════════════════════════════════════════

Check("기기 A 저장", repo.Create(pcA), StoreResult.Created);
Check("기기 B 저장", repo.Create(pcB), StoreResult.Created);
Check("기기 C 저장 (IP 없음)", repo.Create(pcC), StoreResult.Created);

// A-1. IPv4 줄임 표기
Check("IP \"1\" (0.0.0.1 로 읽히는 줄임 표기) 거부", repo.Create(NewDevice(null, "줄임1", "1")), StoreResult.Invalid);
Check("IP \"10.1\" (줄임 표기) 거부", repo.Create(NewDevice(null, "줄임2", "10.1")), StoreResult.Invalid);
Check("IP \"192.168.0.300\" (범위 밖) 거부", repo.Create(NewDevice(null, "범위", "192.168.0.300")), StoreResult.Invalid);
Check("IPv6 \"fe80::1\" 는 허용", repo.Create(NewDevice(null, "v6", "fe80::1")), StoreResult.Created);

// A-2. 동시 저장 경쟁 → 409
//
// "있는지 확인" 과 "INSERT" 사이에 다른 쪽이 같은 기기를 먼저 넣는 상황을 만든다.
// 시험용 DB 에만 트리거를 걸어, 이름이 '경쟁시험' 인 기기를 넣으려 하면 바로 직전에 같은 ID 로 한 줄을 먼저 넣는다.
using (SqliteConnection c = db.Open())
using (SqliteCommand cmd = c.CreateCommand())
{
    cmd.CommandText = @"
CREATE TRIGGER race_sim BEFORE INSERT ON device
WHEN NEW.device_name = '경쟁시험'
BEGIN
    INSERT INTO device (device_id, device_name, is_trusted, paired_at)
    VALUES (NEW.device_id, '먼저 저장된 쪽', 1, NEW.paired_at);
END;";
    cmd.ExecuteNonQuery();
}

Check("같은 기기 두 번 저장 (미리 확인에서 걸림)", repo.Create(pcA), StoreResult.Conflict);
Check("동시 저장 경쟁 (INSERT 에서 PK 충돌) → 400 이 아니라 409", repo.Create(NewDevice(null, "경쟁시험", null)), StoreResult.Conflict);

using (SqliteConnection c = db.Open())
using (SqliteCommand cmd = c.CreateCommand())
{
    cmd.CommandText = "DROP TRIGGER race_sim;";
    cmd.ExecuteNonQuery();
}

// A-3. DB 고장 → 500
//
// DB 파일 자리에 폴더가 있으면 SQLite 가 파일을 열지 못한다. (SQLite 오류 14)
// 예전에는 예외가 화면까지 올라갔다. 이제 500 으로 돌려받는다.
string brokenPath = Path.Combine(folder, "broken.db");
Directory.CreateDirectory(brokenPath);
var broken = new DeviceRepository(new KvmDatabase(brokenPath));

Check("DB 를 열 수 없음 → Create 500", broken.Create(NewDevice(null, "아무거나", null)), StoreResult.Error, broken);
Check("DB 를 열 수 없음 → StartConnection null + 500", broken.StartConnection(pcA.DeviceId) == null ? broken.LastResult : StoreResult.Ok, StoreResult.Error, broken);
Check("DB 를 열 수 없음 → GetDeviceDetail null + 500", broken.GetDeviceDetail(pcA.DeviceId) == null ? broken.LastResult : StoreResult.Ok, StoreResult.Error, broken);


// ═════════════════════════════════════════════════════════
Section("B. 상태 변경 API — 연결 기록 (연결 중 → 종료)");
// ═════════════════════════════════════════════════════════

Check("시작: 빈 기기 ID → 400", Start(Guid.Empty), StoreResult.Invalid);
Check("시작: 저장 안 된 기기 → 404", Start(Guid.NewGuid()), StoreResult.NotFound);

long log1 = repo.StartConnection(pcA.DeviceId) ?? -1;
Check($"시작: 기기 A → 201 (기록 {log1})", repo.LastResult, StoreResult.Created);
ShowLogRow("시작 직후 DB 상태", log1);

Check("종료: 다른 기기(B)가 A 의 기록을 끝내려 함 → 403",
    repo.EndConnection(log1, pcB.DeviceId, "이 PC 에서 끊음", 2.4, 120), StoreResult.Forbidden);
Check("종료: 없는 기록 번호 → 404",
    repo.EndConnection(99999, pcA.DeviceId, "이 PC 에서 끊음", 2.4, 120), StoreResult.NotFound);
Check("종료: 끊긴 이유가 비어 있음 → 400",
    repo.EndConnection(log1, pcA.DeviceId, "  ", 2.4, 120), StoreResult.Invalid);
Check("종료: 메시지 수가 음수 → 400",
    repo.EndConnection(log1, pcA.DeviceId, "이 PC 에서 끊음", 2.4, -1), StoreResult.Invalid);
ShowLogRow("거절된 뒤에도 DB 는 그대로 (연결 중)", log1);

Check("종료: 기기 A 본인 → 200",
    repo.EndConnection(log1, pcA.DeviceId, "이 PC 에서 끊음", 2.4, 120), StoreResult.Ok);
ShowLogRow("종료 후 DB 상태 (disconnected_at·이유·지연·메시지 수가 채워짐)", log1);

Check("종료: 이미 종료된 기록을 또 종료 → 409",
    repo.EndConnection(log1, pcA.DeviceId, "상대가 끊음", 1.0, 5), StoreResult.Conflict);
ShowLogRow("409 뒤에도 처음 종료 값이 그대로 남음", log1);

// 통계를 보기 위해 기록을 두 줄 더 남긴다.
long log2 = repo.StartConnection(pcA.DeviceId) ?? -1;
repo.EndConnection(log2, pcA.DeviceId, "상대가 끊음", 3.6, 80);
long log3 = repo.StartConnection(pcA.DeviceId) ?? -1;   // 아직 연결 중
long logB = repo.StartConnection(pcB.DeviceId) ?? -1;
repo.EndConnection(logB, pcB.DeviceId, "연결 끊김", null, 10);   // 지연을 못 잰 기록


// ═════════════════════════════════════════════════════════
Section("C. 통합 조회 (device + screen_layout + connection_log JOIN)");
// ═════════════════════════════════════════════════════════

repo.SaveLayout(new SavedLayout
{
    DeviceId = pcA.DeviceId,
    Placement = new ScreenPlacement(RemotePosition.Right, 120),
    ScreenWidth = 2560,
    ScreenHeight = 1440,
});

DeviceDetail? detailA = repo.GetDeviceDetail(pcA.DeviceId, recent: 5);
Check("기기 A 상세 → 200", repo.LastResult, StoreResult.Ok);
Expect("  기기 정보가 들어 있음", detailA?.Device.DeviceName == "거실PC");
Expect("  배치가 들어 있음 (RIGHT, 2560×1440)", detailA?.Layout is { ScreenWidth: 2560, Placement.Side: RemotePosition.Right });
Expect("  연결 3회 (연결 중 1회 포함)", detailA?.ConnectionCount == 3);
Expect("  평균 지연 = (2.4 + 3.6) / 2 = 3.0ms (못 잰 기록은 빼고 평균)", detailA?.AvgLatencyMs == 3.0);
Expect("  메시지 합계 120 + 80 = 200", detailA?.TotalMessages == 200);
Expect("  최근 기록 3줄, 최근 것부터 (맨 위는 연결 중)", detailA?.RecentConnections.Count == 3 && detailA.RecentConnections[0].IsOpen);
Expect("  다른 기기(B)의 기록이 섞이지 않음", detailA?.RecentConnections.All(r => r.DeviceId == pcA.DeviceId) == true);

Console.WriteLine();
Console.WriteLine("  ── 응답 (200) ──");
Console.WriteLine(Indent(ToJson(detailA), "  "));

repo.GetDeviceDetail(Guid.NewGuid());
Check("없는 기기 상세 → 404", repo.LastResult, StoreResult.NotFound);
repo.GetDeviceDetail(pcA.DeviceId, recent: 0);
Check("최근 기록 개수 0 → 400", repo.LastResult, StoreResult.Invalid);

List<DeviceDetail> all = repo.GetAllDetails();
Check("전체 통합 조회 → 200", repo.LastResult, StoreResult.Ok);
Expect($"  저장된 기기 4대가 모두 나옴 (배치·기록 없는 기기도 LEFT JOIN 으로 남음) — {all.Count}대", all.Count == 4);
Expect("  기기마다 한 줄 (기록 수만큼 불어나지 않음)", all.Select(d => d.Device.DeviceId).Distinct().Count() == all.Count);
DeviceDetail? detailC = all.FirstOrDefault(d => d.Device.DeviceId == pcC.DeviceId);
Expect("  기기 C: 배치 없음(null), 연결 0회", detailC != null && detailC.Layout == null && detailC.ConnectionCount == 0);
DeviceDetail? detailB = all.FirstOrDefault(d => d.Device.DeviceId == pcB.DeviceId);
Expect("  기기 B: 연결 1회, 지연 모름(null), 메시지 10", detailB is { ConnectionCount: 1, AvgLatencyMs: null, TotalMessages: 10 });

Console.WriteLine();
Console.WriteLine("  ── 전체 통합 조회 결과 ──");
foreach (DeviceDetail d in all)
{
    Console.WriteLine($"  {d.Device.DeviceName,-12} 배치 {(d.Layout == null ? "없음" : $"{d.Layout.Placement.Side} {d.Layout.ScreenWidth}×{d.Layout.ScreenHeight}"),-20}" +
                      $" 연결 {d.ConnectionCount}회  평균 지연 {(d.AvgLatencyMs?.ToString("F1") ?? "-"),4}ms  메시지 {d.TotalMessages}");
}

// N+1 비교: 기기 200대를 넣고 "따로 조회" 와 "통합 조회" 를 비교한다.
Console.WriteLine();
Console.WriteLine("  ── N+1 비교 (기기 200대) ──");
for (int i = 0; i < 200; i++)
{
    var d = NewDevice(null, $"부하{i:D3}", $"10.0.{i / 250}.{i % 250 + 1}");
    repo.Create(d);
    repo.SaveLayout(new SavedLayout { DeviceId = d.DeviceId, Placement = new ScreenPlacement(RemotePosition.Left, 0), ScreenWidth = 1920, ScreenHeight = 1080 });
    long id = repo.StartConnection(d.DeviceId) ?? -1;
    repo.EndConnection(id, d.DeviceId, "시험", 1.5, 10);
}

// 따로 조회: 목록 1번 + 기기마다 상세 1번(안에서 2번) = 1 + 2N
var sw = Stopwatch.StartNew();
List<TrustedDevice> plain = repo.GetAll();
int separateQueries = 1;
foreach (TrustedDevice d in plain)
{
    repo.GetDeviceDetail(d.DeviceId, recent: 1);
    separateQueries += 2;
}
long separateMs = sw.ElapsedMilliseconds;

sw.Restart();
List<DeviceDetail> joined = repo.GetAllDetails();
long joinedMs = sw.ElapsedMilliseconds;

Console.WriteLine($"  따로 조회 : {plain.Count}대, DB 명령 {separateQueries}번, {separateMs}ms");
Console.WriteLine($"  통합 조회 : {joined.Count}대, DB 명령 1번, {joinedMs}ms");
Expect("  통합 조회가 같은 대수를 돌려줌", joined.Count == plain.Count);

Console.WriteLine();
Console.WriteLine("  ── 통합 조회의 실행 계획 (EXPLAIN QUERY PLAN) ──");
using (SqliteConnection conn = db.Open())
using (SqliteCommand cmd = conn.CreateCommand())
{
    cmd.CommandText = "EXPLAIN QUERY PLAN " + DetailSqlForPlan();
    using SqliteDataReader r = cmd.ExecuteReader();
    while (r.Read())
        Console.WriteLine("    " + r.GetString(3));
}


// ═════════════════════════════════════════════════════════
Section("D. 그 밖");
// ═════════════════════════════════════════════════════════

// 프로그램이 갑자기 꺼져서 "연결 중" 으로 남은 기록 (위의 log3) 정리
int closed = repo.CloseDanglingConnections("프로그램이 비정상 종료됨");
Expect($"비정상 종료로 남은 '연결 중' 정리 → {closed}건", closed == 1);
ShowLogRow("정리된 기록", log3);
Expect("다시 불러도 정리할 게 없음 → 0건", repo.CloseDanglingConnections("x") == 0);

Check("기기 B 삭제 → 200", repo.Delete(pcB.DeviceId), StoreResult.Ok);
Expect("  B 의 연결 기록도 함께 지워짐 (ON DELETE CASCADE)", CountLogs(pcB.DeviceId) == 0);
Check("  지워진 기기의 기록 종료 시도 → 404", repo.EndConnection(logB, pcB.DeviceId, "x", null, 0), StoreResult.NotFound);


// ═════════════════════════════════════════════════════════
//  7주차 — 연결 기록 목록: 페이지 · 정렬 · 필터 · 검색
//
//  위(A~D)와 섞이지 않게 시험용 DB 를 하나 더 만든다. (history.db)
//  기기 4대, 연결 기록 26줄. 기대값은 아래 made 목록에서 직접 계산해서 비교한다.
// ═════════════════════════════════════════════════════════

var hdb = new KvmDatabase(Path.Combine(folder, "history.db"));
hdb.EnsureCreated();
var hist = new DeviceRepository(hdb);

TrustedDevice[] hDevices =
{
    NewDevice(null, "거실PC", "192.168.0.10"),
    NewDevice(null, "노트북", "192.168.0.11"),
    NewDevice(null, "회의실PC", "192.168.0.12"),
    NewDevice(null, "1층_PC", "192.168.0.13"),    // 이름에 _ 가 들어 있다 (LIKE 특수 글자 검사용)
};
foreach (TrustedDevice d in hDevices)
    hist.Create(d);

string[] reasons = { "이 PC 에서 끊음", "상대가 끊음", "연결 끊김", "다시 연결 실패" };
var made = new List<HistRow>();

// 25줄: 기기 3대에 돌아가며. 5번째마다 "연결 중" 으로 남기고, 7번째마다 지연을 못 잰 기록.
for (int i = 0; i < 25; i++)
{
    TrustedDevice d = hDevices[i % 3];
    long id = hist.StartConnection(d.DeviceId) ?? -1;

    if (i % 5 == 4)
    {
        made.Add(new HistRow(id, d.DeviceName, true, null, null, 0));
        continue;
    }

    double? latency = i % 7 == 0 ? null : Math.Round(1.0 + i * 0.1, 1);
    hist.EndConnection(id, d.DeviceId, reasons[i % 4], latency, i * 10);
    made.Add(new HistRow(id, d.DeviceName, false, reasons[i % 4], latency, i * 10));
}

// 26번째 줄: 1층_PC
{
    long id = hist.StartConnection(hDevices[3].DeviceId) ?? -1;
    hist.EndConnection(id, hDevices[3].DeviceId, "상대가 끊음", 2.0, 30);
    made.Add(new HistRow(id, "1층_PC", false, "상대가 끊음", 2.0, 30));
}

int totalRows = made.Count;   // 26
List<long> newestFirst = made.Select(m => m.Id).OrderByDescending(x => x).ToList();
Console.WriteLine();
Console.WriteLine($"  (7주차 시험용 DB: 기기 {hDevices.Length}대 · 연결 기록 {totalRows}줄 · 연결 중 {made.Count(m => m.Open)}줄)");


// ═════════════════════════════════════════════════════════
Section("E. 페이지 나누기 · 정렬 (7주차)");
// ═════════════════════════════════════════════════════════

PagedResult<ConnectionHistoryItem>? p = hist.GetConnectionHistory(new ConnectionQuery());
Check("조건 없이 조회 (기본값 적용) → 200", hist.LastResult, StoreResult.Ok, hist);
Expect($"  기본값 page=1 · size=10 · sort=connectedAt,desc · status=all — 실제 page={p?.Page} size={p?.Size} sort={p?.Sort} status={p?.Status}",
    p is { Page: 1, Size: 10, Sort: "connectedAt,desc", Status: "all" });
Expect($"  전체 {totalRows}건 · {(totalRows + 9) / 10}페이지, 이번 페이지 10줄, 다음 페이지 있음, 이전 페이지 없음",
    p != null && p.TotalElements == totalRows && p.TotalPages == (totalRows + 9) / 10 && p.Items.Count == 10 && p.HasNext && !p.HasPrevious);
Expect("  최신순 (기록 번호 큰 것부터 10줄)", p != null && p.Items.Select(x => x.LogId).SequenceEqual(newestFirst.Take(10)));

ShowPage("GET /connections?page=1&size=3", hist.GetConnectionHistory(new ConnectionQuery { Page = 1, Size = 3 }));

p = hist.GetConnectionHistory(new ConnectionQuery { Page = 3 });
Expect($"page=3 → 마지막 페이지 {totalRows - 20}줄, 다음 페이지 없음, 이전 페이지 있음",
    p != null && p.Items.Count == totalRows - 20 && !p.HasNext && p.HasPrevious);

p = hist.GetConnectionHistory(new ConnectionQuery { Page = 4 });
Check("page=4 (데이터가 없는 페이지) → 200, 빈 목록", hist.LastResult, StoreResult.Ok, hist);
Expect($"  빈 목록이지만 전체 개수는 그대로 {totalRows}, 다음 페이지 없음", p != null && p.Items.Count == 0 && p.TotalElements == totalRows && !p.HasNext);

var seen = new List<long>();
for (int pg = 1; pg <= 3; pg++)
    seen.AddRange(hist.GetConnectionHistory(new ConnectionQuery { Page = pg })!.Items.Select(x => x.LogId));
Expect($"1~3 페이지를 이어 붙이면 {totalRows}줄이 빠짐·겹침 없이 한 번씩", seen.Count == totalRows && seen.Distinct().Count() == totalRows);

p = hist.GetConnectionHistory(new ConnectionQuery { Size = 100 });
Expect($"size=100 → 한 페이지에 {totalRows}줄 전부, 1페이지", p != null && p.Items.Count == totalRows && p.TotalPages == 1);

p = hist.GetConnectionHistory(new ConnectionQuery { Sort = "messages,asc", Size = 100 });
Expect("sort=messages,asc → 메시지 수 작은 것부터", p != null && IsSorted(p.Items.Select(x => (double)x.MessageCount), ascending: true));

p = hist.GetConnectionHistory(new ConnectionQuery { Sort = "latency,desc", Size = 100 });
Expect("sort=latency,desc → 평균 지연 큰 것부터, 못 잰 기록(null)은 맨 뒤",
    p != null && IsSorted(p.Items.Where(x => x.AvgLatencyMs != null).Select(x => x.AvgLatencyMs!.Value), ascending: false)
              && p.Items.SkipWhile(x => x.AvgLatencyMs != null).All(x => x.AvgLatencyMs == null));

p = hist.GetConnectionHistory(new ConnectionQuery { Sort = "device,asc", Size = 100 });
Expect("sort=device,asc → 기기 이름순", p != null && p.Items.Select(x => x.DeviceName).SequenceEqual(p.Items.Select(x => x.DeviceName).OrderBy(x => x, StringComparer.Ordinal)));

p = hist.GetConnectionHistory(new ConnectionQuery { Sort = "MESSAGES" });
Expect($"sort=MESSAGES (대문자, 방향 생략) → messages,desc 로 처리 — 실제 {p?.Sort}", p?.Sort == "messages,desc");


// ═════════════════════════════════════════════════════════
Section("F. 상태 필터 · 키워드 검색 (7주차)");
// ═════════════════════════════════════════════════════════

int openCount = made.Count(m => m.Open);
p = hist.GetConnectionHistory(new ConnectionQuery { Status = "open" });
Expect($"status=open → 연결 중 {openCount}건만", p != null && p.TotalElements == openCount && p.Items.All(x => x.IsOpen));

p = hist.GetConnectionHistory(new ConnectionQuery { Status = "closed", Size = 100 });
Expect($"status=closed → 종료 {totalRows - openCount}건만", p != null && p.TotalElements == totalRows - openCount && p.Items.All(x => !x.IsOpen));

p = hist.GetConnectionHistory(new ConnectionQuery { Status = "Open" });
Expect("status=Open (대소문자 섞임) → open 으로 처리", p?.Status == "open" && p.TotalElements == openCount);

int laptop = made.Count(m => m.Device == "노트북");
p = hist.GetConnectionHistory(new ConnectionQuery { Keyword = "노트북", Size = 100 });
Expect($"keyword=노트북 → 기기 이름에 '노트북' 이 든 {laptop}건", p != null && p.TotalElements == laptop && p.Items.All(x => x.DeviceName == "노트북"));

int byPeer = made.Count(m => m.Reason == "상대가 끊음");
p = hist.GetConnectionHistory(new ConnectionQuery { Keyword = "상대", Size = 100 });
Expect($"keyword=상대 → 끊긴 이유에 '상대' 가 든 {byPeer}건", p != null && p.TotalElements == byPeer && p.Items.All(x => x.DisconnectReason == "상대가 끊음"));

int both = made.Count(m => !m.Open && m.Device == "노트북");
p = hist.GetConnectionHistory(new ConnectionQuery { Status = "closed", Keyword = "노트북", Size = 3 });
Expect($"status=closed AND keyword=노트북 → 두 조건 모두 맞는 {both}건 (종료이면서 노트북)",
    p != null && p.TotalElements == both && p.Items.All(x => !x.IsOpen && x.DeviceName == "노트북"));
ShowPage("GET /connections?status=closed&keyword=노트북&size=3", p);

p = hist.GetConnectionHistory(new ConnectionQuery { Status = "open", Keyword = "상대" });
Expect("status=open AND keyword=상대 → 0건 (연결 중인 기록은 끊긴 이유가 없음), 페이지 0", p is { TotalElements: 0, TotalPages: 0, HasNext: false });

p = hist.GetConnectionHistory(new ConnectionQuery { Keyword = "_" });
Expect("keyword=_ → '1층_PC' 1건만 (_ 를 '아무 글자 하나' 가 아니라 글자 그대로 찾음)", p is { TotalElements: 1 } && p.Items[0].DeviceName == "1층_PC");

p = hist.GetConnectionHistory(new ConnectionQuery { Keyword = "%" });
Expect("keyword=% → 0건 (% 를 '아무 글자' 가 아니라 글자 그대로 찾음)", p is { TotalElements: 0 });

p = hist.GetConnectionHistory(new ConnectionQuery { Keyword = "' OR 1=1 --" });
Check("keyword=' OR 1=1 -- (SQL 인젝션 시도) → 200, 글자로만 찾아서 0건", hist.LastResult, StoreResult.Ok, hist);
Expect("  기록이 새어 나오지 않음 (0건)", p is { TotalElements: 0 });

int livingClosed = made.Count(m => m.Device == "거실PC" && !m.Open);
p = hist.GetConnectionHistory(new ConnectionQuery { DeviceId = hDevices[0].DeviceId, Status = "closed", Sort = "latency,asc", Size = 100 });
Expect($"기기(거실PC) + status=closed + sort=latency,asc → {livingClosed}건, 지연 작은 것부터",
    p != null && p.TotalElements == livingClosed && p.Items.All(x => x.DeviceName == "거실PC")
              && IsSorted(p.Items.Where(x => x.AvgLatencyMs != null).Select(x => x.AvgLatencyMs!.Value), ascending: true));


// ═════════════════════════════════════════════════════════
Section("G. 잘못된 조건 → 400 / 비워 두면 기본값 (7주차)");
// ═════════════════════════════════════════════════════════

BadQuery("page=0", new ConnectionQuery { Page = 0 });
BadQuery("page=-1", new ConnectionQuery { Page = -1 });
BadQuery("page=100001 (범위 밖)", new ConnectionQuery { Page = 100_001 });
BadQuery("size=0", new ConnectionQuery { Size = 0 });
BadQuery("size=101 (한 번에 너무 많이)", new ConnectionQuery { Size = 101 });
BadQuery("sort=password,desc (없는 정렬 기준)", new ConnectionQuery { Sort = "password,desc" });
BadQuery("sort=connectedAt,sideways (없는 방향)", new ConnectionQuery { Sort = "connectedAt,sideways" });
BadQuery("sort=connectedAt;DROP TABLE device (SQL 인젝션 시도)", new ConnectionQuery { Sort = "connectedAt;DROP TABLE device" });
BadQuery("status=CANCELLED (없는 상태)", new ConnectionQuery { Status = "CANCELLED" });
BadQuery("keyword 51자 (너무 김)", new ConnectionQuery { Keyword = new string('가', 51) });
BadQuery("deviceId=빈 ID", new ConnectionQuery { DeviceId = Guid.Empty });

p = hist.GetConnectionHistory(new ConnectionQuery { Sort = "", Status = "  ", Keyword = "   " });
Check("sort=\"\" · status=\"  \" · keyword=\"   \" (빈 값) → 400 이 아니라 기본값으로 200", hist.LastResult, StoreResult.Ok, hist);
Expect($"  적용된 값: sort={p?.Sort} status={p?.Status} keyword={(p?.Keyword ?? "없음")} · {p?.TotalElements}건",
    p is { Sort: "connectedAt,desc", Status: "all", Keyword: null } && p.TotalElements == totalRows);

Expect("DB 표(테이블)가 그대로 있음 (인젝션 시도 뒤 기기 4대)", hist.GetAll().Count == hDevices.Length);

broken.GetConnectionHistory(new ConnectionQuery());
Check("DB 를 열 수 없음 → 500 (프로그램이 멈추지 않음)", broken.LastResult, StoreResult.Error, broken);


// ═════════════════════════════════════════════════════════
Section("H. 대량 데이터 (연결 기록 10만 줄) (7주차)");
// ═════════════════════════════════════════════════════════

var bdb = new KvmDatabase(Path.Combine(folder, "bulk.db"));
bdb.EnsureCreated();
var bulk = new DeviceRepository(bdb);
TrustedDevice[] bDevices = Enumerable.Range(0, 10).Select(i => NewDevice(null, i == 3 ? "노트북" : $"PC{i:D2}", $"10.0.0.{i + 1}")).ToArray();
foreach (TrustedDevice d in bDevices)
    bulk.Create(d);

const int bulkRows = 100_000;
var swBulk = Stopwatch.StartNew();
using (SqliteConnection conn = bdb.Open())
using (SqliteTransaction tx = conn.BeginTransaction())
using (SqliteCommand cmd = conn.CreateCommand())
{
    // 시험 데이터는 빨리 넣으려고 SQL 로 바로 넣는다. (조회는 아래에서 저장소 코드로)
    cmd.Transaction = tx;
    cmd.CommandText = @"
INSERT INTO connection_log (device_id, connected_at, disconnected_at, avg_latency_ms, message_count, disconnect_reason)
VALUES ($dev, $at, CASE WHEN $open THEN NULL ELSE $at END, $lat, $msg, CASE WHEN $open THEN NULL ELSE $reason END);";
    var pDev = cmd.Parameters.Add("$dev", SqliteType.Text);
    var pAt = cmd.Parameters.Add("$at", SqliteType.Text);
    var pOpen = cmd.Parameters.Add("$open", SqliteType.Integer);
    var pLat = cmd.Parameters.Add("$lat", SqliteType.Real);
    var pMsg = cmd.Parameters.Add("$msg", SqliteType.Integer);
    var pReason = cmd.Parameters.Add("$reason", SqliteType.Text);
    DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    for (int i = 0; i < bulkRows; i++)
    {
        pDev.Value = bDevices[i % 10].DeviceId.ToString("D");
        pAt.Value = t0.AddMinutes(i).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        pOpen.Value = i % 1000 == 0 ? 1 : 0;
        pLat.Value = 1.0 + i % 50 * 0.1;
        pMsg.Value = i % 500;
        pReason.Value = reasons[i % 4];
        cmd.ExecuteNonQuery();
    }

    tx.Commit();
}
Console.WriteLine($"  연결 기록 {bulkRows:N0}줄 넣음 ({swBulk.ElapsedMilliseconds}ms)");

TimedPage("첫 페이지   page=1&size=20", new ConnectionQuery { Page = 1, Size = 20 }, 20);
TimedPage("마지막 페이지 page=5000&size=20", new ConnectionQuery { Page = 5000, Size = 20 }, 20);
TimedPage("복합 조건   status=closed&keyword=노트북&sort=latency,desc", new ConnectionQuery { Status = "closed", Keyword = "노트북", Sort = "latency,desc" }, 10);
TimedPage("한도 최대   size=100", new ConnectionQuery { Size = 100 }, 100);

Console.WriteLine();
Console.WriteLine("  ── 최신순 첫 페이지의 실행 계획 (EXPLAIN QUERY PLAN) ──");
using (SqliteConnection conn = bdb.Open())
using (SqliteCommand cmd = conn.CreateCommand())
{
    cmd.CommandText = @"EXPLAIN QUERY PLAN
SELECT l.log_id FROM connection_log l JOIN device d ON d.device_id = l.device_id
 ORDER BY l.connected_at DESC, l.log_id DESC LIMIT 20 OFFSET 0;";
    using SqliteDataReader r = cmd.ExecuteReader();
    while (r.Read())
        Console.WriteLine("    " + r.GetString(3));
}


// ═════════════════════════════════════════════════════════
Console.WriteLine();
Console.WriteLine(new string('═', 60));
Console.WriteLine($"  결과: 통과 {passed} · 실패 {failed}");
Console.WriteLine(new string('═', 60));

// 시험용 DB 정리 (연결 풀이 파일을 잡고 있으면 못 지우므로 먼저 비운다)
SqliteConnection.ClearAllPools();
try { Directory.Delete(folder, recursive: true); } catch { /* 못 지워도 임시 폴더라 괜찮다 */ }

return failed;


// ─────────────────────────────────────────────────────────
//  도우미
// ─────────────────────────────────────────────────────────

StoreResult Start(Guid id)
{
    repo.StartConnection(id);
    return repo.LastResult;
}

// from: 결과 설명(LastMessage)을 읽을 저장소. 생략하면 시험용 DB 의 repo
void Check(string name, StoreResult actual, StoreResult expected, DeviceRepository? from = null)
{
    bool ok = actual == expected;
    if (ok) passed++; else failed++;
    Console.WriteLine($"  {(ok ? "✔" : "✘")} [{(int)actual}] {name}");
    Console.WriteLine($"        {(ok ? "" : $"기대 {(int)expected} — ")}{(from ?? repo).LastMessage}");
}

void Expect(string name, bool ok)
{
    if (ok) passed++; else failed++;
    Console.WriteLine($"  {(ok ? "✔" : "✘")} {name}");
}

void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('─', 60));
    Console.WriteLine("  " + title);
    Console.WriteLine(new string('─', 60));
}

// DB 에 실제로 반영됐는지 SQL 로 직접 읽어서 보여준다. (저장소 코드를 거치지 않고)
void ShowLogRow(string title, long logId)
{
    using SqliteConnection conn = db.Open();
    using SqliteCommand cmd = conn.CreateCommand();
    cmd.CommandText = @"
SELECT log_id, substr(device_id, 1, 8), connected_at, IFNULL(disconnected_at, 'NULL'),
       IFNULL(avg_latency_ms, 'NULL'), message_count, IFNULL(disconnect_reason, 'NULL')
  FROM connection_log WHERE log_id = $id;";
    cmd.Parameters.AddWithValue("$id", logId);
    using SqliteDataReader r = cmd.ExecuteReader();
    Console.WriteLine($"      ▷ {title}");
    if (r.Read())
        Console.WriteLine($"        log_id={r.GetValue(0)} device={r.GetValue(1)}… connected_at={r.GetValue(2)} " +
                          $"disconnected_at={r.GetValue(3)} avg_latency_ms={r.GetValue(4)} message_count={r.GetValue(5)} reason={r.GetValue(6)}");
    else
        Console.WriteLine("        (줄 없음)");
}

long CountLogs(Guid id)
{
    using SqliteConnection conn = db.Open();
    using SqliteCommand cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM connection_log WHERE device_id = $id;";
    cmd.Parameters.AddWithValue("$id", id.ToString("D"));
    return Convert.ToInt64(cmd.ExecuteScalar());
}

static TrustedDevice NewDevice(string? id, string name, string? ip) => new()
{
    DeviceId = id == null ? Guid.NewGuid() : Guid.Parse(id),
    DeviceName = name,
    LastIp = ip,
    IsTrusted = true,
    PairedAt = DateTime.UtcNow,
    LastSeenAt = DateTime.UtcNow,
};

static string ToJson(object? value) => JsonSerializer.Serialize(value, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),   // 한글을 \uXXXX 로 바꾸지 않게
    Converters = { new JsonStringEnumConverter() },           // 배치 방향을 숫자(1) 대신 이름(Right)으로
});

static string Indent(string text, string pad) =>
    string.Join(Environment.NewLine, text.Split('\n').Select(line => pad + line.TrimEnd('\r')));

// ── 7주차 도우미 ──────────────────────────────────────────

// 웹 API 응답처럼 요청 줄과 JSON 을 찍는다.
void ShowPage(string request, PagedResult<ConnectionHistoryItem>? page)
{
    Console.WriteLine();
    Console.WriteLine($"  ── {request} → {(int)hist.LastResult} ──");
    Console.WriteLine(Indent(ToJson(page), "  "));
}

// 잘못된 조건이면 400 이고 결과가 null 이어야 한다.
void BadQuery(string name, ConnectionQuery query)
{
    PagedResult<ConnectionHistoryItem>? result = hist.GetConnectionHistory(query);
    Check(name + " → 400", result == null ? hist.LastResult : StoreResult.Ok, StoreResult.Invalid, hist);
}

// 대량 데이터에서 한 페이지를 읽는 시간
void TimedPage(string name, ConnectionQuery query, int expectedItems)
{
    var sw = Stopwatch.StartNew();
    PagedResult<ConnectionHistoryItem>? page = bulk.GetConnectionHistory(query);
    long ms = sw.ElapsedMilliseconds;
    Expect($"{name} → {(int)bulk.LastResult}, {page?.Items.Count}줄 / 전체 {page?.TotalElements:N0}건, {ms}ms",
        page != null && page.Items.Count == expectedItems);
}

static bool IsSorted(IEnumerable<double> values, bool ascending)
{
    double? prev = null;
    foreach (double v in values)
    {
        if (prev.HasValue && (ascending ? v < prev.Value : v > prev.Value))
            return false;
        prev = v;
    }
    return true;
}

// 실행 계획을 보려고 DeviceRepository 의 통합 조회와 같은 SQL 을 둔다.
static string DetailSqlForPlan() => @"
SELECT d.device_id, l.position, IFNULL(c.connection_count, 0)
  FROM device d
  LEFT JOIN screen_layout l
         ON l.layout_id = (SELECT l2.layout_id FROM screen_layout l2
                            WHERE l2.device_id = d.device_id
                            ORDER BY l2.updated_at DESC, l2.layout_id DESC LIMIT 1)
  LEFT JOIN (SELECT device_id, COUNT(*) AS connection_count
               FROM connection_log GROUP BY device_id) c
         ON c.device_id = d.device_id;";

// 7주차 시험 데이터 한 줄 (기대값 계산용)
record HistRow(long Id, string Device, bool Open, string? Reason, double? Latency, long Messages);
