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

        // Master Database (구글 폼 승인 시 채워진다)
        private List<StudentInfo> _masterStudents = new List<StudentInfo>();
        
        // Year & Semester Seat Layout Cache
        private Dictionary<string, List<Seat>> _seatLayoutCache = new Dictionary<string, List<Seat>>();
        
        // Active display seats for current Year & Semester
        private List<Seat> _activeSeats = new List<Seat>();

        // Equipment Rentals
        private ObservableCollection<RentalItem> _rentals = new ObservableCollection<RentalItem>();
        private Stack<RentalItem> _rentalUndoStack = new Stack<RentalItem>();
        private ObservableCollection<RentalItem> _rentalHistory = new ObservableCollection<RentalItem>();

        // Cabinet & SangsangLab Google Form Approvals
        private ObservableCollection<ApprovalRequest> _approvals = new ObservableCollection<ApprovalRequest>();
        private ObservableCollection<ApprovalRequest> _approvalHistory = new ObservableCollection<ApprovalRequest>();

        // Memos
        private ObservableCollection<MemoItem> _memos = new ObservableCollection<MemoItem>();

        private Dictionary<int, (StudentInfo Student, string Period)> _cabinetAllocations = new Dictionary<int, (StudentInfo, string)>();
        private int _currentCabinetPage = 1;

        // App Modes
        private bool _isSeatFixMode = false;
        private bool _isSeatDeleteMode = false;

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

        // ===== 구글 폼(시트) 연동 =====
        private AppConfig _config = new AppConfig();
        private GoogleFormsService? _formsService;
        private System.Windows.Threading.DispatcherTimer? _pollTimer;
        private bool _isSyncing = false;

        /// <summary>
        /// 이미 앱으로 가져온 폼 응답의 SourceKey. 폴링할 때마다 시트 전체를 다시 읽으므로
        /// 승인/반려로 목록에서 사라진 뒤에도 재등록되지 않도록 세션 내내 유지한다.
        /// </summary>
        private readonly HashSet<string> _importedSourceKeys = new HashSet<string>();

        public MainWindow()
        {
            InitializeComponent();

            // Use current system time for date
            _currentSimulatedDate = DateTime.Now;

            // Update date display
            UpdateDateDisplay();

            // Start timer to update date every minute
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromMinutes(1);
            timer.Tick += (s, e) =>
            {
                _currentSimulatedDate = DateTime.Now;
                UpdateDateDisplay();
                UpdateAlertBadges();
            };
            timer.Start();

            // Populate Year dropdown dynamically
            InitializeYearDropdown();

            // 데이터 바인딩만 연결한다. 실제 내용은 구글 폼 동기화와 사용자 입력으로만 채워진다.
            InitializeDataBindings();



            // Refresh UI Badges
            UpdateAlertBadges();

            // Load initial view
            LoadDashboardLayout();

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
            GridMasterStudents.ItemsSource = _masterStudents;
            LstSangsangLabCards.ItemsSource = _approvals.Where(a => a.TabType == "상상Lab").ToList();
            GridCabinetApprovals.ItemsSource = _approvals.Where(a => a.TabType == "캐비닛").ToList();
            BindEquipmentRentals();
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

            if (TxtAvailableMainframeCount != null) TxtAvailableMainframeCount.Text = $"{Math.Max(0, 12 - mainframeRented)}개";
            if (TxtRentedMainframeCount != null) TxtRentedMainframeCount.Text = $"{mainframeRented}개";
            if (TxtAvailableLaptopCount != null) TxtAvailableLaptopCount.Text = $"{Math.Max(0, 12 - laptopRented)}개";
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

                if (TxtAvailableMainframeCount != null) TxtAvailableMainframeCount.Text = $"{Math.Max(0, 12 - mainframeRented)}개";
                if (TxtRentedMainframeCount != null) TxtRentedMainframeCount.Text = $"{mainframeRented}개";
                if (TxtAvailableLaptopCount != null) TxtAvailableLaptopCount.Text = $"{Math.Max(0, 12 - laptopRented)}개";
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
                GridMasterStudents.ItemsSource = null;
                GridMasterStudents.ItemsSource = _masterStudents;
            }

            UpdateAlertBadges();
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

                // Setup specific pillars (Row 3, Col 6, ColSpan 2 -> represents Pillar)
                seats[21].IsPillar = true; // Spot where pillar is located instead of Seat 22

                // Pre-populate students in all active seats using copies of master database
                int studentIdx = 0;
                for (int i = 0; i < seats.Count; i++)
                {
                    var seat = seats[i];
                    if (seat.IsPillar) continue;

                    if (studentIdx < _masterStudents.Count)
                    {
                        // Copy student (Deep copy) so deleting from dashboard won't affect master
                        seat.Student = _masterStudents[studentIdx++].Clone();
                        
                        // Graduate student's seat is always fixed
                        if (seat.Student != null && seat.Student.Department.Contains("대학원"))
                        {
                            seat.IsFixed = true;
                        }
                    }
                }

                _seatLayoutCache[key] = seats;
            }

            _activeSeats = _seatLayoutCache[key];
            RenderSeatGrid();
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
                    Background = pos.IsGray ? new SolidColorBrush(Color.FromRgb(209, 213, 219)) : Brushes.White,
                    BorderThickness = new Thickness(1.5),
                    Margin = new Thickness(3),
                    CornerRadius = new CornerRadius(4),
                    Cursor = Cursors.Hand,
                    Tag = seat
                };

                // Border Color styling depending on state
                bool isFixed = seat.IsFixed || (seat.Student != null && seat.Student.Department.Contains("대학원"));
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
                        Text = seat.Student.StudentId,
                        FontSize = 9,
                        Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    TextBlock nameTxt = new TextBlock
                    {
                        Text = seat.Student.Name,
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

                Grid.SetRow(seatCard, pos.Row);
                Grid.SetColumn(seatCard, pos.Col);
                Grid.SetColumnSpan(seatCard, pos.ColSpan);
                SeatGridContainer.Children.Add(seatCard);
            }
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
                else
                {
                    // Regular Mode: Show student details or add student
                    ShowStudentDetailsModal(seat);
                }
            }
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
            }
            else
            {
                TxtModalSeatNum.Text += " (학생 정보 추가)";
                TxtModalDept.Text = "";
                TxtModalName.Text = "";
                TxtModalId.Text = "";
                TxtModalAdvisor.Text = "";
                TxtModalEmail.Text = "";
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
            // Find newly added students not in the active seats list
            var newStudents = _masterStudents.Where(m => !currentStudentIds.Contains(m.StudentId)).ToList();

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

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult result = MessageBox.Show("데이터 관리에서 학생 정보를 새로고침하시겠습니까?", "새로고침 확인", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                // Clear current seats and reload from master database
                foreach (var seat in _activeSeats)
                {
                    if (seat.Student != null && !seat.IsFixed)
                    {
                        seat.Student = null;
                    }
                }

                var regStudents = _masterStudents.Where(s => !s.Department.Contains("대학원")).ToList();
                var gradStudents = _masterStudents.Where(s => s.Department.Contains("대학원")).ToList();

                int regIdx = 0;
                int gradIdx = 0;

                int[] assignedSeatNumbers = {
                    13, 14, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 34, 36, 37, 38
                };

                foreach (int seatNum in assignedSeatNumbers)
                {
                    if (regIdx < regStudents.Count)
                    {
                        var seat = _activeSeats[seatNum - 1];
                        if (!seat.IsFixed || seat.Student == null)
                        {
                            seat.Student = regStudents[regIdx++].Clone();
                        }
                    }
                }

                for (int seatNum = 43; seatNum <= 52; seatNum++)
                {
                    if (gradIdx < gradStudents.Count)
                    {
                        var seat = _activeSeats[seatNum - 1];
                        seat.Student = gradStudents[gradIdx++].Clone();
                        seat.IsFixed = true;
                    }
                }

                RenderSeatGrid();
                MessageBox.Show("데이터가 새로고침되었습니다.", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    Email = req.Email
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
            RefreshMasterGrid();
            return existing;
        }

        /// <summary>신청서에 값이 없으면 기존 값을 지키고, 있으면 새 값으로 바꾼다.</summary>
        private static string Prefer(string incoming, string current) =>
            string.IsNullOrWhiteSpace(incoming) ? current : incoming;

        /// <summary>_masterStudents는 List라 추가/수정해도 자동 갱신되지 않으므로 다시 바인딩한다.</summary>
        private void RefreshMasterGrid()
        {
            if (GridMasterStudents == null) return;
            GridMasterStudents.ItemsSource = null;
            GridMasterStudents.ItemsSource = _masterStudents;
        }

        // ================= CABINET & SANGSANGLAB APPROVAL HANDLERS =================
        private void BtnApproveRequest_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is ApprovalRequest req)
            {
                // 마스터 반영을 먼저 한다 — 학번 충돌로 관리자가 취소하면 승인 자체를 중단해야 하므로
                var masterStudent = RegisterOrUpdateMaster(req);
                if (masterStudent == null) return;

                req.Status = "승인 완료";
                _approvals.Remove(req);
                _approvalHistory.Add(req);

                // If SangsangLab or Cabinet request, assign student to dashboard if they are approved
                if (req.TabType == "상상Lab" || req.TabType == "캐비닛")
                {
                    // Check duplicate seat assignment and remove from old seat
                    var duplicateSeat = _activeSeats.FirstOrDefault(s => s.Student != null && s.Student.StudentId == masterStudent.StudentId);
                    if (duplicateSeat != null)
                    {
                        duplicateSeat.Student = null;
                        duplicateSeat.IsFixed = false;
                    }

                    // Let's find an empty seat
                    var emptySeat = _activeSeats.FirstOrDefault(s => s.Student == null && !s.IsPillar);
                    if (emptySeat != null)
                    {
                        // 마스터 레코드를 복사해 앉힌다 (신청서가 아니라) — 둘이 어긋나지 않도록
                        emptySeat.Student = masterStudent.Clone();

                        if (emptySeat.Student.Department.Contains("대학원"))
                        {
                            emptySeat.IsFixed = true;
                        }
                    }
                }

                // If cabinet request is approved, assign an empty cabinet block
                if (req.TabType == "캐비닛")
                {
                    int emptyCabinetNumber = Enumerable.Range(1, TotalCabinetCount).FirstOrDefault(n => !_cabinetAllocations.ContainsKey(n));
                    if (emptyCabinetNumber > 0)
                    {
                        _cabinetAllocations[emptyCabinetNumber] =
                            (masterStudent.Clone(), $"{_currentSimulatedDate:MM/dd}~{_currentSimulatedDate.AddMonths(1):MM/dd}");
                    }
                }

                // If equipment request is approved, add to rentals list and history list
                if (req.TabType == "기자재")
                {
                    string equipType = string.IsNullOrEmpty(req.EquipmentType)
                        ? (new Random().Next(2) == 0 ? "HP Z2 G9 (본체)" : "HP OMEN (노트북)")
                        : req.EquipmentType;

                    // 폼에 반납예정일이 있으면 그 날짜에 맞춘다 (없거나 과거면 기본 7일)
                    int periodDays = 7;
                    if (req.DueDate.HasValue)
                    {
                        int requested = (req.DueDate.Value.Date - _currentSimulatedDate.Date).Days;
                        if (requested >= 1) periodDays = requested;
                    }

                    var newRental = new RentalItem
                    {
                        StudentName = masterStudent.Name,
                        EquipmentType = equipType,
                        RentalDate = _currentSimulatedDate,
                        RentalPeriodDays = periodDays,
                        IsReturned = false,

                        // 신청서에 없는 항목은 RentalItem의 기본값을 그대로 둔다
                        StudentId = Prefer(req.StudentId, "20261234"),
                        Department = Prefer(req.Department, "소프트웨어융합학과"),
                        YearLevel = Prefer(req.YearLevel, "3학년"),
                        Phone = Prefer(req.Phone, "010-0000-0000"),
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
            }
        }

        private void BtnRejectRequest_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is ApprovalRequest req)
            {
                req.Status = "반려";
                _approvals.Remove(req);
                _approvalHistory.Add(req);

                // Add to history list
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

                GridMasterStudents.ItemsSource = null;
                GridMasterStudents.ItemsSource = _masterStudents;
                RenderSeatGrid();
                MessageBox.Show("마스터 데이터가 수정되었으며 즉시 대시보드에 데이터가 업데이트되었습니다.", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnSyncData_Click(object sender, RoutedEventArgs e)
        {
            int syncCount = 0;
            foreach (var seat in _activeSeats)
            {
                if (seat.Student != null)
                {
                    var master = _masterStudents.FirstOrDefault(m => m.StudentId == seat.Student.StudentId);
                    if (master != null)
                    {
                        seat.Student.Name = master.Name;
                        seat.Student.Department = master.Department;
                        seat.Student.Advisor = master.Advisor;
                        seat.Student.Email = master.Email;
                        if (seat.Student.Department.Contains("대학원"))
                        {
                            seat.IsFixed = true;
                        }
                        syncCount++;
                    }
                }
            }
            RenderSeatGrid();
            UpdateAlertBadges();
            MessageBox.Show($"총 {syncCount}명의 학생 데이터가 마스터 데이터베이스와 동기화되었습니다.", "동기화 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnDeleteMaster_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMasterStudent != null)
            {
                var result = MessageBox.Show($"정말 {_selectedMasterStudent.Name} 학생을 마스터 데이터베이스에서 삭제하시겠습니까?", "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    _masterStudents.Remove(_selectedMasterStudent);
                    GridMasterStudents.ItemsSource = null;
                    GridMasterStudents.ItemsSource = _masterStudents;
                    PanelMasterEdit.IsEnabled = false;
                    MessageBox.Show("마스터 데이터가 삭제되었습니다. (기존 배치된 좌석 데이터는 유지됩니다.)", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        // ================= GOOGLE FORM (SHEETS) SYNC =================

        /// <summary>사용자가 만든 응답 시트. 설정에 값이 없을 때의 기본값.</summary>
        private const string DefaultSpreadsheetId = "1TejlN7HTU7n2mvkKIilCrMlSivHsey3h-nOuFOeR7t4";

        /// <summary>기자재 폼 전용 응답 시트. 설정에 값이 없을 때의 기본값.</summary>
        private const string DefaultEquipmentSpreadsheetId = "152AqDmrq6LF82E41bMNVOeD74h0Ks4VXAAyzHlA4gVw";

        private void InitializeGoogleFormSync()
        {
            _config = AppConfig.Load();
            if (string.IsNullOrWhiteSpace(_config.SpreadsheetId))
                _config.SpreadsheetId = DefaultSpreadsheetId;
            if (string.IsNullOrWhiteSpace(_config.EquipmentFormUrl))
                _config.EquipmentFormUrl = DefaultEquipmentSpreadsheetId;

            _formsService = new GoogleFormsService(_config.ResolveServiceAccountKeyPath(), _config.SpreadsheetId);
            _formsService.EquipmentSpreadsheetId =
                GoogleFormsService.ExtractSpreadsheetId(_config.EquipmentFormUrl);

            // 설정 화면에 현재 값 반영
            TxtSpreadsheetId.Text = _config.SpreadsheetId;
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
            _config.SpreadsheetId = TxtSpreadsheetId.Text.Trim();
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

            // 기자재 폼 칸에서 응답 시트 ID를 뽑아낸다. 폼 주소를 그대로 넣으면 Sheets API로는 읽을 수 없다.
            string equipmentSheetId = GoogleFormsService.ExtractSpreadsheetId(_config.EquipmentFormUrl);

            _config.Save();
            if (_formsService != null)
            {
                _formsService.SpreadsheetId = _config.SpreadsheetId;
                _formsService.EquipmentSpreadsheetId = equipmentSheetId;
            }
            ApplyPollingTimer();

            string message;
            if (_config.EquipmentFormUrl.Length == 0)
            {
                message = "연동 설정을 저장했습니다.";
            }
            else if (equipmentSheetId.Length > 0)
            {
                message = "연동 설정을 저장했습니다.\n\n" +
                          $"기자재 신청은 별도 시트에서 받아옵니다.\n시트 ID: {equipmentSheetId}\n\n" +
                          "이 시트도 서비스 계정에 '뷰어'로 공유되어 있어야 합니다.";
            }
            else
            {
                message = "연동 설정을 저장했지만, 기자재 칸의 주소로는 시트를 읽을 수 없습니다.\n\n" +
                          (GoogleFormsService.IsFormUrl(_config.EquipmentFormUrl)
                              ? "구글 폼 주소가 입력되어 있습니다. 폼 편집 화면의 [응답] → [시트에서 보기]로 열리는 "
                              : "스프레드시트 주소를 알아볼 수 없습니다. ") +
                          "스프레드시트 주소(docs.google.com/spreadsheets/d/...)를 붙여넣어 주세요.\n\n" +
                          "그전까지 기자재 신청은 위 '응답 스프레드시트 ID' 시트에서만 받아옵니다.";
            }

            MessageBox.Show(message, "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
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

                    _importedSourceKeys.Add(req.SourceKey);
                    _approvals.Add(req);
                }

                RefreshApprovalViews();

                string summary = $"[{DateTime.Now:HH:mm:ss}] 응답 {result.TotalRows}건 중 신규 {result.NewRequests.Count}건 반영";
                if (result.DuplicateCount > 0) summary += $" (기존 {result.DuplicateCount}건 건너뜀)";

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
                else if (result.NewRequests.Count > 0)
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
            MessageBox.Show("캐비닛 데이터 고정 기능 구현용 이벤트 핸들러입니다.", "캐비닛 고정", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnCabinetExport_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("캐비닛 데이터 추출 기능 구현용 이벤트 핸들러입니다.", "캐비닛 추출", MessageBoxButton.OK, MessageBoxImage.Information);
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
                FontSize = 10,
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

                isMatch = alloc.Student.Name.ToLower().Contains(query) || 
                          alloc.Student.StudentId.ToLower().Contains(query) || 
                          alloc.Student.Department.ToLower().Contains(query);

                TextBlock studentTxt = new TextBlock
                {
                    Text = $"{alloc.Student.StudentId} {alloc.Student.Name}",
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                stack.Children.Add(studentTxt);

                TextBlock periodTxt = new TextBlock
                {
                    Text = alloc.Period,
                    FontSize = 8,
                    Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 0)
                };
                stack.Children.Add(periodTxt);
            }
            else
            {
                numTxt.FontSize = 13;
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
                _currentEditingCabinetNum = number;
                if (_cabinetAllocations.ContainsKey(number))
                {
                    var alloc = _cabinetAllocations[number];
                    TxtCabinetModalTitle.Text = $"캐비닛 {number}번 상세 정보";
                    TxtCabinetModalName.Text = alloc.Student.Name;
                    TxtCabinetModalId.Text = alloc.Student.StudentId;
                    TxtCabinetModalPeriod.Text = alloc.Period;
                    
                    SetCabinetModalEditMode(false);
                    ModalCabinetDetails.Visibility = Visibility.Visible;
                }
                else
                {
                    var result = MessageBox.Show($"[캐비닛 {number}번] 사용 가능 (미배정) 상태입니다.\n이 캐비닛에 새로운 학생을 임의 배정하시겠습니까?", "캐비닛 배정", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        TxtCabinetModalTitle.Text = $"캐비닛 {number}번 임의 배정";
                        TxtCabinetModalName.Text = "";
                        TxtCabinetModalId.Text = "";
                        TxtCabinetModalPeriod.Text = $"{_currentSimulatedDate:yyyy.MM.dd}~{_currentSimulatedDate.AddMonths(1):yyyy.MM.dd}";
                        
                        SetCabinetModalEditMode(true);
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
                BtnEditCabinetInfo.Content = "취소";
            }
            else
            {
                TxtCabinetModalName.Background = Brushes.Transparent; TxtCabinetModalName.BorderThickness = new Thickness(0);
                TxtCabinetModalId.Background = Brushes.Transparent; TxtCabinetModalId.BorderThickness = new Thickness(0);
                TxtCabinetModalPeriod.Background = Brushes.Transparent; TxtCabinetModalPeriod.BorderThickness = new Thickness(0);
                BtnSaveCabinetModal.Visibility = Visibility.Collapsed;
                BtnEditCabinetInfo.Content = "정보 수정";
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

            var student = new StudentInfo
            {
                StudentId = id,
                Name = name,
                Department = "소프트웨어융합학과"
            };

            _cabinetAllocations[_currentEditingCabinetNum] = (student, period);
            
            RenderCabinetGrid();
            UpdateAlertBadges();
            SetCabinetModalEditMode(false);
            ModalCabinetDetails.Visibility = Visibility.Collapsed;
            
            MessageBox.Show("캐비닛 정보가 저장되었습니다.", "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
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
            string query = TxtSearchStudent.Text.Trim().ToLower();
            if (string.IsNullOrEmpty(query))
            {
                GridMasterStudents.ItemsSource = _masterStudents;
            }
            else
            {
                var filtered = _masterStudents.Where(s => 
                    s.StudentId.ToLower().Contains(query) ||
                    s.Name.ToLower().Contains(query) ||
                    s.Advisor.ToLower().Contains(query) ||
                    s.Department.ToLower().Contains(query)
                ).ToList();
                GridMasterStudents.ItemsSource = filtered;
            }
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
            GridMasterStudents.ItemsSource = _masterStudents;
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

        private void BtnImportMainframeExcel_Click(object sender, RoutedEventArgs e)
        {
            ImportEquipmentExcel("본체 (HP Z2 G9)");
        }

        private void BtnImportLaptopExcel_Click(object sender, RoutedEventArgs e)
        {
            ImportEquipmentExcel("노트북(HP OMEN 게이밍 노트북)");
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
                        string period = values.Count > 4 ? (values[4]?.ToString() ?? "") : $"{_currentSimulatedDate:MM/dd}~{_currentSimulatedDate.AddMonths(1):MM/dd}";

                        var student = new StudentInfo
                        {
                            StudentId = id,
                            Name = name,
                            Department = dept
                        };

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
                                Email = email
                            };
                            _masterStudents.Add(student);
                            importedCount++;
                        }
                    }

                    GridMasterStudents.ItemsSource = null;
                    GridMasterStudents.ItemsSource = _masterStudents;
                    MessageBox.Show($"엑셀 파일로부터 {importedCount}명의 마스터 학생 정보를 등록했습니다.", "가져오기 성공", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"엑셀 파일을 읽는 도중 오류가 발생했습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void TxtSearchEquipment_KeyUp(object sender, KeyEventArgs e)
        {
            BindEquipmentRentals();
        }

        private void TxtSearchCabinet_KeyUp(object sender, KeyEventArgs e)
        {
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
        private void BtnSelectStudent_Click(object sender, RoutedEventArgs e)
        {
            TxtSearchStudentSelector.Clear();
            GridStudentSelector.ItemsSource = _masterStudents;
            ModalStudentListSelector.Visibility = Visibility.Visible;
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
            if (GridStudentSelector.SelectedItem is StudentInfo selected && _currentEditingSeat != null)
            {
                // Check duplicate seat assignment and remove from old seat
                var duplicateSeat = _activeSeats.FirstOrDefault(s => s.Student != null && s.Student.StudentId == selected.StudentId && s.SeatNumber != _currentEditingSeat.SeatNumber);
                if (duplicateSeat != null)
                {
                    duplicateSeat.Student = null;
                    duplicateSeat.IsFixed = false;
                }

                // Assign selected student to current seat (Cloned copy)
                _currentEditingSeat.Student = selected.Clone();

                // Check graduate
                if (_currentEditingSeat.Student.Department.Contains("대학원"))
                {
                    _currentEditingSeat.IsFixed = true;
                }

                // Update UI fields in parent modal
                TxtModalName.Text = selected.Name;
                TxtModalId.Text = selected.StudentId;
                TxtModalDept.Text = selected.Department;
                TxtModalAdvisor.Text = selected.Advisor;
                TxtModalEmail.Text = selected.Email;
                ItemsModalAttendance.ItemsSource = selected.Attendance;

                RenderSeatGrid();
                UpdateAlertBadges();

                ModalStudentListSelector.Visibility = Visibility.Collapsed;
                MessageBox.Show($"학생 '{selected.Name}'이(가) 좌석에 배정되었습니다. '저장'을 누르면 최종 적용됩니다.", "학생 배정 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("배정할 학생을 먼저 선택하세요.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ================= APPROVAL HISTORY LOGIC =================
        private void BtnShowApprovalHistory_Click(object sender, RoutedEventArgs e)
        {
            GridApprovalHistory.ItemsSource = null;
            GridApprovalHistory.ItemsSource = _approvalHistory;
            ModalApprovalHistory.Visibility = Visibility.Visible;
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
                    GridApprovalHistory.ItemsSource = null;
                    GridApprovalHistory.ItemsSource = _approvalHistory;

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
    }
}