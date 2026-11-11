using System.Diagnostics;
using MultiPcKvm.App.Storage;

namespace MultiPcKvm.Wpf
{
    // ─────────────────────────────────────────────────────────────
    //  연결 기록 목록 · 기기 상세를 가져오는 요청 모듈. (9주차, 기기 상세는 10주차)
    //
    //  웹 프론트엔드의 "API 요청 함수"(getPostList({ page, status, keyword })) 자리다.
    //  화면(ConnectionHistoryWindow)은 이것만 부르고, DB·SQL 은 모른다.
    //
    //    GetHistoryAsync(조건) → HistoryResponse (한 페이지 + 결과 코드 + 걸린 시간 + 조건 글자)
    //
    //  비동기(Task.Run)로 하는 이유:
    //    DB 를 읽는 동안 화면 스레드가 멈추면 창이 "응답 없음" 이 된다.
    //    다른 스레드에서 읽고, 끝나면 화면이 결과만 받아서 그린다.
    //
    //  요청마다 저장소(DeviceRepository)를 새로 만드는 이유:
    //    저장소의 LastResult · LastMessage 는 한 스레드에서만 쓴다는 전제다. (DeviceRepository 설명)
    //    메인 창이 쓰는 저장소를 다른 스레드에서 같이 쓰면 결과 설명이 섞일 수 있다.
    //    새로 만드는 비용은 거의 없다. (연결은 Microsoft.Data.Sqlite 가 모아 두었다가 다시 쓴다)
    //
    //  DB 위치는 KvmDatabase.DefaultPath 한 곳에서만 정한다. (여기에 경로를 적지 않는다)
    // ─────────────────────────────────────────────────────────────
    internal sealed class ConnectionHistoryClient
    {
        private readonly string _dbPath;

        // dbPath 를 비우면 프로그램이 쓰는 기본 DB(%AppData%\MultiPcKvm\kvm.db)
        public ConnectionHistoryClient(string? dbPath = null)
        {
            _dbPath = dbPath ?? KvmDatabase.DefaultPath;
        }

        public Task<HistoryResponse> GetHistoryAsync(ConnectionQuery query)
        {
            string queryString = ToQueryString(query);

            return Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();

                try
                {
                    var repository = new DeviceRepository(new KvmDatabase(_dbPath));
                    PagedResult<ConnectionHistoryItem>? page = repository.GetConnectionHistory(query);

                    var response = new HistoryResponse(page, repository.LastResult, repository.LastMessage,
                        watch.ElapsedMilliseconds, queryString);

                    // 웹의 "개발자 도구 Network 탭" 대신 진단 기록에 요청 한 줄을 남긴다.
                    // 조건 · 결과 코드 · 개수 · 걸린 시간을 보고 요청이 제대로 갔는지 확인할 수 있다.
                    DiagLog.Write($"연결 기록 조회 | {queryString} | {(int)response.Code} | " +
                                  $"{(page == null ? response.Message : $"{page.TotalElements}건 중 {page.Items.Count}건")} | {response.ElapsedMs}ms");

                    return response;
                }
                catch (Exception ex)
                {
                    // 저장소가 잡지 못한 예외(파일 권한 등)도 화면까지 올라가지 않게 500 으로 바꾼다.
                    DiagLog.Write($"연결 기록 조회 실패 | {queryString} | {ex.Message}");
                    return new HistoryResponse(null, StoreResult.Error, "예상하지 못한 오류: " + ex.Message, watch.ElapsedMilliseconds, queryString);
                }
            });
        }

        // 기기 한 대의 상세 (기기 정보 + 화면 배치 + 연결 통계 + 최근 기록). 10주차 기기 상세 창에서 쓴다.
        // 6주차 GetDeviceDetail 을 다른 스레드에서 부른다. 없는 기기면 Code = 404, Detail = null.
        public Task<DetailResponse> GetDeviceDetailAsync(Guid deviceId, int recent = 10)
        {
            return Task.Run(() =>
            {
                try
                {
                    var repository = new DeviceRepository(new KvmDatabase(_dbPath));
                    DeviceDetail? detail = repository.GetDeviceDetail(deviceId, recent);

                    DiagLog.Write($"기기 상세 조회 | {deviceId} | {(int)repository.LastResult} {repository.LastMessage}");
                    return new DetailResponse(detail, repository.LastResult, repository.LastMessage);
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"기기 상세 조회 실패 | {deviceId} | {ex.Message}");
                    return new DetailResponse(null, StoreResult.Error, "예상하지 못한 오류: " + ex.Message);
                }
            });
        }

        // 조건 → "page=1&size=10&sort=connectedAt,desc&status=all&keyword=노트북"
        // 웹의 URL 쿼리스트링에 해당한다. 창 아래쪽과 진단 기록에 그대로 보여준다.
        // (사람이 읽으려는 글자라 한글을 %XX 로 바꾸지 않는다)
        public static string ToQueryString(ConnectionQuery query)
        {
            var parts = new List<string>
            {
                $"page={query.EffectivePage}",
                $"size={query.EffectiveSize}",
                $"sort={query.EffectiveSort}",
                $"status={query.EffectiveStatus}",
            };

            if (query.EffectiveKeyword is string keyword)
                parts.Add($"keyword={keyword}");

            if (query.DeviceId is Guid id)
                parts.Add($"deviceId={id}");

            return string.Join("&", parts);
        }
    }

    // 요청 한 번의 결과.
    //   Page    : 성공(200)이면 한 페이지, 실패면 null
    //   Code    : 200 / 400 / 500
    //   Message : 실패 이유 (화면에 보여줄 글자)
    internal sealed record HistoryResponse(
        PagedResult<ConnectionHistoryItem>? Page,
        StoreResult Code,
        string Message,
        long ElapsedMs,
        string QueryString);

    // 기기 상세 요청 한 번의 결과. Detail 은 성공(200)일 때만 있다.
    internal sealed record DetailResponse(DeviceDetail? Detail, StoreResult Code, string Message);
}
