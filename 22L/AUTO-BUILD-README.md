# 자동 빌드 & 실행 가이드

## 개요
이 도구는 코드를 수정할 때마다 **자동으로 빌드하고 실행**하는 기능을 제공합니다.

## 사용 방법

### 방법 1: 배치 파일로 실행 (권장) 
```
auto-build.bat 파일을 더블클릭하면 됩니다.
```

### 방법 2: PowerShell로 실행
```powershell
powershell -ExecutionPolicy Bypass -File auto-build.ps1
```

또는 PowerShell에서:
```powershell
Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope CurrentUser
.\auto-build.ps1
```

## 동작 원리

1. **초기 빌드**: 스크립트 시작 시 먼저 전체 프로젝트를 빌드합니다.
2. **파일 감시**: 다음 파일들의 변경을 자동으로 감시합니다:
   - `*.xaml` (UI 정의 파일)
   - `*.xaml.cs` (UI 코드 파일)
   - `*.cs` (C# 소스 파일)
3. **자동 빌드**: 파일이 변경되면 자동으로 빌드를 시작합니다.
4. **자동 실행**: 빌드가 성공하면 기존 앱을 종료하고 새 버전을 실행합니다.

## 기능

✅ **파일 변경 감시**: 코드 저장 시 자동 감지
✅ **자동 빌드**: dotnet build 자동 실행
✅ **충돌 방지**: 이전 프로세스 자동 종료 후 새로 실행
✅ **실시간 피드백**: 빌드 진행상황 실시간 표시
✅ **오류 감시**: 빌드 실패 시 즉시 알림

## 종료 방법

스크립트를 종료하려면:
- **배치 파일**: 커맨드 창 닫기 또는 `Ctrl+C`
- **PowerShell**: `Ctrl+C` 입력

## 트러블슈팅

### 문제: "권한 거부" 오류
**해결책**: PowerShell 실행 정책 변경
```powershell
Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope CurrentUser
```

### 문제: 앱이 실행되지 않음
**확인 사항**:
1. 빌드 로그에서 오류 메시지 확인
2. 컴파일 오류가 있는지 확인
3. `bin\Debug\net10.0-windows\SeatManagerApp.exe` 파일 존재 확인

### 문제: 빈번한 빌드 (성능 저하)
**해결책**: 임시 파일들이 변경되지 않도록 주의
- `.gitignore`에 등록된 파일은 자동 무시됨

## 팁

1. **효율적인 개발**: 파일을 저장하면 즉시 변경사항을 앱에서 확인할 수 있습니다.
2. **빌드 로그**: 콘솔 창에서 빌드 과정과 오류를 실시간으로 확인 가능합니다.
3. **다중 창**: 코드 에디터와 자동 빌드 창을 나란히 띄워 작업하면 편리합니다.

## 설정 커스터마이징

스크립트를 편집하여 다음을 변경할 수 있습니다:

### 감시 대상 파일 변경
```powershell
$fileFilter = @("*.xaml", "*.xaml.cs", "*.cs")  # 이 부분 수정
```

### 감시 경로 변경
```powershell
$projectPath = "C:\Users\kimji\OneDrive\Desktop\22L"  # 이 부분 수정
```

### 중복 감시 무시 시간 조정
```powershell
if (($currentTime - $script:lastChangeTime).TotalMilliseconds -lt 500)  # 500ms 변경 가능
```

---

**이 도구로 개발 생산성을 크게 향상시킬 수 있습니다!** 🚀
