using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SeatManagerApp
{
    /// <summary>
    /// 구글 시트 연동 설정. %APPDATA%\SeatManagerApp\settings.json 에 저장한다.
    /// 서비스 계정 키도 같은 폴더에 두므로 저장소(OneDrive/git)에는 아무것도 남지 않는다.
    /// </summary>
    public class AppConfig
    {
        public string SpreadsheetId { get; set; } = string.Empty;
        public bool PollingEnabled { get; set; } = true;
        public int PollingIntervalSeconds { get; set; } = 60;

        // 신청 폼 주소 (기록/안내용 — 동기화는 위 SpreadsheetId로 이루어진다)
        public string SangsangLabFormUrl { get; set; } = string.Empty;
        public string CabinetFormUrl { get; set; } = string.Empty;

        /// <summary>
        /// 기자재 신청 창구. 응답 스프레드시트 주소를 넣으면 기자재 신청은 그 시트에서 따로 동기화한다
        /// (<see cref="GoogleFormsService.ExtractSpreadsheetId"/>로 ID를 뽑는다).
        /// 폼 주소만 넣으면 안내용으로만 남고 동기화에는 쓰이지 않는다.
        /// </summary>
        public string EquipmentFormUrl { get; set; } = string.Empty;

        public static string ConfigDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SeatManagerApp");

        /// <summary>사용자가 [키 파일 선택]으로 직접 지정한 경로. 비어 있으면 기본 위치를 쓴다.</summary>
        public string KeyPathOverride { get; set; } = string.Empty;

        public static string ConfigPath => Path.Combine(ConfigDirectory, "settings.json");

        /// <summary>키의 표준 보관 위치</summary>
        public static string DefaultServiceAccountKeyPath =>
            Path.Combine(ConfigDirectory, "google-service-account.json");

        /// <summary>
        /// 키 파일을 실제로 찾는다. 지정 경로 → 표준 위치 → 실행 파일 옆 순으로 확인하고,
        /// 어디에도 없으면 표준 위치를 돌려준다(오류 메시지에 쓰기 위해).
        /// </summary>
        public string ResolveServiceAccountKeyPath()
        {
            foreach (string candidate in CandidateKeyPaths())
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }
            return DefaultServiceAccountKeyPath;
        }

        private IEnumerable<string> CandidateKeyPaths()
        {
            yield return KeyPathOverride;
            yield return DefaultServiceAccountKeyPath;

            // 실행 파일 옆에 두고 쓰는 경우 (배포판에서 흔함)
            string exeDir = AppContext.BaseDirectory;
            yield return Path.Combine(exeDir, "google-service-account.json");
        }

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                    if (cfg != null) return cfg;
                }
            }
            catch
            {
                // 설정 파일이 깨졌으면 기본값으로 시작한다
            }
            return new AppConfig();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch
            {
                // 저장 실패는 치명적이지 않다 — 다음 실행에서 기본값을 쓴다
            }
        }
    }
}
