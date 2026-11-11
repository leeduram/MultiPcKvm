using System;
using System.IO;
using System.Threading;

namespace MultiPcKvm.Network.Discovery
{
    /// <summary>
    /// 이 PC를 구분하는 고정 DeviceId를 관리한다.
    ///
    /// 최초 실행:
    ///   Guid 생성 → %AppData%\MultiPcKvm\device-id.txt 저장
    ///
    /// 이후 실행:
    ///   파일에 저장된 Guid를 다시 사용
    ///
    /// LanDiscovery 밖에 따로 둔 이유:
    ///   "한 번 연결한 기기 저장 → 자동 연결" 기능에서도
    ///   LanDiscovery를 만들지 않고 내 ID를 알아야 하기 때문.
    /// </summary>
    public static class DeviceIdentity
    {
        /// <summary>
        /// DeviceId 저장 파일 경로
        /// </summary>
        public static string FilePath { get; } =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData),
                "MultiPcKvm",
                "device-id.txt");

        // 프로그램 안에서는 한 번만 읽는다.
        // 여러 스레드가 동시에 처음 접근해도 한 번만 실행된다.
        private static readonly Lazy<Guid> _deviceId =
            new Lazy<Guid>(
                LoadOrCreate,
                LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// 이 PC의 고정 DeviceId
        /// </summary>
        public static Guid DeviceId =>
            _deviceId.Value;

        /// <summary>
        /// 파일 저장/읽기에 실패했을 때의 오류 메시지.
        ///
        /// null 이 아니면 이번 실행에서만 쓰는 임시 ID를 사용 중이다.
        /// (다음 실행 때 ID가 바뀔 수 있다)
        /// </summary>
        public static string? LastError { get; private set; }


        // ---------------------------------------------------------
        // DeviceId 로드 / 생성
        // ---------------------------------------------------------
        private static Guid LoadOrCreate()
        {
            try
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(FilePath)!);


                // -------------------------------------------------
                // 기존 DeviceId 읽기
                // -------------------------------------------------

                if (TryRead(out Guid savedId))
                    return savedId;


                // -------------------------------------------------
                // 최초 실행 또는 손상된 파일 → 새로 만들어 저장
                //
                // 임시 파일에 먼저 쓰고 교체한다.
                // 쓰는 도중 꺼져도 반쪽짜리 파일이 남지 않는다.
                // -------------------------------------------------

                Guid newId =
                    Guid.NewGuid();

                string tempPath =
                    FilePath + "." + Environment.ProcessId + ".tmp";

                File.WriteAllText(
                    tempPath,
                    newId.ToString("D"));

                File.Move(
                    tempPath,
                    FilePath,
                    overwrite: true);


                // 거의 동시에 두 프로그램이 처음 실행됐다면
                // 마지막에 저장된 값을 같이 쓰도록 다시 읽는다.
                if (TryRead(out Guid finalId))
                    return finalId;

                return newId;
            }
            catch (Exception ex)
            {
                // 저장이 안 되더라도 프로그램은 계속 동작해야 한다.
                // 이번 실행 동안만 쓰는 임시 ID를 사용한다.
                LastError =
                    $"DeviceId 파일을 읽거나 저장하지 못했습니다 ({FilePath}): {ex.Message}";

                Console.WriteLine(
                    LastError);

                return Guid.NewGuid();
            }
        }


        // ---------------------------------------------------------
        // 파일에서 Guid 읽기
        // ---------------------------------------------------------
        private static bool TryRead(
            out Guid deviceId)
        {
            deviceId = Guid.Empty;

            if (!File.Exists(FilePath))
                return false;

            string text =
                File.ReadAllText(FilePath).Trim();

            // 빈 Guid(000...)는 잘못된 값으로 본다.
            return Guid.TryParse(
                       text,
                       out deviceId) &&
                   deviceId != Guid.Empty;
        }
    }
}
