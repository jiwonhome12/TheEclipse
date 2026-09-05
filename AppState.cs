using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SeatManagerApp
{
    /// <summary>
    /// 캐비닛 배정 한 칸(번호 + 학생 + 대여 기간). Dictionary&lt;int, (StudentInfo, string)&gt;는
    /// 튜플이라 그대로 직렬화할 수 없어서 저장용으로 풀어 놓은 형태.
    /// </summary>
    public class CabinetAllocationEntry
    {
        public int CabinetNum { get; set; }
        public StudentInfo? Student { get; set; }
        public string Period { get; set; } = string.Empty;
    }

    /// <summary>
    /// 좌석·시즌 일정을 뺀 나머지 앱 데이터(학생 마스터, 승인 신청서, 대여, 캐비닛, 메모 등).
    /// %APPDATA%\SeatManagerApp\appstate.json 에 저장한다.
    /// 이 파일이 없으면(처음 실행) 전부 빈 상태로 시작한다.
    /// </summary>
    public class AppState
    {
        public List<StudentInfo> MasterStudents { get; set; } = new List<StudentInfo>();
        public List<ApprovalRequest> Approvals { get; set; } = new List<ApprovalRequest>();
        public List<ApprovalRequest> ApprovalHistory { get; set; } = new List<ApprovalRequest>();

        /// <summary>
        /// 이미 앱으로 가져온 구글 폼 응답의 SourceKey. 이게 비어서 시작하면 폴링할 때마다
        /// 이미 처리한 신청서를 다시 새 신청서로 불러오게 된다 — 그래서 반드시 저장해야 한다.
        /// </summary>
        public List<string> ImportedSourceKeys { get; set; } = new List<string>();

        public List<RentalItem> Rentals { get; set; } = new List<RentalItem>();
        public List<RentalItem> RentalHistory { get; set; } = new List<RentalItem>();
        public List<EquipmentIssue> EquipmentIssues { get; set; } = new List<EquipmentIssue>();
        public List<CabinetAllocationEntry> CabinetAllocations { get; set; } = new List<CabinetAllocationEntry>();
        public List<MemoItem> Memos { get; set; } = new List<MemoItem>();

        private static string ConfigPath => Path.Combine(AppConfig.ConfigDirectory, "appstate.json");

        public static AppState Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var state = JsonSerializer.Deserialize<AppState>(json);
                    if (state != null) return state;
                }
            }
            catch
            {
                // 파일이 깨졌으면 빈 상태로 시작한다 (기존 파일은 그대로 두어 나중에 확인할 수 있게 한다)
            }
            return new AppState();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppConfig.ConfigDirectory);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch
            {
                // 저장 실패는 치명적이지 않다 — 다음 종료 때 다시 시도한다
            }
        }
    }
}
