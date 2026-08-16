using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;

namespace SeatManagerApp
{
    /// <summary>구글 폼 응답 시트를 한 번 조회한 결과</summary>
    public class FormSyncResult
    {
        public List<ApprovalRequest> NewRequests { get; } = new List<ApprovalRequest>();
        public int TotalRows { get; set; }
        public int DuplicateCount { get; set; }

        /// <summary>"원하는 공간" 값이 상상Lab/캐비닛/기자재 중 무엇인지 판별하지 못한 원본 문자열</summary>
        public List<string> UnmappedSpaces { get; } = new List<string>();

        /// <summary>헤더에서 찾지 못한 항목 (예: "학번")</summary>
        public List<string> MissingColumns { get; } = new List<string>();

        /// <summary>보조 시트(기자재 전용 시트) 조회 실패처럼 전체를 중단시키지는 않는 문제</summary>
        public List<string> Warnings { get; } = new List<string>();

        public string? Error { get; set; }
        public bool IsSuccess => Error == null;
    }

    /// <summary>
    /// 구글 폼 응답이 쌓이는 스프레드시트를 읽어 <see cref="ApprovalRequest"/> 목록으로 변환한다.
    /// 폼 질문 문구는 학기마다 바뀌므로 컬럼 위치나 정확한 문자열이 아니라
    /// 정규화한 헤더의 키워드로 컬럼을 찾는다.
    /// </summary>
    public class GoogleFormsService
    {
        private string _keyPath;
        private SheetsService? _service;

        public string SpreadsheetId { get; set; }

        /// <summary>
        /// 기자재 전용 응답 시트 ID. 설정의 "기자재용 구글 폼 URL"에서 뽑아낸다.
        /// 비어 있거나 메인 시트와 같으면 기자재 신청도 메인 시트에서만 읽는다.
        /// </summary>
        public string EquipmentSpreadsheetId { get; set; } = string.Empty;

        /// <summary>
        /// 캐비닛 전용 응답 시트 ID. 설정의 "캐비닛용 구글 폼 URL"에서 뽑아낸다.
        /// 비어 있거나 메인 시트와 같으면 캐비닛 신청도 메인 시트에서만 읽는다.
        /// </summary>
        public string CabinetSpreadsheetId { get; set; } = string.Empty;

        /// <summary>키 경로가 바뀌면 캐시된 인증 클라이언트를 버리고 다시 만든다.</summary>
        public string KeyPath
        {
            get => _keyPath;
            set
            {
                if (_keyPath == value) return;
                _keyPath = value;
                _service = null;
                ServiceAccountEmail = string.Empty;
            }
        }

        /// <summary>서비스 계정 이메일 (시트를 이 주소에 공유해야 읽을 수 있다)</summary>
        public string ServiceAccountEmail { get; private set; } = string.Empty;

        public GoogleFormsService(string keyPath, string spreadsheetId)
        {
            _keyPath = keyPath;
            SpreadsheetId = spreadsheetId;
        }

        public bool KeyFileExists => File.Exists(_keyPath);

        // ================= 스프레드시트 주소 해석 =================

        /// <summary>
        /// 스프레드시트 주소나 ID 문자열에서 시트 ID만 뽑아낸다.
        /// 폼 주소(/forms/)·게시용 주소(/d/e/)처럼 API로 읽을 수 없는 값이면 빈 문자열을 돌려준다.
        /// </summary>
        public static string ExtractSpreadsheetId(string? urlOrId)
        {
            string s = (urlOrId ?? string.Empty).Trim();
            if (s.Length == 0) return string.Empty;

            var m = Regex.Match(s, @"/spreadsheets/d/([A-Za-z0-9_-]{20,})");
            if (m.Success) return m.Groups[1].Value;

            // 주소가 아니라 ID를 그대로 붙여넣은 경우
            if (s.IndexOf('/') < 0 && Regex.IsMatch(s, @"^[A-Za-z0-9_-]{20,}$")) return s;

            return string.Empty;
        }

