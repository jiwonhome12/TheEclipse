using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SeatManagerApp
{
    /// <summary>얼굴인식 출결 시스템(별도 파이썬 프로그램, insightface-attendance)에 넘겨줄 학생 한 명.</summary>
    public class FaceRosterEntry
    {
        public string StudentId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
    }

    /// <summary>얼굴인식 출결 시스템이 기록한 하루치 출근/퇴근 한 건.</summary>
    public class FaceAttendanceEntry
    {
        public string StudentId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;      // "yyyy-MM-dd"
        public string? CheckIn { get; set; }                  // "HH:mm:ss" — 없으면 null
        public string? CheckOut { get; set; }                 // "HH:mm:ss" — 없으면 null
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
                        Department = s.Department
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
    }
}
