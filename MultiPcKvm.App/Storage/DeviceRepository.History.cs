using Microsoft.Data.Sqlite;

namespace MultiPcKvm.App.Storage
{
    // ─────────────────────────────────────────────────────────────
    //  연결 기록 목록 — 필터·검색·정렬·페이지 나누기 (7주차)
    //
    //    GetConnectionHistory(조건)  → 200 + 한 페이지 / 400 (조건이 잘못됨) / 500 (DB 오류)
    //
    //  연결 기록은 쓸수록 계속 쌓인다. 전부 한 번에 읽으면 기록이 많아질수록 느려지고 화면도 버벅인다.
    //  그래서 조건에 맞는 것만, 한 페이지(기본 10줄)씩 읽는다.
    //
    //  쿼리는 두 번이다.
    //    1) 개수 : SELECT COUNT(*)  ... WHERE 조건         → 전체 개수·페이지 수·다음 페이지 여부
    //    2) 목록 : SELECT 칸들 ... WHERE 조건 ORDER BY ... LIMIT 몇 줄 OFFSET 건너뛸 줄
    //  개수가 0 이거나 요청한 페이지가 마지막 페이지 뒤면 2) 는 하지 않는다. (빈 페이지, 200)
    //
    //  DeviceRepository 가 길어져서 파일만 나눴다. (partial = 한 클래스를 여러 파일에 나눠 쓰기)
    // ─────────────────────────────────────────────────────────────
    public partial class DeviceRepository
    {
        // 정렬 항목 → SQL. 사용자가 보낸 글자를 SQL 에 그대로 넣지 않고 이 표에서 골라 쓴다. (SQL 인젝션 방지)
        // ORDER BY 에는 $이름 자리를 쓸 수 없어서 글자를 이어 붙여야 하므로, 여기 있는 고정 글자만 허용한다.
        private static readonly Dictionary<string, string> SortColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            ["connectedAt"] = "l.connected_at",
            ["device"] = "d.device_name",
            ["latency"] = "l.avg_latency_ms",
            ["messages"] = "l.message_count",
        };

