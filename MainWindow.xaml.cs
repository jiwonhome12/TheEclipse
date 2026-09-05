using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SeatManagerApp
{
    public partial class MainWindow : Window
    {
        /// <summary>보유 기자재 총 수량 (실제 보유량에 맞춰 조정)</summary>
        private const int TotalEquipmentCount = 24;

        /// <summary>캐비닛 총 개수. RenderCabinetGrid()가 그리는 칸 수와 반드시 같아야 한다.</summary>
        private const int TotalCabinetCount = 48;

        /// <summary>노트북 개체 수. 상태 등록 창에서 고를 수 있는 번호 범위(1~20)다.</summary>
        private const int TotalLaptopCount = 20;

        /// <summary>본체 개체 수. 상태 등록 창에서 고를 수 있는 번호 범위(1~10)다.</summary>
        private const int TotalMainframeCount = 10;

        // Master Database (구글 폼 승인 시 채워진다)
        private List<StudentInfo> _masterStudents = new List<StudentInfo>();
        
        // Year & Semester Seat Layout Cache
        private Dictionary<string, List<Seat>> _seatLayoutCache = new Dictionary<string, List<Seat>>();

        // Active display seats for current Year & Semester
        private List<Seat> _activeSeats = new List<Seat>();

        // ===== 학사 시즌 일정 =====
        private SeasonConfig _seasonConfig = new SeasonConfig();

        /// <summary>현재 날짜로 자동 판별된 (연도, 시즌). 어느 시즌에도 들지 않으면 null.</summary>
        private (int Year, string Season)? _currentSeason;

        /// <summary>설정 화면 시즌 표(편집용 사본).</summary>
        private ObservableCollection<SeasonPeriod> _seasonEditRows = new ObservableCollection<SeasonPeriod>();

        /// <summary>설정 화면에서 지금 편집 중인 연도(연도 카드로 선택). null이면 편집 영역을 숨긴다.</summary>
        private int? _selectedSeasonYear;

        // Equipment Rentals
        private ObservableCollection<RentalItem> _rentals = new ObservableCollection<RentalItem>();
        private Stack<RentalItem> _rentalUndoStack = new Stack<RentalItem>();
        private ObservableCollection<RentalItem> _rentalHistory = new ObservableCollection<RentalItem>();

        /// <summary>노트북 [기타] 버튼으로 등록한 고장/수리/기타 목록. [처리 완료]를 누르면 빠진다.</summary>
        private ObservableCollection<EquipmentIssue> _equipmentIssues = new ObservableCollection<EquipmentIssue>();

        // Cabinet & SangsangLab Google Form Approvals
        private ObservableCollection<ApprovalRequest> _approvals = new ObservableCollection<ApprovalRequest>();
        private ObservableCollection<ApprovalRequest> _approvalHistory = new ObservableCollection<ApprovalRequest>();

        // Memos
        private ObservableCollection<MemoItem> _memos = new ObservableCollection<MemoItem>();

        private Dictionary<int, (StudentInfo Student, string Period)> _cabinetAllocations = new Dictionary<int, (StudentInfo, string)>();
        private string _activeCabinetPeriod = string.Empty;
        private int _currentCabinetPage = 1;

        // App Modes
        private bool _isSeatFixMode = false;
        private bool _isSeatDeleteMode = false;
        private bool _isSeatEditMode = false;
        private bool _isCabinetFixed = false;

        /// <summary>좌석 수정 모드에서 드래그를 시작할 수도 있는 좌석(마우스를 누른 시점의 좌석).</summary>
        private Seat? _seatDragCandidate;
        private Point _seatDragStartPoint;

        /// <summary>대시보드 좌석에서 학번 가운데 4자리와 이름 가운데 한 글자를 '*'로 가릴지 여부.</summary>
        private bool _maskSeatInfo = false;

        // Current simulated date
        private DateTime _currentSimulatedDate;

        // Current editing seat for modal
        private Seat? _currentEditingSeat;
         
        // Edit mode for memo
        private MemoItem? _editingMemo;
        private string? _originalMemoContent;

        private bool _isModalEditing = false;
        private int _currentEditingCabinetNum = 0;
        private bool _isCabinetModalEditing = false;

        /// <summary>[좌석 옮기기]로 방금 옮긴 캐비닛. 어디로 갔는지 보이도록 다음 조작 전까지만 강조한다.</summary>
        private int _highlightedCabinetNum = 0;

        // ===== 구글 폼(시트) 연동 =====
        private AppConfig _config = new AppConfig();
        private GoogleFormsService? _formsService;
        private System.Windows.Threading.DispatcherTimer? _pollTimer;
        private bool _isSyncing = false;

        /// <summary>
        /// 이미 앱으로 가져온 폼 응답의 SourceKey. 폴링할 때마다 시트 전체를 다시 읽으므로
        /// 승인/반려로 목록에서 사라진 뒤에도 재등록되지 않도록 저장해 둔다(앱을 껐다 켜도 유지).
        /// </summary>
        private readonly HashSet<string> _importedSourceKeys = new HashSet<string>();

        public MainWindow()
        {
            _currentSimulatedDate = DateTime.Now;
            RentalItem.SimulatedDate = _currentSimulatedDate;
            InitializeComponent();

            // 학사 시즌 일정과 시즌별 좌석 데이터를 디스크에서 불러온다
            _seasonConfig = SeasonConfig.Load();
            SeedDefaultSeasonYearsIfEmpty();
            LoadSeatCache();

            // 학생 마스터·승인 신청서·대여·캐비닛·메모 등 나머지 데이터도 불러온다
            LoadAppState();

            this.Closing += MainWindow_Closing;

            // Update date display
            UpdateDateDisplay();

            // Start timer to update date every minute
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromMinutes(1);
            timer.Tick += (s, e) =>
            {
                _currentSimulatedDate = DateTime.Now;
                RentalItem.SimulatedDate = _currentSimulatedDate;
                UpdateDateDisplay();
                // 날짜가 바뀌면 현재 시즌을 다시 판별한다
                DetectAndApplyCurrentSeason();
                UpdateAlertBadges();
            };
            timer.Start();

            // Populate Year dropdown dynamically
            InitializeYearDropdown();

            // 데이터 바인딩만 연결한다. 실제 내용은 구글 폼 동기화와 사용자 입력으로만 채워진다.
            InitializeDataBindings();



            // Refresh UI Badges
            UpdateAlertBadges();

            // 현재 날짜가 포함되는 시즌을 자동으로 골라 대시보드에 반영한다
            DetectAndApplyCurrentSeason(initial: true);

            // 시즌이 정해진 뒤에 학생 목록을 정리한다 (시즌 표시가 없던 예전 데이터 보정 + 목록 갱신)
            MigrateStudentsWithoutSeason();
            RefreshMasterGrid();

            // Set default resolution selection programmatically after initialization
            ComboResolution.SelectedIndex = 0;

            // 구글 시트 연동 시작
            InitializeGoogleFormSync();
        }

        private void UpdateDateDisplay()
        {
            string dayOfWeek = _currentSimulatedDate.DayOfWeek switch
            {
                DayOfWeek.Monday => "월",
                DayOfWeek.Tuesday => "화",
                DayOfWeek.Wednesday => "수",
                DayOfWeek.Thursday => "목",
                DayOfWeek.Friday => "금",
                DayOfWeek.Saturday => "토",
                DayOfWeek.Sunday => "일",
                _ => ""
            };
            TxtCurrentDate.Text = $"{_currentSimulatedDate:yyyy-MM-dd} ({dayOfWeek})";
        }

        private void InitializeYearDropdown()
        {
            ComboSearchYear.Items.Clear();
            int currentYear = _currentSimulatedDate.Year;
            // Let's populate years from 2020 up to current year
            for (int y = 2020; y <= currentYear; y++)
            {
                ComboSearchYear.Items.Add(y.ToString());
            }
            ComboSearchYear.SelectedIndex = ComboSearchYear.Items.Count - 1; // Default to current year
        }

        /// <summary>
        /// 컬렉션과 화면을 연결하기만 한다. 초기 데이터는 넣지 않는다 —
        /// 학생/신청 정보는 구글 폼 동기화, 나머지는 사용자 입력으로만 생긴다.
        /// </summary>
        private void InitializeDataBindings()
        {
            RefreshMasterGrid();
            LstSangsangLabCards.ItemsSource = _approvals.Where(a => a.TabType == "상상Lab").ToList();
            GridCabinetApprovals.ItemsSource = _approvals.Where(a => a.TabType == "캐비닛").ToList();
            BindEquipmentRentals();
            GridEquipmentIssues.ItemsSource = _equipmentIssues;
            LstMemos.ItemsSource = _memos;
        }

        private void UpdateAlertBadges()
        {
            // SangsangLab approval count
            int sangsangLabCount = _approvals.Count(a => a.Status == "승인 대기" && a.TabType == "상상Lab");
            TxtSangsangLabCount.Text = $"{sangsangLabCount}건";
            if (CardSangsangLabAlert != null)
                CardSangsangLabAlert.Visibility = sangsangLabCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Cabinet approval count
            int cabinetCount = _approvals.Count(a => a.Status == "승인 대기" && a.TabType == "캐비닛");
            TxtCabinetCount.Text = $"{cabinetCount}건";
            if (CardCabinetAlert != null)
                CardCabinetAlert.Visibility = cabinetCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Equipment pending approval count
            int equipmentCount = _approvals.Count(a => a.Status == "승인 대기" && a.TabType == "기자재");
            TxtEquipmentCount.Text = $"{equipmentCount}건";
            if (CardEquipmentAlert != null)
                CardEquipmentAlert.Visibility = equipmentCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Sync other counts in tabs
            if (TxtEquipmentPendingCount != null)
                TxtEquipmentPendingCount.Text = $"{_approvals.Count(a => a.TabType == "기자재")}건";
            
            int mainframeRented = _rentals.Count(r => r.IsMainframe);
            int laptopRented = _rentals.Count(r => !r.IsMainframe);

            if (TxtAvailableMainframeCount != null) TxtAvailableMainframeCount.Text = $"{Math.Max(0, 10 - mainframeRented)}개";
            if (TxtRentedMainframeCount != null) TxtRentedMainframeCount.Text = $"{mainframeRented}개";
            if (TxtAvailableLaptopCount != null) TxtAvailableLaptopCount.Text = $"{Math.Max(0, 20 - laptopRented)}개";
            if (TxtRentedLaptopCount != null) TxtRentedLaptopCount.Text = $"{laptopRented}개";

            if (TxtCabinetPendingCount != null)
                TxtCabinetPendingCount.Text = $"{_approvals.Count(a => a.TabType == "캐비닛")}건";

            if (TxtRentedCabinetCount != null)
                TxtRentedCabinetCount.Text = $"{_cabinetAllocations.Count}개";

            if (TxtAvailableCabinetCount != null)
                TxtAvailableCabinetCount.Text = $"{Math.Max(0, TotalCabinetCount - _cabinetAllocations.Count)}개";

            if (TxtSangsangLabPendingCount != null)
                TxtSangsangLabPendingCount.Text = $"{_approvals.Count(a => a.TabType == "상상Lab")}건";
        }

        // ================= NAVIGATION =================
        private void ResetSidebarButtons()
        {
            BtnDashboard.Style = (Style)FindResource("SidebarBtn");
            BtnEquipment.Style = (Style)FindResource("SidebarBtn");
            BtnCabinet.Style = (Style)FindResource("SidebarBtn");
            BtnSangsangLab.Style = (Style)FindResource("SidebarBtn");
            BtnDataManage.Style = (Style)FindResource("SidebarBtn");
            BtnSettings.Style = (Style)FindResource("SidebarBtn");

            TabDashboard.Visibility = Visibility.Collapsed;
            TabEquipment.Visibility = Visibility.Collapsed;
            TabCabinet.Visibility = Visibility.Collapsed;
            TabSangsangLab.Visibility = Visibility.Collapsed;
            TabDataManage.Visibility = Visibility.Collapsed;
            TabSettings.Visibility = Visibility.Collapsed;
        }

        private void SwitchTab(Button btn, Grid tabGrid, string title)
        {
            ResetSidebarButtons();
            btn.Style = (Style)FindResource("SidebarBtnActive");
            tabGrid.Visibility = Visibility.Visible;
            TxtHeaderTitle.Text = title;

            // Only show Header alerts on Dashboard tab
            HeaderAlertArea.Visibility = (tabGrid == TabDashboard) ? Visibility.Visible : Visibility.Collapsed;

            // Refresh data-grids if needed
            if (tabGrid == TabEquipment)
            {
                GridEquipmentApprovals.ItemsSource = null;
                GridEquipmentApprovals.ItemsSource = _approvals.Where(a => a.TabType == "기자재").ToList();
 
                BindEquipmentRentals();
 
                int mainframeRented = _rentals.Count(r => r.IsMainframe);
                int laptopRented = _rentals.Count(r => !r.IsMainframe);

                if (TxtAvailableMainframeCount != null) TxtAvailableMainframeCount.Text = $"{Math.Max(0, 10 - mainframeRented)}개";
                if (TxtRentedMainframeCount != null) TxtRentedMainframeCount.Text = $"{mainframeRented}개";
                if (TxtAvailableLaptopCount != null) TxtAvailableLaptopCount.Text = $"{Math.Max(0, 20 - laptopRented)}개";
                if (TxtRentedLaptopCount != null) TxtRentedLaptopCount.Text = $"{laptopRented}개";
                TxtEquipmentPendingCount.Text = $"{_approvals.Count(a => a.TabType == "기자재")}건";
            }
            else if (tabGrid == TabCabinet)
            {
                GridCabinetApprovals.ItemsSource = null;
                GridCabinetApprovals.ItemsSource = _approvals.Where(a => a.TabType == "캐비닛").ToList();
                TxtCabinetPendingCount.Text = $"{_approvals.Count(a => a.TabType == "캐비닛")}건";

                RenderCabinetGrid();

                TxtRentedCabinetCount.Text = $"{_cabinetAllocations.Count}개";
                TxtAvailableCabinetCount.Text = $"{Math.Max(0, TotalCabinetCount - _cabinetAllocations.Count)}개";
            }
            else if (tabGrid == TabSangsangLab)
            {
                LstSangsangLabCards.ItemsSource = null;
                LstSangsangLabCards.ItemsSource = _approvals.Where(a => a.TabType == "상상Lab").ToList();
                TxtSangsangLabPendingCount.Text = $"{_approvals.Count(a => a.TabType == "상상Lab")}건";
            }
            else if (tabGrid == TabDataManage)
            {
                RefreshMasterGrid();
            }
            else if (tabGrid == TabSettings)
            {
                LoadSeasonSettingsUI();
            }

            UpdateAlertBadges();

            // 탭을 열 때 기간이 지난 데이터가 있으면 정리할지 물어본다
            if (tabGrid == TabDashboard) CheckExpiredItems("대시보드");
            else if (tabGrid == TabEquipment)
            {
                CheckExpiredItems("기자재");
                ShowEquipmentExpiryWarningDialog();
            }
            else if (tabGrid == TabCabinet) ShowCabinetExpiryWarningDialog();
        }

        private void BtnDashboard_Click(object sender, RoutedEventArgs e) => SwitchTab(BtnDashboard, TabDashboard, "대시보드");
        private void BtnEquipment_Click(object sender, RoutedEventArgs e) => SwitchTab(BtnEquipment, TabEquipment, "기자재 현황");
        private void BtnCabinet_Click(object sender, RoutedEventArgs e) => SwitchTab(BtnCabinet, TabCabinet, "캐비닛 현황");
        private void BtnSangsangLab_Click(object sender, RoutedEventArgs e) => SwitchTab(BtnSangsangLab, TabSangsangLab, "상상Lab 승인");
        private void BtnDataManage_Click(object sender, RoutedEventArgs e) => SwitchTab(BtnDataManage, TabDataManage, "데이터 관리");
        private void BtnSettings_Click(object sender, RoutedEventArgs e) => SwitchTab(BtnSettings, TabSettings, "설정");

        private void AlertSangsangLab_MouseDown(object sender, MouseButtonEventArgs e) => SwitchTab(BtnSangsangLab, TabSangsangLab, "상상Lab 승인");
        private void AlertCabinet_MouseDown(object sender, MouseButtonEventArgs e) => SwitchTab(BtnCabinet, TabCabinet, "캐비닛 현황");
        private void AlertEquipment_MouseDown(object sender, MouseButtonEventArgs e) => SwitchTab(BtnEquipment, TabEquipment, "기자재 현황");

        // ================= SEAT GRID CONSTRUCTOR =================
        private void LoadDashboardLayout()
        {
            string year = ComboSearchYear.SelectedItem as string ?? _currentSimulatedDate.Year.ToString();
            string semester = (ComboSearchSemester.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "1학기";
            string key = $"{year}_{semester}";

            if (!_seatLayoutCache.ContainsKey(key))
            {
                // Initialize default seating layout
                var seats = new List<Seat>();
                for (int i = 1; i <= 52; i++)
                {
                    seats.Add(new Seat { SeatNumber = i });
                }

                // 기둥(棟)은 좌석이 아니라 화면에 따로 그려지는 칸(SeatNum -1, RenderSeatGrid 참고)이다.
                // 22번은 그 옆에 있는 정상 좌석이므로 여기서 IsPillar를 세우면 안 된다.

                // 처음 조회하는 시즌은 완전히 빈 상태(전부 빈 좌석)로 시작한다.
                // 마스터 학생 목록에서 자동으로 채우면 다른 시즌 데이터가 그대로 옮겨온 것처럼 보이므로 채우지 않는다.
                // 좌석에 학생을 앉히는 것은 관리자가 직접(좌석 클릭·랜덤 배정·엑셀 불러오기 등으로) 해야 한다.

                _seatLayoutCache[key] = seats;
                SaveSeatCache();
            }

            _activeSeats = _seatLayoutCache[key];
            RenderSeatGrid();
            UpdateCurrentSeasonInfo();
        }

        // ================= 학사 시즌 일정 / 자동 판별 =================

        /// <summary>시즌 일정이 하나도 없으면 지난해·올해·내년을 기본값으로 만들어 둔다.</summary>
        private void SeedDefaultSeasonYearsIfEmpty()
        {
            if (_seasonConfig.Schedules.Count > 0) return;
            int cy = _currentSimulatedDate.Year;
            _seasonConfig.EnsureYear(cy - 1);
            _seasonConfig.EnsureYear(cy);
            _seasonConfig.EnsureYear(cy + 1);
            _seasonConfig.Save();
        }

        private static string SeatCachePath =>
            System.IO.Path.Combine(AppConfig.ConfigDirectory, "seats.json");

        /// <summary>시즌별 좌석 데이터를 디스크에서 불러온다. 키는 "{연도}_{시즌}".</summary>
        private void LoadSeatCache()
        {
            try
            {
                if (!System.IO.File.Exists(SeatCachePath)) return;
                string json = System.IO.File.ReadAllText(SeatCachePath);
                var data = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, List<Seat>>>(json);
                if (data != null) _seatLayoutCache = data;

                // 예전 버전에서 22번 좌석을 기둥으로 잘못 저장해 뒀다면 여기서 바로잡는다
                // (22번은 실제 좌석이고, 기둥은 화면에 따로 그려지는 칸이다).
                foreach (var seats in _seatLayoutCache.Values)
                {
                    var seat22 = seats.FirstOrDefault(s => s.SeatNumber == 22);
                    if (seat22 != null) seat22.IsPillar = false;
                }
            }
            catch
            {
                // 손상된 파일이면 빈 캐시로 시작한다
            }
        }

        /// <summary>시즌별 좌석 데이터를 디스크에 저장한다. 시즌마다 독립적으로 보관된다.</summary>
        private void SaveSeatCache()
        {
            try
            {
                System.IO.Directory.CreateDirectory(AppConfig.ConfigDirectory);
                string json = System.Text.Json.JsonSerializer.Serialize(
                    _seatLayoutCache,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
                System.IO.File.WriteAllText(SeatCachePath, json);
            }
            catch
            {
                // 저장 실패는 치명적이지 않다
            }
        }

        /// <summary>학생 마스터·승인 신청서·대여·캐비닛·메모 등을 디스크에서 불러온다. 처음 실행이면 아무것도 하지 않는다.</summary>
        private void LoadAppState()
        {
            var state = AppState.Load();

            _masterStudents.AddRange(state.MasterStudents);
            foreach (var a in state.Approvals) _approvals.Add(a);
            foreach (var a in state.ApprovalHistory) _approvalHistory.Add(a);
            _importedSourceKeys.UnionWith(state.ImportedSourceKeys);
            foreach (var r in state.Rentals) _rentals.Add(r);
            foreach (var r in state.RentalHistory) _rentalHistory.Add(r);
            foreach (var i in state.EquipmentIssues) _equipmentIssues.Add(i);
            foreach (var c in state.CabinetAllocations)
                _cabinetAllocations[c.CabinetNum] = (c.Student!, c.Period);
            foreach (var m in state.Memos) _memos.Add(m);
        }

        /// <summary>학생 마스터·승인 신청서·대여·캐비닛·메모 등을 디스크에 저장한다.</summary>
        private void SaveAppState()
        {
            var state = new AppState
            {
                MasterStudents = _masterStudents,
                Approvals = _approvals.ToList(),
                ApprovalHistory = _approvalHistory.ToList(),
                ImportedSourceKeys = _importedSourceKeys.ToList(),
                Rentals = _rentals.ToList(),
                RentalHistory = _rentalHistory.ToList(),
                EquipmentIssues = _equipmentIssues.ToList(),
                CabinetAllocations = _cabinetAllocations
                    .Select(kvp => new CabinetAllocationEntry
                    {
                        CabinetNum = kvp.Key,
                        Student = kvp.Value.Student,
                        Period = kvp.Value.Period
                    })
                    .ToList(),
                Memos = _memos.ToList()
            };
            state.Save();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveSeatCache();
            SaveAppState();
            _seasonConfig.Save();
        }

        /// <summary>
        /// 현재 날짜가 포함되는 시즌을 찾아 대시보드에 반영한다.
        /// 시즌이 바뀌었을 때만(또는 <paramref name="initial"/>일 때) 대시보드 좌석 데이터를 다시 로드한다.
        /// </summary>
        private void DetectAndApplyCurrentSeason(bool initial = false)
        {
            var resolved = _seasonConfig.ResolveSeason(_currentSimulatedDate);

            if (resolved == null)
            {
                // 어느 시즌에도 들지 않는다 — 콤보 선택은 그대로 두고 안내만 갱신한다
                if (initial) LoadDashboardLayout();
                else UpdateCurrentSeasonInfo();
                return;
            }

            bool changed = _currentSeason == null || !_currentSeason.Value.Equals(resolved.Value);
            if (initial || changed)
            {
                _currentSeason = resolved;
                SyncDashboardCombosToSeason();
                LoadDashboardLayout(); // 맞춰진 콤보를 읽어 해당 시즌 좌석 데이터를 띄운다
                RefreshMasterGrid();   // 데이터 관리 목록도 새 시즌 기준으로 다시 거른다
            }
        }

        /// <summary>대시보드의 연도/학기 콤보를 자동 판별된 시즌에 맞춘다.</summary>
        private void SyncDashboardCombosToSeason()
        {
            if (_currentSeason == null) return;

            string yearStr = _currentSeason.Value.Year.ToString();
            if (!ComboSearchYear.Items.Cast<object>().Any(it => (it as string) == yearStr))
                ComboSearchYear.Items.Add(yearStr);
            ComboSearchYear.SelectedItem = ComboSearchYear.Items.Cast<object>()
                .FirstOrDefault(it => (it as string) == yearStr);

            foreach (var obj in ComboSearchSemester.Items)
            {
                if (obj is ComboBoxItem cbi &&
                    (cbi.Content?.ToString() ?? "") == _currentSeason.Value.Season)
                {
                    ComboSearchSemester.SelectedItem = cbi;
                    break;
                }
            }
        }

        /// <summary>대시보드 배지와 설정 화면에 현재 시즌 정보를 표시한다.</summary>
        private void UpdateCurrentSeasonInfo()
        {
            string dateStr = _currentSimulatedDate.ToString("yyyy-MM-dd");

            if (TxtDashboardSeason != null)
            {
                TxtDashboardSeason.Text = _currentSeason != null
                    ? $"현재 시즌: {_currentSeason.Value.Year}년 {_currentSeason.Value.Season} (자동)"
                    : "현재 시즌: 설정된 시즌 없음";
            }

            if (TxtCurrentSeasonInfo != null)
            {
                TxtCurrentSeasonInfo.Text = _currentSeason != null
                    ? $"현재 시즌: {_currentSeason.Value.Year}년 {_currentSeason.Value.Season}  ·  기준 날짜 {dateStr}"
                    : $"오늘({dateStr})이 포함되는 시즌이 없습니다. 연도별 시작일·종료일을 확인해주세요.";
            }
        }

        // ---- 설정 화면: 시즌 일정 편집 ----

        private void LoadSeasonSettingsUI()
        {
            // 선택 연도 기본값: 현재 시즌 연도 → 없으면 올해 → 없으면 가장 최근 연도
            int cy = _currentSimulatedDate.Year;
            int target = _currentSeason?.Year ?? cy;
            if (!_seasonConfig.HasYear(target))
            {
                var years = _seasonConfig.Years.ToList();
                target = years.Contains(cy) ? cy : (years.Count > 0 ? years[years.Count - 1] : cy);
            }
            _selectedSeasonYear = _seasonConfig.HasYear(target) ? target : (int?)null;

            RebuildSeasonYearCards();
            LoadSeasonRowsForSelectedYear();
            UpdateCurrentSeasonInfo();
        }

        /// <summary>설정 탭의 연도 카드를 다시 그린다. 마지막에 [＋ 연도 추가] 카드를 붙인다.</summary>
        private void RebuildSeasonYearCards()
        {
            if (PanelSeasonYears == null) return;
            PanelSeasonYears.Children.Clear();

            // 최근 연도가 왼쪽 위로 오도록 내림차순
            foreach (int year in _seasonConfig.Years.OrderByDescending(y => y))
            {
                bool isSelected = _selectedSeasonYear == year;
                bool isCurrent = _currentSeason?.Year == year;

                var card = new Border
                {
                    Width = 150,
                    Height = 74,
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(0, 0, 14, 14),
                    Background = MakeBrush("#1E7A5F"),
                    BorderBrush = MakeBrush(isSelected ? "#F59E0B" : (isCurrent ? "#93C5FD" : "#0F5132")),
                    BorderThickness = new Thickness(isSelected || isCurrent ? 3 : 1),
                    Cursor = Cursors.Hand,
                    Tag = year
                };

                var stack = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                stack.Children.Add(new TextBlock
                {
                    Text = year.ToString(),
                    Foreground = Brushes.White,
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                if (isCurrent)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = "● 현재 시즌",
                        Foreground = MakeBrush("#DBEAFE"),
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 3, 0, 0)
                    });
                }
                card.Child = stack;
                card.MouseLeftButtonUp += SeasonYearCard_Click;
                PanelSeasonYears.Children.Add(card);
            }

            // [＋ 연도 추가] 카드
            var addCard = new Border
            {
                Width = 150,
                Height = 74,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 14, 14),
                Background = MakeBrush("#ECFDF5"),
                BorderBrush = MakeBrush("#10B981"),
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand
            };
            addCard.Child = new TextBlock
            {
                Text = "＋ 연도 추가",
                Foreground = MakeBrush("#047857"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            addCard.MouseLeftButtonUp += AddSeasonYearCard_Click;
            PanelSeasonYears.Children.Add(addCard);
        }

        private static SolidColorBrush MakeBrush(string hex) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        private void SeasonYearCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is int year)
            {
                _selectedSeasonYear = year;
                RebuildSeasonYearCards();
                LoadSeasonRowsForSelectedYear();
            }
        }

        private void LoadSeasonRowsForSelectedYear()
        {
            _seasonEditRows = new ObservableCollection<SeasonPeriod>();

            int? y = _selectedSeasonYear;
            if (y == null)
            {
                GridSeasonSchedule.ItemsSource = _seasonEditRows;
                if (PanelSeasonEditor != null) PanelSeasonEditor.Visibility = Visibility.Collapsed;
                return;
            }

            var sched = _seasonConfig.GetYear(y.Value);
            var seasons = sched != null
                ? SeasonConfig.NormalizeSeasons(y.Value, sched.Seasons)
                : SeasonConfig.DefaultSeasonsFor(y.Value);
            foreach (var p in seasons) _seasonEditRows.Add(p.Clone());

            GridSeasonSchedule.ItemsSource = _seasonEditRows;
            if (TxtSeasonEditorTitle != null) TxtSeasonEditorTitle.Text = $"{y}년 시즌 일정";
            if (PanelSeasonEditor != null) PanelSeasonEditor.Visibility = Visibility.Visible;
        }

        private void AddSeasonYearCard_Click(object sender, MouseButtonEventArgs e)
        {
            // 다음 해(가장 큰 연도 + 1)를 기본값으로 추가한다. 이미 있으면 그 다음 해로.
            var years = _seasonConfig.Years.ToList();
            int newYear = years.Count > 0 ? years[years.Count - 1] + 1 : _currentSimulatedDate.Year;
            while (_seasonConfig.HasYear(newYear)) newYear++;

            var r = MessageBox.Show(
                $"{newYear}년 시즌 일정을 기본값으로 추가하시겠습니까?\n" +
                "(1학기 3/1~6/30, 여름방학 7/1~8/31, 2학기 9/1~12/31, 겨울방학 익년 1/1~2월 말일)\n\n" +
                "추가한 뒤 각 시즌 날짜를 원하는 대로 수정하고 [💾 시즌 일정 저장]을 누르세요.",
                "연도 추가", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            _seasonConfig.EnsureYear(newYear); // 예시 기준 기본 일정으로 생성
            _seasonConfig.Save();

            _selectedSeasonYear = newYear;
            RebuildSeasonYearCards();
            LoadSeasonRowsForSelectedYear();
            DetectAndApplyCurrentSeason(initial: true);
        }

        private void BtnDeleteSeasonYear_Click(object sender, RoutedEventArgs e)
        {
            int? y = _selectedSeasonYear;
            if (y == null || !_seasonConfig.HasYear(y.Value))
            {
                MessageBox.Show("삭제할 연도 일정이 없습니다.", "연도 삭제",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var r = MessageBox.Show(
                $"{y}년 시즌 일정을 삭제하시겠습니까?\n(해당 시즌의 좌석 데이터 자체는 그대로 보관됩니다.)",
                "연도 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            _seasonConfig.RemoveYear(y.Value);
            _seasonConfig.Save();

            LoadSeasonSettingsUI();
            DetectAndApplyCurrentSeason(initial: true);

            MessageBox.Show($"{y}년 시즌 일정을 삭제했습니다.", "완료",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnSaveSeasonSchedule_Click(object sender, RoutedEventArgs e)
        {
            int? y = _selectedSeasonYear;
            if (y == null)
            {
                MessageBox.Show("연도 카드를 먼저 선택하세요.", "저장 실패",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            GridSeasonSchedule.CommitEdit(DataGridEditingUnit.Cell, true);
            GridSeasonSchedule.CommitEdit(DataGridEditingUnit.Row, true);

            foreach (var p in _seasonEditRows)
            {
                if (p.EndDate.Date < p.StartDate.Date)
                {
                    MessageBox.Show($"[{p.Name}] 종료일이 시작일보다 빠릅니다.", "저장 실패",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var ordered = _seasonEditRows.OrderBy(p => p.StartDate).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].StartDate.Date <= ordered[i - 1].EndDate.Date)
                {
                    var w = MessageBox.Show(
                        "시즌 기간이 서로 겹칩니다. 그래도 저장하시겠습니까?\n" +
                        "(겹치는 날짜는 먼저 시작하는 시즌으로 판별됩니다.)",
                        "겹침 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (w != MessageBoxResult.Yes) return;
                    break;
                }
            }

            var sched = _seasonConfig.EnsureYear(y.Value);
            sched.Seasons = SeasonConfig.NormalizeSeasons(y.Value, _seasonEditRows);
            _seasonConfig.Save();

            DetectAndApplyCurrentSeason(initial: true);
            RebuildSeasonYearCards();
            UpdateCurrentSeasonInfo();

            MessageBox.Show($"{y}년 시즌 일정을 저장했습니다.", "완료",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RenderSeatGrid()
        {
            SeatGridContainer.Children.Clear();
            SeatGridContainer.RowDefinitions.Clear();
            SeatGridContainer.ColumnDefinitions.Clear();

            // Columns definition
            // Col 0, 1, 2 (Group 1), Col 3 (Aisle), Col 4, 5, 6, 7 (Group 2)
            double[] colWidths = { 1.0, 1.0, 1.0, 0.4, 1.0, 1.0, 1.0, 1.0 };
            for (int i = 0; i < colWidths.Length; i++)
            {
                SeatGridContainer.ColumnDefinitions.Add(new ColumnDefinition 
                { 
                    Width = new GridLength(colWidths[i], GridUnitType.Star) 
                });
            }

            // Rows definition: 7 main rows + 2 bottom rows (gray background)
            for (int i = 0; i < 9; i++)
            {
                SeatGridContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
            }

            // Add seat UI elements
            // Let's create an explicit layout positioning array
            var positions = new List<(int SeatNum, int Row, int Col, int ColSpan, bool IsGray)>();

            // Row 0
            positions.Add((1, 0, 0, 1, false));
            positions.Add((2, 0, 1, 1, false));
            positions.Add((3, 0, 2, 1, false));
            positions.Add((4, 0, 4, 1, false));
            positions.Add((5, 0, 5, 1, false));
            positions.Add((6, 0, 6, 1, false));

            // Row 1
            positions.Add((7, 1, 0, 1, false));
            positions.Add((8, 1, 1, 1, false));
            positions.Add((9, 1, 2, 1, false));
            positions.Add((10, 1, 4, 1, false));
            positions.Add((11, 1, 5, 1, false));
            positions.Add((12, 1, 6, 1, false));

            // Row 2
            positions.Add((13, 2, 0, 1, false));
            positions.Add((14, 2, 1, 1, false));
            positions.Add((15, 2, 2, 1, false));
            positions.Add((16, 2, 4, 1, false));
            positions.Add((17, 2, 5, 1, false));
            positions.Add((18, 2, 6, 1, false));

            // Row 3
            positions.Add((19, 3, 0, 1, false));
            positions.Add((20, 3, 1, 1, false));
            positions.Add((21, 3, 2, 1, false));
            positions.Add((22, 3, 4, 1, false));
            positions.Add((23, 3, 5, 1, false));
            positions.Add((-1, 3, 6, 2, false)); // Pillar (기둥)

            // Row 4
            positions.Add((24, 4, 0, 1, false));
            positions.Add((25, 4, 1, 1, false));
            positions.Add((26, 4, 2, 1, false));
            positions.Add((27, 4, 4, 1, false));
            positions.Add((28, 4, 5, 1, false));

            // Row 5
            positions.Add((29, 5, 0, 1, false));
            positions.Add((30, 5, 1, 1, false));
            positions.Add((31, 5, 2, 1, false));
            positions.Add((32, 5, 4, 1, false));
            positions.Add((33, 5, 5, 1, false));
            positions.Add((34, 5, 6, 1, false));
            positions.Add((35, 5, 7, 1, false));

            // Row 6
            positions.Add((36, 6, 0, 1, false));
            positions.Add((37, 6, 1, 1, false));
            positions.Add((38, 6, 2, 1, false));
            positions.Add((39, 6, 4, 1, false));
            positions.Add((40, 6, 5, 1, false));
            positions.Add((41, 6, 6, 1, false));
            positions.Add((42, 6, 7, 1, false));

            // Row 7 (Bottom Row 1, Gray Background)
            positions.Add((43, 7, 0, 1, true));
            positions.Add((44, 7, 1, 1, true));
            positions.Add((45, 7, 4, 1, true));
            positions.Add((46, 7, 5, 1, true));
            positions.Add((47, 7, 6, 1, true));

            // Row 8 (Bottom Row 2, Gray Background)
            positions.Add((48, 8, 0, 1, true));
            positions.Add((49, 8, 1, 1, true));
            positions.Add((50, 8, 4, 1, true));
            positions.Add((51, 8, 5, 1, true));
            positions.Add((52, 8, 6, 1, true));

            foreach (var pos in positions)
            {
                if (pos.SeatNum == -1) // Pillar
                {
                    Border pillarBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(229, 230, 235)),
                        BorderThickness = new Thickness(1),
                        Margin = new Thickness(3),
                        CornerRadius = new CornerRadius(4)
                    };
                    TextBlock pText = new TextBlock
                    {
                        Text = "기둥",
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    pillarBorder.Child = pText;
                    Grid.SetRow(pillarBorder, pos.Row);
                    Grid.SetColumn(pillarBorder, pos.Col);
                    Grid.SetColumnSpan(pillarBorder, pos.ColSpan);
                    SeatGridContainer.Children.Add(pillarBorder);
                    continue;
                }

                Seat seat = _activeSeats[pos.SeatNum - 1];

                // Outer Card Border
                Border seatCard = new Border
                {
                    BorderThickness = new Thickness(1.5),
                    Margin = new Thickness(3),
                    CornerRadius = new CornerRadius(4),
                    Cursor = Cursors.Hand,
                    Tag = seat
                };
                ApplySeatCardDefaultStyle(seatCard, seat, pos.IsGray);

                // Grid inside Card
                Grid cardGrid = new Grid();
                cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
                cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });

                // Seat Number & Lock Status Label
                StackPanel topStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 2, 0, 0) };
                TextBlock numTxt = new TextBlock
                {
                    Text = pos.SeatNum.ToString(),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128))
                };
                topStack.Children.Add(numTxt);

                bool isFixed = seat.IsFixed || (seat.Student != null && seat.Student.Department.Contains("대학원"));
                if (isFixed)
                {
                    TextBlock lockTxt = new TextBlock
                    {
                        Text = " 🔒",
                        FontSize = 9,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    topStack.Children.Add(lockTxt);
                }
                Grid.SetRow(topStack, 0);
                cardGrid.Children.Add(topStack);

                // Student Details (StudentID & Name)
                if (seat.Student != null)
                {
                    StackPanel studentStack = new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };

                    TextBlock idTxt = new TextBlock
                    {
                        Text = _maskSeatInfo ? MaskStudentId(seat.Student.StudentId) : seat.Student.StudentId,
                        FontSize = 9,
                        Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    TextBlock nameTxt = new TextBlock
                    {
                        Text = _maskSeatInfo ? MaskName(seat.Student.Name) : seat.Student.Name,
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
                        HorizontalAlignment = HorizontalAlignment.Center
                    };

                    studentStack.Children.Add(idTxt);
                    studentStack.Children.Add(nameTxt);
                    Grid.SetRow(studentStack, 1);
                    cardGrid.Children.Add(studentStack);
                }

                seatCard.Child = cardGrid;
                seatCard.MouseDown += SeatCard_MouseDown;
                seatCard.PreviewMouseLeftButtonDown += SeatCard_DragCandidate;
                seatCard.PreviewMouseMove += SeatCard_DragMove;
                seatCard.AllowDrop = true;
                seatCard.DragEnter += SeatCard_DragEnter;
                seatCard.DragOver += SeatCard_DragOver;
                seatCard.DragLeave += SeatCard_DragLeave;
                seatCard.Drop += SeatCard_Drop;

                Grid.SetRow(seatCard, pos.Row);
                Grid.SetColumn(seatCard, pos.Col);
                Grid.SetColumnSpan(seatCard, pos.ColSpan);
                SeatGridContainer.Children.Add(seatCard);
            }
        }

        // ================= 좌석 정보 가리기 (학번 중간 4자리 / 이름 가운데 글자) =================
        private void BtnMaskSeatInfo_Click(object sender, RoutedEventArgs e)
        {
            _maskSeatInfo = !_maskSeatInfo;
            BtnMaskSeatInfo.Content = _maskSeatInfo ? "🔓 정보 표시" : "🔒 정보 가리기";
            RenderSeatGrid();
        }

        /// <summary>학번의 가운데 4자리를 '*'로 바꾼다. (예: 20221227 → 20****27) 8자리가 아니면 가운데 절반을 가린다.</summary>
        private static string MaskStudentId(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;

            if (id.Length == 8)
                return id.Substring(0, 2) + "****" + id.Substring(6);

            if (id.Length <= 2) return id;

            int hide = Math.Min(4, id.Length - 2);
            int start = (id.Length - hide) / 2;
            return id.Substring(0, start) + new string('*', hide) + id.Substring(start + hide);
        }

        /// <summary>이름의 가운데 한 글자를 '*'로 바꾼다. (예: 홍길동 → 홍*동, 이산 → 이*)</summary>
        private static string MaskName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 2) return name;

            int mid = name.Length / 2; // 2글자→뒷글자, 3글자→가운데, 4글자→세번째
            return name.Substring(0, mid) + "*" + name.Substring(mid + 1);
        }

        private void SeatCard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is Seat seat)
            {
                if (_isSeatFixMode)
                {
                    // Toggle selection for fixing
                    seat.IsSelected = !seat.IsSelected;
                    RenderSeatGrid();
                }
                else if (_isSeatDeleteMode)
                {
                    // Toggle selection for deletion
                    seat.IsSelected = !seat.IsSelected;
                    RenderSeatGrid();

                    // Show Delete Selected button if any are selected
                    bool anySelected = _activeSeats.Any(s => s.IsSelected);
                    BtnDeleteSelected.Visibility = anySelected ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (_isSeatEditMode)
                {
                    // 좌석 수정 모드에서는 클릭 자체로는 아무것도 하지 않는다 — 드래그로만 자리를 옮긴다.
                }
                else
                {
                    // Regular Mode: Show student details or add student
                    ShowStudentDetailsModal(seat);
                }
            }
        }

        // ================= 좌석 수정 모드 (드래그 앤 드롭으로 자리 이동/교체) =================
        private void BtnSeatEditMode_Click(object sender, RoutedEventArgs e)
        {
            if (_isSeatFixMode)
            {
                _isSeatFixMode = false;
                BtnSeatFixMode.Content = "좌석 고정 모드";
                BtnSeatFixMode.Background = Brushes.White;
                foreach (var s in _activeSeats) s.IsSelected = false;
            }
            if (_isSeatDeleteMode)
            {
                _isSeatDeleteMode = false;
                BtnDeleteSelected.Visibility = Visibility.Collapsed;
                BtnSeatDeleteMode.Content = "좌석 데이터 삭제";
            }

            _isSeatEditMode = !_isSeatEditMode;
            BtnSeatEditMode.Content = _isSeatEditMode ? "✅ 좌석 수정 종료" : "🔀 좌석 수정 모드";
            BtnSeatEditMode.Background = _isSeatEditMode
                ? new SolidColorBrush(Color.FromRgb(191, 219, 254)) // 하늘색으로 활성 표시
                : Brushes.White;

            _seatDragCandidate = null;
            RenderSeatGrid();
        }

        /// <summary>좌석 카드를 누른 시점 — 학생이 있는 좌석이면 드래그 시작 후보로 기록해 둔다.</summary>
        /// <summary>좌석 카드의 기본 배경/테두리를 seat 상태(선택·고정·회색줄)에 맞춰 다시 계산해 적용한다.</summary>
        private static void ApplySeatCardDefaultStyle(Border seatCard, Seat seat, bool isGray)
        {
            bool isFixed = seat.IsFixed || (seat.Student != null && seat.Student.Department.Contains("대학원"));

            seatCard.Background = isGray ? new SolidColorBrush(Color.FromRgb(209, 213, 219)) : Brushes.White;

            if (seat.IsSelected)
            {
                seatCard.BorderBrush = Brushes.Yellow; // Select mode highlight
                seatCard.Background = new SolidColorBrush(Color.FromRgb(254, 249, 195)); // Soft yellow
            }
            else if (isFixed)
            {
                seatCard.BorderBrush = new SolidColorBrush(Color.FromRgb(249, 115, 22)); // Orange border for fixed
            }
            else
            {
                seatCard.BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235));
            }
            seatCard.BorderThickness = new Thickness(1.5);
        }

        private void SeatCard_DragCandidate(object sender, MouseButtonEventArgs e)
        {
            if (!_isSeatEditMode) return;
            if (sender is Border border && border.Tag is Seat seat && seat.Student != null && !seat.IsPillar)
            {
                _seatDragCandidate = seat;
                _seatDragStartPoint = e.GetPosition(null);
            }
        }

        /// <summary>누른 채로 일정 거리 이상 움직이면 실제 드래그를 시작한다. 드래그 중엔 원래 좌석을 흐리게 표시해 "지금 옮기고 있다"는 걸 보여준다.</summary>
        private void SeatCard_DragMove(object sender, MouseEventArgs e)
        {
            if (!_isSeatEditMode || _seatDragCandidate == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _seatDragCandidate = null; return; }
            if (sender is not Border border) return;

            Point pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _seatDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _seatDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var seat = _seatDragCandidate;
            _seatDragCandidate = null; // 같은 드래그를 두 번 시작하지 않도록

            border.Opacity = 0.3; // 지금 들고 있는 좌석은 흐리게
            border.Cursor = Cursors.Hand;
            try
            {
                DragDrop.DoDragDrop(border, seat, DragDropEffects.Move);
            }
            finally
            {
                // 드롭 성공 시 RenderSeatGrid가 새 카드를 만들지만, 취소되거나 실패한 경우를 위해 원상 복구한다.
                border.Opacity = 1.0;
                ApplySeatCardDefaultStyle(border, seat, seat.SeatNumber >= 43);
            }
        }

        /// <summary>드래그가 이 좌석 위로 들어오면 놓을 수 있는 자리인지 표시한다.</summary>
        private void SeatCard_DragOver(object sender, DragEventArgs e)
        {
            bool canDrop = _isSeatEditMode && e.Data.GetDataPresent(typeof(Seat)) &&
                           sender is Border b && b.Tag is Seat s && !s.IsPillar &&
                           !ReferenceEquals(e.Data.GetData(typeof(Seat)), s);
            e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>마우스가 올라온 좌석 카드를 파란 테두리로 강조해서 "여기에 놓인다"를 분명히 보여준다.</summary>
        private void SeatCard_DragEnter(object sender, DragEventArgs e)
        {
            if (!_isSeatEditMode || !e.Data.GetDataPresent(typeof(Seat))) return;
            if (sender is Border border && border.Tag is Seat seat && !seat.IsPillar &&
                !ReferenceEquals(e.Data.GetData(typeof(Seat)), seat))
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235)); // 진한 파랑
                border.BorderThickness = new Thickness(3);
                border.Background = new SolidColorBrush(Color.FromRgb(219, 234, 254)); // 옅은 파랑
            }
        }

        /// <summary>드래그가 이 좌석을 벗어나면 원래 모습으로 되돌린다.</summary>
        private void SeatCard_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border border && border.Tag is Seat seat)
            {
                ApplySeatCardDefaultStyle(border, seat, seat.SeatNumber >= 43);
            }
        }

        /// <summary>다른 좌석 카드 위에 놓았을 때 — 자리를 옮기거나(빈 자리) 맞바꾼다(학생이 있는 자리).</summary>
        private void SeatCard_Drop(object sender, DragEventArgs e)
        {
            if (!_isSeatEditMode) return;
            if (!e.Data.GetDataPresent(typeof(Seat))) return;
            if (sender is not Border border || border.Tag is not Seat targetSeat) return;
            if (targetSeat.IsPillar) return;

            var sourceSeat = (Seat)e.Data.GetData(typeof(Seat));
            if (sourceSeat == targetSeat || sourceSeat.Student == null) return;

            var moving = sourceSeat.Student;
            var occupant = targetSeat.Student;

            if (occupant != null)
            {
                string occName = string.IsNullOrWhiteSpace(occupant.Name) ? occupant.StudentId : occupant.Name;
                string movingName = string.IsNullOrWhiteSpace(moving.Name) ? moving.StudentId : moving.Name;
                var confirm = MessageBox.Show(
                    $"{targetSeat.SeatNumber}번 좌석에는 {occName} 학생이 있습니다.\n" +
                    $"{movingName} 학생과 자리를 맞바꿀까요?",
                    "좌석 맞바꾸기", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
            }

            sourceSeat.Student = occupant;
            sourceSeat.IsFixed = occupant != null && occupant.Department.Contains("대학원");

            targetSeat.Student = moving;
            targetSeat.IsFixed = moving.Department.Contains("대학원");

            MarkStudentActiveThisSeason(moving);
            MarkStudentActiveThisSeason(occupant);

            RenderSeatGrid();
            SaveSeatCache();
        }

        private void ShowStudentDetailsModal(Seat seat)
        {
            _currentEditingSeat = seat;
            TxtModalSeatNum.Text = $"좌석 {seat.SeatNumber}";

            if (seat.Student != null)
            {
                TxtModalSeatNum.Text += " 상세 정보";
                TxtModalDept.Text = seat.Student.Department;
                TxtModalName.Text = seat.Student.Name;
                TxtModalId.Text = seat.Student.StudentId;
                TxtModalAdvisor.Text = seat.Student.Advisor;
                TxtModalEmail.Text = seat.Student.Email;
                ItemsModalAttendance.ItemsSource = seat.Student.Attendance;

                // 캐비닛 사용 여부 확인
                var studentId = seat.Student.StudentId;
                var cabAllocation = _cabinetAllocations.FirstOrDefault(kvp => kvp.Value.Student?.StudentId == studentId);
                if (cabAllocation.Value.Student != null)
                {
                    TxtModalCabinet.Text = $"사용 중 (번호: {cabAllocation.Key}번)";
                }
                else
                {
                    TxtModalCabinet.Text = "사용 안 함";
                }
            }
            else
            {
                TxtModalSeatNum.Text += " (학생 정보 추가)";
                TxtModalDept.Text = "";
                TxtModalName.Text = "";
                TxtModalId.Text = "";
                TxtModalAdvisor.Text = "";
                TxtModalEmail.Text = "";
                TxtModalCabinet.Text = "사용 안 함";
                ItemsModalAttendance.ItemsSource = new List<AttendanceRecord>();
            }

            BtnEditStudentModal.Visibility = Visibility.Visible;
            BtnEditStudentInfo.Visibility = Visibility.Collapsed;

            SetModalEditMode(false);
            ModalSeatDetails.Visibility = Visibility.Visible;
        }

        private void BtnEditStudentInfo_Click(object sender, RoutedEventArgs e)
        {
            SetModalEditMode(true);
            BtnEditStudentInfo.Visibility = Visibility.Collapsed;
            BtnEditStudentModal.Visibility = Visibility.Visible;
        }

        private void SetModalEditMode(bool enable)
        {
            _isModalEditing = enable;
            TxtModalName.IsReadOnly = !enable;
            TxtModalDept.IsReadOnly = !enable;
            TxtModalAdvisor.IsReadOnly = !enable;
            TxtModalEmail.IsReadOnly = !enable;

            if (enable)
            {
                TxtModalName.Background = Brushes.White; TxtModalName.BorderThickness = new Thickness(1);
                TxtModalDept.Background = Brushes.White; TxtModalDept.BorderThickness = new Thickness(1);
                TxtModalAdvisor.Background = Brushes.White; TxtModalAdvisor.BorderThickness = new Thickness(1);
                TxtModalEmail.Background = Brushes.White; TxtModalEmail.BorderThickness = new Thickness(1);
                BtnEditStudentModal.Content = "저장";
            }
            else
            {
                TxtModalName.Background = Brushes.Transparent; TxtModalName.BorderThickness = new Thickness(0);
                TxtModalDept.Background = Brushes.Transparent; TxtModalDept.BorderThickness = new Thickness(0);
                TxtModalAdvisor.Background = Brushes.Transparent; TxtModalAdvisor.BorderThickness = new Thickness(0);
                TxtModalEmail.Background = Brushes.Transparent; TxtModalEmail.BorderThickness = new Thickness(0);
                BtnEditStudentModal.Content = "좌석 수정";
            }
        }

        private void BtnCloseModal_Click(object sender, RoutedEventArgs e)
        {
            ModalSeatDetails.Visibility = Visibility.Collapsed;
        }

        // ================= SEARCH BY YEAR & SEMESTER =================
        private void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            LoadDashboardLayout();
        }

        // ================= SEAT FIX MODE =================
        private void BtnSeatFixMode_Click(object sender, RoutedEventArgs e)
        {
            if (_isSeatDeleteMode)
            {
                // Deactivate delete mode
                _isSeatDeleteMode = false;
                BtnDeleteSelected.Visibility = Visibility.Collapsed;
                BtnSeatDeleteMode.Content = "좌석 데이터 삭제";
            }
            if (_isSeatEditMode)
            {
                _isSeatEditMode = false;
                BtnSeatEditMode.Content = "🔀 좌석 수정 모드";
                BtnSeatEditMode.Background = Brushes.White;
            }

            if (!_isSeatFixMode)
            {
                // Enter Fix Mode
                _isSeatFixMode = true;
                BtnSeatFixMode.Content = "💾 고정 저장/종료";
                BtnSeatFixMode.Background = new SolidColorBrush(Color.FromRgb(254, 240, 138)); // Yellow indicator
                // Clear any selection state
                foreach (var s in _activeSeats) s.IsSelected = false;
            }
            else
            {
                // Exit Fix Mode and Save Fixed Seats
                _isSeatFixMode = false;
                BtnSeatFixMode.Content = "좌석 고정 모드";
                BtnSeatFixMode.Background = Brushes.White;

                foreach (var s in _activeSeats)
                {
                    if (s.IsSelected)
                    {
                        if (IsGraduateSeat(s))
                        {
                            s.IsSelected = false;
                            continue;
                        }
                        s.IsFixed = !s.IsFixed;
                        s.IsSelected = false;
                    }
                }
            }
            RenderSeatGrid();
        }

        private bool IsGraduateSeat(Seat seat)
        {
            return seat.Student != null && seat.Student.Department.Contains("대학원");
        }

        // ================= RANDOM ALLOCATION =================
        private void BtnRandomAllocation_Click(object sender, RoutedEventArgs e)
        {
            var positions = GetSeatLayoutCoordinates();

            // Find existing students currently assigned to seats
            var currentStudentIds = _activeSeats.Where(s => s.Student != null).Select(s => s.Student.StudentId).ToHashSet();

            // 이번 시즌에 등록(활동)된 학생 중 아직 자리가 없는 학생만 배정 대상이다.
            // (지난 시즌 학생까지 끌어오면 안 되므로 시즌으로 먼저 거른다)
            string seasonKey = CurrentSeasonKey();
            var newStudents = _masterStudents
                .Where(m => m.LastActiveSeason == seasonKey && !currentStudentIds.Contains(m.StudentId))
                .ToList();

            if (newStudents.Count > 0)
            {
                // Only place new students randomly into empty seats (filling from back)
                var emptySeats = _activeSeats.Where(s => s.Student == null && !s.IsPillar && (s.SeatNumber < 43 || s.SeatNumber > 52)).OrderBy(s => s.SeatNumber).ToList();
                if (emptySeats.Count < newStudents.Count)
                {
                    MessageBox.Show("빈 좌석이 부족하여 추가 학생을 배치할 수 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Get target empty seats starting from the back
                var targetSeats = emptySeats.Skip(Math.Max(0, emptySeats.Count - newStudents.Count)).ToList();
                Random r = new Random();
                var shuffledNew = newStudents.OrderBy(x => r.Next()).ToList();

                for (int i = 0; i < shuffledNew.Count && i < targetSeats.Count; i++)
                {
                    shuffledNew[i].LastActiveSeason = seasonKey;
                    targetSeats[i].Student = shuffledNew[i].Clone();
                    
                    // Graduate student check for new assignments
                    if (targetSeats[i].Student.Department.Contains("대학원"))
                    {
                        targetSeats[i].IsFixed = true;
                    }
                }

                RenderSeatGrid();
                UpdateAlertBadges();
                MessageBox.Show($"신규 등록 학생 {shuffledNew.Count}명이 빈 자리(뒷자리 우선)에 랜덤 배치되었습니다.", "신규 배치 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Otherwise, shuffle all un-fixed, non-pillar, and non-graduate seats, filling from the back
            var unFixedSeats = _activeSeats.Where(s => !s.IsFixed && !s.IsPillar && !IsGraduateSeat(s) && !(s.SeatNumber >= 43 && s.SeatNumber <= 52)).OrderBy(s => s.SeatNumber).ToList();
            if (unFixedSeats.Count <= 1)
            {
                MessageBox.Show("고정되지 않은 좌석이 부족하여 배정할 수 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Extract student objects
            var studentPool = unFixedSeats.Where(s => s.Student != null).Select(s => s.Student!).ToList();

            // Clear students from un-fixed seats temporarily
            foreach (var s in unFixedSeats)
            {
                s.Student = null;
            }

            // Select target seats starting from back (fill from back)
            var targetSeatsToFill = unFixedSeats.Skip(Math.Max(0, unFixedSeats.Count - studentPool.Count)).ToList();

            // Shuffle with constraint
            Random rand = new Random();
            bool success = false;
            List<StudentInfo> shuffledPool = new List<StudentInfo>(studentPool);

            // Backtracking/Retry solver
            for (int attempt = 0; attempt < 1000; attempt++)
            {
                // Shuffle pool
                shuffledPool = studentPool.OrderBy(x => rand.Next()).ToList();
                bool violation = false;

                // Check constraints
                for (int i = 0; i < shuffledPool.Count; i++)
                {
                    var student = shuffledPool[i];
                    var newSeatNum = targetSeatsToFill[i].SeatNumber;

                    // Find original seat of this student
                    var oldSeatNum = _activeSeats.FindIndex(s => s.Student?.StudentId == student.StudentId) + 1;
                    if (oldSeatNum > 0)
                    {
                        var oldCoord = positions.FirstOrDefault(p => p.SeatNum == oldSeatNum);
                        var newCoord = positions.FirstOrDefault(p => p.SeatNum == newSeatNum);

                        if (oldCoord.SeatNum != 0 && newCoord.SeatNum != 0)
                        {
                            if (oldCoord.Row == newCoord.Row)
                            {
                                if (Math.Abs(oldCoord.Col - newCoord.Col) < 2)
                                {
                                    violation = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (!violation)
                {
                    success = true;
                    break;
                }
            }

            if (!success)
            {
                MessageBox.Show("수평 거리 제약조건(2칸 이상)을 맞출 수 없어 기본 랜덤 배정으로 진행합니다.", "참고", MessageBoxButton.OK, MessageBoxImage.Information);
                shuffledPool = studentPool.OrderBy(x => rand.Next()).ToList();
            }

            // Assign shuffled to target seats
            for (int i = 0; i < targetSeatsToFill.Count; i++)
            {
                if (i < shuffledPool.Count)
                {
                    targetSeatsToFill[i].Student = shuffledPool[i];
                }
            }

            RenderSeatGrid();
        }

        private List<(int SeatNum, int Row, int Col)> GetSeatLayoutCoordinates()
        {
            var coords = new List<(int SeatNum, int Row, int Col)>();
            // Matches RenderSeatGrid mapping
            coords.Add((1, 0, 0)); coords.Add((2, 0, 1)); coords.Add((3, 0, 2)); coords.Add((4, 0, 4)); coords.Add((5, 0, 5)); coords.Add((6, 0, 6));
            coords.Add((7, 1, 0)); coords.Add((8, 1, 1)); coords.Add((9, 1, 2)); coords.Add((10, 1, 4)); coords.Add((11, 1, 5)); coords.Add((12, 1, 6));
            coords.Add((13, 2, 0)); coords.Add((14, 2, 1)); coords.Add((15, 2, 2)); coords.Add((16, 2, 4)); coords.Add((17, 2, 5)); coords.Add((18, 2, 6));
            coords.Add((19, 3, 0)); coords.Add((20, 3, 1)); coords.Add((21, 3, 2)); coords.Add((22, 3, 4)); coords.Add((23, 3, 5));
            coords.Add((24, 4, 0)); coords.Add((25, 4, 1)); coords.Add((26, 4, 2)); coords.Add((27, 4, 4)); coords.Add((28, 4, 5));
            coords.Add((29, 5, 0)); coords.Add((30, 5, 1)); coords.Add((31, 5, 2)); coords.Add((32, 5, 4)); coords.Add((33, 5, 5)); coords.Add((34, 5, 6)); coords.Add((35, 5, 7));
            coords.Add((36, 6, 0)); coords.Add((37, 6, 1)); coords.Add((38, 6, 2)); coords.Add((39, 6, 4)); coords.Add((40, 6, 5)); coords.Add((41, 6, 6)); coords.Add((42, 6, 7));
            
            // Bottom rows
            coords.Add((43, 7, 0)); coords.Add((44, 7, 1)); coords.Add((45, 7, 4)); coords.Add((46, 7, 5)); coords.Add((47, 7, 6));
            coords.Add((48, 8, 0)); coords.Add((49, 8, 1)); coords.Add((50, 8, 4)); coords.Add((51, 8, 5)); coords.Add((52, 8, 6));

            return coords;
        }

        // ================= DELETE SEATS =================
        private void BtnSeatDeleteMode_Click(object sender, RoutedEventArgs e)
        {
            if (_isSeatFixMode)
            {
                _isSeatFixMode = false;
                BtnSeatFixMode.Content = "좌석 고정 모드";
                BtnSeatFixMode.Background = Brushes.White;
            }
            if (_isSeatEditMode)
            {
                _isSeatEditMode = false;
                BtnSeatEditMode.Content = "🔀 좌석 수정 모드";
                BtnSeatEditMode.Background = Brushes.White;
            }

            // Create context menu to choose between selection delete and all delete
            ContextMenu menu = new ContextMenu();
            MenuItem m1 = new MenuItem { Header = "선택 삭제 모드 토글" };
            m1.Click += (s, ev) => ToggleSeatDeleteMode();
            
            MenuItem m2 = new MenuItem { Header = "전체 삭제" };
            m2.Click += (s, ev) => DeleteAllSeats();

            menu.Items.Add(m1);
            menu.Items.Add(m2);

            BtnSeatDeleteMode.ContextMenu = menu;
            BtnSeatDeleteMode.ContextMenu.IsOpen = true;
        }

        private void ToggleSeatDeleteMode()
        {
            if (!_isSeatDeleteMode)
            {
                _isSeatDeleteMode = true;
                BtnSeatDeleteMode.Content = "💾 선택 삭제 모드 활성 중";
                foreach (var s in _activeSeats) s.IsSelected = false;
            }
            else
            {
                _isSeatDeleteMode = false;
                BtnSeatDeleteMode.Content = "좌석 데이터 삭제";
                BtnDeleteSelected.Visibility = Visibility.Collapsed;
                foreach (var s in _activeSeats) s.IsSelected = false;
            }
            RenderSeatGrid();
        }

        private void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult result = MessageBox.Show("선택한 좌석들의 인적사항과 출석 데이터를 삭제하시겠습니까?", "확인", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                foreach (var s in _activeSeats)
                {
                    if (s.IsSelected)
                    {
                        s.Student = null;
                        s.IsSelected = false;
                        s.IsFixed = false;
                    }
                }
                _isSeatDeleteMode = false;
                BtnSeatDeleteMode.Content = "좌석 데이터 삭제";
                BtnDeleteSelected.Visibility = Visibility.Collapsed;
                RenderSeatGrid();
            }
        }

        private void DeleteAllSeats()
        {
            MessageBoxResult result = MessageBox.Show("모든 좌석의 인적 사항과 출석 데이터를 삭제하시겠습니까? (좌석 번호와 고정상태는 남음)", "경고", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                foreach (var s in _activeSeats)
                {
                    s.Student = null;
                    s.IsSelected = false;
                }
                RenderSeatGrid();
            }
        }

        // ================= MEMO HANDLERS =================
        private void BtnAddMemo_Click(object sender, RoutedEventArgs e)
        {
            if (_editingMemo != null)
            {
                if (!string.IsNullOrWhiteSpace(TxtNewMemo.Text))
                {
                    _editingMemo.Content = TxtNewMemo.Text.Trim();
                }
                _editingMemo = null;
                _originalMemoContent = null;
                TxtNewMemo.Clear();
                BtnAddMemo.Content = "+ 메모 추가";
                LstMemos.ItemsSource = null;
                LstMemos.ItemsSource = _memos;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(TxtNewMemo.Text))
                {
                    _memos.Add(new MemoItem { Content = TxtNewMemo.Text.Trim() });
                    TxtNewMemo.Clear();
                }
            }
        }

        private void BtnDeleteMemo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is MemoItem memo)
            {
                _memos.Remove(memo);
            }
        }

        private void BtnEditMemo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is MemoItem memo)
            {
                _editingMemo = memo;
                _originalMemoContent = memo.Content;
                TxtNewMemo.Text = memo.Content;
                BtnAddMemo.Content = "편집 완료";
            }
        }

        // ================= STUDENT MODAL EDIT =================
        private void HandleModalEditSave()
        {
            if (_currentEditingSeat == null) return;

            if (!_isModalEditing)
            {
                SetModalEditMode(true);
            }
            else
            {
                if (_currentEditingSeat.Student == null)
                {
                    _currentEditingSeat.Student = new StudentInfo
                    {
                        StudentId = "2026" + new Random().Next(1000, 9999).ToString()
                    };
                }

                _currentEditingSeat.Student.Name = TxtModalName.Text;
                _currentEditingSeat.Student.Department = TxtModalDept.Text;
                _currentEditingSeat.Student.Advisor = TxtModalAdvisor.Text;
                _currentEditingSeat.Student.Email = TxtModalEmail.Text;

                // Graduate check
                if (_currentEditingSeat.Student.Department.Contains("대학원"))
                {
                    _currentEditingSeat.IsFixed = true;
                }

                var master = _masterStudents.FirstOrDefault(m => m.StudentId == _currentEditingSeat.Student.StudentId);
                if (master != null)
                {
                    master.Name = TxtModalName.Text;
                    master.Department = TxtModalDept.Text;
                    master.Advisor = TxtModalAdvisor.Text;
                    master.Email = TxtModalEmail.Text;
                }
                else
                {
                    _masterStudents.Add(_currentEditingSeat.Student.Clone());
                }

                SetModalEditMode(false);
                RenderSeatGrid();
                UpdateAlertBadges();
                MessageBox.Show("학생 정보가 수정되었으며 즉시 대시보드에 데이터가 업데이트되었습니다.", "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnEditStudentModal_Click(object sender, RoutedEventArgs e) => HandleModalEditSave();





        // ================= EQUIPMENT TAB (RENTALS / STATUS LIGHTS) =================
        private void BtnReturnEquipment_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is RentalItem rental)
            {
                MessageBoxResult res = MessageBox.Show($"{rental.StudentName}의 '{rental.EquipmentType}' 반납 처리를 하시겠습니까?", "반납 확인", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    rental.IsReturned = true;
                    _rentals.Remove(rental);
                    
                    // Push to undo LIFO stack
                    _rentalUndoStack.Push(rental);

                    UpdateAlertBadges();
                    BindEquipmentRentals();
                }
            }
        }

        private void BtnUndoReturn_Click(object sender, RoutedEventArgs e)
        {
            GridApprovalHistory.ItemsSource = null;
            GridApprovalHistory.ItemsSource = _approvalHistory;
            ModalApprovalHistory.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 승인된 신청을 마스터 DB에 반영하고, 좌석/캐비닛에 쓸 학생 레코드를 돌려준다.
        /// 학번이 이미 있는데 이름이 다르면 관리자에게 물어본다.
        /// </summary>
        /// <returns>반영된 마스터 레코드. 관리자가 취소하면 null(= 승인 중단).</returns>
        private StudentInfo? RegisterOrUpdateMaster(ApprovalRequest req)
        {
            var existing = _masterStudents.FirstOrDefault(m => m.StudentId == req.StudentId);

            if (existing == null)
            {
                var created = new StudentInfo
                {
                    StudentId = req.StudentId,
                    Name = req.StudentName,
                    Department = req.Department,
                    Advisor = req.Advisor,
                    Email = req.Email,
                    LastActiveSeason = CurrentSeasonKey()
                };
                created.Attendance.Add(new AttendanceRecord
                {
                    DateString = _currentSimulatedDate.ToString("yyyy-MM-dd"),
                    Status = "출석"
                });
                _masterStudents.Add(created);
                RefreshMasterGrid();
                return created;
            }

            // 같은 학번인데 이름이 다르다 — 둘 중 하나는 잘못 적은 것이므로 조용히 넘기면 안 된다.
            // 단, 기자재 폼처럼 이름 질문이 아예 없는 신청서는 '다름'이 아니라 '모름'이므로 묻지 않는다.
            if (!string.IsNullOrWhiteSpace(req.StudentName) &&
                !string.Equals(existing.Name, req.StudentName, StringComparison.Ordinal))
            {
                var answer = MessageBox.Show(
                    $"학번 {req.StudentId}이(가) 이미 다른 이름으로 등록되어 있습니다.\n\n" +
                    $"[기존]  {existing.Name} / {existing.Department} / {existing.Advisor}\n" +
                    $"[신청]  {req.StudentName} / {req.Department} / {req.Advisor}\n\n" +
                    "신청 내용으로 덮어쓸까요?\n" +
                    "· 예    → 마스터를 신청 내용으로 수정하고 승인합니다\n" +
                    "· 아니오 → 승인을 취소합니다 (학번을 확인해주세요)",
                    "학번 중복", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (answer != MessageBoxResult.Yes) return null;
            }

            // 재신청이거나 관리자가 덮어쓰기를 택했다 — 최신 정보로 갱신.
            // 신청서에 없는 항목(빈 값)으로 기존 정보를 지우면 안 된다.
            existing.Name = Prefer(req.StudentName, existing.Name);
            existing.Department = Prefer(req.Department, existing.Department);
            existing.Advisor = Prefer(req.Advisor, existing.Advisor);
            existing.Email = Prefer(req.Email, existing.Email);
            existing.LastActiveSeason = CurrentSeasonKey(); // 재신청 — 이번 시즌 활동자로 올린다
            RefreshMasterGrid();
            return existing;
        }

        /// <summary>신청서에 값이 없으면 기존 값을 지키고, 있으면 새 값으로 바꾼다.</summary>
        private static string Prefer(string incoming, string current) =>
            string.IsNullOrWhiteSpace(incoming) ? current : incoming;

        /// <summary>
        /// 지금 진행 중인 시즌 키("{연도}_{시즌}"). 자동 판별된 시즌을 우선 쓰고,
        /// 판별이 안 되면 대시보드에서 조회 중인 연도/학기를 쓴다.
        /// </summary>
        private string CurrentSeasonKey()
        {
            if (_currentSeason != null)
                return $"{_currentSeason.Value.Year}_{_currentSeason.Value.Season}";

            string year = ComboSearchYear?.SelectedItem as string ?? _currentSimulatedDate.Year.ToString();
            string semester = (ComboSearchSemester?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "1학기";
            return $"{year}_{semester}";
        }

        /// <summary>
        /// 데이터 관리 탭의 학생 목록을 다시 그린다.
        /// 기본은 이번 시즌에 활동한 학생만 보여주고, [지난 시즌까지 보기]를 켜면 전부 보여준다.
        /// 검색어가 있으면 그 결과 안에서 다시 걸러낸다. (학생 정보 자체는 시즌이 바뀌어도 지워지지 않는다)
        /// </summary>
        private void RefreshMasterGrid()
        {
            if (GridMasterStudents == null) return;

            IEnumerable<StudentInfo> view = _masterStudents;

            if (ChkShowAllSeasonStudents?.IsChecked != true)
            {
                string season = CurrentSeasonKey();
                view = view.Where(s => s.LastActiveSeason == season);
            }

            string query = (TxtSearchStudent?.Text ?? string.Empty).Trim().ToLower();
            if (query.Length > 0)
            {
                view = view.Where(s =>
                    s.StudentId.ToLower().Contains(query) ||
                    s.Name.ToLower().Contains(query) ||
                    s.Advisor.ToLower().Contains(query) ||
                    s.Department.ToLower().Contains(query));
            }

            GridMasterStudents.ItemsSource = view.ToList();
        }

        /// <summary>
        /// 시즌 표시가 비어 있는 학생(이 기능이 생기기 전에 등록된 데이터)을 지금 시즌 학생으로 본다.
        /// 그대로 두면 시즌 필터에 하나도 안 걸려 목록이 통째로 비어 보인다.
        /// </summary>
        private void MigrateStudentsWithoutSeason()
        {
            string seasonKey = CurrentSeasonKey();
            foreach (var st in _masterStudents)
            {
                if (string.IsNullOrWhiteSpace(st.LastActiveSeason))
                    st.LastActiveSeason = seasonKey;
            }
        }

        private void ChkShowAllSeasonStudents_Changed(object sender, RoutedEventArgs e)
        {
            RefreshMasterGrid();
        }

        /// <summary>학생을 이번 시즌 활동자로 표시한다(마스터 기준). 학번으로 마스터를 찾아 갱신한다.</summary>
        private void MarkStudentActiveThisSeason(StudentInfo? student)
        {
            if (student == null || string.IsNullOrWhiteSpace(student.StudentId)) return;
            var master = _masterStudents.FirstOrDefault(m => m.StudentId == student.StudentId);
            if (master != null) master.LastActiveSeason = CurrentSeasonKey();
        }

        // ================= CABINET & SANGSANGLAB APPROVAL HANDLERS =================
        private void BtnApproveRequest_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is ApprovalRequest req)
            {
                StudentInfo? masterStudent;
                if (req.TabType == "기자재")
                {
                    // 기자재 대여는 일회성 처리라 데이터 관리(학생 마스터)에 등록/수정하지 않는다.
                    // 이미 마스터에 있는 학생이면 이름 등 표시용 정보만 읽어오고, 마스터 자체는 건드리지 않는다.
                    masterStudent = _masterStudents.FirstOrDefault(m => m.StudentId == req.StudentId);
                }
                else
                {
                    // 마스터 반영을 먼저 한다 — 학번 충돌로 관리자가 취소하면 승인 자체를 중단해야 하므로
                    masterStudent = RegisterOrUpdateMaster(req);
                    if (masterStudent == null) return;
                }

                string chosenEquipType = "";
                string chosenPhone = req.Phone;
                int chosenCabinetNum = -1;
                EquipmentSelectionDialog? equipDialog = null;
                if (req.TabType == "기자재")
                {
                    var activeRentals = _rentals.Where(r => !r.IsReturned).ToList();
                    // 상태 관리(고장/수리/기타)에 올라가 있는 개체는 대여도 못 나가게 뺀다
                    var dialog = new EquipmentSelectionDialog(activeRentals, req.Phone, req.EquipmentType, _equipmentIssues.ToList(), req.DueDate, _currentSimulatedDate) { Owner = this };
                    if (dialog.ShowDialog() != true)
                    {
                        // Admin cancelled, abort the whole approval!
                        return;
                    }
                    equipDialog = dialog;
                    chosenEquipType = $"{dialog.SelectedEquipmentType} ({dialog.SelectedUnitNumber})";
                    chosenPhone = dialog.SelectedPhone;
                }
                else if (req.TabType == "캐비닛")
                {
                    // 이 분기는 위에서 이미 null이 아님을 확인한 경우에만 온다 (기자재만 null 허용)
                    var m = masterStudent!;

                    // Check if this student already has a cabinet
                    var duplicateCabinetNum = _cabinetAllocations.FirstOrDefault(kvp => kvp.Value.Student?.StudentId == m.StudentId).Key;
                    if (duplicateCabinetNum > 0)
                    {
                        string existingPeriod = _cabinetAllocations[duplicateCabinetNum].Period;
                        // 대여 기간은 '승인을 누른 오늘'부터 이번 시즌 마지막 날까지로 잡는다
                        string newPeriod = SeasonCabinetPeriod();

                        if (ArePeriodsOverlapping(existingPeriod, newPeriod))
                        {
                            MessageBox.Show(
                                $"해당 학생({m.Name} / {m.StudentId})은 이미 {duplicateCabinetNum}번 캐비닛에 배정되어 있으며, " +
                                $"대여 기간({existingPeriod})이 신청 기간({newPeriod})과 중복됩니다.\n먼저 기존 배정을 해제한 후 승인해 주세요.",
                                "배정 중복 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        // 기간이 겹치지 않으면 새 칸을 주는 대신 쓰던 칸의 기간을 늘려준다 (연장)
                        string mergedPeriod = MergePeriods(existingPeriod, newPeriod);
                        var confirm = MessageBox.Show(
                            $"{m.Name} ({m.StudentId}) 학생은 {duplicateCabinetNum}번 캐비닛을 {existingPeriod} 기간으로 쓰고 있습니다.\n" +
                            $"신청 기간({newPeriod})이 겹치지 않으므로 {duplicateCabinetNum}번 캐비닛을 {mergedPeriod} 로 연장합니다.\n\n계속할까요?",
                            "대여 기간 연장", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (confirm != MessageBoxResult.Yes) return;

                        _cabinetAllocations[duplicateCabinetNum] = (m.Clone(), mergedPeriod);
                        req.RentalPeriod = mergedPeriod; // 승인 내역에도 확정된 기간으로 남긴다
                        chosenCabinetNum = duplicateCabinetNum;
                    }
                    else
                    {
                        var dialog = new CabinetSelectionDialog(_cabinetAllocations, TotalCabinetCount) { Owner = this };
                        if (dialog.ShowDialog() != true)
                        {
                            // Admin cancelled, abort the whole approval!
                            return;
                        }
                        chosenCabinetNum = dialog.SelectedCabinetNumber;
                    }
                }

                req.Status = "승인 완료";
                _approvals.Remove(req);
                _approvalHistory.Add(req);

                // 상상Lab 신청 승인은 학생 마스터 데이터(데이터 관리 탭)만 만든다.
                // 대시보드 좌석에는 자동으로 앉히지 않는다 — 좌석 배정은 관리자가 직접 한다.

                // If cabinet request is approved, assign the chosen cabinet block.
                // 연장이면 위에서 이미 합친 기간으로 써 두었으므로 덮어쓰지 않는다.
                if (req.TabType == "캐비닛" && chosenCabinetNum > 0 &&
                    (!_cabinetAllocations.TryGetValue(chosenCabinetNum, out var current) ||
                     current.Student?.StudentId != masterStudent!.StudentId))
                {
                    // 대여 기간 = 승인을 누른 오늘 ~ 이번 시즌 마지막 날
                    string period = SeasonCabinetPeriod();
                    req.RentalPeriod = period; // 승인 내역에도 실제 확정된 기간으로 남긴다
                    _cabinetAllocations[chosenCabinetNum] = (masterStudent!.Clone(), period);
                }

                // If equipment request is approved, add to rentals list and history list
                if (req.TabType == "기자재" && equipDialog != null)
                {
                    int periodDays = (equipDialog.SelectedDueDate.Date - equipDialog.SelectedRentalDate.Date).Days;
                    if (periodDays <= 0) periodDays = 7;

                    var newRental = new RentalItem
                    {
                        StudentName = Prefer(masterStudent?.Name ?? string.Empty, req.StudentName),
                        EquipmentType = chosenEquipType,
                        RentalDate = equipDialog.SelectedRentalDate,
                        RentalPeriodDays = periodDays,
                        IsReturned = false,

                        // 신청서에 없는 항목은 RentalItem의 기본값을 그대로 둔다
                        StudentId = Prefer(req.StudentId, "20261234"),
                        Department = Prefer(req.Department, "소프트웨어융합학과"),
                        YearLevel = Prefer(req.YearLevel, "3학년"),
                        Phone = Prefer(chosenPhone, "010-0000-0000"),
                        Advisor = Prefer(req.Advisor, "김동욱 교수"),
                        Location = Prefer(req.Location, "DSU 창의공간"),
                        Purpose = Prefer(req.Purpose, "개인 실습"),
                        Remarks = req.Note
                    };
                    _rentals.Add(newRental);
                    _rentalHistory.Add(newRental);
                }

                // Refresh tab binding
                Button targetBtn;
                Grid targetTab;
                string targetTitle;

                if (req.TabType == "상상Lab")
                {
                    targetBtn = BtnSangsangLab;
                    targetTab = TabSangsangLab;
                    targetTitle = "상상Lab 승인";
                }
                else if (req.TabType == "기자재")
                {
                    targetBtn = BtnEquipment;
                    targetTab = TabEquipment;
                    targetTitle = "기자재 현황";
                }
                else
                {
                    targetBtn = BtnCabinet;
                    targetTab = TabCabinet;
                    targetTitle = "캐비닛 현황";
                }

                SwitchTab(targetBtn, targetTab, targetTitle);
                RenderSeatGrid();
                SaveAppState();
            }
        }

        private void BtnRejectRequest_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is ApprovalRequest req)
            {
                req.Status = "반려";
                _approvals.Remove(req);
                _approvalHistory.Add(req);

                Button targetBtn;
                Grid targetTab;
                string targetTitle;

                if (req.TabType == "상상Lab")
                {
                    targetBtn = BtnSangsangLab;
                    targetTab = TabSangsangLab;
                    targetTitle = "상상Lab 승인";
                }
                else if (req.TabType == "기자재")
                {
                    targetBtn = BtnEquipment;
                    targetTab = TabEquipment;
                    targetTitle = "기자재 현황";
                }
                else
                {
                    targetBtn = BtnCabinet;
                    targetTab = TabCabinet;
                    targetTitle = "캐비닛 현황";
                }

                SwitchTab(targetBtn, targetTab, targetTitle);
                SaveAppState();
            }
        }

        // ================= MASTER DATABASE HANDLERS =================
        private StudentInfo? _selectedMasterStudent;

        private void GridMasterStudents_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridMasterStudents.SelectedItem is StudentInfo student)
            {
                _selectedMasterStudent = student;
                TxtMasterId.Text = student.StudentId;
                TxtMasterName.Text = student.Name;
                TxtMasterDept.Text = student.Department;
                TxtMasterAdvisor.Text = student.Advisor;
                TxtMasterEmail.Text = student.Email;
                PanelMasterEdit.IsEnabled = true;
            }
            else
            {
                PanelMasterEdit.IsEnabled = false;
            }
        }

        private void BtnSaveMaster_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMasterStudent != null)
            {
                string oldId = _selectedMasterStudent.StudentId;
                string newId = TxtMasterId.Text.Trim();

                if (string.IsNullOrEmpty(newId))
                {
                    MessageBox.Show("학번은 비워둘 수 없습니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (oldId != newId && _masterStudents.Any(m => m.StudentId == newId))
                {
                    MessageBox.Show("이미 존재하는 학번입니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _selectedMasterStudent.StudentId = newId;
                _selectedMasterStudent.Name = TxtMasterName.Text;
                _selectedMasterStudent.Department = TxtMasterDept.Text;
                _selectedMasterStudent.Advisor = TxtMasterAdvisor.Text;
                _selectedMasterStudent.Email = TxtMasterEmail.Text;

                // Sync with all seat layouts in cache recursively
                foreach (var layout in _seatLayoutCache.Values)
                {
                    foreach (var s in layout)
                    {
                        if (s.Student != null && s.Student.StudentId == oldId)
                        {
                            s.Student.StudentId = newId;
                            s.Student.Name = _selectedMasterStudent.Name;
                            s.Student.Department = _selectedMasterStudent.Department;
                            s.Student.Advisor = _selectedMasterStudent.Advisor;
                            s.Student.Email = _selectedMasterStudent.Email;

                            if (s.Student.Department.Contains("대학원"))
                            {
                                s.IsFixed = true;
                            }
                        }
                    }
                }

                // Also update active seats
                foreach (var s in _activeSeats)
                {
                    if (s.Student != null && s.Student.StudentId == oldId)
                    {
                        s.Student.StudentId = newId;
                        s.Student.Name = _selectedMasterStudent.Name;
                        s.Student.Department = _selectedMasterStudent.Department;
                        s.Student.Advisor = _selectedMasterStudent.Advisor;
                        s.Student.Email = _selectedMasterStudent.Email;

                        if (s.Student.Department.Contains("대학원"))
                        {
                            s.IsFixed = true;
                        }
                    }
                }

                RefreshMasterGrid();
                RenderSeatGrid();
                SaveAppState();
                MessageBox.Show("마스터 데이터가 수정되었으며 즉시 대시보드에 데이터가 업데이트되었습니다.", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnDeleteMaster_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMasterStudent != null)
            {
                var result = MessageBox.Show($"정말 {_selectedMasterStudent.Name} 학생을 마스터 데이터베이스에서 삭제하시겠습니까?", "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    _masterStudents.Remove(_selectedMasterStudent);
                    RefreshMasterGrid();
                    PanelMasterEdit.IsEnabled = false;
                    SaveAppState();
                    MessageBox.Show("마스터 데이터가 삭제되었습니다. (기존 배치된 좌석 데이터는 유지됩니다.)", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        // ================= GOOGLE FORM (SHEETS) SYNC =================

        /// <summary>사용자가 만든 응답 시트. 설정에 값이 없을 때의 기본값.</summary>
        private const string DefaultSpreadsheetId = "1TejlN7HTU7n2mvkKIilCrMlSivHsey3h-nOuFOeR7t4";

        /// <summary>기자재 폼 전용 응답 시트. 설정에 값이 없을 때의 기본값.</summary>
        private const string DefaultEquipmentSpreadsheetId = "152AqDmrq6LF82E41bMNVOeD74h0Ks4VXAAyzHlA4gVw";

        /// <summary>캐비닛 폼 전용 응답 시트. 설정에 값이 없을 때의 기본값.</summary>
        private const string DefaultCabinetSpreadsheetId = "1JL6TKyzYUFd2K7_66JXk6_SPr1me0Rr3UqwRua7DZtk";

        private void InitializeGoogleFormSync()
        {
            _config = AppConfig.Load();

            // (마이그레이션) 예전 '응답 스프레드시트 ID' 칸에 값이 있었고 새 칸이 비어 있으면 한 번만 옮겨 담는다
            if (string.IsNullOrWhiteSpace(_config.SangsangLabFormUrl) && !string.IsNullOrWhiteSpace(_config.SpreadsheetId))
                _config.SangsangLabFormUrl = _config.SpreadsheetId;
            if (string.IsNullOrWhiteSpace(_config.SangsangLabFormUrl))
                _config.SangsangLabFormUrl = DefaultSpreadsheetId;

            if (string.IsNullOrWhiteSpace(_config.EquipmentFormUrl))
                _config.EquipmentFormUrl = DefaultEquipmentSpreadsheetId;
            if (string.IsNullOrWhiteSpace(_config.CabinetFormUrl))
                _config.CabinetFormUrl = DefaultCabinetSpreadsheetId;

            string mainSheetId = GoogleFormsService.ExtractSpreadsheetId(_config.SangsangLabFormUrl);
            _formsService = new GoogleFormsService(_config.ResolveServiceAccountKeyPath(), mainSheetId);
            _formsService.EquipmentSpreadsheetId =
                GoogleFormsService.ExtractSpreadsheetId(_config.EquipmentFormUrl);
            _formsService.CabinetSpreadsheetId =
                GoogleFormsService.ExtractSpreadsheetId(_config.CabinetFormUrl);

            // 설정 화면에 현재 값 반영
            TxtPollingInterval.Text = _config.PollingIntervalSeconds.ToString();
            TxtSangsangLabFormUrl.Text = _config.SangsangLabFormUrl;
            TxtCabinetFormUrl.Text = _config.CabinetFormUrl;
            TxtEquipmentFormUrl.Text = _config.EquipmentFormUrl;
            UpdateKeyStatusDisplay();

            // 체크박스는 Checked 이벤트로 ApplyPollingTimer를 부르므로 마지막에 설정한다
            ChkPollingEnabled.IsChecked = _config.PollingEnabled;

            if (!_formsService.KeyFileExists)
            {
                SetSyncStatus(
                    "서비스 계정 키를 찾지 못했습니다. [키 파일 선택...]으로 지정해주세요.\n" +
                    $"찾아본 위치: {_formsService.KeyPath}", true);
                return;
            }

            ApplyPollingTimer();

            // 시작 직후 1회 동기화 (조용히)
            _ = SyncFromGoogleFormAsync(silent: true);
        }

        /// <summary>키 경로와 서비스 계정 주소를 설정 화면에 반영한다.</summary>
        private void UpdateKeyStatusDisplay()
        {
            if (_formsService == null) return;

            string path = _formsService.KeyPath;
            bool exists = _formsService.KeyFileExists;

            TxtKeyPath.Text = exists ? path : $"(없음) {path}";
            TxtServiceAccountEmail.Text = exists ? ReadServiceAccountEmail(path) : "(키 파일 없음)";
        }

        private void BtnPickKeyFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "구글 서비스 계정 키(JSON) 선택",
                Filter = "서비스 계정 키 (*.json)|*.json|모든 파일 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) != true) return;

            // 고른 파일이 정말 서비스 계정 키인지 확인한다
            string email;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(dlg.FileName));
                var root = doc.RootElement;
                bool isServiceAccount =
                    root.TryGetProperty("type", out var t) && t.GetString() == "service_account" &&
                    root.TryGetProperty("private_key", out _) &&
                    root.TryGetProperty("client_email", out var em);

                if (!isServiceAccount)
                {
                    MessageBox.Show(
                        "이 파일은 서비스 계정 키가 아닙니다.\n" +
                        "Google Cloud에서 발급한 서비스 계정 JSON 키를 선택해주세요.",
                        "잘못된 키 파일", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                email = root.GetProperty("client_email").GetString() ?? "";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"키 파일을 읽지 못했습니다.\n{ex.Message}",
                    "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 표준 위치로 복사해 둔다. 복사에 실패하면 고른 위치를 그대로 쓴다.
            string target = AppConfig.DefaultServiceAccountKeyPath;
            try
            {
                System.IO.Directory.CreateDirectory(AppConfig.ConfigDirectory);
                if (!string.Equals(System.IO.Path.GetFullPath(dlg.FileName),
                                   System.IO.Path.GetFullPath(target),
                                   StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.Copy(dlg.FileName, target, overwrite: true);
                }
                _config.KeyPathOverride = string.Empty;
            }
            catch
            {
                target = dlg.FileName;
                _config.KeyPathOverride = dlg.FileName;
            }

            _config.Save();

            if (_formsService != null) _formsService.KeyPath = target;
            UpdateKeyStatusDisplay();
            ApplyPollingTimer();

            MessageBox.Show($"키를 등록했습니다.\n\n서비스 계정: {email}\n보관 위치: {target}\n\n" +
                            "이 주소로 응답 스프레드시트를 '뷰어' 공유해야 읽을 수 있습니다.",
                            "키 등록 완료", MessageBoxButton.OK, MessageBoxImage.Information);

            _ = SyncFromGoogleFormAsync(silent: false);
        }

        /// <summary>키 파일에서 client_email만 읽어 화면에 표시한다 (private_key는 건드리지 않는다).</summary>
        private static string ReadServiceAccountEmail(string keyPath)
        {
            try
            {
                if (!System.IO.File.Exists(keyPath)) return "(키 파일 없음)";
                using var doc = System.Text.Json.JsonDocument.Parse(
                    System.IO.File.ReadAllText(keyPath));
                return doc.RootElement.TryGetProperty("client_email", out var el)
                    ? el.GetString() ?? "(확인 불가)"
                    : "(확인 불가)";
            }
            catch
            {
                return "(확인 불가)";
            }
        }

        private void ApplyPollingTimer()
        {
            _pollTimer?.Stop();

            if (!_config.PollingEnabled) return;

            // 키가 없으면 타이머가 매번 실패만 반복하므로 아예 돌리지 않는다.
            // (설정 화면의 체크박스를 코드로 켜는 시점에 Checked 이벤트가 먼저 들어오므로 여기서 막아야 한다)
            if (_formsService == null || !_formsService.KeyFileExists) return;

            int seconds = Math.Max(15, _config.PollingIntervalSeconds); // 과도한 호출 방지
            _pollTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(seconds)
            };
            _pollTimer.Tick += async (s, e) => await SyncFromGoogleFormAsync(silent: true);
            _pollTimer.Start();
        }

        private async void BtnSyncNow_Click(object sender, RoutedEventArgs e)
        {
            await SyncFromGoogleFormAsync(silent: false);
        }

        private void BtnSaveSyncSettings_Click(object sender, RoutedEventArgs e)
        {
            _config.PollingEnabled = ChkPollingEnabled.IsChecked == true;
            _config.SangsangLabFormUrl = TxtSangsangLabFormUrl.Text.Trim();
            _config.CabinetFormUrl = TxtCabinetFormUrl.Text.Trim();
            _config.EquipmentFormUrl = TxtEquipmentFormUrl.Text.Trim();

            if (int.TryParse(TxtPollingInterval.Text.Trim(), out int sec) && sec >= 15)
            {
                _config.PollingIntervalSeconds = sec;
            }
            else
            {
                MessageBox.Show("동기화 주기는 15초 이상의 숫자로 입력해주세요.", "설정 오류",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtPollingInterval.Text = _config.PollingIntervalSeconds.ToString();
                return;
            }

            // 폼/전용 칸에서 응답 시트 ID를 뽑아낸다. 폼 주소를 그대로 넣으면 Sheets API로는 읽을 수 없다.
            string mainSheetId = GoogleFormsService.ExtractSpreadsheetId(_config.SangsangLabFormUrl);
            string equipmentSheetId = GoogleFormsService.ExtractSpreadsheetId(_config.EquipmentFormUrl);
            string cabinetSheetId = GoogleFormsService.ExtractSpreadsheetId(_config.CabinetFormUrl);

            if (mainSheetId.Length == 0)
            {
                MessageBox.Show(
                    "상상Lab 신청 폼 URL 칸의 주소로는 시트를 읽을 수 없습니다.\n\n" +
                    (GoogleFormsService.IsFormUrl(_config.SangsangLabFormUrl)
                        ? "폼 편집 화면의 [응답] → [시트에서 보기]로 열리는 " : "") +
                    "스프레드시트 주소(docs.google.com/spreadsheets/d/...)를 붙여넣어 주세요.\n\n" +
                    "이 값이 없으면 상상Lab 신청과, 전용 시트를 지정하지 않은 캐비닛·기자재 신청을 전혀 받아올 수 없습니다.",
                    "설정 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _config.Save();
            if (_formsService != null)
            {
                _formsService.SpreadsheetId = mainSheetId;
                _formsService.EquipmentSpreadsheetId = equipmentSheetId;
                _formsService.CabinetSpreadsheetId = cabinetSheetId;
            }
            ApplyPollingTimer();

            var notes = new List<string>();
            foreach (string? note in new[]
                     {
                         DescribeDedicatedSheet("캐비닛", _config.CabinetFormUrl, cabinetSheetId),
                         DescribeDedicatedSheet("기자재", _config.EquipmentFormUrl, equipmentSheetId)
                     })
            {
                if (note != null) notes.Add(note);
            }

            string message = $"연동 설정을 저장했습니다. (메인 시트 ID: {mainSheetId})";
            if (notes.Count > 0)
            {
                message += "\n\n" + string.Join("\n\n", notes) +
                           "\n\n전용 시트도 서비스 계정에 '뷰어'로 공유되어 있어야 합니다.";
            }

            MessageBox.Show(message, "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 구분 전용 폼 칸에 넣은 값이 실제로 동기화에 쓰이는지 설명한다. 칸이 비어 있으면 null.
        /// </summary>
        private static string? DescribeDedicatedSheet(string label, string enteredUrl, string sheetId)
        {
            if (enteredUrl.Length == 0) return null;

            if (sheetId.Length > 0)
                return $"· {label} 신청은 별도 시트에서 받아옵니다. (시트 ID: {sheetId})";

            return $"· {label} 칸의 주소로는 시트를 읽을 수 없어, {label} 신청도 '상상Lab 신청 폼 URL' 시트에서만 받아옵니다.\n" +
                   "  " +
                   (GoogleFormsService.IsFormUrl(enteredUrl)
                       ? "폼 편집 화면의 [응답] → [시트에서 보기]로 열리는 "
                       : "") +
                   "스프레드시트 주소(docs.google.com/spreadsheets/d/...)를 붙여넣어 주세요.";
        }

        private void ChkPollingEnabled_Changed(object sender, RoutedEventArgs e)
        {
            // 초기화 도중(_formsService가 아직 없을 때)에는 무시
            if (_formsService == null) return;

            _config.PollingEnabled = ChkPollingEnabled.IsChecked == true;
            ApplyPollingTimer();
        }

        /// <param name="silent">true면 결과 팝업을 띄우지 않고 상태 표시줄만 갱신한다 (자동 폴링용).</param>
        private async Task SyncFromGoogleFormAsync(bool silent)
        {
            if (_formsService == null || _isSyncing) return;

            _isSyncing = true;
            BtnSyncNow.IsEnabled = false;
            SetSyncStatus("동기화 중...", false);

            try
            {
                var result = await _formsService.FetchAsync(key => _importedSourceKeys.Contains(key));

                if (!result.IsSuccess)
                {
                    SetSyncStatus($"[{DateTime.Now:HH:mm:ss}] {result.Error}", true);
                    if (!silent)
                        MessageBox.Show(result.Error, "동기화 실패", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 중복으로 자동 반려된 신청. 폴링 한 번에 여러 건이 걸릴 수 있으므로 모아서 한 번만 알린다.
                var autoRejected = new List<ApprovalRequest>();

                // 기자재 시트에서 아직 승인 전이라 이번에도 건너뛴 건수 (신규로 잡히지만 앱에는 안 들어간다)
                int equipmentPendingOnSheet = 0;

                foreach (var req in result.NewRequests)
                {
                    // 기자재 폼에는 이름 질문이 없다 — 같은 학번을 아는 곳에서 이름을 채워 넣는다.
                    // (마스터 → 다른 신청서 순. 끝내 못 찾으면 빈칸으로 두고 학번으로 식별한다)
                    if (string.IsNullOrWhiteSpace(req.StudentName))
                    {
                        req.StudentName =
                            _masterStudents.FirstOrDefault(m => m.StudentId == req.StudentId)?.Name
                            ?? _approvals.FirstOrDefault(a => a.StudentId == req.StudentId &&
                                                             !string.IsNullOrWhiteSpace(a.StudentName))?.StudentName
                            ?? string.Empty;
                    }

                    if (req.TabType == "기자재")
                    {
                        if (IsApprovedOnSheet(req.SheetApprovalStatus))
                        {
                            // 시트에서 승인됨을 확인했다 — 이제 앱이 넘겨받으므로 '처리 완료'로 기록해서
                            // 다음 동기화부터는 (앱에서 승인/반려하든 상관없이) 다시 새 신청으로 들어오지 않게 한다.
                            _importedSourceKeys.Add(req.SourceKey);
                            _approvals.Add(req);
                        }
                        else
                        {
                            // 아직 시트에서 승인 전이다 — SourceKey를 기록하지 않는다.
                            // 그래야 다음 동기화 때 이 행을 다시 읽어서 승인상태가 바뀌었는지 재확인할 수 있다.
                            equipmentPendingOnSheet++;
                        }
                        continue;
                    }

                    _importedSourceKeys.Add(req.SourceKey);

                    if (req.TabType == "캐비닛")
                    {
                        // 폼에 대여 기간 질문이 없으면 현재 선택된 학기의 기간으로 본다
                        if (string.IsNullOrWhiteSpace(req.RentalPeriod))
                            req.RentalPeriod = DefaultCabinetPeriod;

                        // 기간이 겹치지 않으면 '연장' 신청이므로 그대로 받는다.
                        // 겹치면 같은 학생이 같은 기간에 두 칸을 쓰게 되므로 자동 반려한다.
                        string reason = FindCabinetConflict(req);
                        if (reason.Length > 0)
                        {
                            req.Status = "반려";
                            req.Note = string.IsNullOrWhiteSpace(req.Note) ? reason : $"{req.Note} / {reason}";
                            _approvalHistory.Add(req);
                            autoRejected.Add(req);
                            continue;
                        }
                    }

                    _approvals.Add(req);
                }

                RefreshApprovalViews();

                // 새로 받아온 신청서(및 SourceKey)가 있으면 즉시 저장한다 —
                // 저장하지 않으면 다음 실행 때 같은 신청서를 또 새 신청서로 받아오게 된다.
                if (result.NewRequests.Count > 0) SaveAppState();

                if (autoRejected.Count > 0)
                {
                    string lines = string.Join("\n", autoRejected.Select(
                        r => $"• {r.StudentName} ({r.StudentId}) — {r.RentalPeriod}"));
                    MessageBox.Show(
                        $"아래 {autoRejected.Count}건은 캐비닛 배정 기간이 중복되어 자동으로 반려 처리되었습니다.\n\n{lines}\n\n" +
                        "[📋 승인/반려 내역]에서 확인하거나 복구할 수 있습니다.",
                        "배정 중복 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                int actuallyAdded = result.NewRequests.Count - equipmentPendingOnSheet;
                string summary = $"[{DateTime.Now:HH:mm:ss}] 응답 {result.TotalRows}건 중 신규 {actuallyAdded}건 반영";
                if (result.DuplicateCount > 0) summary += $" (기존 {result.DuplicateCount}건 건너뜀)";
                if (equipmentPendingOnSheet > 0) summary += $" (기자재 시트 승인 대기 {equipmentPendingOnSheet}건 — 다음 동기화 때 재확인)";

                // 현재 승인 대기 현황을 구분별로 보여준다
                var byType = _approvals.GroupBy(a => a.TabType)
                                       .Select(g => $"{g.Key} {g.Count()}건")
                                       .ToList();
                summary += $"\n승인 대기: {(byType.Count > 0 ? string.Join(" / ", byType) : "없음")}";

                if (result.MissingColumns.Count > 0)
                    summary += $"\n⚠ 헤더에서 찾지 못한 항목: {string.Join(", ", result.MissingColumns)}";

                if (result.UnmappedSpaces.Count > 0)
                    summary += $"\n⚠ 신청 공간을 판별하지 못한 값: {string.Join(" / ", result.UnmappedSpaces)} → '미분류'로 등록됨";

                foreach (string warning in result.Warnings)
                    summary += $"\n⚠ {warning}";

                SetSyncStatus(summary, result.MissingColumns.Count > 0 ||
                                       result.UnmappedSpaces.Count > 0 ||
                                       result.Warnings.Count > 0);

                if (!silent)
                {
                    MessageBox.Show(summary, "동기화 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (!silent && result.NewRequests.Count > 0)
                {
                    MessageBox.Show(
                        $"구글폼으로 새 신청 {result.NewRequests.Count}건이 접수되었습니다.\n\n" +
                        string.Join("\n", result.NewRequests.Take(5)
                            .Select(r => $"· {r.StudentName} ({r.StudentId}) - {r.TabType}")),
                        "구글폼 신청 도착", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            finally
            {
                _isSyncing = false;
                BtnSyncNow.IsEnabled = true;
            }
        }

        /// <summary>탭을 전환하지 않고 승인 대기 목록/뱃지만 다시 그린다.</summary>
        private void RefreshApprovalViews()
        {
            if (LstSangsangLabCards != null)
            {
                LstSangsangLabCards.ItemsSource = null;
                LstSangsangLabCards.ItemsSource = _approvals.Where(a => a.TabType == "상상Lab").ToList();
            }
            if (GridCabinetApprovals != null)
            {
                GridCabinetApprovals.ItemsSource = null;
                GridCabinetApprovals.ItemsSource = _approvals.Where(a => a.TabType == "캐비닛").ToList();
            }
            if (GridEquipmentApprovals != null)
            {
                GridEquipmentApprovals.ItemsSource = null;
                GridEquipmentApprovals.ItemsSource = _approvals.Where(a => a.TabType == "기자재").ToList();
            }

            UpdateAlertBadges();
        }

        private void SetSyncStatus(string message, bool isWarning)
        {
            if (TxtSyncStatus == null) return;
            TxtSyncStatus.Text = message;
            TxtSyncStatus.Foreground = new SolidColorBrush(
                isWarning ? Color.FromRgb(0xB4, 0x53, 0x09) : Color.FromRgb(0x6B, 0x72, 0x80));
        }

        // ================= NEW UI STUB EVENT HANDLERS & HELPERS =================

        private void BtnEquipmentDelete_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("기자재 데이터 삭제 기능 구현용 이벤트 핸들러입니다.", "기자재 삭제", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnEquipmentFix_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("기자재 데이터 고정 기능 구현용 이벤트 핸들러입니다.", "기자재 고정", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnEquipmentExport_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("기자재 데이터 추출 기능 구현용 이벤트 핸들러입니다.", "기자재 추출", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnCabinetDelete_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("캐비닛 데이터 삭제 기능 구현용 이벤트 핸들러입니다.", "캐비닛 삭제", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnCabinetFix_Click(object sender, RoutedEventArgs e)
        {
            _isCabinetFixed = !_isCabinetFixed;
            if (_isCabinetFixed)
            {
                BtnCabinetFix.Content = "🔓 캐비닛 고정 해제";
                BtnCabinetFix.Background = new SolidColorBrush(Color.FromRgb(254, 240, 138)); // Yellow indicator
                MessageBox.Show("캐비닛 배정 현황이 고정되었습니다. 고정 해제 전까지는 배정 변경, 이동 및 신규 배정이 불가능합니다.", "캐비닛 고정 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                BtnCabinetFix.Content = "캐비닛 데이터 고정";
                BtnCabinetFix.Background = Brushes.White;
                MessageBox.Show("캐비닛 배정 현황 고정이 해제되었습니다.", "캐비닛 고정 해제", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnCabinetExport_Click(object sender, RoutedEventArgs e)
        {
            if (_cabinetAllocations.Count == 0)
            {
                MessageBox.Show("추출할 캐비닛 배정 데이터가 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx",
                Title = "캐비닛 배정 현황 엑셀 추출",
                FileName = $"캐비닛_배정현황_{_currentSimulatedDate:yyyyMMdd}.xlsx"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var exportData = new List<Dictionary<string, object>>();
                    foreach (var kvp in _cabinetAllocations.OrderBy(x => x.Key))
                    {
                        var dict = new Dictionary<string, object>
                        {
                            { "캐비닛번호", kvp.Key },
                            { "학번", kvp.Value.Student?.StudentId ?? "" },
                            { "이름", kvp.Value.Student?.Name ?? "" },
                            { "소속", kvp.Value.Student?.Department ?? "" },
                            { "지도교수", kvp.Value.Student?.Advisor ?? "" },
                            { "이메일", kvp.Value.Student?.Email ?? "" },
                            { "대여기간", kvp.Value.Period }
                        };
                        exportData.Add(dict);
                    }

                    MiniExcelLibs.MiniExcel.SaveAs(dialog.FileName, exportData);
                    MessageBox.Show("캐비닛 배정 데이터가 성공적으로 엑셀 파일로 추출되었습니다.", "추출 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"파일 저장 중 오류가 발생했습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnViewSangsangLabDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is ApprovalRequest req)
            {
                // Create a temporary Seat model to match ShowStudentDetailsModal signature
                var dummySeat = new Seat
                {
                    SeatNumber = 0,
                    Student = new StudentInfo
                    {
                        StudentId = req.StudentId,
                        Name = req.StudentName,
                        Department = req.Department,
                        Advisor = req.Advisor,
                        Email = req.Email
                    }
                };
                ShowStudentDetailsModal(dummySeat);
            }
        }

        private void RenderCabinetGrid()
        {
            GridCabinetBlock1.Children.Clear();
            GridCabinetBlock2.Children.Clear();

            // Row 0: 1, 4, 7...
            // Row 1: 2, 5, 8...
            // Row 2: 3, 6, 9...
            int[] block1Numbers = new int[24];
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    int cabinetNum = (c * 3) + r + 1;
                    block1Numbers[r * 8 + c] = cabinetNum;
                }
            }

            foreach (int num in block1Numbers)
            {
                GridCabinetBlock1.Children.Add(CreateCabinetCell(num));
            }

            // Row 0: 25, 28, 31...
            int[] block2Numbers = new int[24];
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    int cabinetNum = (c * 3) + r + 25;
                    block2Numbers[r * 8 + c] = cabinetNum;
                }
            }

            foreach (int num in block2Numbers)
            {
                GridCabinetBlock2.Children.Add(CreateCabinetCell(num));
            }

            UpdateCabinetPage();
        }

        private Border CreateCabinetCell(int number)
        {
            Border border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                Padding = new Thickness(5),
                Background = Brushes.White
            };

            StackPanel stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            TextBlock numTxt = new TextBlock
            {
                Text = number.ToString(),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            stack.Children.Add(numTxt);

            bool isMatch = false;
            string query = TxtSearchCabinet != null ? TxtSearchCabinet.Text.Trim().ToLower() : "";
            bool hasQuery = !string.IsNullOrEmpty(query);

            if (_cabinetAllocations.ContainsKey(number))
            {
                var alloc = _cabinetAllocations[number];
                border.Background = new SolidColorBrush(Color.FromRgb(239, 246, 255));

                var range = ParsePeriodDates(alloc.Period);
                if (range != null)
                {
                    DateTime today = _currentSimulatedDate.Date;
                    int daysRemaining = (range.Value.End.Date - today).Days;
                    if (daysRemaining <= 7)
                    {
                        border.Background = new SolidColorBrush(Color.FromRgb(254, 226, 226)); // Soft red
                    }
                }

                isMatch = alloc.Student.Name.ToLower().Contains(query) || 
                          alloc.Student.StudentId.ToLower().Contains(query) || 
                          alloc.Student.Department.ToLower().Contains(query);

                // 칸이 좁아 학번과 이름을 한 줄에 붙이면 글자를 키울 수 없다 — 줄을 나눈다
                TextBlock idTxt = new TextBlock
                {
                    Text = alloc.Student.StudentId,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 3, 0, 0)
                };
                stack.Children.Add(idTxt);

                TextBlock nameTxt = new TextBlock
                {
                    Text = alloc.Student.Name,
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                stack.Children.Add(nameTxt);

                TextBlock periodTxt = new TextBlock
                {
                    Text = alloc.Period,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    // 기간이 길어 한 줄에 안 들어가면 잘라내지 말고 접는다
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                stack.Children.Add(periodTxt);
            }
            else
            {
                // 빈 칸은 번호만 보이므로 더 크게
                numTxt.FontSize = 20;
                numTxt.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
            }

            if (hasQuery)
            {
                if (isMatch)
                {
                    border.Opacity = 1.0;
                    border.BorderBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235));
                    border.BorderThickness = new Thickness(2);
                }
                else
                {
                    border.Opacity = 0.2;
                }
            }

            // 방금 옮겨온 자리는 검색 강조보다 우선해서 보여준다
            if (number == _highlightedCabinetNum)
            {
                border.Opacity = 1.0;
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(234, 88, 12));
                border.BorderThickness = new Thickness(3);
            }

            border.Tag = number;
            border.Cursor = Cursors.Hand;
            border.MouseDown += CabinetCell_MouseDown;

            border.Child = stack;
            return border;
        }

        private void CabinetCell_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is int number)
            {
                // 다른 칸을 건드리는 순간 직전 이동 강조는 걷어낸다
                if (_highlightedCabinetNum != 0)
                {
                    _highlightedCabinetNum = 0;
                    RenderCabinetGrid();
                }

                _currentEditingCabinetNum = number;
                if (_cabinetAllocations.ContainsKey(number))
                {
                    var alloc = _cabinetAllocations[number];
                    TxtCabinetModalTitle.Text = $"캐비닛 {number}번 상세 정보";
                    TxtCabinetModalName.Text = alloc.Student.Name;
                    TxtCabinetModalId.Text = alloc.Student.StudentId;
                    TxtCabinetModalPeriod.Text = alloc.Period;
                    
                    SetCabinetModalEditMode(false);
                    if (_isCabinetFixed)
                    {
                        BtnEditCabinetInfo.Visibility = Visibility.Collapsed;
                        BtnMoveCabinet.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        BtnEditCabinetInfo.Visibility = Visibility.Visible;
                        BtnMoveCabinet.Visibility = Visibility.Visible;
                    }
                    ModalCabinetDetails.Visibility = Visibility.Visible;
                }
                else
                {
                    if (_isCabinetFixed)
                    {
                        MessageBox.Show("캐비닛 데이터 고정 상태입니다. 고정 해제 전까지는 신규 배정이 불가능합니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    
                    var result = MessageBox.Show($"[캐비닛 {number}번] 사용 가능 (미배정) 상태입니다.\n이 캐비닛에 새로운 학생을 임의 배정하시겠습니까?", "캐비닛 배정", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        TxtCabinetModalTitle.Text = $"캐비닛 {number}번 임의 배정";
                        TxtCabinetModalName.Text = "";
                        TxtCabinetModalId.Text = "";
                        TxtCabinetModalPeriod.Text = DefaultCabinetPeriod;
                        
                        SetCabinetModalEditMode(true);
                        BtnMoveCabinet.Visibility = Visibility.Collapsed;
                        ModalCabinetDetails.Visibility = Visibility.Visible;
                    }
                }
            }
        }

        private void BtnCloseCabinetModal_Click(object sender, RoutedEventArgs e)
        {
            ModalCabinetDetails.Visibility = Visibility.Collapsed;
        }

        private void BtnEditCabinetInfo_Click(object sender, RoutedEventArgs e)
        {
            SetCabinetModalEditMode(!_isCabinetModalEditing);
        }

        private void SetCabinetModalEditMode(bool enable)
        {
            _isCabinetModalEditing = enable;
            TxtCabinetModalName.IsReadOnly = !enable;
            TxtCabinetModalId.IsReadOnly = !enable;
            TxtCabinetModalPeriod.IsReadOnly = !enable;

            if (enable)
            {
                TxtCabinetModalName.Background = Brushes.White; TxtCabinetModalName.BorderThickness = new Thickness(1);
                TxtCabinetModalId.Background = Brushes.White; TxtCabinetModalId.BorderThickness = new Thickness(1);
                TxtCabinetModalPeriod.Background = Brushes.White; TxtCabinetModalPeriod.BorderThickness = new Thickness(1);
                BtnSaveCabinetModal.Visibility = Visibility.Visible;
                BtnMoveCabinet.Visibility = Visibility.Collapsed;
                BtnSelectCabinetStudent.Visibility = Visibility.Visible;
                BtnEditCabinetInfo.Content = "취소";
            }
            else
            {
                TxtCabinetModalName.Background = Brushes.Transparent; TxtCabinetModalName.BorderThickness = new Thickness(0);
                TxtCabinetModalId.Background = Brushes.Transparent; TxtCabinetModalId.BorderThickness = new Thickness(0);
                TxtCabinetModalPeriod.Background = Brushes.Transparent; TxtCabinetModalPeriod.BorderThickness = new Thickness(0);
                BtnSaveCabinetModal.Visibility = Visibility.Collapsed;
                BtnSelectCabinetStudent.Visibility = Visibility.Collapsed;
                if (_cabinetAllocations.ContainsKey(_currentEditingCabinetNum) && !_isCabinetFixed)
                {
                    BtnMoveCabinet.Visibility = Visibility.Visible;
                }
                else
                {
                    BtnMoveCabinet.Visibility = Visibility.Collapsed;
                }
                
                if (_isCabinetFixed)
                {
                    BtnEditCabinetInfo.Visibility = Visibility.Collapsed;
                }
                else
                {
                    BtnEditCabinetInfo.Visibility = Visibility.Visible;
                    BtnEditCabinetInfo.Content = "정보 수정";
                }
            }
        }

        private void BtnSaveCabinetModal_Click(object sender, RoutedEventArgs e)
        {
            if (_currentEditingCabinetNum == 0) return;

            string name = TxtCabinetModalName.Text.Trim();
            string id = TxtCabinetModalId.Text.Trim();
            string period = TxtCabinetModalPeriod.Text.Trim();

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(period))
            {
                MessageBox.Show("모든 항목을 입력해 주세요.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 손으로 적은 기간도 YY.MM.DD ~ YY.MM.DD 로 맞춘다
            var typed = ParsePeriodDates(period);
            if (typed == null)
            {
                MessageBox.Show(
                    $"대여 기간을 읽지 못했습니다: {period}\n\n{FormatPeriod(_currentSimulatedDate, _currentSimulatedDate.AddMonths(1))} 형식(YY.MM.DD ~ YY.MM.DD)으로 입력해 주세요.",
                    "대여 기간 형식 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            period = FormatPeriod(typed.Value.Start, typed.Value.End);

            // 이미 이 칸에 있던 학생이거나 마스터에 있는 학번이면 그 기록을 이어 쓴다
            // (여기서 새로 만들면 [학생선택]으로 옮겨온 학생의 소속·지도교수가 지워진다)
            StudentInfo student;
            if (_cabinetAllocations.TryGetValue(_currentEditingCabinetNum, out var existing) &&
                existing.Student != null && existing.Student.StudentId == id)
            {
                student = existing.Student;
            }
            else
            {
                student = _masterStudents.FirstOrDefault(m => m.StudentId == id)?.Clone()
                          ?? new StudentInfo { StudentId = id, Department = "소프트웨어융합학과" };
            }
            student.Name = name;

            // Check if student ID is already assigned to another cabinet
            var duplicateCabinetNum = _cabinetAllocations.FirstOrDefault(kvp => kvp.Value.Student?.StudentId == id && kvp.Key != _currentEditingCabinetNum).Key;
            if (duplicateCabinetNum > 0)
            {
                MessageBox.Show($"해당 학번({id})은 이미 {duplicateCabinetNum}번 캐비닛에 배정되어 있습니다. 중복 배정할 수 없습니다.", "배정 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _cabinetAllocations[_currentEditingCabinetNum] = (student, period);
            
            RenderCabinetGrid();
            UpdateAlertBadges();
            SetCabinetModalEditMode(false);
            ModalCabinetDetails.Visibility = Visibility.Collapsed;
            
            MessageBox.Show("캐비닛 정보가 저장되었습니다.", "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>사용 중인 캐비닛의 학생을 비어 있는 다른 번호로 옮긴다.</summary>
        private void BtnMoveCabinet_Click(object sender, RoutedEventArgs e)
        {
            int from = _currentEditingCabinetNum;
            if (from == 0 || !_cabinetAllocations.TryGetValue(from, out var alloc)) return;

            var dialog = new CabinetSelectionDialog(_cabinetAllocations, TotalCabinetCount, movingFrom: from)
            {
                Owner = this,
                Title = $"캐비닛 {from}번 좌석 옮기기"
            };
            if (dialog.ShowDialog() != true) return;

            int to = dialog.SelectedCabinetNumber;
            if (to <= 0 || to == from) return;

            _cabinetAllocations.Remove(from);
            _cabinetAllocations[to] = alloc;

            // 옮긴 자리가 바로 보이도록 그 번호가 있는 페이지로 넘기고 잠시 강조한다
            // (1~24번은 1페이지, 25~48번은 2페이지 — RenderCabinetGrid의 블록 구성과 같다)
            _highlightedCabinetNum = to;
            _currentCabinetPage = to <= 24 ? 1 : 2;

            RenderCabinetGrid();
            UpdateAlertBadges();
            ModalCabinetDetails.Visibility = Visibility.Collapsed;

            string who = string.IsNullOrWhiteSpace(alloc.Student.Name)
                ? alloc.Student.StudentId
                : $"{alloc.Student.Name}({alloc.Student.StudentId})";

            MessageBox.Show($"{who} 학생의 좌석을 {from}번 → {to}번으로 옮겼습니다.",
                "좌석 이동 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void UpdateCabinetPage()
        {
            if (BorderCabinetBlock1 == null || BorderCabinetBlock2 == null || TxtCabinetPageNum == null) return;

            if (_currentCabinetPage == 1)
            {
                BorderCabinetBlock1.Visibility = Visibility.Visible;
                BorderCabinetBlock2.Visibility = Visibility.Collapsed;
                TxtCabinetPageNum.Text = "1 / 2";
            }
            else
            {
                BorderCabinetBlock1.Visibility = Visibility.Collapsed;
                BorderCabinetBlock2.Visibility = Visibility.Visible;
                TxtCabinetPageNum.Text = "2 / 2";
            }
        }

        private void BtnCabinetPrev_Click(object sender, RoutedEventArgs e)
        {
            _currentCabinetPage = _currentCabinetPage == 1 ? 2 : 1;
            UpdateCabinetPage();
        }

        private void BtnCabinetNext_Click(object sender, RoutedEventArgs e)
        {
            _currentCabinetPage = _currentCabinetPage == 2 ? 1 : 2;
            UpdateCabinetPage();
        }

        private void PerformStudentSearch()
        {
            // 시즌 필터 + 검색어를 한곳에서 함께 적용한다
            RefreshMasterGrid();
        }

        private void TxtSearchStudent_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                PerformStudentSearch();
            }
        }

        private void BtnSearchStudent_Click(object sender, RoutedEventArgs e) => PerformStudentSearch();

        private void BtnClearStudentSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearchStudent.Clear();
            RefreshMasterGrid();
        }

        private void ComboResolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!this.IsLoaded) return;
            if (ComboResolution == null) return;
            var selected = (ComboResolution.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (selected == "기본 (1600 x 900)")
            {
                this.Width = 1600;
                this.Height = 900;
            }
            else if (selected == "소형 (1024 x 768)")
            {
                this.Width = 1024;
                this.Height = 768;
            }
            else if (selected == "중형 (1200 x 780)")
            {
                this.Width = 1200;
                this.Height = 780;
            }
            else if (selected == "대형 (1920 x 1080)")
            {
                this.Width = 1920;
                this.Height = 1080;
            }
            else if (selected == "기타 (1200 x 780)")
            {
                this.Width = 1200;
                this.Height = 780;
            }
        }



        private void BindEquipmentRentals()
        {
            string query = TxtSearchEquipment != null ? TxtSearchEquipment.Text.Trim().ToLower() : "";
            var filtered = _rentals.Where(r => 
                string.IsNullOrEmpty(query) || 
                r.StudentName.ToLower().Contains(query) || 
                r.StudentId.ToLower().Contains(query) || 
                r.Department.ToLower().Contains(query) ||
                r.EquipmentType.ToLower().Contains(query)
            ).ToList();

            if (GridQuestRentals != null)
            {
                GridQuestRentals.ItemsSource = null;
                GridQuestRentals.ItemsSource = filtered.Where(r => r.IsMainframe).ToList();
            }
            if (GridLaptopRentals != null)
            {
                GridLaptopRentals.ItemsSource = null;
                GridLaptopRentals.ItemsSource = filtered.Where(r => !r.IsMainframe).ToList();
            }
        }

        // ================= 좌석 데이터 불러오기 (상상Labs 입실신청 응답 엑셀) =================
        private void BtnLoadSeatData_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|All Files (*.*)|*.*",
                Title = "좌석 데이터 불러오기 (상상Labs 입실신청 응답 엑셀)"
            };
            if (dialog.ShowDialog() != true) return;

            List<StudentInfo> imported;
            try
            {
                imported = ParseSangsangLabResponses(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"엑셀 파일을 읽는 도중 오류가 발생했습니다: {ex.Message}",
                    "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (imported.Count == 0)
            {
                MessageBox.Show(
                    "불러올 학생 정보를 찾지 못했습니다.\n상상Labs 입실신청(응답) 엑셀 양식(타임스탬프·소속·이름·학번·연락처·이메일·지도교수 열)인지 확인해주세요.",
                    "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string seasonLabel = _currentSeason != null
                ? $"{_currentSeason.Value.Year}년 {_currentSeason.Value.Season}"
                : "현재 시즌";

            var confirm = MessageBox.Show(
                $"{seasonLabel} 좌석 데이터를 불러온 명단 {imported.Count}명으로 교체합니다.\n" +
                "고정된 좌석은 그대로 유지됩니다.\n\n계속하시겠습니까?",
                "좌석데이터 불러오기", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            // 고정 좌석에 이미 앉아 있는 학번은 다시 배치하지 않는다
            var fixedIds = _activeSeats
                .Where(s => s.IsFixed && s.Student != null)
                .Select(s => s.Student!.StudentId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet();

            var queue = new Queue<StudentInfo>(
                imported.Where(st => string.IsNullOrEmpty(st.StudentId) || !fixedIds.Contains(st.StudentId)));

            // 고정이 아닌 좌석(기둥 제외)을 앞에서부터 순서대로 채우고, 남는 좌석은 비운다
            int placed = 0;
            foreach (var seat in _activeSeats.OrderBy(s => s.SeatNumber))
            {
                if (seat.IsPillar || seat.IsFixed) continue;

                if (queue.Count == 0)
                {
                    seat.Student = null;
                    continue;
                }

                var st = queue.Dequeue();
                seat.Student = st;
                if (st.Department.Contains("대학원")) seat.IsFixed = true; // 대학원생 좌석은 고정
                placed++;
            }

            RenderSeatGrid();
            SaveSeatCache();

            // 불러온 학생을 마스터 학생 데이터(데이터 관리 탭)에도 반영한다
            int addedToMaster = 0;
            int updatedInMaster = 0;
            foreach (var st in imported)
            {
                var master = !string.IsNullOrEmpty(st.StudentId)
                    ? _masterStudents.FirstOrDefault(m => m.StudentId == st.StudentId)
                    : _masterStudents.FirstOrDefault(m => m.Name == st.Name && m.Email == st.Email);

                if (master == null)
                {
                    var added = st.Clone();
                    added.LastActiveSeason = CurrentSeasonKey();
                    _masterStudents.Add(added);
                    addedToMaster++;
                    continue;
                }

                // 이번 시즌 좌석 명단으로 불러온 학생이므로 이번 시즌 활동자로 올린다
                master.LastActiveSeason = CurrentSeasonKey();

                // 이미 있으면 비어 있던 항목만 채워 넣는다 (기존 값은 덮어쓰지 않는다)
                bool changed = false;
                if (string.IsNullOrWhiteSpace(master.Name) && !string.IsNullOrWhiteSpace(st.Name)) { master.Name = st.Name; changed = true; }
                if (string.IsNullOrWhiteSpace(master.Department) && !string.IsNullOrWhiteSpace(st.Department)) { master.Department = st.Department; changed = true; }
                if (string.IsNullOrWhiteSpace(master.Advisor) && !string.IsNullOrWhiteSpace(st.Advisor)) { master.Advisor = st.Advisor; changed = true; }
                if (string.IsNullOrWhiteSpace(master.Email) && !string.IsNullOrWhiteSpace(st.Email)) { master.Email = st.Email; changed = true; }
                if (changed) updatedInMaster++;
            }

            if (addedToMaster > 0 || updatedInMaster > 0)
            {
                RefreshMasterGrid();
            }

            SaveAppState();
            UpdateAlertBadges();

            string leftover = queue.Count > 0
                ? $"\n\n좌석 수보다 {queue.Count}명이 많아 배치되지 못했습니다."
                : "";
            string masterMsg = $"\n학생 데이터: 신규 {addedToMaster}명 추가"
                + (updatedInMaster > 0 ? $", {updatedInMaster}명 정보 보완" : "");
            MessageBox.Show($"{seasonLabel} 좌석에 {placed}명을 배치했습니다.{masterMsg}{leftover}",
                "불러오기 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 상상Labs(U301) 입실신청 구글폼 응답 엑셀을 읽어 학생 목록으로 만든다.
        /// 열 순서: A 타임스탬프 / B 신청공간 / C 소속 / D 이름 / E 학번 / F 연락처 / G 이메일 / H 지도교수 / I 계획서 / J 기타.
        /// </summary>
        private static List<StudentInfo> ParseSangsangLabResponses(string path)
        {
            var rows = MiniExcelLibs.MiniExcel.Query(path).ToList();
            var result = new List<StudentInfo>();
            var seenIds = new HashSet<string>();

            foreach (IDictionary<string, object> row in rows)
            {
                var v = row.Values.ToList();
                string Cell(int i) => i < v.Count ? (v[i]?.ToString()?.Trim() ?? string.Empty) : string.Empty;

                string space = Cell(1);   // B
                string dept = Cell(2);    // C
                string name = Cell(3);    // D
                string idRaw = Cell(4);   // E
                string email = Cell(6);   // G
                string advisor = Cell(7); // H

                // 헤더 행 / 안내 문구 행 걸러내기
                if (name.Contains("이름을 작성") || dept.Contains("소속을 작성") ||
                    idRaw.Contains("학번") || space.Contains("공간을 선택"))
                    continue;

                string id = NormalizeStudentId(v.Count > 4 ? v[4] : null);
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id))
                    continue;

                // 상상Labs 이외의 응답이 섞여 있으면 건너뛴다 (공간 값이 비어 있으면 통과)
                if (!string.IsNullOrEmpty(space) && !space.Contains("상상") && !space.Contains("U301"))
                    continue;

                if (!string.IsNullOrEmpty(id) && !seenIds.Add(id))
                    continue; // 같은 학번 중복 제거 (마지막이 아니라 첫 응답 유지)

                advisor = advisor.Replace("교수님", "").Replace("교수", "").Trim();

                result.Add(new StudentInfo
                {
                    StudentId = id,
                    Name = name,
                    Department = string.IsNullOrWhiteSpace(dept) ? "소프트웨어전공" : dept,
                    Email = email,
                    Advisor = advisor
                });
            }

            return result;
        }

        /// <summary>학번 셀을 문자열로 정규화한다. 숫자형(2.02e7 등)으로 들어와도 정수 학번으로 되돌린다.</summary>
        private static string NormalizeStudentId(object? v)
        {
            if (v == null) return string.Empty;
            if (v is double d) return ((long)Math.Round(d)).ToString();

            string s = v.ToString()?.Trim() ?? string.Empty;
            if (s.Length == 0) return string.Empty;

            if ((s.Contains('E') || s.Contains('e') || s.Contains('.')) &&
                double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double dd))
                return ((long)Math.Round(dd)).ToString();

            return s;
        }

        private void BtnImportDashboardExcel_Click(object sender, RoutedEventArgs e)
        {
            var occupiedSeats = _activeSeats.Where(s => s.Student != null).OrderBy(s => s.SeatNumber).ToList();
            if (occupiedSeats.Count == 0)
            {
                MessageBox.Show("대시보드에 착석한 학생 정보가 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx",
                Title = "대시보드 학생 정보 엑셀 내보내기",
                FileName = $"대시보드_학생현황_{_currentSimulatedDate:yyyyMMdd}.xlsx"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var exportData = new List<Dictionary<string, object>>();
                    foreach (var seat in occupiedSeats)
                    {
                        var dict = new Dictionary<string, object>
                        {
                            { "좌석번호", seat.SeatNumber },
                            { "학번", seat.Student?.StudentId ?? "" },
                            { "이름", seat.Student?.Name ?? "" },
                            { "소속", seat.Student?.Department ?? "" },
                            { "지도교수", seat.Student?.Advisor ?? "" },
                            { "이메일", seat.Student?.Email ?? "" },
                            { "고정여부", seat.IsFixed ? "고정" : "미고정" }
                        };
                        exportData.Add(dict);
                    }

                    MiniExcelLibs.MiniExcel.SaveAs(dialog.FileName, exportData);
                    MessageBox.Show("좌석 학생 정보가 성공적으로 엑셀 파일로 저장되었습니다.", "내보내기 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"파일 저장 중 오류가 발생했습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnExportMainframeExcel_Click(object sender, RoutedEventArgs e)
        {
            ExportEquipmentExcel(true, "본체 (HP Z2 G9) 대여 현황");
        }

        private void BtnExportLaptopExcel_Click(object sender, RoutedEventArgs e)
        {
            ExportEquipmentExcel(false, "노트북(HP OMEN 게이밍 노트북) 대여 현황");
        }

        private void ExportEquipmentExcel(bool isMainframe, string targetEquipTitle)
        {
            var targetRentals = _rentals.Where(r => r.IsMainframe == isMainframe).ToList();
            if (targetRentals.Count == 0)
            {
                MessageBox.Show("추출할 대여 데이터가 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx",
                Title = $"{targetEquipTitle} 엑셀 추출",
                FileName = $"{targetEquipTitle}_{_currentSimulatedDate:yyyyMMdd}.xlsx"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var exportData = new List<Dictionary<string, object>>();
                    int no = 1;
                    foreach (var item in targetRentals)
                    {
                        var dict = new Dictionary<string, object>
                        {
                            { "번호", no++ },
                            { "수량", item.Quantity },
                            { "추가 대여물품(수량)", item.ExtraItems },
                            { "사용장소", item.Location },
                            { "사용목적", item.Purpose },
                            { "특이사항", item.Remarks },
                            { "전공", item.Department },
                            { "학년", item.YearLevel },
                            { "학번", item.StudentId },
                            { "연락처", item.Phone },
                            { "지도교수", item.Advisor },
                            { "성명", item.StudentName },
                            { "대여일", item.RentalDate.ToString("yyyy-MM-dd") },
                            { "반납예정일", item.DueDate.ToString("yyyy-MM-dd") },
                            { "반납일", item.ReturnDate?.ToString("yyyy-MM-dd") ?? "" }
                        };
                        exportData.Add(dict);
                    }

                    MiniExcelLibs.MiniExcel.SaveAs(dialog.FileName, exportData);
                    MessageBox.Show("대여 데이터가 성공적으로 엑셀 파일로 추출되었습니다.", "추출 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"파일 저장 중 오류가 발생했습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnImportMainframeExcel_Click(object sender, RoutedEventArgs e)
        {
            ImportEquipmentExcel("본체 (HP Z2 G9)");
        }

        private void BtnImportLaptopExcel_Click(object sender, RoutedEventArgs e)
        {
            ImportEquipmentExcel("노트북(HP OMEN 게이밍 노트북)");
        }

        // ===== 기자재 상태 관리 (고장 / 수리 / 기타) =====

        /// <summary>기자재 종류와 번호를 고르고 [고장/수리/기타] 중 하나로 상태를 등록한다.</summary>
        private void BtnEquipmentIssue_Click(object sender, RoutedEventArgs e)
        {
            // 대여 중인 개체는 고를 수 없도록 지금 나가 있는 목록을 넘긴다
            var activeRentals = _rentals.Where(r => !r.IsReturned).ToList();

            var dialog = new EquipmentIssueDialog(activeRentals, _equipmentIssues.ToList(), TotalMainframeCount, TotalLaptopCount) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            _equipmentIssues.Insert(0, new EquipmentIssue
            {
                EquipmentName = dialog.SelectedEquipmentName,
                UnitNumber = dialog.SelectedUnitNumber,
                IssueType = dialog.SelectedIssueType,
                Detail = dialog.Detail
            });

            // 방금 등록한 줄을 바로 보여준다
            GridEquipmentIssues.SelectedIndex = 0;
            GridEquipmentIssues.ScrollIntoView(_equipmentIssues[0]);
        }

        /// <summary>목록을 클릭하면 열리는 [처리 완료] 버튼. 해당 줄을 목록에서 없앤다.</summary>
        private void BtnIssueComplete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.CommandParameter is not EquipmentIssue issue) return;

            var result = MessageBox.Show(
                $"[{issue.DisplayUnit}] {issue.DisplayDetail}\n\n처리 완료로 목록에서 지울까요?",
                "처리 완료", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _equipmentIssues.Remove(issue);
        }

        private void ImportEquipmentExcel(string targetEquipType)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|All Files (*.*)|*.*",
                Title = $"{targetEquipType} 대여 현황 엑셀 가져오기"
            };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var rows = MiniExcelLibs.MiniExcel.Query(dialog.FileName).ToList();
                    int importedCount = 0;
                    foreach (IDictionary<string, object> row in rows)
                    {
                        var values = row.Values.ToList();
                        if (values.Count < 2) continue;

                        string col0 = values[0]?.ToString() ?? "";
                        if (col0 == "번호" || col0 == "No" || col0 == "No." || string.IsNullOrWhiteSpace(col0)) continue;

                        // Skip rows with missing StudentId (Column 8 / Index 8) or StudentName (Column 11 / Index 11)
                        string id = values.Count > 8 ? (values[8]?.ToString() ?? "") : "";
                        string studentName = values.Count > 11 ? (values[11]?.ToString() ?? "") : "";
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(studentName)) continue;

                        int.TryParse(values.Count > 1 ? (values[1]?.ToString() ?? "") : "", out int qty);
                        string extra = values.Count > 2 ? (values[2]?.ToString() ?? "") : "";
                        string loc = values.Count > 3 ? (values[3]?.ToString() ?? "") : "";
                        string purp = values.Count > 4 ? (values[4]?.ToString() ?? "") : "";
                        string rem = values.Count > 5 ? (values[5]?.ToString() ?? "") : "";
                        string dept = values.Count > 6 ? (values[6]?.ToString() ?? "") : "";
                        string level = values.Count > 7 ? (values[7]?.ToString() ?? "") : "";
                        string phone = values.Count > 9 ? (values[9]?.ToString() ?? "") : "";
                        string advisor = values.Count > 10 ? (values[10]?.ToString() ?? "") : "";

                        DateTime rentalDate = _currentSimulatedDate;
                        if (values.Count > 12 && DateTime.TryParse(values[12]?.ToString() ?? "", out var rd)) rentalDate = rd;

                        int period = 7;
                        if (values.Count > 13 && DateTime.TryParse(values[13]?.ToString() ?? "", out var dd))
                        {
                            period = (dd - rentalDate).Days;
                            if (period <= 0) period = 7;
                        }

                        DateTime? retDate = null;
                        if (values.Count > 14 && DateTime.TryParse(values[14]?.ToString() ?? "", out var retd)) retDate = retd;

                        string equipType = targetEquipType;

                        var rental = new RentalItem
                        {
                            Quantity = qty,
                            ExtraItems = extra,
                            Location = loc,
                            Purpose = purp,
                            Remarks = rem,
                            Department = dept,
                            YearLevel = level,
                            StudentId = id,
                            Phone = phone,
                            Advisor = advisor,
                            StudentName = studentName,
                            RentalDate = rentalDate,
                            RentalPeriodDays = period,
                            ReturnDate = retDate,
                            EquipmentType = equipType,
                            IsReturned = retDate != null
                        };

                        _rentals.Add(rental);
                        _rentalHistory.Add(rental);
                        importedCount++;
                    }

                    BindEquipmentRentals();
                    UpdateAlertBadges();
                    MessageBox.Show($"엑셀 파일로부터 {importedCount}건의 기자재 대여 내역을 추가했습니다.", "가져오기 성공", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"엑셀 파일을 읽는 도중 오류가 발생했습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnImportCabinetExcel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|All Files (*.*)|*.*",
                Title = "캐비닛 배정 현황 엑셀 가져오기"
            };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var rows = MiniExcelLibs.MiniExcel.Query(dialog.FileName).ToList();
                    int importedCount = 0;
                    foreach (IDictionary<string, object> row in rows)
                    {
                        var values = row.Values.ToList();
                        if (values.Count < 2) continue;

                        string col0 = values[0]?.ToString() ?? "";
                        if (col0 == "CabinetNumber" || col0 == "캐비닛번호" || string.IsNullOrWhiteSpace(col0)) continue;

                        int.TryParse(col0, out int cabNum);
                        if (cabNum <= 0 || cabNum > 48) continue;

                        string id = values.Count > 1 ? (values[1]?.ToString() ?? "") : "";
                        string name = values.Count > 2 ? (values[2]?.ToString() ?? "") : "";
                        string dept = values.Count > 3 ? (values[3]?.ToString() ?? "") : "소프트웨어융합학과";
                        string period = values.Count > 4 ? (values[4]?.ToString() ?? "") : DefaultCabinetPeriod;

                        var student = new StudentInfo
                        {
                            StudentId = id,
                            Name = name,
                            Department = dept
                        };

                        // Check if student already has another cabinet assigned during import
                        var duplicateCabKey = _cabinetAllocations.FirstOrDefault(kvp => kvp.Value.Student?.StudentId == id && kvp.Key != cabNum).Key;
                        if (duplicateCabKey > 0)
                        {
                            _cabinetAllocations.Remove(duplicateCabKey);
                        }

                        _cabinetAllocations[cabNum] = (student, period);
                        importedCount++;
                    }

                    RenderCabinetGrid();
                    UpdateAlertBadges();
                    MessageBox.Show($"엑셀 파일로부터 {importedCount}개의 캐비닛 배정을 완료했습니다.", "가져오기 성공", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"엑셀 파일을 읽는 도중 오류가 발생했습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnImportDataManageExcel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|All Files (*.*)|*.*",
                Title = "마스터 학생 데이터베이스 엑셀 가져오기"
            };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var rows = MiniExcelLibs.MiniExcel.Query(dialog.FileName).ToList();
                    int importedCount = 0;
                    foreach (IDictionary<string, object> row in rows)
                    {
                        var values = row.Values.ToList();
                        if (values.Count < 2) continue;

                        string col0 = values[0]?.ToString() ?? "";
                        if (col0 == "StudentId" || col0 == "학번" || string.IsNullOrWhiteSpace(col0)) continue;

                        string id = col0;
                        string name = values.Count > 1 ? (values[1]?.ToString() ?? "") : "";
                        string dept = values.Count > 2 ? (values[2]?.ToString() ?? "") : "소프트웨어융합학과";
                        string advisor = values.Count > 3 ? (values[3]?.ToString() ?? "") : "김동욱 교수";
                        string email = values.Count > 4 ? (values[4]?.ToString() ?? "") : "";

                        if (!_masterStudents.Any(m => m.StudentId == id))
                        {
                            var student = new StudentInfo
                            {
                                StudentId = id,
                                Name = name,
                                Department = dept,
                                Advisor = advisor,
                                Email = email,
                                LastActiveSeason = CurrentSeasonKey()
                            };
                            _masterStudents.Add(student);
                            importedCount++;
                        }
                    }

                    RefreshMasterGrid();
                    SaveAppState();
                    MessageBox.Show($"엑셀 파일로부터 {importedCount}명의 마스터 학생 정보를 등록했습니다.", "가져오기 성공", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"엑셀 파일을 읽는 도중 오류가 발생했습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnDeleteAllStudents_Click(object sender, RoutedEventArgs e)
        {
            if (_masterStudents.Count == 0)
            {
                MessageBox.Show("삭제할 학생 정보가 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"등록된 모든 학생 정보({_masterStudents.Count}명)를 삭제하시겠습니까? 이 작업은 되돌릴 수 없습니다.", "전체 삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                _masterStudents.Clear();
                RefreshMasterGrid();
                SaveAppState();
                MessageBox.Show("모든 학생 정보가 삭제되었습니다.", "삭제 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void TxtSearchEquipment_KeyUp(object sender, KeyEventArgs e)
        {
            BindEquipmentRentals();
        }

        private void TxtSearchCabinet_KeyUp(object sender, KeyEventArgs e)
        {
            _highlightedCabinetNum = 0;
            RenderCabinetGrid();
        }

        private void BtnDeleteSelectedRental_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = new List<RentalItem>();
            if (GridQuestRentals != null)
            {
                foreach (var item in GridQuestRentals.SelectedItems)
                {
                    if (item is RentalItem rental)
                        selectedItems.Add(rental);
                }
            }
            if (GridLaptopRentals != null)
            {
                foreach (var item in GridLaptopRentals.SelectedItems)
                {
                    if (item is RentalItem rental)
                        selectedItems.Add(rental);
                }
            }

            if (selectedItems.Count == 0)
            {
                MessageBox.Show("삭제할 대여 데이터를 목록에서 먼저 선택해 주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"선택한 {selectedItems.Count}개의 대여 데이터를 삭제하시겠습니까?", "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                foreach (var item in selectedItems)
                {
                    _rentals.Remove(item);
                }
                BindEquipmentRentals();
                UpdateAlertBadges();
                MessageBox.Show("선택한 대여 데이터가 삭제되었습니다.", "삭제 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnDownloadTemplate_Click(object sender, RoutedEventArgs e)
        {
            string fileName = "2026-여름방학_기자제 (노트북(HP OMEN 게이밍 노트북) 대여 현황서양식.xlsx";
            string[] possiblePaths = new[]
            {
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SpinnerApp", fileName),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "SpinnerApp", fileName),
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "SpinnerApp", fileName),
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), fileName)
            };

            string sourcePath = "";
            foreach (var p in possiblePaths)
            {
                if (System.IO.File.Exists(p))
                {
                    sourcePath = p;
                    break;
                }
            }

            if (string.IsNullOrEmpty(sourcePath))
            {
                MessageBox.Show("기자재 대여 양식 파일을 찾을 수 없습니다. (SpinnerApp 폴더에 파일이 존재하는지 확인해 주세요)", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx",
                Title = "기자재 대여 양식 다운로드",
                FileName = fileName
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    System.IO.File.Copy(sourcePath, dialog.FileName, true);
                    MessageBox.Show("기자재 대여 템플릿 양식 파일이 성공적으로 다운로드되었습니다.", "다운로드 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"파일 저장 중 오류가 발생했습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ================= STUDENT SELECTOR OVERLAY LOGIC =================

        /// <summary>학생 선택 오버레이를 좌석용으로 열었는지 캐비닛용으로 열었는지. 같은 오버레이를 둘이 나눠 쓴다.</summary>
        private bool _studentSelectorForCabinet = false;

        private void BtnSelectStudent_Click(object sender, RoutedEventArgs e)
        {
            _studentSelectorForCabinet = false;
            OpenStudentSelector();
        }

        /// <summary>캐비닛 상세 모달의 [학생선택] — 고른 학생을 이 캐비닛으로 옮긴다.</summary>
        private void BtnSelectCabinetStudent_Click(object sender, RoutedEventArgs e)
        {
            _studentSelectorForCabinet = true;
            OpenStudentSelector();
        }

        private void OpenStudentSelector()
        {
            TxtSearchStudentSelector.Clear();
            SyncStudentSelectorColumns();

            GridStudentSelector.ItemsSource = null;
            GridStudentSelector.ItemsSource = _masterStudents;
            ModalStudentListSelector.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 캐비닛에서 열었으면 학번·이름·대여 기간만 보여준다 (소속은 캐비닛 배정과 무관하다).
        /// 좌석에서 열었으면 종전대로 소속을 보여준다.
        /// </summary>
        private void SyncStudentSelectorColumns()
        {
            UpdateMasterCabinetPeriods();

            ColStudentDept.Visibility = _studentSelectorForCabinet ? Visibility.Collapsed : Visibility.Visible;
            ColStudentCabinetPeriod.Visibility = _studentSelectorForCabinet ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnCloseStudentSelector_Click(object sender, RoutedEventArgs e)
        {
            ModalStudentListSelector.Visibility = Visibility.Collapsed;
        }

        private void TxtSearchStudentSelector_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterStudentSelectorGrid();
        }

        private void BtnSearchStudentSelector_Click(object sender, RoutedEventArgs e)
        {
            FilterStudentSelectorGrid();
        }

        private void FilterStudentSelectorGrid()
        {
            string query = TxtSearchStudentSelector.Text.Trim();
            SyncStudentSelectorColumns();

            GridStudentSelector.ItemsSource = null;
            if (string.IsNullOrEmpty(query))
            {
                GridStudentSelector.ItemsSource = _masterStudents;
            }
            else
            {
                GridStudentSelector.ItemsSource = _masterStudents
                    .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || 
                                s.StudentId.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        private void GridStudentSelector_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ConfirmStudentSelection();
        }

        private void BtnConfirmStudentSelection_Click(object sender, RoutedEventArgs e)
        {
            ConfirmStudentSelection();
        }

        private void ConfirmStudentSelection()
        {
            if (_studentSelectorForCabinet)
            {
                if (GridStudentSelector.SelectedItem is StudentInfo pick)
                    MoveStudentToCabinet(pick);
                else
                    MessageBox.Show("배정할 학생을 먼저 선택하세요.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (GridStudentSelector.SelectedItem is StudentInfo selected && _currentEditingSeat != null)
            {
                var targetSeat = _currentEditingSeat;

                // 고른 학생이 지금 앉아 있는 다른 좌석 (없으면 null)
                var sourceSeat = _activeSeats.FirstOrDefault(s =>
                    s.Student != null && s.Student.StudentId == selected.StudentId && s.SeatNumber != targetSeat.SeatNumber);

                // 이미 이 좌석에 그 학생이 앉아 있으면 할 일이 없다
                if (sourceSeat == null && targetSeat.Student != null && targetSeat.Student.StudentId == selected.StudentId)
                {
                    ModalStudentListSelector.Visibility = Visibility.Collapsed;
                    return;
                }

                // 대상 좌석에 원래 있던 학생 (없으면 null) — 삭제하지 않고 자리를 맞바꾼다
                var occupant = targetSeat.Student;

                if (occupant != null)
                {
                    string occName = string.IsNullOrWhiteSpace(occupant.Name) ? occupant.StudentId : occupant.Name;
                    string ask = sourceSeat != null
                        ? $"{targetSeat.SeatNumber}번 좌석에는 {occName} 학생이 있습니다.\n\n" +
                          $"{selected.Name} 학생과 자리를 맞바꿀까요?\n" +
                          $"→ {selected.Name}: {sourceSeat.SeatNumber}번 → {targetSeat.SeatNumber}번 / {occName}: {targetSeat.SeatNumber}번 → {sourceSeat.SeatNumber}번"
                        : $"{targetSeat.SeatNumber}번 좌석에는 {occName} 학생이 있습니다.\n\n" +
                          $"{occName} 학생을 빼고 {selected.Name} 학생을 배정할까요?";

                    if (MessageBox.Show(ask, "좌석 사용 중", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                        return;
                }

                // 옮겨 갈 실제 인스턴스 — 이미 앉아 있었다면 그 자리에 있던 객체를 그대로 옮긴다
                var movingIn = sourceSeat?.Student ?? selected.Clone();

                if (sourceSeat != null)
                {
                    // 원래 있던 학생을 고른 학생의 옛 자리로 (양쪽 다 있으면 맞교환, 비어 있으면 이동)
                    sourceSeat.Student = occupant;
                    sourceSeat.IsFixed = occupant != null && occupant.Department.Contains("대학원");
                }

                targetSeat.Student = movingIn;
                targetSeat.IsFixed = movingIn.Department.Contains("대학원");

                // 이번 시즌 좌석에 앉혔으므로 마스터에서도 이번 시즌 활동자로 표시한다
                MarkStudentActiveThisSeason(movingIn);
                MarkStudentActiveThisSeason(occupant);

                // Update UI fields in parent modal
                TxtModalName.Text = movingIn.Name;
                TxtModalId.Text = movingIn.StudentId;
                TxtModalDept.Text = movingIn.Department;
                TxtModalAdvisor.Text = movingIn.Advisor;
                TxtModalEmail.Text = movingIn.Email;
                ItemsModalAttendance.ItemsSource = movingIn.Attendance;

                RenderSeatGrid();
                UpdateAlertBadges();

                ModalStudentListSelector.Visibility = Visibility.Collapsed;

                string msg = (occupant != null && sourceSeat != null)
                    ? $"{selected.Name} 학생과 {(string.IsNullOrWhiteSpace(occupant.Name) ? occupant.StudentId : occupant.Name)} 학생의 자리를 맞바꿨습니다. '저장'을 누르면 최종 적용됩니다."
                    : $"학생 '{selected.Name}'이(가) 좌석에 배정되었습니다. '저장'을 누르면 최종 적용됩니다.";
                MessageBox.Show(msg, "좌석 배정", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("배정할 학생을 먼저 선택하세요.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 고른 학생을 지금 열어 둔 캐비닛으로 옮긴다 (대시보드의 좌석 배정과 같은 흐름).
        /// 그 학생이 쓰던 캐비닛은 비우고, 옮겨갈 자리에 다른 학생이 있으면 맞바꿀지 물어본다.
        /// </summary>
        private void MoveStudentToCabinet(StudentInfo selected)
        {
            int target = _currentEditingCabinetNum;
            if (target == 0) return;

            // 이 학생이 이미 쓰고 있는 캐비닛 (없으면 0)
            int from = 0;
            foreach (var kvp in _cabinetAllocations)
            {
                if (kvp.Value.Student?.StudentId == selected.StudentId) { from = kvp.Key; break; }
            }

            if (from == target)
            {
                MessageBox.Show($"{selected.Name} 학생은 이미 {target}번 캐비닛을 쓰고 있습니다.",
                    "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                ModalStudentListSelector.Visibility = Visibility.Collapsed;
                return;
            }

            // 옮기는 경우에는 쓰던 대여 기간을 그대로 가져가고, 새 배정이면 선택된 학기의 기간을 준다
            string period = from > 0 ? _cabinetAllocations[from].Period : DefaultCabinetPeriod;

            bool targetOccupied = _cabinetAllocations.TryGetValue(target, out var occupant);
            if (targetOccupied)
            {
                string occupantName = occupant.Student == null
                    ? "다른 학생"
                    : string.IsNullOrWhiteSpace(occupant.Student.Name)
                        ? occupant.Student.StudentId
                        : occupant.Student.Name;

                string ask = from > 0
                    ? $"{target}번은 {occupantName} 학생이 쓰고 있습니다.\n\n" +
                      $"{selected.Name}({from}번)와 자리를 맞바꿀까요?\n" +
                      $"→ {selected.Name}: {from}번 → {target}번 / {occupantName}: {target}번 → {from}번"
                    : $"{target}번은 {occupantName} 학생이 쓰고 있습니다.\n\n" +
                      $"{occupantName} 학생을 빼고 {selected.Name} 학생을 넣을까요?";

                if (MessageBox.Show(ask, "캐비닛 사용 중", MessageBoxButton.YesNo, MessageBoxImage.Question)
                    != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            if (from > 0) _cabinetAllocations.Remove(from);

            // 양쪽 다 사용 중이면 맞바꾸고, 아니면 원래 쓰던 학생은 배정에서 빠진다
            if (targetOccupied && from > 0) _cabinetAllocations[from] = occupant;

            _cabinetAllocations[target] = (selected.Clone(), period);

            // 모달을 옮겨온 학생 정보로 갱신 (기간을 고치고 [저장]할 수 있게 편집 상태는 유지한다)
            TxtCabinetModalTitle.Text = $"캐비닛 {target}번 상세 정보";
            TxtCabinetModalName.Text = selected.Name;
            TxtCabinetModalId.Text = selected.StudentId;
            TxtCabinetModalPeriod.Text = period;

            _highlightedCabinetNum = target;
            _currentCabinetPage = target <= 24 ? 1 : 2;

            RenderCabinetGrid();
            UpdateAlertBadges();
            ModalStudentListSelector.Visibility = Visibility.Collapsed;

            string done = from > 0
                ? $"{selected.Name} 학생을 {from}번 → {target}번 캐비닛으로 옮겼습니다."
                : $"{selected.Name} 학생을 {target}번 캐비닛에 배정했습니다.";

            MessageBox.Show(done, "캐비닛 배정 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ================= APPROVAL HISTORY LOGIC =================

        /// <summary>지금 내역 창이 보여주고 있는 구분("기자재"/"캐비닛"/"상상Lab"). 빈 값이면 전체.</summary>
        private string _historyScope = string.Empty;

        /// <summary>버튼의 Tag에 적힌 구분만 보여준다 — 탭마다 자기 내역만 확인하도록.</summary>
        private void BtnShowApprovalHistory_Click(object sender, RoutedEventArgs e)
        {
            _historyScope = (sender as Button)?.Tag?.ToString() ?? string.Empty;

            TxtApprovalHistoryTitle.Text = _historyScope.Length == 0
                ? "승인/반려 내역 확인"
                : $"{_historyScope} 승인/반려 내역 확인";

            RefreshApprovalHistoryGrid();
            ModalApprovalHistory.Visibility = Visibility.Visible;
        }

        private void RefreshApprovalHistoryGrid()
        {
            var items = _historyScope.Length == 0
                ? _approvalHistory.ToList()
                : _approvalHistory.Where(a => a.TabType == _historyScope).ToList();

            GridApprovalHistory.ItemsSource = null;
            GridApprovalHistory.ItemsSource = items;
        }

        private void BtnCloseApprovalHistory_Click(object sender, RoutedEventArgs e)
        {
            ModalApprovalHistory.Visibility = Visibility.Collapsed;
        }

        private void BtnRestoreFromHistory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is ApprovalRequest req)
            {
                MessageBoxResult res = MessageBox.Show($"'{req.StudentName}' 학생의 내역을 대기 목록으로 되돌리시겠습니까?", "복구 확인", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    // Clean up allocations if request was "승인 완료"
                    if (req.Status == "승인 완료")
                    {
                        if (req.TabType == "상상Lab")
                        {
                            // Clear student from active seats
                            var seat = _activeSeats.FirstOrDefault(s => s.Student?.StudentId == req.StudentId);
                            if (seat != null)
                            {
                                seat.Student = null;
                                seat.IsFixed = false;
                            }
                        }
                        else if (req.TabType == "캐비닛")
                        {
                            // Clear cabinet allocation
                            var keyToRemove = _cabinetAllocations.FirstOrDefault(kvp => kvp.Value.Student?.StudentId == req.StudentId).Key;
                            if (keyToRemove > 0)
                            {
                                _cabinetAllocations.Remove(keyToRemove);
                            }
                        }
                        else if (req.TabType == "기자재")
                        {
                            // Remove from active rentals
                            var rentalToRemove = _rentals.FirstOrDefault(r => r.StudentName == req.StudentName);
                            if (rentalToRemove != null)
                            {
                                _rentals.Remove(rentalToRemove);
                            }
                        }
                    }

                    // Restore status to pending and move collections
                    req.Status = "승인 대기";
                    _approvalHistory.Remove(req);
                    _approvals.Add(req);

                    // Refresh grids
                    RefreshApprovalHistoryGrid();

                    // Switch tab according to request type to let the user see it
                    Button targetBtn;
                    Grid targetTab;
                    string targetTitle;

                    if (req.TabType == "상상Lab")
                    {
                        targetBtn = BtnSangsangLab;
                        targetTab = TabSangsangLab;
                        targetTitle = "상상Lab 승인";
                    }
                    else if (req.TabType == "기자재")
                    {
                        targetBtn = BtnEquipment;
                        targetTab = TabEquipment;
                        targetTitle = "기자재 현황";
                    }
                    else
                    {
                        targetBtn = BtnCabinet;
                        targetTab = TabCabinet;
                        targetTitle = "캐비닛 현황";
                    }

                    SwitchTab(targetBtn, targetTab, targetTitle);
                    RenderSeatGrid();
                    UpdateAlertBadges();

                    ModalApprovalHistory.Visibility = Visibility.Collapsed;
                    MessageBox.Show($"신청 내역이 대기 목록으로 성공적으로 복구되었습니다.", "복구 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        // ================= RENTAL HISTORY LOGIC =================
        private void BtnRentalHistory_Click(object sender, RoutedEventArgs e)
        {
            GridRentalHistory.ItemsSource = null;
            GridRentalHistory.ItemsSource = _rentalHistory;
            ModalRentalHistory.Visibility = Visibility.Visible;
        }

        private void BtnCloseRentalHistory_Click(object sender, RoutedEventArgs e)
        {
            ModalRentalHistory.Visibility = Visibility.Collapsed;
        }

        // ================= 대여 학기 / 대여 기간 =================

        /// <summary>
        /// [1학기/여름방학/2학기/겨울방학] 선택에 따라 새 배정에 기본으로 붙일 대여 기간을 정한다.
        /// 학사일정 기준: 1학기 3/2~6/20, 하계방학 6/21~8/31, 2학기 9/1~12/20, 동계방학 12/21~2월 말.
        /// </summary>
        private void RadioSemester_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton radio) return;

            int year = _currentSimulatedDate.Year;
            string content = radio.Content?.ToString() ?? string.Empty;

            if (content.Contains("1학기"))
                _activeCabinetPeriod = FormatPeriod(new DateTime(year, 3, 2), new DateTime(year, 6, 20));
            else if (content.Contains("여름방학"))
                _activeCabinetPeriod = FormatPeriod(new DateTime(year, 6, 21), new DateTime(year, 8, 31));
            else if (content.Contains("2학기"))
                _activeCabinetPeriod = FormatPeriod(new DateTime(year, 9, 1), new DateTime(year, 12, 20));
            else if (content.Contains("겨울방학"))
            {
                // 해를 넘기므로 다음 해 2월 말일까지 (윤년이면 29일)
                int endYear = year + 1;
                var end = new DateTime(endYear, 2, DateTime.DaysInMonth(endYear, 2));
                _activeCabinetPeriod = FormatPeriod(new DateTime(year, 12, 21), end);
            }

            // 이미 열려 있는 화면의 기본 기간 표시를 새 학기 기준으로 맞춘다
            if (TxtCabinetModalPeriod != null && _isCabinetModalEditing &&
                !_cabinetAllocations.ContainsKey(_currentEditingCabinetNum))
            {
                TxtCabinetModalPeriod.Text = _activeCabinetPeriod;
            }
        }

        /// <summary>대여 기간 표기는 YY.MM.DD ~ YY.MM.DD 로 통일한다.</summary>
        private static string FormatPeriod(DateTime start, DateTime end) =>
            $"{start:yy.MM.dd} ~ {end:yy.MM.dd}";

        /// <summary>
        /// 지금 진행 중인 시즌의 종료일 (설정 탭의 시즌 일정 기준).
        /// 시즌이 판별되지 않거나 일정이 없으면 null.
        /// </summary>
        private DateTime? CurrentSeasonEndDate()
        {
            if (_currentSeason == null) return null;

            var schedule = _seasonConfig.GetYear(_currentSeason.Value.Year);
            var period = schedule?.Seasons.FirstOrDefault(p => p.Name == _currentSeason.Value.Season);
            return period?.EndDate;
        }

        /// <summary>
        /// 캐비닛 대여 기간 — 오늘(승인을 누른 날)부터 이번 시즌 마지막 날까지.
        /// 시즌 일정이 없거나 이미 시즌이 끝난 날짜면 오늘부터 한 달로 둔다.
        /// </summary>
        private string SeasonCabinetPeriod()
        {
            DateTime? end = CurrentSeasonEndDate();
            if (end == null || end.Value.Date < _currentSimulatedDate.Date)
                return FormatPeriod(_currentSimulatedDate, _currentSimulatedDate.AddMonths(1));

            return FormatPeriod(_currentSimulatedDate, end.Value);
        }

        /// <summary>
        /// 기본 대여 기간. 설정 탭의 시즌 일정(오늘 ~ 시즌 종료일)을 우선 쓰고,
        /// 시즌 일정이 없을 때만 학기 라디오 버튼으로 고른 기간을 쓴다.
        /// </summary>
        private string DefaultCabinetPeriod
        {
            get
            {
                if (CurrentSeasonEndDate() != null) return SeasonCabinetPeriod();

                return string.IsNullOrEmpty(_activeCabinetPeriod)
                    ? FormatPeriod(_currentSimulatedDate, _currentSimulatedDate.AddMonths(1))
                    : _activeCabinetPeriod;
            }
        }

        /// <summary>
        /// 두 대여 기간이 겹치는지. 겹치지 않아야 같은 학생의 추가 신청을 '연장'으로 받아줄 수 있다.
        /// 한쪽이 비어 있으면 안전하게 겹친다고 본다 (모르는 채로 중복 배정하는 것보다 낫다).
        /// 날짜가 아니라 "1학기"처럼 적혀 있으면 문자열이 같을 때만 겹친 것으로 본다.
        /// </summary>
        private bool ArePeriodsOverlapping(string p1, string p2)
        {
            if (string.IsNullOrWhiteSpace(p1) || string.IsNullOrWhiteSpace(p2))
                return true;

            var range1 = ParsePeriodDates(p1);
            var range2 = ParsePeriodDates(p2);

            if (range1 == null || range2 == null)
                return p1.Trim().Equals(p2.Trim(), StringComparison.OrdinalIgnoreCase);

            return range1.Value.Start <= range2.Value.End && range2.Value.Start <= range1.Value.End;
        }

        /// <summary>
        /// 캐비닛 신청이 같은 학생의 기존 배정·대기 신청과 기간이 겹치는지 본다.
        /// 겹치면 반려 사유를, 겹치지 않으면(= 연장으로 받아도 되면) 빈 문자열을 돌려준다.
        /// </summary>
        /// <summary>기자재 시트의 승인상태 칸 값이 '승인됨'으로 볼 수 있는 값인지.</summary>
        private static bool IsApprovedOnSheet(string sheetApprovalStatus)
        {
            string s = (sheetApprovalStatus ?? string.Empty).Trim();
            return s == "승인" || s == "승인 완료" || s == "승인완료" || s == "승인함" ||
                   s.Equals("O", StringComparison.OrdinalIgnoreCase) ||
                   s.Equals("Y", StringComparison.OrdinalIgnoreCase);
        }

        private string FindCabinetConflict(ApprovalRequest req)
        {
            var allocated = _cabinetAllocations
                .FirstOrDefault(kvp => kvp.Value.Student?.StudentId == req.StudentId);

            if (allocated.Value.Student != null &&
                ArePeriodsOverlapping(req.RentalPeriod, allocated.Value.Period))
            {
                return $"이미 {allocated.Key}번 캐비닛에 배정됨 ({allocated.Value.Period})";
            }

            var pending = _approvals.FirstOrDefault(
                a => a.TabType == "캐비닛" && a.StudentId == req.StudentId &&
                     ArePeriodsOverlapping(req.RentalPeriod, a.RentalPeriod));

            if (pending != null)
                return $"승인 대기 중인 신청과 기간 중복 ({pending.RentalPeriod})";

            return string.Empty;
        }

        /// <summary>"26.03.02 ~ 26.06.20", "2026-03-02~2026-06-20", "03/02~06/20" 등을 관대하게 읽는다.</summary>
        private (DateTime Start, DateTime End)? ParsePeriodDates(string period)
        {
            try
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(period, @"(\d{2,4})[\./-](\d{1,2})[\./-](\d{1,2})");
                if (matches.Count >= 2)
                    return (ParseRegexDate(matches[0]), ParseRegexDate(matches[1]));

                var matchesMD = System.Text.RegularExpressions.Regex.Matches(period, @"(\d{1,2})/(\d{1,2})");
                if (matchesMD.Count >= 2)
                {
                    int y = _currentSimulatedDate.Year;
                    var start = new DateTime(y, int.Parse(matchesMD[0].Groups[1].Value), int.Parse(matchesMD[0].Groups[2].Value));
                    var end = new DateTime(y, int.Parse(matchesMD[1].Groups[1].Value), int.Parse(matchesMD[1].Groups[2].Value));
                    // 겨울방학처럼 해를 넘기는 표기
                    if (end < start) end = end.AddYears(1);
                    return (start, end);
                }
            }
            catch
            {
                // 13월 32일 같은 값이 들어오면 읽지 못한 것으로 처리한다
            }
            return null;
        }

        private static DateTime ParseRegexDate(System.Text.RegularExpressions.Match m)
        {
            int y = int.Parse(m.Groups[1].Value);
            if (y < 100) y += 2000;
            return new DateTime(y, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        }

        /// <summary>연장 승인 시 기존 기간과 새 기간을 하나로 합친다.</summary>
        private string MergePeriods(string p1, string p2)
        {
            var r1 = ParsePeriodDates(p1);
            var r2 = ParsePeriodDates(p2);
            if (r1 == null || r2 == null) return $"{p1} / {p2}";

            DateTime start = r1.Value.Start < r2.Value.Start ? r1.Value.Start : r2.Value.Start;
            DateTime end = r1.Value.End > r2.Value.End ? r1.Value.End : r2.Value.End;
            return FormatPeriod(start, end);
        }

        /// <summary>학생 선택창의 '대여 기간' 칸을 현재 배정 상태로 채운다.</summary>
        private void UpdateMasterCabinetPeriods()
        {
            foreach (var student in _masterStudents)
            {
                var allocation = _cabinetAllocations.Values
                    .FirstOrDefault(a => a.Student?.StudentId == student.StudentId);
                student.CabinetPeriod = allocation.Student != null ? allocation.Period : string.Empty;
            }
        }

        // ================= 기간이 지난 데이터 정리 =================

        /// <summary>기간이 지난 항목 하나. 팝업에 보여줄 문구와 지우는 방법을 함께 들고 다닌다.</summary>
        private class ExpiredEntry
        {
            public string Key { get; set; } = string.Empty;
            public string Label { get; set; } = string.Empty;
            public Action Delete { get; set; } = () => { };
        }

        private void ShowCabinetExpiryWarningDialog()
        {
            DateTime today = _currentSimulatedDate.Date;
            var warningItems = new List<(int Number, StudentInfo Student, string Period, int DaysRemaining)>();

            foreach (var kvp in _cabinetAllocations)
            {
                var range = ParsePeriodDates(kvp.Value.Period);
                if (range == null) continue;

                int daysRemaining = (range.Value.End.Date - today).Days;
                if (daysRemaining <= 7)
                {
                    warningItems.Add((kvp.Key, kvp.Value.Student, kvp.Value.Period, daysRemaining));
                }
            }

            if (warningItems.Count > 0)
            {
                var dialog = new CabinetExpiryWarningDialog(today, warningItems.OrderBy(x => x.DaysRemaining).ToList())
                {
                    Owner = this
                };
                dialog.ShowDialog();
            }
        }

        private void ShowEquipmentExpiryWarningDialog()
        {
            DateTime today = _currentSimulatedDate.Date;
            var warningItems = new List<(RentalItem Rental, int DaysRemaining)>();

            foreach (var rental in _rentals)
            {
                if (rental.IsReturned) continue;

                int daysRemaining = (rental.DueDate.Date - today).Days;
                if (daysRemaining <= 7)
                {
                    warningItems.Add((rental, daysRemaining));
                }
            }

            if (warningItems.Count > 0)
            {
                var dialog = new EquipmentExpiryWarningDialog(today, warningItems.OrderBy(x => x.DaysRemaining).ToList())
                {
                    Owner = this
                };
                dialog.ShowDialog();
            }
        }

        /// <summary>[유지]를 눌러 넘어간 항목. 같은 것으로 계속 묻지 않도록 세션 내내 기억한다.</summary>
        private readonly HashSet<string> _keptExpiredKeys = new HashSet<string>();

        /// <summary>
        /// 탭을 열 때 기간이 지난 데이터가 있으면 [삭제 / 유지] 팝업을 띄운다.
        /// [유지]를 고른 항목은 새로 생긴 것이 없는 한 다시 묻지 않는다.
        /// </summary>
        private void CheckExpiredItems(string scope)
        {
            List<ExpiredEntry> expired = scope switch
            {
                "대시보드" => CollectExpiredSeats(),
                "기자재" => CollectExpiredRentals(),
                "캐비닛" => CollectExpiredCabinets(),
                _ => new List<ExpiredEntry>()
            };

            var fresh = expired.Where(x => !_keptExpiredKeys.Contains(x.Key)).ToList();
            if (fresh.Count == 0) return;

            var dialog = new ExpiredCleanupDialog(scope, _currentSimulatedDate, fresh.Select(x => (x.Key, x.Label)).ToList())
            {
                Owner = this
            };
            dialog.ShowDialog();

            if (!dialog.DeleteConfirmed)
            {
                // [유지] — 아무것도 건드리지 않는다
                foreach (var entry in fresh) _keptExpiredKeys.Add(entry.Key);
                return;
            }

            int deletedCount = 0;
            foreach (var entry in fresh)
            {
                if (dialog.SelectedKeys.Contains(entry.Key))
                {
                    entry.Delete();
                    deletedCount++;
                }
                else
                {
                    // 선택되지 않은 항목은 '유지' 처리하여 다음 번에 다시 묻지 않게 함
                    _keptExpiredKeys.Add(entry.Key);
                }
            }

            if (scope == "대시보드") RenderSeatGrid();
            else if (scope == "기자재") BindEquipmentRentals();
            else if (scope == "캐비닛") RenderCabinetGrid();

            UpdateAlertBadges();
            if (deletedCount > 0)
            {
                MessageBox.Show($"선택한 {deletedCount}건을 삭제했습니다.", "정리 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>대시보드 좌석 — 배정 기간이 끝난 학생.</summary>
        private List<ExpiredEntry> CollectExpiredSeats()
        {
            var list = new List<ExpiredEntry>();
            DateTime today = _currentSimulatedDate.Date;

            foreach (var seat in _activeSeats)
            {
                if (seat.IsPillar || seat.Student == null) continue;

                string period = FindStudentPeriod(seat.Student);
                var range = ParsePeriodDates(period);
                if (range == null || range.Value.End.Date >= today) continue;

                var target = seat;
                list.Add(new ExpiredEntry
                {
                    Key = $"좌석|{seat.SeatNumber}|{seat.Student.StudentId}|{period}",
                    Label = $"{seat.SeatNumber}번 좌석 · {seat.Student.Name}({seat.Student.StudentId}) · {period}",
                    Delete = () =>
                    {
                        target.Student = null;
                        target.IsFixed = false;
                    }
                });
            }
            return list;
        }

        /// <summary>기자재 — 반납 예정일이 지난 대여.</summary>
        private List<ExpiredEntry> CollectExpiredRentals()
        {
            var list = new List<ExpiredEntry>();
            DateTime today = _currentSimulatedDate.Date;

            foreach (var rental in _rentals.Where(r => !r.IsReturned && r.DueDate.Date < today).ToList())
            {
                var target = rental;
                list.Add(new ExpiredEntry
                {
                    Key = $"대여|{rental.Id}",
                    Label = $"{rental.DisplayEquipment} · {rental.StudentName}({rental.StudentId}) · 반납예정 {rental.DueDate:yyyy-MM-dd}",
                    Delete = () =>
                    {
                        // 반납 버튼과 같은 처리 — 내역에는 남기고 현황에서만 뺀다
                        target.IsReturned = true;
                        _rentals.Remove(target);
                    }
                });
            }
            return list;
        }

        /// <summary>캐비닛 — 대여 기간이 끝난 배정.</summary>
        private List<ExpiredEntry> CollectExpiredCabinets()
        {
            var list = new List<ExpiredEntry>();
            DateTime today = _currentSimulatedDate.Date;

            foreach (var kvp in _cabinetAllocations.ToList())
            {
                var range = ParsePeriodDates(kvp.Value.Period);
                if (range == null || range.Value.End.Date >= today) continue;

                int number = kvp.Key;
                string name = kvp.Value.Student?.Name ?? "";
                string id = kvp.Value.Student?.StudentId ?? "";

                list.Add(new ExpiredEntry
                {
                    Key = $"캐비닛|{number}|{id}|{kvp.Value.Period}",
                    Label = $"{number}번 캐비닛 · {name}({id}) · {kvp.Value.Period}",
                    Delete = () =>
                    {
                        _cabinetAllocations.Remove(number);
                        UpdateMasterCabinetPeriods();
                    }
                });
            }
            return list;
        }

        /// <summary>좌석에 앉은 학생의 기간을 찾는다. 좌석 기록 → 캐비닛 배정 → 승인 내역 순으로 본다.</summary>
        private string FindStudentPeriod(StudentInfo student)
        {
            if (!string.IsNullOrWhiteSpace(student.CabinetPeriod)) return student.CabinetPeriod;

            var allocation = _cabinetAllocations.Values
                .FirstOrDefault(a => a.Student?.StudentId == student.StudentId);
            if (allocation.Student != null && !string.IsNullOrWhiteSpace(allocation.Period))
                return allocation.Period;

            var approved = _approvalHistory
                .LastOrDefault(a => a.StudentId == student.StudentId &&
                                    a.Status == "승인 완료" &&
                                    !string.IsNullOrWhiteSpace(a.RentalPeriod));

            return approved?.RentalPeriod ?? string.Empty;
        }

        private void DataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid grid) return;

            // Find the clicked element
            var dep = (DependencyObject)e.OriginalSource;

            // If clicked on a Button or its children, do nothing
            var current = dep;
            while (current != null && current != grid)
            {
                if (current is Button) return;
                current = VisualTreeHelper.GetParent(current);
            }

            // Find the DataGridRow
            current = dep;
            while (current != null && current is not DataGridRow)
            {
                current = VisualTreeHelper.GetParent(current);
            }

            if (current is DataGridRow row)
            {
                if (row.IsSelected)
                {
                    grid.SelectedItem = null;
                    e.Handled = true;
                }
            }
        }
    }

    /// <summary>
    /// 기간이 지난 항목을 보여주고 [삭제] / [유지] 중 하나를 받는 팝업.
    /// [유지]는 아무 일도 하지 않고 창만 닫는다.
    /// </summary>
    public class ExpiredCleanupDialog : Window
    {
        public bool DeleteConfirmed { get; private set; }
        public List<string> SelectedKeys { get; private set; } = new List<string>();

        public ExpiredCleanupDialog(string scope, DateTime today, List<(string Key, string Label)> items)
        {
            Title = $"{scope} 기간 만료 정리";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(24) };

            root.Children.Add(new TextBlock
            {
                Text = $"⏰ {scope} — 기간이 지난 항목 {items.Count}건",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
                Margin = new Thickness(0, 0, 0, 6)
            });

            root.Children.Add(new TextBlock
            {
                Text = $"기준일 {today:yyyy-MM-dd} 보다 기간이 먼저 끝난 항목입니다. 삭제할 항목을 선택해 주세요.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                Margin = new Thickness(0, 0, 0, 14)
            });

            var checkboxMap = new Dictionary<CheckBox, string>();

            var listBox = new ListBox
            {
                MaxHeight = 280,
                BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(249, 250, 251)),
                Margin = new Thickness(0, 0, 0, 10)
            };

            foreach (var item in items)
            {
                var cb = new CheckBox
                {
                    Content = item.Label,
                    IsChecked = true,
                    Margin = new Thickness(4),
                    VerticalAlignment = VerticalAlignment.Center
                };
                listBox.Items.Add(cb);
                checkboxMap[cb] = item.Key;
            }

            var selectAllCb = new CheckBox
            {
                Content = "전체 선택",
                IsChecked = true,
                Margin = new Thickness(4, 0, 0, 8),
                FontWeight = FontWeights.Bold
            };
            selectAllCb.Checked += (s, e) =>
            {
                foreach (var cb in checkboxMap.Keys) cb.IsChecked = true;
            };
            selectAllCb.Unchecked += (s, e) =>
            {
                foreach (var cb in checkboxMap.Keys) cb.IsChecked = false;
            };

            root.Children.Add(selectAllCb);
            root.Children.Add(listBox);

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var deleteBtn = new Button
            {
                Content = "선택 삭제",
                Width = 100,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            deleteBtn.Click += (s, e) =>
            {
                SelectedKeys.Clear();
                foreach (var kvp in checkboxMap)
                {
                    if (kvp.Key.IsChecked == true)
                    {
                        SelectedKeys.Add(kvp.Value);
                    }
                }

                if (SelectedKeys.Count == 0)
                {
                    MessageBox.Show("삭제할 항목을 최소 하나 이상 선택해 주세요.", "선택 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                DeleteConfirmed = true;
                DialogResult = true;
                Close();
            };

            var keepBtn = new Button
            {
                Content = "유지",
                Width = 100,
                Height = 34,
                IsDefault = true,
                IsCancel = true,
                Background = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            keepBtn.Click += (s, e) =>
            {
                DeleteConfirmed = false;
                DialogResult = false;
                Close();
            };

            buttonRow.Children.Add(deleteBtn);
            buttonRow.Children.Add(keepBtn);
            root.Children.Add(buttonRow);

            Content = root;
        }
    }

    public class EquipmentSelectionDialog : Window
    {
        private ComboBox _typeCombo;
        private ComboBox _numberCombo;
        private TextBox _phoneTextBox;
        private DatePicker _rentalDatePicker;
        private DatePicker _dueDatePicker;
        private Button _okButton;
        private Button _cancelButton;
        private List<RentalItem> _activeRentals;

        /// <summary>고장/수리/기타로 등록되어 지금 대여를 내보내면 안 되는 개체들.</summary>
        private List<EquipmentIssue> _issues;

        public string SelectedEquipmentType { get; private set; } = "";
        public string SelectedUnitNumber { get; private set; } = "";
        public string SelectedPhone { get; private set; } = "";
        public DateTime SelectedRentalDate { get; private set; }
        public DateTime SelectedDueDate { get; private set; }

        public EquipmentSelectionDialog(List<RentalItem> activeRentals, string initialPhone, string requestedEquipmentType, List<EquipmentIssue>? issues = null, DateTime? initialDueDate = null, DateTime? initialRentalDate = null)
        {
            _activeRentals = activeRentals;
            _issues = issues ?? new List<EquipmentIssue>();

            Title = "대여 기자재 및 기간 선택";
            Width = 350;
            Height = 350;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var mainGrid = new Grid { Margin = new Thickness(20) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: Type
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) }); // 1
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: Number
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) }); // 3
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4: Phone
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) }); // 5
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 6: Rental Date
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) }); // 7
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 8: Due Date
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) }); // 9
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 10: Buttons

            // Row 0: Equipment Type Selection
            var typePanel = new StackPanel { Orientation = Orientation.Horizontal };
            typePanel.Children.Add(new TextBlock { Text = "기자재 종류:", Width = 100, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold });
            _typeCombo = new ComboBox { Width = 180 };
            _typeCombo.Items.Add("본체 (HP Z2 G9)");
            _typeCombo.Items.Add("노트북(HP OMEN 게이밍 노트북)");

            // Set selections based on requestedEquipmentType
            if (!string.IsNullOrEmpty(requestedEquipmentType) && 
                (requestedEquipmentType.Contains("노트북") || requestedEquipmentType.Contains("Laptop") || requestedEquipmentType.Contains("OMEN")))
            {
                _typeCombo.SelectedItem = "노트북(HP OMEN 게이밍 노트북)";
            }
            else
            {
                _typeCombo.SelectedItem = "본체 (HP Z2 G9)";
            }
            _typeCombo.IsEnabled = false; // Disable selecting the equipment type!

            _typeCombo.SelectionChanged += TypeCombo_SelectionChanged;
            typePanel.Children.Add(_typeCombo);
            Grid.SetRow(typePanel, 0);
            mainGrid.Children.Add(typePanel);

            // Row 2: Unit Number Selection
            var numberPanel = new StackPanel { Orientation = Orientation.Horizontal };
            numberPanel.Children.Add(new TextBlock { Text = "기자재 번호:", Width = 100, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold });
            _numberCombo = new ComboBox { Width = 180 };
            numberPanel.Children.Add(_numberCombo);
            Grid.SetRow(numberPanel, 2);
            mainGrid.Children.Add(numberPanel);

            // Row 4: Phone Input
            var phonePanel = new StackPanel { Orientation = Orientation.Horizontal };
            phonePanel.Children.Add(new TextBlock { Text = "연락처:", Width = 100, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold });
            _phoneTextBox = new TextBox { Width = 180, Height = 25, VerticalContentAlignment = VerticalAlignment.Center };
            _phoneTextBox.Text = initialPhone;
            phonePanel.Children.Add(_phoneTextBox);
            Grid.SetRow(phonePanel, 4);
            mainGrid.Children.Add(phonePanel);

            // Row 6: Rental Date Picker
            var calendarStyle = new Style(typeof(Calendar));
            calendarStyle.Setters.Add(new Setter(Calendar.LayoutTransformProperty, new ScaleTransform(1.5, 1.5)));

            var rentalDatePanel = new StackPanel { Orientation = Orientation.Horizontal };
            rentalDatePanel.Children.Add(new TextBlock { Text = "대여 시작일:", Width = 100, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold });
            _rentalDatePicker = new DatePicker { Width = 180, SelectedDate = initialRentalDate ?? DateTime.Today };
            _rentalDatePicker.Resources.Add(typeof(Calendar), calendarStyle);
            rentalDatePanel.Children.Add(_rentalDatePicker);
            Grid.SetRow(rentalDatePanel, 6);
            mainGrid.Children.Add(rentalDatePanel);

            // Row 8: Due Date Picker
            var dueDatePanel = new StackPanel { Orientation = Orientation.Horizontal };
            dueDatePanel.Children.Add(new TextBlock { Text = "반납 예정일:", Width = 100, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold });
            _dueDatePicker = new DatePicker { Width = 180, SelectedDate = initialDueDate ?? (initialRentalDate ?? DateTime.Today).AddDays(7) };
            _dueDatePicker.Resources.Add(typeof(Calendar), calendarStyle);
            dueDatePanel.Children.Add(_dueDatePicker);
            Grid.SetRow(dueDatePanel, 8);
            mainGrid.Children.Add(dueDatePanel);

            // Row 10: Buttons
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _okButton = new Button { Content = "대여 완료", Width = 80, Height = 30, Margin = new Thickness(0, 0, 10, 0), IsDefault = true, Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)), Foreground = Brushes.White, FontWeight = FontWeights.Bold };
            _okButton.Click += OkButton_Click;
            _cancelButton = new Button { Content = "취소", Width = 80, Height = 30, IsCancel = true };
            buttonPanel.Children.Add(_okButton);
            buttonPanel.Children.Add(_cancelButton);
            Grid.SetRow(buttonPanel, 10);
            mainGrid.Children.Add(buttonPanel);

            Content = mainGrid;
            UpdateAvailableNumbers();
        }

        private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateAvailableNumbers();
        }

        private void UpdateAvailableNumbers()
        {
            _numberCombo.Items.Clear();
            string selectedType = _typeCombo.SelectedItem?.ToString() ?? "";
            bool isMainframe = selectedType.Contains("본체");
            int maxCount = isMainframe ? 10 : 20;

            // Get busy numbers
            var busyNumbers = new HashSet<int>();
            foreach (var r in _activeRentals)
            {
                // check if r matches selected type
                bool rIsMainframe = r.EquipmentType.Contains("VR") || r.EquipmentType.Contains("Quest") || r.EquipmentType.Contains("본체");
                if (isMainframe == rIsMainframe)
                {
                    // Parse number from UnitNumber (e.g. "No. 1" -> 1) to avoid matching "2" or "9" in "HP Z2 G9"
                    string unitNo = r.UnitNumber;
                    var match = System.Text.RegularExpressions.Regex.Match(unitNo, @"\d+");
                    if (match.Success && int.TryParse(match.Value, out int num))
                    {
                        busyNumbers.Add(num);
                    }
                }
            }

            // 고장/수리/기타로 등록된 개체도 같은 번호로 겹치지 않게 뺀다
            foreach (var issue in _issues)
            {
                bool issueIsMainframe = issue.EquipmentName.Contains("본체");
                if (issueIsMainframe == isMainframe) busyNumbers.Add(issue.UnitNumber);
            }

            // Add available numbers
            for (int i = 1; i <= maxCount; i++)
            {
                if (!busyNumbers.Contains(i))
                {
                    _numberCombo.Items.Add($"No. {i}");
                }
            }

            if (_numberCombo.Items.Count > 0)
            {
                _numberCombo.SelectedIndex = 0;
                _okButton.IsEnabled = true;
            }
            else
            {
                _numberCombo.Items.Add("사용 가능한 기자재 없음");
                _numberCombo.SelectedIndex = 0;
                _okButton.IsEnabled = false;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedEquipmentType = _typeCombo.SelectedItem?.ToString() ?? "";
            string numStr = _numberCombo.SelectedItem?.ToString() ?? "";
            if (numStr == "사용 가능한 기자재 없음" || string.IsNullOrEmpty(numStr))
            {
                MessageBox.Show("대여 가능한 기자재가 없습니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SelectedUnitNumber = numStr;
            SelectedPhone = _phoneTextBox.Text.Trim();
            SelectedRentalDate = _rentalDatePicker.SelectedDate ?? DateTime.Today;
            SelectedDueDate = _dueDatePicker.SelectedDate ?? DateTime.Today.AddDays(7);
            DialogResult = true;
            Close();
        }
    }

    /// <summary>
    /// 기자재 종류(본체/노트북)와 번호를 고르면 [고장 / 수리 / 기타] 버튼이 나타나고,
    /// [기타]를 누르면 내용을 수기로 적어 넣는 칸이 열린다.
    /// 지금 대여 중인 개체는 번호 목록에서 빠진다.
    /// </summary>
    public class EquipmentIssueDialog : Window
    {
        private const string MainframeLabel = "본체 (HP Z2 G9)";
        private const string LaptopLabel = "노트북(HP OMEN 게이밍 노트북)";
        private const string NoneLabel = "선택 가능한 번호 없음 (대여 중 / 이미 등록됨)";

        private readonly ComboBox _equipCombo;
        private readonly ComboBox _numberCombo;
        private readonly StackPanel _typePanel;
        private readonly StackPanel _etcPanel;
        private readonly TextBox _detailTextBox;

        private readonly List<RentalItem> _activeRentals;

        /// <summary>이미 상태 관리 목록에 올라가 있는 개체들. 같은 기자재·같은 번호는 다시 못 올린다.</summary>
        private readonly List<EquipmentIssue> _issues;

        private readonly int _totalMainframes;
        private readonly int _totalLaptops;

        /// <summary>"본체" 또는 "노트북"</summary>
        public string SelectedEquipmentName { get; private set; } = string.Empty;
        public int SelectedUnitNumber { get; private set; }
        public string SelectedIssueType { get; private set; } = string.Empty;
        public string Detail { get; private set; } = string.Empty;

        public EquipmentIssueDialog(List<RentalItem> activeRentals, List<EquipmentIssue> issues, int totalMainframes, int totalLaptops)
        {
            _activeRentals = activeRentals;
            _issues = issues;
            _totalMainframes = totalMainframes;
            _totalLaptops = totalLaptops;

            Title = "기자재 상태 등록";
            Width = 400;
            // 상태 버튼과 기타 입력칸이 열리는 만큼 창 높이가 따라 늘어난다
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(20) };

            // 1단계: 기자재 종류 고르기
            var equipPanel = new StackPanel { Orientation = Orientation.Horizontal };
            equipPanel.Children.Add(new TextBlock
            {
                Text = "기자재 종류:",
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold
            });
            _equipCombo = new ComboBox { Width = 250 };
            _equipCombo.Items.Add(MainframeLabel);
            _equipCombo.Items.Add(LaptopLabel);
            _equipCombo.SelectedIndex = -1;
            _equipCombo.SelectionChanged += EquipCombo_SelectionChanged;
            equipPanel.Children.Add(_equipCombo);
            root.Children.Add(equipPanel);

            // 2단계: 번호 고르기 (종류를 골라야 채워진다)
            var numberPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            numberPanel.Children.Add(new TextBlock
            {
                Text = "기자재 번호:",
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.Bold
            });
            _numberCombo = new ComboBox { Width = 250, IsEnabled = false };
            _numberCombo.SelectionChanged += NumberCombo_SelectionChanged;
            numberPanel.Children.Add(_numberCombo);
            root.Children.Add(numberPanel);

            // 3단계: 번호를 고르면 나타나는 구분 버튼
            _typePanel = new StackPanel { Margin = new Thickness(0, 18, 0, 0), Visibility = Visibility.Collapsed };
            _typePanel.Children.Add(new TextBlock
            {
                Text = "상태를 고르세요",
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                Margin = new Thickness(0, 0, 0, 8)
            });

            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal };
            buttonRow.Children.Add(MakeTypeButton("고장", Color.FromRgb(239, 68, 68)));
            buttonRow.Children.Add(MakeTypeButton("수리", Color.FromRgb(245, 158, 11)));
            buttonRow.Children.Add(MakeTypeButton("기타", Color.FromRgb(107, 114, 128)));
            _typePanel.Children.Add(buttonRow);
            root.Children.Add(_typePanel);

            // 4단계: [기타]일 때만 열리는 수기 입력
            _etcPanel = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
            _etcPanel.Children.Add(new TextBlock
            {
                Text = "내용을 직접 적어 주세요",
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                Margin = new Thickness(0, 0, 0, 6)
            });
            _detailTextBox = new TextBox
            {
                Height = 60,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(5)
            };
            _etcPanel.Children.Add(_detailTextBox);

            var etcButtonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var registerBtn = new Button
            {
                Content = "등록",
                Width = 80,
                Height = 28,
                IsDefault = true,
                Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0)
            };
            registerBtn.Click += RegisterEtc_Click;
            etcButtonRow.Children.Add(registerBtn);
            _etcPanel.Children.Add(etcButtonRow);
            root.Children.Add(_etcPanel);

            // 취소
            var cancelRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            cancelRow.Children.Add(new Button { Content = "취소", Width = 80, Height = 28, IsCancel = true });
            root.Children.Add(cancelRow);

            Content = root;
        }

        private Button MakeTypeButton(string label, Color color)
        {
            var btn = new Button
            {
                Content = label,
                Tag = label,
                Width = 95,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(color),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btn.Click += TypeButton_Click;
            return btn;
        }

        private void EquipCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 종류가 바뀌면 번호부터 다시 고른다
            _typePanel.Visibility = Visibility.Collapsed;
            _etcPanel.Visibility = Visibility.Collapsed;
            FillAvailableNumbers();
        }

        /// <summary>지금 대여 중인 개체는 빼고 번호를 채운다.</summary>
        private void FillAvailableNumbers()
        {
            _numberCombo.Items.Clear();

            string selected = _equipCombo.SelectedItem?.ToString() ?? "";
            if (selected.Length == 0)
            {
                _numberCombo.IsEnabled = false;
                return;
            }

            bool isMainframe = selected == MainframeLabel;
            int maxCount = isMainframe ? _totalMainframes : _totalLaptops;

            // 대여 중인 번호 — "No. 17" 같은 개체 번호만 뽑아 쓴다 ("HP Z2 G9"의 숫자와 섞이지 않게)
            var busyNumbers = new HashSet<int>();
            foreach (var r in _activeRentals)
            {
                if (r.IsMainframe != isMainframe) continue;

                var match = System.Text.RegularExpressions.Regex.Match(r.UnitNumber, @"\d+");
                if (match.Success && int.TryParse(match.Value, out int busy)) busyNumbers.Add(busy);
            }

            // 이미 상태 관리 목록에 있는 번호도 뺀다 (같은 기자재·같은 번호 중복 금지)
            foreach (var issue in _issues)
            {
                bool issueIsMainframe = issue.EquipmentName.Contains("본체");
                if (issueIsMainframe == isMainframe) busyNumbers.Add(issue.UnitNumber);
            }

            for (int i = 1; i <= maxCount; i++)
            {
                if (!busyNumbers.Contains(i)) _numberCombo.Items.Add($"No. {i}");
            }

            if (_numberCombo.Items.Count == 0)
            {
                _numberCombo.Items.Add(NoneLabel);
                _numberCombo.SelectedIndex = 0;
                _numberCombo.IsEnabled = false;
                return;
            }

            _numberCombo.IsEnabled = true;
            _numberCombo.SelectedIndex = -1;
        }

        private void NumberCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 번호를 골라야 상태 버튼이 나온다
            bool picked = _numberCombo.SelectedIndex >= 0 && _numberCombo.SelectedItem?.ToString() != NoneLabel;
            _typePanel.Visibility = picked ? Visibility.Visible : Visibility.Collapsed;
            if (!picked) _etcPanel.Visibility = Visibility.Collapsed;
        }

        private void TypeButton_Click(object sender, RoutedEventArgs e)
        {
            string type = (sender as Button)?.Tag?.ToString() ?? "";

            if (type == "기타")
            {
                // 기타는 수기 입력까지 받고 나서 등록한다
                _etcPanel.Visibility = Visibility.Visible;
                _detailTextBox.Focus();
                return;
            }

            _etcPanel.Visibility = Visibility.Collapsed;
            SelectedIssueType = type;
            Detail = string.Empty;
            Commit();
        }

        private void RegisterEtc_Click(object sender, RoutedEventArgs e)
        {
            string text = _detailTextBox.Text.Trim();
            if (text.Length == 0)
            {
                MessageBox.Show("내용을 입력해 주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                _detailTextBox.Focus();
                return;
            }

            SelectedIssueType = "기타";
            Detail = text;
            Commit();
        }

        private void Commit()
        {
            string equip = _equipCombo.SelectedItem?.ToString() ?? "";
            if (equip.Length == 0)
            {
                MessageBox.Show("기자재 종류를 먼저 고르세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string numStr = _numberCombo.SelectedItem?.ToString() ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(numStr, @"\d+");
            if (numStr == NoneLabel || !m.Success || !int.TryParse(m.Value, out int num))
            {
                MessageBox.Show("기자재 번호를 먼저 고르세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedEquipmentName = equip == MainframeLabel ? "본체" : "노트북";
            SelectedUnitNumber = num;
            DialogResult = true;
            Close();
        }
    }

    public class CabinetSelectionDialog : Window
    {
        private ComboBox _cabinetCombo;
        private Button _okButton;
        private Button _cancelButton;

        public int SelectedCabinetNumber { get; private set; } = -1;

        /// <param name="movingFrom">
        /// 좌석 옮기기로 열었으면 지금 쓰고 있는 번호. 0이면 새 배정으로 보고 안내 문구를 띄우지 않는다.
        /// </param>
        public CabinetSelectionDialog(Dictionary<int, (StudentInfo Student, string Period)> allocations, int totalCabinets, int movingFrom = 0)
        {
            Title = movingFrom > 0 ? "캐비닛 좌석 옮기기" : "캐비닛 번호 배정";
            Width = 340;
            Height = movingFrom > 0 ? 210 : 180;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var mainGrid = new Grid { Margin = new Thickness(20) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 안내 문구
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 번호 선택
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 버튼

            if (movingFrom > 0)
            {
                var guide = new TextBlock
                {
                    Text = $"현재 {movingFrom}번 → 옮길 번호를 고르세요.\n(비어 있는 번호만 보입니다)",
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 12),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetRow(guide, 0);
                mainGrid.Children.Add(guide);
            }

            // Row 1: Combo
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = "캐비닛 번호 선택:", Width = 110, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold });
            
            _cabinetCombo = new ComboBox { Width = 150 };
            for (int i = 1; i <= totalCabinets; i++)
            {
                if (!allocations.ContainsKey(i))
                {
                    _cabinetCombo.Items.Add(i);
                }
            }
            if (_cabinetCombo.Items.Count > 0)
            {
                _cabinetCombo.SelectedIndex = 0;
            }
            else
            {
                _cabinetCombo.Items.Add("사용 가능한 캐비닛 없음");
                _cabinetCombo.SelectedIndex = 0;
            }
            panel.Children.Add(_cabinetCombo);
            Grid.SetRow(panel, 1);
            mainGrid.Children.Add(panel);

            // Row 3: Buttons
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _okButton = new Button { Content = movingFrom > 0 ? "여기로 옮기기" : "배정 승인", Width = movingFrom > 0 ? 100 : 80, Height = 30, Margin = new Thickness(0, 0, 10, 0), IsDefault = true, Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)), Foreground = Brushes.White, FontWeight = FontWeights.Bold };
            _okButton.Click += OkButton_Click;
            _cancelButton = new Button { Content = "취소", Width = 80, Height = 30, IsCancel = true };
            buttonPanel.Children.Add(_okButton);
            buttonPanel.Children.Add(_cancelButton);
            Grid.SetRow(buttonPanel, 3);
            mainGrid.Children.Add(buttonPanel);

            Content = mainGrid;

            if (_cabinetCombo.Items.Count == 1 && _cabinetCombo.Items[0].ToString() == "사용 가능한 캐비닛 없음")
            {
                _okButton.IsEnabled = false;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cabinetCombo.SelectedItem is int num)
            {
                SelectedCabinetNumber = num;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("올바른 캐비닛 번호를 선택해 주세요.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    public class CabinetExpiryWarningDialog : Window
    {
        public CabinetExpiryWarningDialog(DateTime today, List<(int Number, StudentInfo Student, string Period, int DaysRemaining)> items)
        {
            Title = "캐비닛 대여 기간 만료/임박 안내";
            Width = 550;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(24) };

            root.Children.Add(new TextBlock
            {
                Text = $"⚠️ 캐비닛 대여 기간 만료 및 임박 ({items.Count}건)",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38)), // Red text
                Margin = new Thickness(0, 0, 0, 6)
            });

            root.Children.Add(new TextBlock
            {
                Text = $"기준일: {today:yyyy-MM-dd}\n대여 기간이 만료되었거나 7일 이하로 남은 캐비닛 목록입니다.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                Margin = new Thickness(0, 0, 0, 14)
            });

            var listBox = new ListBox
            {
                MaxHeight = 350,
                BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(249, 250, 251)),
                Margin = new Thickness(0, 0, 0, 16),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            foreach (var item in items)
            {
                string statusStr = item.DaysRemaining < 0 
                    ? $"만료 ({Math.Abs(item.DaysRemaining)}일 경과)" 
                    : (item.DaysRemaining == 0 ? "오늘 만료 (D-Day)" : $"{item.DaysRemaining}일 남음");

                Brush statusBrush = item.DaysRemaining < 0 
                    ? new SolidColorBrush(Color.FromRgb(220, 38, 38)) // Red for expired
                    : (item.DaysRemaining == 0 
                        ? new SolidColorBrush(Color.FromRgb(217, 119, 6)) // Orange for D-Day
                        : new SolidColorBrush(Color.FromRgb(37, 99, 235))); // Blue for <=7 days

                var grid = new Grid { Margin = new Thickness(6, 4, 6, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) }); // Cabinet No
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Student info
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) }); // Status/Days remaining

                var numTxt = new TextBlock
                {
                    Text = $"{item.Number}번 캐비닛",
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(numTxt, 0);
                grid.Children.Add(numTxt);

                var infoTxt = new TextBlock
                {
                    Text = $"{item.Student?.Name}({item.Student?.StudentId}) · {item.Period}",
                    Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(infoTxt, 1);
                grid.Children.Add(infoTxt);

                var statusTxt = new TextBlock
                {
                    Text = statusStr,
                    FontWeight = FontWeights.Bold,
                    Foreground = statusBrush,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(statusTxt, 2);
                grid.Children.Add(statusTxt);

                listBox.Items.Add(grid);
            }

            root.Children.Add(listBox);

            var closeBtn = new Button
            {
                Content = "닫기",
                Width = 100,
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true,
                IsCancel = true,
                Background = new SolidColorBrush(Color.FromRgb(79, 70, 229)), // Indigo
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            closeBtn.Click += (s, e) => Close();
            root.Children.Add(closeBtn);

            Content = root;
        }
    }

    public class EquipmentExpiryWarningDialog : Window
    {
        public EquipmentExpiryWarningDialog(DateTime today, List<(RentalItem Rental, int DaysRemaining)> items)
        {
            Title = "기자재 대여 기간 만료/임박 안내";
            Width = 550;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(24) };

            root.Children.Add(new TextBlock
            {
                Text = $"⚠️ 기자재 대여 기간 만료 및 임박 ({items.Count}건)",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38)), // Red text
                Margin = new Thickness(0, 0, 0, 6)
            });

            root.Children.Add(new TextBlock
            {
                Text = $"기준일: {today:yyyy-MM-dd}\n대여 기간이 만료되었거나 7일 이하로 남은 기자재 목록입니다.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                Margin = new Thickness(0, 0, 0, 14)
            });

            var listBox = new ListBox
            {
                MaxHeight = 350,
                BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(249, 250, 251)),
                Margin = new Thickness(0, 0, 0, 16),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            foreach (var item in items)
            {
                string statusStr = item.DaysRemaining < 0 
                    ? $"만료 ({Math.Abs(item.DaysRemaining)}일 경과)" 
                    : (item.DaysRemaining == 0 ? "오늘 만료 (D-Day)" : $"{item.DaysRemaining}일 남음");

                Brush statusBrush = item.DaysRemaining < 0 
                    ? new SolidColorBrush(Color.FromRgb(220, 38, 38)) // Red for expired
                    : (item.DaysRemaining == 0 
                        ? new SolidColorBrush(Color.FromRgb(217, 119, 6)) // Orange for D-Day
                        : new SolidColorBrush(Color.FromRgb(37, 99, 235))); // Blue for <=7 days

                var grid = new Grid { Margin = new Thickness(6, 4, 6, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) }); // Equipment type/No
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Student info
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) }); // Status/Days remaining

                var numTxt = new TextBlock
                {
                    Text = $"{item.Rental.EquipmentType}",
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(numTxt, 0);
                grid.Children.Add(numTxt);

                var infoTxt = new TextBlock
                {
                    Text = $"{item.Rental.StudentName}({item.Rental.StudentId}) · {item.Rental.RentalDate:yyyy-MM-dd} ~ {item.Rental.DueDate:yyyy-MM-dd}",
                    Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(infoTxt, 1);
                grid.Children.Add(infoTxt);

                var statusTxt = new TextBlock
                {
                    Text = statusStr,
                    FontWeight = FontWeights.Bold,
                    Foreground = statusBrush,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(statusTxt, 2);
                grid.Children.Add(statusTxt);

                listBox.Items.Add(grid);
            }

            root.Children.Add(listBox);

            var closeBtn = new Button
            {
                Content = "닫기",
                Width = 100,
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true,
                IsCancel = true,
                Background = new SolidColorBrush(Color.FromRgb(79, 70, 229)), // Indigo
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            closeBtn.Click += (s, e) => Close();
            root.Children.Add(closeBtn);

            Content = root;
        }
    }
}