        /// <summary>응답 시트가 아니라 구글 폼 주소(단축 주소 포함)를 넣었는지 판별 (안내 문구용)</summary>
        public static bool IsFormUrl(string? urlOrId)
        {
            string s = urlOrId ?? string.Empty;
            return s.IndexOf("/forms/", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("forms.gle/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private SheetsService GetService()
        {
            if (_service != null) return _service;

            var saCred = CredentialFactory.FromFile<ServiceAccountCredential>(_keyPath);
            ServiceAccountEmail = saCred.Id ?? string.Empty;

            var cred = saCred.ToGoogleCredential()
                             .CreateScoped(SheetsService.Scope.SpreadsheetsReadonly);

            _service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = cred,
                ApplicationName = "SeatManagerApp"
            });
            return _service;
        }

        /// <param name="isAlreadyKnown">이미 처리한 신청인지 판별 (중복 방지). 인자는 SourceKey.</param>
        public async Task<FormSyncResult> FetchAsync(Func<string, bool> isAlreadyKnown, CancellationToken ct = default)
        {
            var result = new FormSyncResult();

            if (string.IsNullOrWhiteSpace(SpreadsheetId))
            {
                result.Error = "스프레드시트 ID가 설정되지 않았습니다. [설정] 탭에서 입력해주세요.";
                return result;
            }
            if (!KeyFileExists)
            {
                result.Error = $"서비스 계정 키 파일을 찾을 수 없습니다.\n{_keyPath}";
                return result;
            }

            await FetchSheetAsync(result, SpreadsheetId.Trim(), null, isAlreadyKnown, ct);
            if (!result.IsSuccess) return result;

            // 구분 전용 응답 시트를 따로 지정했으면 이어서 읽는다.
            // 이 시트들의 행은 "원하는 공간" 질문이 없으므로 전부 해당 구분의 신청으로 본다.
            var fetched = new HashSet<string>(StringComparer.Ordinal) { SpreadsheetId.Trim() };
            await MergeDedicatedSheetAsync(result, EquipmentSpreadsheetId, "기자재", fetched, isAlreadyKnown, ct);
            await MergeDedicatedSheetAsync(result, CabinetSpreadsheetId, "캐비닛", fetched, isAlreadyKnown, ct);

            return result;
        }

        /// <summary>
        /// 구분 전용 시트를 읽어 메인 결과에 합친다.
        /// 비어 있거나 이미 읽은 시트면 건너뛴다 (같은 시트를 두 번 읽으면 같은 행이 두 번 등록된다).
        /// 전용 시트만 실패한 경우는 전체를 중단시키지 않고 경고로 남긴다.
        /// </summary>
        private async Task MergeDedicatedSheetAsync(
            FormSyncResult target,
            string? spreadsheetId,
            string tabType,
            HashSet<string> fetched,
            Func<string, bool> isAlreadyKnown,
            CancellationToken ct)
        {
            string id = (spreadsheetId ?? string.Empty).Trim();
            if (id.Length == 0 || !fetched.Add(id)) return;

            var sheet = new FormSyncResult();
            await FetchSheetAsync(sheet, id, tabType, isAlreadyKnown, ct);

            if (!sheet.IsSuccess)
            {
                target.Warnings.Add($"{tabType} 시트를 읽지 못했습니다 — {sheet.Error}");
                return;
            }

            target.NewRequests.AddRange(sheet.NewRequests);
            target.TotalRows += sheet.TotalRows;
            target.DuplicateCount += sheet.DuplicateCount;

            foreach (string missing in sheet.MissingColumns)
                target.MissingColumns.Add($"({tabType} 시트) {missing}");
        }

        /// <param name="forcedTabType">
        /// 지정하면 "원하는 공간" 컬럼을 보지 않고 모든 행을 이 구분으로 등록한다 (기자재 전용 시트용).
        /// </param>
        private async Task FetchSheetAsync(
            FormSyncResult result,
            string spreadsheetId,
            string? forcedTabType,
            Func<string, bool> isAlreadyKnown,
            CancellationToken ct)
        {
            try
            {
                var service = GetService();

                // 첫 번째 탭 이름을 알아낸다 (폼 응답 시트 이름은 언어/설정에 따라 다르다)
                var meta = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync(ct);
                if (meta.Sheets == null || meta.Sheets.Count == 0)
                {
                    result.Error = "스프레드시트에 시트가 없습니다.";
                    return;
                }
                string tabName = meta.Sheets[0].Properties.Title;

                var resp = await service.Spreadsheets.Values
                    .Get(spreadsheetId, $"'{tabName}'!A:Z")
                    .ExecuteAsync(ct);

                var rows = resp.Values;
                if (rows == null || rows.Count == 0)
                {
                    result.Error = "시트가 비어 있습니다.";
                    return;
                }

                var header = rows[0];
                var map = new ColumnMap(header, isMainForm: forcedTabType == null);
                result.MissingColumns.AddRange(map.Missing);

                // 학번이 없으면 신청자를 특정할 수 없다 — 진행해도 쓰레기 데이터만 생기므로 중단
                if (map.StudentId < 0)
                {
                    result.Error = "시트 헤더에서 '학번' 컬럼을 찾지 못했습니다.\n" +
                                   "구글 폼 질문 문구를 확인해주세요.";
                    return;
                }

                // 메인 시트는 구분이 섞여 있어 이름까지 있어야 하지만,
                // 기자재 폼처럼 이름 질문이 없는 시트도 있으므로 학번만으로도 받는다.
                if (map.Name < 0 && forcedTabType == null)
                {
                    result.Error = "시트 헤더에서 '이름' 컬럼을 찾지 못했습니다.\n" +
                                   "구글 폼 질문 문구를 확인해주세요.";
                    return;
                }

                result.TotalRows = rows.Count - 1;

                for (int r = 1; r < rows.Count; r++)
                {
                    ct.ThrowIfCancellationRequested();
                    var row = rows[r];

                    string name = map.Get(row, map.Name);
                    string studentId = map.Get(row, map.StudentId);

                    // 완전히 빈 행은 건너뛴다
                    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(studentId))
                    {
                        result.TotalRows--;
                        continue;
                    }

                    string rawTimestamp = map.Get(row, map.Timestamp);
                    string equipmentType = map.Get(row, map.Equipment);

                    string rawSpace = forcedTabType == null ? map.Get(row, map.Space) : string.Empty;
                    string tabType = forcedTabType ?? MapTabType(rawSpace);

                    if (tabType.Length == 0 && rawSpace.Length > 0 &&
                        !result.UnmappedSpaces.Contains(rawSpace))
                    {
                        result.UnmappedSpaces.Add(rawSpace);
                    }

                    // 타임스탬프 문자열을 그대로 키에 쓴다 — 파싱에 실패해도 중복 판정은 안정적이다.
                    // 시트가 여럿이므로 시트 ID까지 넣어야 서로 다른 폼의 응답이 겹치지 않는다.
                    string discriminator = forcedTabType == null ? rawSpace : equipmentType;
                    string sourceKey = $"{spreadsheetId}|{rawTimestamp}|{studentId}|{discriminator}";

                    if (isAlreadyKnown(sourceKey))
                    {
                        result.DuplicateCount++;
                        continue;
                    }

                    result.NewRequests.Add(new ApprovalRequest
                    {
                        StudentName = name,
                        StudentId = studentId,
                        Department = map.Get(row, map.Department),
                        Advisor = map.Get(row, map.Advisor),
                        Email = map.Get(row, map.Email),
                        Phone = map.Get(row, map.Phone),
                        PlanUrl = map.Get(row, map.Plan),
                        Note = map.Get(row, map.Note),
                        EquipmentType = equipmentType,
                        YearLevel = map.Get(row, map.YearLevel),
                        Location = map.Get(row, map.Location),
                        Purpose = map.Get(row, map.Purpose),
                        DueDate = ParseOptionalDate(map.Get(row, map.DueDate)),
                        RentalPeriod = map.Get(row, map.RentalPeriod),
                        TabType = tabType.Length > 0 ? tabType : "미분류",
                        RequestDate = ParseTimestamp(rawTimestamp),
                        SourceKey = sourceKey,
                        Status = "승인 대기"
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Google.GoogleApiException gex)
            {
                result.Error = gex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "시트 접근이 거부되었습니다(403).\n" +
                      $"스프레드시트를 서비스 계정({ServiceAccountEmail})에 '뷰어'로 공유했는지, " +
                      "Google Cloud에서 Sheets API를 사용 설정했는지 확인해주세요."
                    : gex.HttpStatusCode == System.Net.HttpStatusCode.NotFound
                        ? "스프레드시트를 찾을 수 없습니다(404). ID를 확인해주세요."
                        : $"구글 API 오류: {gex.Message}";
            }
            catch (Exception ex)
            {
                result.Error = $"동기화 실패: {ex.Message}";
            }
        }

        // ================= 헤더 매핑 =================

        /// <summary>공백·줄바꿈·별표 등 장식 문자를 걷어내 비교 가능한 형태로 만든다</summary>
        internal static string Normalize(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (char.IsWhiteSpace(ch)) continue;
                if (ch == '*' || ch == ':' || ch == '~' || ch == ',' || ch == '.') continue;
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>"원하는 공간" 응답값을 앱의 TabType("상상Lab"/"캐비닛"/"기자재")으로 변환</summary>
        internal static string MapTabType(string? raw)
        {
            string v = Normalize(raw);
            if (v.Length == 0) return string.Empty;

            if (v.Contains("상상") || v.Contains("lab") || v.Contains("랩")) return "상상Lab";
            if (v.Contains("캐비닛") || v.Contains("캐비넷") || v.Contains("사물함") ||
                v.Contains("락커") || v.Contains("라커")) return "캐비닛";
            if (v.Contains("기자재") || v.Contains("장비") || v.Contains("대여")) return "기자재";

            return string.Empty;
        }

        /// <summary>구글 시트가 돌려주는 타임스탬프 문자열을 관대하게 파싱</summary>
        internal static DateTime ParseTimestamp(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return DateTime.Now;

            var ko = new CultureInfo("ko-KR");
            if (DateTime.TryParse(raw, ko, DateTimeStyles.None, out var dt)) return dt;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt;

            // 시트 설정에 따라 직렬 숫자(예: 45900.64)로 넘어오는 경우
            if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double serial))
            {
                try { return DateTime.FromOADate(serial); } catch { /* 범위 밖이면 무시 */ }
            }

            return DateTime.Now;
        }

        /// <summary>반납예정일처럼 비어 있을 수 있는 날짜 — 읽지 못하면 null(= 기본 대여기간 사용)</summary>
        internal static DateTime? ParseOptionalDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var ko = new CultureInfo("ko-KR");
            if (DateTime.TryParse(raw, ko, DateTimeStyles.None, out var dt)) return dt;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt;

            if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double serial))
            {
                try { return DateTime.FromOADate(serial); } catch { /* 범위 밖이면 무시 */ }
            }

