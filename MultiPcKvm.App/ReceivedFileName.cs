namespace MultiPcKvm.App
{
    // ─────────────────────────────────────────────────────────────
    //  받은 파일을 저장할 이름 정하기
    //
    //  파일 이름은 보낸 쪽 PC 가 알려준 것이다. 그대로 믿으면 위험하다.
    //    예) "..\..\Windows\System32\x.dll"  → 받은 파일 폴더 밖에 저장된다
    //        "C:\Users\me\시작프로그램\a.exe" → 원하는 곳에 파일을 심을 수 있다
    //        "a.txt:숨김"                      → NTFS 숨은 스트림에 저장된다
    //        "CON", "NUL"                      → Windows 가 장치로 여겨서 저장이 안 된다
    //  그래서 이름 부분만 남기고, Windows 가 이름에 못 쓰는 글자를 지운다.
    //
    //  같은 이름이 이미 있으면 덮어쓰지 않고 "이름 (1).확장자" 처럼 번호를 붙인다.
    //  (Windows 탐색기가 복사할 때 붙이는 방식과 같다)
    //
    //  Windows 나 파일을 직접 건드리지 않고 글자만 다루므로 Linux 에서도 시험할 수 있다.
    //  (못 쓰는 글자 목록도 Path.GetInvalidFileNameChars 를 쓰지 않고 Windows 기준으로 직접 적었다.
    //   그 함수는 실행하는 OS 기준이라 Linux 에서는 '/' 하나만 돌려준다)
    // ─────────────────────────────────────────────────────────────
    public static class ReceivedFileName
    {
        // 이름이 다 지워졌을 때 쓰는 이름
        public const string Fallback = "받은 파일";

        // 너무 긴 이름은 자른다. (Windows 경로 길이 260 제한 안에 폴더 경로까지 들어가도록)
        public const int MaxLength = 150;

        // Windows 파일 이름에 쓸 수 없는 글자 (0~31 제어 문자는 따로 거른다)
        private const string InvalidChars = "<>:\"/\\|?*";

        // Windows 가 장치 이름으로 쓰는 이름. 확장자를 붙여도("CON.txt") 저장이 안 된다.
        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        // 보낸 쪽이 준 이름 → 안전한 파일 이름 (폴더 없이 이름만)
        public static string Sanitize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Fallback;

            // 1) 폴더 부분을 버리고 마지막 이름만 남긴다. (\ 와 / 둘 다 구분자로 본다)
            int cut = name.LastIndexOfAny(new[] { '\\', '/' });
            string s = cut >= 0 ? name[(cut + 1)..] : name;

            // 2) 못 쓰는 글자와 제어 문자를 지운다. (':' 도 지워져서 숨은 스트림·드라이브 문자가 막힌다)
            var chars = s.Where(c => c >= 32 && !InvalidChars.Contains(c)).ToArray();
            s = new string(chars);

            // 3) 끝의 점·공백은 Windows 가 저장할 때 몰래 지운다. 미리 지워야 이름이 예상과 같다.
            //    앞의 공백도 지운다. ("." 이나 ".." 도 여기서 빈 글자가 된다)
            s = s.Trim().TrimEnd('.', ' ');

            if (s.Length == 0)
                return Fallback;

            // 4) 장치 이름이면 앞에 _ 를 붙인다. ("CON.txt" → "_CON.txt")
            //    점 앞부분으로 판단한다. Windows 가 그렇게 판단하기 때문이다.
            int dot = s.IndexOf('.');
            string stem = (dot >= 0 ? s[..dot] : s).TrimEnd(' ');

            if (Reserved.Contains(stem))
                s = "_" + s;

            // 5) 너무 길면 확장자는 살리고 앞부분을 자른다.
            if (s.Length > MaxLength)
            {
                string ext = Extension(s);

                // 확장자가 비정상적으로 길면(20자 넘음) 확장자로 보지 않고 통째로 자른다.
                if (ext.Length > 20)
                    ext = "";

                s = s[..(MaxLength - ext.Length)].TrimEnd('.', ' ') + ext;
            }

            return s;
        }

        // 겹치지 않는 이름 고르기.
        //
        // taken: 그 이름을 이미 쓰고 있으면 true. (WPF 는 "폴더에 파일이 있거나, 방금 다른 받기에 내준 이름")
        // 이름 (1), 이름 (2) ... 순서로 빈 이름을 찾는다.
        public static string MakeUnique(string safeName, Func<string, bool> taken)
        {
            if (!taken(safeName))
                return safeName;

            string ext = Extension(safeName);
            string stem = safeName[..(safeName.Length - ext.Length)];

            for (int i = 1; i < 100000; i++)
            {
                string candidate = $"{stem} ({i}){ext}";

                if (!taken(candidate))
                    return candidate;
            }

            // 10만 개가 다 찼을 리는 없지만, 그래도 끝나지 않는 일은 없게 한다.
            return $"{stem} ({Guid.NewGuid():N}){ext}";
        }

        // ".txt" 처럼 점을 포함한 확장자. 없으면 빈 글자.
        // ".gitignore" 처럼 점으로 시작하는 이름은 확장자가 없는 것으로 본다. (번호가 뒤에 붙도록)
        private static string Extension(string name)
        {
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name[dot..] : "";
        }
    }
}
