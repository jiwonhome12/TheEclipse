# Auto Build and Run Script for SeatManagerApp
# 코드 변경 감시하고 자동 빌드/실행

$projectPath = "C:\Users\kimji\OneDrive\Desktop\22L"
$exePath = "$projectPath\bin\Debug\net10.0-windows\SeatManagerApp.exe"
$lastBuildTime = Get-Date -Year 2000 -Month 1 -Day 1

# 감시할 파일 패턴
$fileFilter = @("*.xaml", "*.xaml.cs", "*.cs")

Write-Host "================================" -ForegroundColor Cyan
Write-Host "SeatManagerApp Auto Build & Run" -ForegroundColor Cyan
Write-Host "================================" -ForegroundColor Cyan 
Write-Host ""
Write-Host "감시 경로: $projectPath" -ForegroundColor Yellow
Write-Host "코드 변경을 감시하고 있습니다..." -ForegroundColor Yellow
Write-Host ""

function Build-And-Run {
    Write-Host ""
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] 코드 변경 감지됨. 빌드 시작..." -ForegroundColor Green

    # 기존 프로세스 종료
    $proc = Get-Process -Name "SeatManagerApp" -ErrorAction SilentlyContinue
    if ($proc) {
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] 실행 중인 앱 종료 중..." -ForegroundColor Yellow
        Stop-Process -Name "SeatManagerApp" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }

    # 빌드
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] dotnet build 실행 중..." -ForegroundColor Yellow
    Push-Location $projectPath
    $buildOutput = dotnet build 2>&1
    Pop-Location

    # 빌드 결과 확인
    if ($buildOutput -like "*오류*" -or $buildOutput -like "*error*") {
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ❌ 빌드 실패!" -ForegroundColor Red
        Write-Host $buildOutput -ForegroundColor Red
        return $false
    }

    # exe 파일 존재 확인
    if (-not (Test-Path $exePath)) {
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ❌ exe 파일을 찾을 수 없습니다: $exePath" -ForegroundColor Red
        return $false
    }

    # 앱 실행
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ✅ 빌드 성공! 앱 실행 중..." -ForegroundColor Green
    Start-Process $exePath
    $script:lastBuildTime = Get-Date

    return $true
}

# 처음 빌드
Write-Host "[$(Get-Date -Format 'HH:mm:ss')] 초기 빌드 실행..." -ForegroundColor Cyan
Build-And-Run

# 파일 감시
$watcher = New-Object System.IO.FileSystemWatcher
$watcher.Path = $projectPath
$watcher.IncludeSubdirectories = $true
$watcher.EnableRaisingEvents = $true

$lastChangeTime = Get-Date

$onChanged = {
    # 0.5초 이내의 중복 이벤트 무시
    $currentTime = Get-Date
    if (($currentTime - $script:lastChangeTime).TotalMilliseconds -lt 500) {
        return
    }

    $item = $Event.SourceEventArgs

    # 감시 대상 파일인지 확인
    $isWatchedFile = $false
    foreach ($filter in $fileFilter) {
        if ($item.Name -like $filter) {
            $isWatchedFile = $true
            break
        }
    }

    if ($isWatchedFile -and $item.ChangeType -ne "Deleted") {
        $script:lastChangeTime = Get-Date
        Build-And-Run
    }
}

Register-ObjectEvent -InputObject $watcher -EventName "Changed" -SourceIdentifier "FileChanged" -Action $onChanged | Out-Null

Write-Host "[$(Get-Date -Format 'HH:mm:ss')] 파일 감시 시작. (Ctrl+C로 종료)" -ForegroundColor Green
Write-Host ""

# Ctrl+C까지 계속 실행
try {
    while ($true) {
        Start-Sleep -Milliseconds 100
    }
}
finally {
    Write-Host ""
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] 모니터링 종료" -ForegroundColor Yellow
    Unregister-Event -SourceIdentifier "FileChanged" -ErrorAction SilentlyContinue
    $watcher.Dispose()
}
