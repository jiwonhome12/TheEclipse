using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SeatManagerApp
{
    public class AttendanceRecord
    {
        public string DateString { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // "출석", "결석", "지각" 
    }

    public class StudentInfo
    {
        public string Department { get; set; } = string.Empty;  // 소속
        public string Name { get; set; } = string.Empty;        // 이름
        public string Advisor { get; set; } = string.Empty;     // 지도교수
        public string StudentId { get; set; } = string.Empty;   // 학번
        public string Email { get; set; } = string.Empty;       // 이메일
        public string CabinetPeriod { get; set; } = string.Empty; // 대여 기간
        public List<AttendanceRecord> Attendance { get; set; } = new List<AttendanceRecord>();

        public StudentInfo Clone()
        {
            var cloned = new StudentInfo
            {
                Department = this.Department,
                Name = this.Name,
                Advisor = this.Advisor,
                StudentId = this.StudentId,
                Email = this.Email,
                CabinetPeriod = this.CabinetPeriod,
                Attendance = new List<AttendanceRecord>()
            };
            foreach (var att in this.Attendance)
            {
                cloned.Attendance.Add(new AttendanceRecord
                {
                    DateString = att.DateString,
                    Status = att.Status
                });
            }
            return cloned;
        }
    }

    public class Seat
    {
        public int SeatNumber { get; set; }
        public StudentInfo? Student { get; set; }
        public bool IsFixed { get; set; }
        public bool IsSelected { get; set; }
        public bool IsPillar { get; set; }

        // Helper properties for UI binding
        public string DisplayId => Student != null ? Student.StudentId : string.Empty;
        public string DisplayName => Student != null ? Student.Name : string.Empty;
        public bool HasStudent => Student != null && !IsPillar;
    }

    public class MemoItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Content { get; set; } = string.Empty;
    }

    public class RentalItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string StudentName { get; set; } = string.Empty;
        public string EquipmentType { get; set; } = string.Empty;
        public DateTime RentalDate { get; set; }
        public int RentalPeriodDays { get; set; } = 7; // Default 7 days rental
        public DateTime DueDate => RentalDate.AddDays(RentalPeriodDays);
        public bool IsReturned { get; set; } = false;
        public bool IsOverdue => DueDate < new DateTime(2026, 7, 16);
        public bool IsWarning => !IsReturned && !IsOverdue && (DueDate - new DateTime(2026, 7, 16)).TotalDays <= 7;
        public string StatusDisplay => IsReturned ? "반납 완료" : (IsOverdue ? "연체됨" : (IsWarning ? "반납 임박" : "대여중"));

        // Columns requested in screenshot
        public int Quantity { get; set; } = 1; // 대여수량
        public string ExtraItems { get; set; } = string.Empty; // 추가 대여물품(수량)
        public string Location { get; set; } = "DSU 창의공간"; // 사용장소
        public string Purpose { get; set; } = "개인 실습"; // 사용목적
        public string Remarks { get; set; } = ""; // 특이사항
        public string Department { get; set; } = "소프트웨어융합학과"; // 전공
        public string YearLevel { get; set; } = "3학년"; // 학년
        public string StudentId { get; set; } = "20261234"; // 학번
        public string Phone { get; set; } = "010-0000-0000"; // 연락처
        public string Advisor { get; set; } = "김동욱 교수"; // 지도교수
        public DateTime? ReturnDate { get; set; } // 반납일
        public string DisplayDeptYear => $"{Department} / {YearLevel}";
        public string DisplayPhoneAdvisor => $"{Phone} / {Advisor}";

        // ===== 개체 번호 표기 =====

        /// <summary>본체(VR/Quest 포함)인지.</summary>
        public bool IsMainframe =>
            EquipmentType.Contains("VR") || EquipmentType.Contains("Quest") || EquipmentType.Contains("본체");

        /// <summary>개체 번호("No. 17"). 번호를 못 찾으면 빈 문자열.</summary>
        public string UnitNumber => ExtractUnitNumber(EquipmentType);

        /// <summary>
        /// 대여품목 표시용. 개체 번호를 함께 보여준다.
        /// </summary>
        public string DisplayEquipment
        {
            get
            {
                string baseName = IsMainframe ? "본체" : "노트북";
                string no = UnitNumber;
                if (no.Length == 0) return baseName;
                return $"{baseName} ({no})";
            }
        }

        /// <summary>"(No .17)" "(No.17)" "17번" 처럼 흔들리는 표기를 모두 "No. 17"로 맞춘다.</summary>
        private static string ExtractUnitNumber(string equipmentType)
        {
            if (string.IsNullOrEmpty(equipmentType)) return string.Empty;

            var m = Regex.Match(equipmentType, @"no\s*\.?\s*(\d+)", RegexOptions.IgnoreCase);

            // 뒤에 말이 이어지는 "2번실"은 개체 번호가 아니다 (StripUnitNumber와 같은 기준)
            if (!m.Success) m = Regex.Match(equipmentType, @"(\d+)\s*번(?![가-힣])");

            return m.Success ? $"No. {m.Groups[1].Value}" : string.Empty;
        }

        /// <summary>품목명에서 번호 표기와 그 괄호를 걷어낸다. "노트북 HP OMEN (No. 17)" → "노트북 HP OMEN"</summary>
        private static string StripUnitNumber(string equipmentType)
        {
            if (string.IsNullOrEmpty(equipmentType)) return string.Empty;

            string s = Regex.Replace(equipmentType, @"\(\s*no\s*\.?\s*\d+\s*\)", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bno\s*\.?\s*\d+", "", RegexOptions.IgnoreCase);

            // 괄호 없는 "3번"도 걷어낸다. 단 "2번실"처럼 뒤에 말이 이어지면 품목명의 일부다.
            s = Regex.Replace(s, @"\(?\s*\d+\s*번\s*\)?(?![가-힣])", "");

            s = Regex.Replace(s, @"\(\s*\)", "");
            return Regex.Replace(s, @"\s{2,}", " ").Trim();
        }
    }

    /// <summary>
    /// 기자재 개체 하나에 생긴 문제(고장/수리/기타). 대여와 무관하게 관리자가 직접 등록하고,
    /// [처리 완료]를 누르면 목록에서 사라진다.
    /// </summary>
    public class EquipmentIssue
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public DateTime ReportDate { get; set; } = DateTime.Now;

        /// <summary>기자재 이름. 지금은 노트북만 등록한다.</summary>
        public string EquipmentName { get; set; } = "노트북";

        /// <summary>개체 번호 (1~20).</summary>
        public int UnitNumber { get; set; }

        /// <summary>"고장", "수리", "기타"</summary>
        public string IssueType { get; set; } = string.Empty;

        /// <summary>[기타]로 등록할 때 수기로 적어 넣은 내용.</summary>
        public string Detail { get; set; } = string.Empty;

        public string DisplayUnit => $"{EquipmentName} No. {UnitNumber}";

        /// <summary>목록에 보여줄 내용. 기타가 아니면 구분명을 그대로 쓴다.</summary>
        public string DisplayDetail => string.IsNullOrWhiteSpace(Detail) ? IssueType : Detail;
    }

    public class ApprovalRequest
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string StudentName { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string StudentId { get; set; } = string.Empty;
        public string Advisor { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string TabType { get; set; } = string.Empty; // "상상Lab" or "캐비닛" or "기자재" or "미분류"
        public string EquipmentType { get; set; } = string.Empty; // Added for equipment rentals
        public DateTime RequestDate { get; set; } = DateTime.Now;
        public string Status { get; set; } = "승인 대기"; // "승인 대기", "승인 완료", "반려"
        public string RentalPeriod { get; set; } = string.Empty; // 대여 기간

        // ===== 구글 폼 연동으로 채워지는 항목 =====
        public string Phone { get; set; } = string.Empty;    // 연락처
        public string PlanUrl { get; set; } = string.Empty;  // 학업/프로젝트 계획서 파일 링크
        public string Note { get; set; } = string.Empty;     // 기타 의견 / 특이사항

        // ===== 기자재 신청 폼에만 있는 항목 (승인 시 RentalItem으로 넘어간다) =====
        public string YearLevel { get; set; } = string.Empty; // 학년
        public string Location { get; set; } = string.Empty;  // 사용장소
        public string Purpose { get; set; } = string.Empty;   // 사용목적
        public DateTime? DueDate { get; set; }                // 반납예정일

        /// <summary>
        /// 구글 폼 응답 1건을 가리키는 고유 키(타임스탬프|학번|신청공간).
        /// 폴링할 때마다 시트 전체를 다시 읽으므로 이 값으로 중복 등록을 막는다.
        /// 수기로 만든 신청은 비어 있다.
        /// </summary>
        public string SourceKey { get; set; } = string.Empty;
    }
}
