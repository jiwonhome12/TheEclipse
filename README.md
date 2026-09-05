# 동서대학교 좌석 및 기자재 관리 시스템

좌석 배정, 캐비닛 배정, 기자재 대여, 상상Lab 신청 승인을 한 프로그램에서 관리합니다.

## 설치 파일

- 위치: [`installer_output/`](installer_output/) 폴더 안의 `SeatManagerApp_Setup_v버전.exe` (예: `SeatManagerApp_Setup_v1.1.0.exe`)
- 가장 최근 버전 번호가 최신 설치 파일입니다.
- 이 폴더는 빌드 결과물이라 git에는 올라가지 않습니다. 폴더가 비어 있거나 최신 버전이 없다면 아래 "설치 파일 새로 만들기"를 참고하세요.

### 설치 방법

1. `SeatManagerApp_Setup_v*.exe` 더블클릭
2. 관리자 권한 없이 내 계정 폴더에 설치됩니다 (설치 위치를 따로 바꿀 필요 없음)
3. 이미 설치돼 있는 상태에서 새 버전을 실행하면 그 자리에서 업데이트됩니다 (기존 데이터 유지)
4. 설치 마지막 화면에서 바로 실행하거나, 시작 메뉴 또는 바탕화면 아이콘으로 실행

### 제거 방법

- **설정 → 앱 → 동서대학교 좌석 및 기자재 관리 시스템 → 제거** (일반 윈도우 프로그램 제거와 동일)

## 설치 후 사용법

### 처음 실행 시

- 오늘 날짜를 기준으로 현재 학기/방학 시즌이 자동으로 선택됩니다 (설정 탭에서 연도별 시즌 일정을 바꿀 수 있습니다)
- 구글 시트 연동을 쓰려면 **설정 탭 → 구글 폼 연동 설정**에서 서비스 계정 키 파일을 등록해야 신청서를 자동으로 받아올 수 있습니다

### 주요 탭

| 탭 | 용도 |
|---|---|
| 대시보드 | 좌석 배정/조회 (연도·학기별로 독립 관리, 랜덤 배정, 좌석 수정 모드로 드래그 이동) |
| 기자재 현황 | 노트북/기기 대여 승인 및 반납 처리 |
| 캐비닛 현황 | 캐비닛 배정, 대여기간은 승인일부터 해당 시즌 마지막 날까지 자동 계산 |
| 상상Lab 승인 | 구글 폼으로 들어온 신청서 승인/반려 |
| 데이터 관리 | 학생 정보 마스터 목록 (시즌별로 필터링해서 볼 수 있음) |
| 설정 | 구글 시트 연동, 시즌 일정, 화면 해상도 |

### 데이터는 어디에 저장되나요

- 전부 `%APPDATA%\SeatManagerApp\` 폴더에 저장됩니다 (설치 위치와 무관)
- 프로그램을 업데이트하거나 다시 설치해도 이 데이터는 그대로 유지됩니다

## 설치 파일 새로 만들기 (개발자용)

코드를 수정한 뒤 새 설치 파일을 만들려면:

```bash
dotnet publish SeatManagerApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

그 다음 [`installer/SeatManagerApp.iss`](installer/SeatManagerApp.iss)의 `MyAppVersion`을 새 버전으로 바꾸고 Inno Setup으로 컴파일합니다:

```bash
"C:\Users\<사용자명>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" installer\SeatManagerApp.iss
```

`installer_output/` 폴더에 새 `SeatManagerApp_Setup_v버전.exe`가 생성됩니다.
