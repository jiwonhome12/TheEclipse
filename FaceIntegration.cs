using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeatManagerApp
{
    /// <summary>얼굴인식 출결 시스템(별도 파이썬 프로그램, insightface-attendance)에 넘겨줄 학생 한 명.</summary>
    public class FaceRosterEntry
    {
        public string StudentId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        // 아래는 얼굴인식 쪽 등록 화면에 같이 보여주는 항목 (연락처는 얼굴인식의 '전화번호' 칸으로 들어간다)
        public string Phone { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Advisor { get; set; } = string.Empty;
    }

    /// <summary>얼굴인식 출결 시스템이 기록한 하루치 출근/퇴근 한 건.</summary>
    public class FaceAttendanceEntry
    {
        public string StudentId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;      // "yyyy-MM-dd"
        public string? CheckIn { get; set; }                  // "HH:mm:ss" — 없으면 null
        public string? CheckOut { get; set; }                 // "HH:mm:ss" — 없으면 null

        /// <summary>
        /// 출근~퇴근 인정 시간(초). 얼굴인식 프로그램의 계산 방식과 같다 —
        /// 출근/퇴근 중 하나라도 없거나, 00:00~06:00 사이에 찍은 기록이면 0.
        /// </summary>
        [JsonIgnore]
        public double WorkSeconds
        {
            get
            {
                if (!IsValidTime(CheckIn) || !IsValidTime(CheckOut)) return 0;
                if (!TimeSpan.TryParse(CheckIn, out var tIn) || !TimeSpan.TryParse(CheckOut, out var tOut)) return 0;
                return Math.Max(0, (tOut - tIn).TotalSeconds);
            }
        }

        [JsonIgnore]
        public string WorkTimeText => FaceIntegration.FormatDuration(WorkSeconds);

        [JsonIgnore]
        public DateTime? DateValue =>
            DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : null;

        private static bool IsValidTime(string? time) =>
            !string.IsNullOrEmpty(time) && string.CompareOrdinal(time, "06:00:00") >= 0;
    }

    /// <summary>
    /// 창공시스템(C#, 이 프로그램)과 얼굴인식 출결 시스템(Python, insightface-attendance) 사이의
    /// 파일 기반 연동. 두 프로그램이 어느 컴퓨터에서 실행되든(설치 경로가 서로 달라도) 항상 같은
    /// 위치를 가리키도록, 코드에 특정 컴퓨터 경로를 적어두지 않고 사용자 홈 폴더
    /// (%USERPROFILE%\SeatManagerApp\face-integration)를 공유 폴더로 쓴다.
    /// %APPDATA%가 아니라 %USERPROFILE%을 쓰는 이유: 일부 파이썬 배포판(예: Microsoft Store/
    /// Python Install Manager로 설치된 패키지형 파이썬)은 %APPDATA%·%LOCALAPPDATA%를 앱별로
    /// 가상화해서, 다른 프로그램이 그 경로에 쓴 파일이 파이썬 쪽에는 안 보이는 경우가 있다.
    /// 사용자 홈 폴더 바로 아래는 이런 가상화 대상이 아니라서 항상 그대로 공유된다.
    /// - 창공시스템 → roster.json 작성 → 얼굴인식이 읽어서 학생 등록 시 자동으로 채워 넣는다.
    /// - 얼굴인식 → attendance.json 작성 → 창공시스템이 읽어서 출결 현황을 보여준다.
    /// </summary>
    public static class FaceIntegration
    {
        public static string Directory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "SeatManagerApp", "face-integration");

        public static string RosterPath => Path.Combine(Directory, "roster.json");
        public static string AttendancePath => Path.Combine(Directory, "attendance.json");

        /// <summary>학생 마스터 목록을 얼굴인식 시스템이 읽을 수 있는 roster.json으로 내보낸다.</summary>
        public static void ExportRoster(IEnumerable<StudentInfo> masterStudents)
        {
            try
            {
                var roster = masterStudents
                    .Where(s => !string.IsNullOrWhiteSpace(s.StudentId))
                    .GroupBy(s => s.StudentId)
                    .Select(g => g.First())
                    .Select(s => new FaceRosterEntry
                    {
                        StudentId = s.StudentId,
                        Name = s.Name,
                        Department = s.Department,
                        Phone = s.Contact,
                        Email = s.Email,
                        Advisor = s.Advisor
                    })
                    .OrderBy(s => s.StudentId)
                    .ToList();

                System.IO.Directory.CreateDirectory(Directory);
                string json = JsonSerializer.Serialize(roster, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(RosterPath, json);
            }
            catch
            {
                // 내보내기 실패는 치명적이지 않다 — 다음 저장 때 다시 시도한다
            }
        }

        /// <summary>얼굴인식 시스템이 기록한 출결 데이터를 읽는다. 파일이 없거나 깨졌으면 빈 목록.</summary>
        public static List<FaceAttendanceEntry> LoadAttendance()
        {
            try
            {
                if (!File.Exists(AttendancePath)) return new List<FaceAttendanceEntry>();
                string json = File.ReadAllText(AttendancePath);
                var list = JsonSerializer.Deserialize<List<FaceAttendanceEntry>>(json);
                return list ?? new List<FaceAttendanceEntry>();
            }
            catch
            {
                return new List<FaceAttendanceEntry>();
            }
        }

        /// <summary>초를 'N시간 NN분' 형태로 바꾼다.</summary>
        public static string FormatDuration(double seconds)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{(int)span.TotalHours}시간 {span.Minutes:00}분";
        }

        // 월간 출석 기준 — 그 달의 모든 주(월~일)마다 근무 20시간 이상 + 출석 3일 이상
        public const double WeeklyRequiredHours = 20;
        public const int WeeklyRequiredDays = 3;

        /// <summary>
        /// 그 달에 걸친 주(월요일 시작) 목록. 1일이 들어 있는 주부터 말일이 들어 있는 주까지이며,
        /// 달 경계에 걸친 주는 앞뒤 달의 날짜까지 포함한 한 주 전체로 본다.
        /// </summary>
        public static List<(DateTime Start, DateTime End)> WeeksOfMonth(int year, int month)
        {
            var first = new DateTime(year, month, 1);
            var last = first.AddMonths(1).AddDays(-1);
            var weeks = new List<(DateTime, DateTime)>();
            for (var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7)); start <= last; start = start.AddDays(7))
                weeks.Add((start, start.AddDays(6)));
            return weeks;
        }

        /// <summary>출결 기록에 나온 학생마다 그 달의 주별 근무시간·출석일과 기준 충족 여부를 계산한다.</summary>
        public static List<MonthlyCriteriaRow> EvaluateMonth(IEnumerable<FaceAttendanceEntry> records, int year, int month)
        {
            var weeks = WeeksOfMonth(year, month);
            DateTime today = DateTime.Today;
            return records
                .Where(r => !string.IsNullOrWhiteSpace(r.StudentId))
                .GroupBy(r => r.StudentId)
                .Select(g =>
                {
                    var row = new MonthlyCriteriaRow
                    {
                        StudentId = g.Key,
                        Name = g.OrderByDescending(r => r.Date).First().Name
                    };
                    foreach (var (start, end) in weeks)
                    {
                        var inWeek = g.Where(r => r.DateValue != null && r.DateValue >= start && r.DateValue <= end).ToList();
                        row.Weeks.Add(new WeeklyCriteriaResult
                        {
                            Start = start,
                            End = end,
                            Seconds = inWeek.Sum(r => r.WorkSeconds),
                            // 출석 횟수는 출근을 찍은 날 수 (하루에 여러 번 출근해도 1회)
                            Days = inWeek.Where(r => !string.IsNullOrEmpty(r.CheckIn)).Select(r => r.Date).Distinct().Count(),
                            Finished = end < today
                        });
                    }
                    return row;
                })
                .OrderBy(r => r.StudentId)
                .ToList();
        }
    }

    public enum CriteriaState { Passed, Failed, InProgress }

    /// <summary>한 학생의 한 주 결과.</summary>
    public class WeeklyCriteriaResult
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public double Seconds { get; set; }
        public int Days { get; set; }
        /// <summary>주가 이미 끝났는지 — 끝나지 않은 주는 아직 못 채웠어도 실패로 보지 않는다.</summary>
        public bool Finished { get; set; }

        public bool Met => Seconds >= FaceIntegration.WeeklyRequiredHours * 3600 && Days >= FaceIntegration.WeeklyRequiredDays;

        public CriteriaState State => Met ? CriteriaState.Passed : Finished ? CriteriaState.Failed : CriteriaState.InProgress;

        public string Text
        {
            get
            {
                string mark = State switch { CriteriaState.Passed => "✅", CriteriaState.Failed => "❌", _ => "⏳" };
                return $"{mark} {Seconds / 3600:0.#}h · {Days}회";
            }
        }
    }

    /// <summary>한 학생의 한 달 결과 — 모든 주가 기준을 채워야 충족.</summary>
    public class MonthlyCriteriaRow
    {
        public string StudentId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<WeeklyCriteriaResult> Weeks { get; } = new List<WeeklyCriteriaResult>();

        public CriteriaState State =>
            Weeks.Any(w => w.State == CriteriaState.Failed) ? CriteriaState.Failed
            : Weeks.All(w => w.State == CriteriaState.Passed) ? CriteriaState.Passed
            : CriteriaState.InProgress;

        public int PassedWeeks => Weeks.Count(w => w.Met);

        public string ResultText => State switch
        {
            CriteriaState.Passed => "✅ 충족",
            CriteriaState.Failed => $"❌ 미충족 ({PassedWeeks}/{Weeks.Count}주)",
            _ => $"⏳ 진행 중 ({PassedWeeks}/{Weeks.Count}주)"
        };
    }
}