        // 조건에 맞는 연결 기록 한 페이지. 조건이 잘못됐거나(400) DB 오류(500)면 null.
        // null 일 때 이유는 LastResult · LastMessage 로 본다.
        public PagedResult<ConnectionHistoryItem>? GetConnectionHistory(ConnectionQuery query)
        {
            // 1) 조건 검사 (400). DB 에 묻기 전에 거른다.
            string? problem = query.Validate();

            if (problem != null)
            {
                Fail(StoreResult.Invalid, problem);
                return null;
            }

            PagedResult<ConnectionHistoryItem>? page = null;

            Guarded("연결 기록 목록 조회", () =>
            {
                int pageNo = query.EffectivePage;
                int size = query.EffectiveSize;
                bool descending = query.EffectiveDescending;
                string? keyword = query.EffectiveKeyword;

                // 2) WHERE 만들기 — 들어온 조건만 AND 로 잇는다. (동적 쿼리)
                //    값은 전부 $이름 자리로 넘긴다. 글자로 이어 붙이는 건 위에서 고른 고정 글자뿐이다.
                var where = new List<string>();

                switch (query.EffectiveStatus)
                {
                    case "open": where.Add("l.disconnected_at IS NULL"); break;
                    case "closed": where.Add("l.disconnected_at IS NOT NULL"); break;
                }

                if (query.DeviceId.HasValue)
                    where.Add("l.device_id = $deviceId");

                if (keyword != null)
                {
                    // 기기 이름 또는 끊긴 이유에 검색어가 들어 있는가.
                    // LIKE 에서 % 와 _ 는 "아무 글자" 라는 특별한 뜻이라, 검색어에 들어 있으면 글자 그대로 찾게 \ 를 붙인다.
                    // (안 그러면 "100%" 로 찾았을 때 "100" 으로 시작하는 모든 기록이 나온다)
                    where.Add(@"(d.device_name LIKE $keyword ESCAPE '\' OR IFNULL(l.disconnect_reason, '') LIKE $keyword ESCAPE '\')");
                }

                string whereSql = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);

                const string fromSql = @"
  FROM connection_log l
  JOIN device d ON d.device_id = l.device_id";

                void Bind(SqliteCommand command)
                {
                    if (query.DeviceId.HasValue)
                        command.Parameters.AddWithValue("$deviceId", ToDb(query.DeviceId.Value));

                    if (keyword != null)
                        command.Parameters.AddWithValue("$keyword", "%" + EscapeLike(keyword) + "%");
                }

                using SqliteConnection connection = _db.Open();

                // 3) 전체 개수
                long total;

                using (SqliteCommand count = connection.CreateCommand())
                {
                    count.CommandText = "SELECT COUNT(*)" + fromSql + whereSql + ";";
                    Bind(count);
                    total = Convert.ToInt64(count.ExecuteScalar());
                }

                int totalPages = (int)((total + size - 1) / size);   // 올림 나눗셈

                page = new PagedResult<ConnectionHistoryItem>
                {
                    Page = pageNo,
                    Size = size,
                    TotalElements = total,
                    TotalPages = totalPages,
                    HasNext = pageNo < totalPages,
                    HasPrevious = pageNo > 1,
                    Sort = query.EffectiveSort,
                    Status = query.EffectiveStatus,
                    Keyword = keyword,
                };

                // 4) 목록 (요청한 페이지가 비어 있으면 묻지 않는다)
                long offset = (long)(pageNo - 1) * size;

                if (offset < total)
                {
                    string column = SortColumns[query.EffectiveSortField];
                    string direction = descending ? "DESC" : "ASC";

                    // 평균 지연만 비어 있을(NULL) 수 있다. 못 잰 기록은 오름차순이든 내림차순이든 맨 뒤로.
                    // 다른 칸은 NOT NULL 이라 붙이지 않는다. (붙이면 색인으로 정렬을 못 할 수 있다)
                    string nulls = query.EffectiveSortField == "latency" ? " NULLS LAST" : "";

                    using SqliteCommand list = connection.CreateCommand();

                    // log_id 를 두 번째 기준으로 거는 이유: 같은 값이 여러 줄이면 순서가 매번 달라질 수 있고,
                    //   그러면 페이지를 넘길 때 같은 줄이 두 번 나오거나 빠진다. 번호로 순서를 고정한다.
                    list.CommandText = @"
SELECT l.log_id, l.device_id, l.connected_at, l.disconnected_at, l.avg_latency_ms,
       l.message_count, l.disconnect_reason, d.device_name" + fromSql + whereSql + $@"
 ORDER BY {column} {direction}{nulls}, l.log_id {direction}
 LIMIT $limit OFFSET $offset;";
                    Bind(list);
                    list.Parameters.AddWithValue("$limit", size);
                    list.Parameters.AddWithValue("$offset", offset);

                    using SqliteDataReader reader = list.ExecuteReader();

                    while (reader.Read())
                    {
                        // 0~6 칸은 6주차 ReadConnection 과 같은 순서라 그대로 쓰고, 기기 이름만 더한다.
                        ConnectionRecord record = ReadConnection(reader);

                        page.Items.Add(new ConnectionHistoryItem
                        {
                            LogId = record.LogId,
                            DeviceId = record.DeviceId,
                            ConnectedAt = record.ConnectedAt,
                            DisconnectedAt = record.DisconnectedAt,
                            AvgLatencyMs = record.AvgLatencyMs,
                            MessageCount = record.MessageCount,
                            DisconnectReason = record.DisconnectReason,
                            DeviceName = reader.GetString(7),
                        });
                    }
                }

                return Done(StoreResult.Ok,
                    $"{total}건 중 {page.Items.Count}건 ({pageNo}/{Math.Max(totalPages, 1)} 페이지)");
            });

            return LastResult == StoreResult.Ok ? page : null;
        }

        // LIKE 의 특별한 글자(\ % _)를 글자 그대로 찾게 바꾼다. (ESCAPE '\' 와 짝)
        private static string EscapeLike(string text) =>
            text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
    }
}
