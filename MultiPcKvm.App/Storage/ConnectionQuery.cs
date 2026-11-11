namespace MultiPcKvm.App.Storage
{
    // ─────────────────────────────────────────────────────────────
    //  연결 기록 목록 조회 조건. (7주차)
    //
    //  웹 API 의 쿼리 파라미터에 해당한다.
    //    GET /connections?page=1&size=10&sort=connectedAt,desc&status=closed&keyword=노트북
    //      ↔ new ConnectionQuery { Page = 1, Size = 10, Sort = "connectedAt,desc", Status = "closed", Keyword = "노트북" }
    //
    //  비워 두면(null) 기본값을 쓴다.
    //    Page 1 · Size 10 · Sort "connectedAt,desc"(최신순) · Status "all" · Keyword 없음
    //
    //  값이 있는데 잘못됐으면 Validate() 가 이유를 돌려준다. → 저장소는 400 으로 거절한다.
    //  (빈 글자 "" 는 "안 넣음" 과 같게 보고 기본값을 쓴다)
    //
    //  Sort·Status 를 enum 이 아니라 글자로 받는 이유:
    //    사용자가 고른 값이 그대로 들어오는 자리라, 틀린 값이 들어왔을 때 400 으로 알려줘야 한다.
    //    enum 이면 틀린 값을 아예 표현할 수 없어서 그 검사를 시험할 수 없다.
    // ─────────────────────────────────────────────────────────────
    public class ConnectionQuery
    {
        // ── 기본값과 한도 ─────────────────────────────────────
        public const int DefaultPage = 1;
        public const int DefaultSize = 10;
        public const int MaxSize = 100;          // 한 번에 너무 많이 가져가지 못하게
        public const int MaxPage = 100_000;      // OFFSET 이 터무니없이 커지지 않게 (범위 밖 값)
        public const int MaxKeywordLength = 50;
        public const string DefaultSort = "connectedAt,desc";

        // 정렬할 수 있는 항목. (글자 → 설명) SQL 로 바꾸는 건 저장소가 한다.
        public static readonly IReadOnlyList<string> SortFields =
            new[] { "connectedAt", "device", "latency", "messages" };

        public static readonly IReadOnlyList<string> StatusValues =
            new[] { "all", "open", "closed" };

        // ── 조건 ──────────────────────────────────────────────

        // 몇 번째 페이지인가. 1부터 센다.
        public int? Page { get; set; }

        // 한 페이지에 몇 줄인가. 1~100
        public int? Size { get; set; }

        // "항목,방향". 예) "connectedAt,desc", "latency,asc". 방향을 빼면 desc
        //   connectedAt : 연결 시각   device : 기기 이름   latency : 평균 지연   messages : 메시지 수
        public string? Sort { get; set; }

        // all(전부) · open(연결 중) · closed(종료). 6주차에 만든 상태값 기준
        public string? Status { get; set; }

        // 기기 이름 또는 끊긴 이유에 이 글자가 들어 있는 기록만. (대소문자 구분 없음)
        public string? Keyword { get; set; }

        // 이 기기의 기록만. (비우면 모든 기기)
        public Guid? DeviceId { get; set; }


        // ── 검사 ──────────────────────────────────────────────

        // 문제가 있으면 이유를, 없으면 null 을 돌려준다.
        public string? Validate()
        {
            if (Page is < 1)
                return $"page 는 1 이상이어야 합니다. (받은 값 {Page})";

            if (Page is > MaxPage)
                return $"page 가 너무 큽니다. ({MaxPage} 이하, 받은 값 {Page})";

            if (Size is < 1 or > MaxSize)
                return $"size 는 1~{MaxSize} 이어야 합니다. (받은 값 {Size})";

            if (!string.IsNullOrWhiteSpace(Sort))
            {
                string[] parts = Sort.Split(',');
                string field = parts[0].Trim();

                if (parts.Length > 2 || !SortFields.Contains(field, StringComparer.OrdinalIgnoreCase))
                    return $"정렬 기준을 알 수 없습니다: \"{Sort}\" (가능: {string.Join(", ", SortFields)})";

                if (parts.Length == 2)
                {
                    string direction = parts[1].Trim();

                    if (!direction.Equals("asc", StringComparison.OrdinalIgnoreCase) &&
                        !direction.Equals("desc", StringComparison.OrdinalIgnoreCase))
                        return $"정렬 방향은 asc 또는 desc 입니다: \"{parts[1]}\"";
                }
            }

            if (!string.IsNullOrWhiteSpace(Status) &&
                !StatusValues.Contains(Status.Trim(), StringComparer.OrdinalIgnoreCase))
                return $"상태 값을 알 수 없습니다: \"{Status}\" (가능: {string.Join(", ", StatusValues)})";

            if (Keyword != null && Keyword.Trim().Length > MaxKeywordLength)
                return $"검색어가 너무 깁니다. ({MaxKeywordLength}자 이하)";

            if (DeviceId == Guid.Empty)
                return "기기 ID 가 비어 있습니다.";

            return null;
        }


        // ── 기본값을 채운 실제 값 (Validate 를 통과한 뒤에만 쓴다) ──

        public int EffectivePage => Page ?? DefaultPage;

        public int EffectiveSize => Size ?? DefaultSize;

        // 정렬 항목 (대소문자를 SortFields 표기에 맞춘다)
        public string EffectiveSortField
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Sort))
                    return "connectedAt";

                string name = Sort.Split(',')[0].Trim();
                return SortFields.First(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool EffectiveDescending
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Sort))
                    return true;

                string[] parts = Sort.Split(',');
                return parts.Length < 2 || parts[1].Trim().Equals("desc", StringComparison.OrdinalIgnoreCase);
            }
        }

        public string EffectiveSort => $"{EffectiveSortField},{(EffectiveDescending ? "desc" : "asc")}";

        public string EffectiveStatus =>
            string.IsNullOrWhiteSpace(Status) ? "all" : Status.Trim().ToLowerInvariant();

        // 앞뒤 빈칸을 뗀 검색어. 비었으면 null (검색 안 함)
        public string? EffectiveKeyword =>
            string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
    }


    // ─────────────────────────────────────────────────────────────
    //  목록 조회의 공통 응답. (7주차)
    //
    //  웹 API 의 페이지 응답과 같은 모양이다.
    //    Items          이번 페이지의 줄들
    //    Page · Size    이번 페이지 번호와 크기
    //    TotalElements  조건에 맞는 전체 개수 (페이지와 상관없이)
    //    TotalPages     전체 페이지 수
    //    HasNext        다음 페이지가 있는가
    //    Sort · Status · Keyword  실제로 적용된 조건 (기본값이 들어갔는지 확인용)
    // ─────────────────────────────────────────────────────────────
    public class PagedResult<T>
    {
        public List<T> Items { get; set; } = new();

        public int Page { get; set; }

        public int Size { get; set; }

        public long TotalElements { get; set; }

        public int TotalPages { get; set; }

        public bool HasNext { get; set; }

        public bool HasPrevious { get; set; }

        public string Sort { get; set; } = "";

        public string Status { get; set; } = "";

        public string? Keyword { get; set; }
    }


    // 목록의 한 줄 = 연결 기록 + 기기 이름. (기록만으로는 어느 기기인지 사람이 알아보기 어렵다)
    public class ConnectionHistoryItem : ConnectionRecord
    {
        public string DeviceName { get; set; } = "";
    }
}