            return null;
        }

        /// <summary>헤더 행에서 각 항목의 컬럼 인덱스를 키워드로 찾아둔다</summary>
        internal sealed class ColumnMap
        {
            private readonly IList<object> _header;

            public int Timestamp { get; }
            public int Space { get; }
            public int Department { get; }
            public int Name { get; }
            public int StudentId { get; }
            public int Phone { get; }
            public int Email { get; }
            public int Advisor { get; }
            public int Plan { get; }
            public int Note { get; }

            // 기자재 폼 전용 (메인 폼에는 없다)
            public int Equipment { get; }
            public int YearLevel { get; }
            public int Location { get; }
            public int Purpose { get; }
            public int DueDate { get; }
            public int RentalPeriod { get; }

            public List<string> Missing { get; } = new List<string>();

            /// <param name="isMainForm">
            /// 메인 신청 폼이면 true. 기자재 전용 폼에는 "원하는 공간"·이름·이메일 질문이 아예 없으므로,
            /// false를 주면 그 항목들을 '누락'으로 보고하지 않는다 (매번 뜨는 경고는 소음이 된다).
            /// </param>
            public ColumnMap(IList<object> header, bool isMainForm = true)
            {
                _header = header;

                Timestamp = Find("타임스탬프", "timestamp");
                Space = Find("공간을선택", "공간");
                Department = Find("소속", "전공");
                Name = Find("이름");
                StudentId = Find("학번");
                Phone = Find("연락처", "전화", "휴대");
                Email = Find("이메일", "메일");
                Advisor = Find("지도교수", "교수");
                Plan = Find("계획서");
                Note = Find("기타의견", "기타", "특이사항");

                Equipment = Find("대여품목", "기자재종류", "대여물품", "기자재", "장비", "품목");
                YearLevel = Find("학년");
                Location = Find("사용장소", "장소");
                Purpose = Find("사용목적", "목적");
                DueDate = Find("반납예정일", "반납예정", "반납");
                RentalPeriod = Find("대여기간", "신청기간", "사용기간", "기간");

                // "원하는 공간" 질문의 보기 안에 '기자재'가 들어 있으면 같은 컬럼이 잡힌다 — 떼어낸다
                if (Equipment >= 0 && Equipment == Space) Equipment = -1;

                Check(Department, "소속");
                Check(StudentId, "학번");
                Check(Advisor, "지도교수");

                if (isMainForm)
                {
                    Check(Space, "원하는 공간");
                    Check(Name, "이름");
                    Check(Email, "이메일");
                }
            }

            private void Check(int idx, string label)
            {
                if (idx < 0) Missing.Add(label);
            }

            private int Find(params string[] keywords)
            {
                // 키워드는 구체적인 것부터 넘어오므로 순서대로 훑는다
                foreach (string kw in keywords)
                {
                    for (int i = 0; i < _header.Count; i++)
                    {
                        if (Normalize(_header[i]?.ToString()).Contains(kw)) return i;
                    }
                }
                return -1;
            }

            public string Get(IList<object> row, int idx)
            {
                if (idx < 0 || idx >= row.Count) return string.Empty;
                return row[idx]?.ToString()?.Trim() ?? string.Empty;
            }
        }
    }
}
