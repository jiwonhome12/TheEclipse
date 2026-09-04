using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeatManagerApp
{
    /// <summary>
    /// 한 시즌(1학기 / 여름방학 / 2학기 / 겨울방학)의 시작일·종료일.
    /// </summary>
    public class SeasonPeriod
    {
        public string Name { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }

        public SeasonPeriod Clone() => new SeasonPeriod
        {
            Name = Name,
            StartDate = StartDate,
            EndDate = EndDate
        };
    }

    /// <summary>
    /// 한 학사 연도의 4개 시즌 일정.
    /// </summary>
    public class YearSeasonSchedule
    {
        public int Year { get; set; }
        public List<SeasonPeriod> Seasons { get; set; } = new List<SeasonPeriod>();
    }

    /// <summary>
    /// 연도별 학사 시즌 일정. %APPDATA%\SeatManagerApp\seasons.json 에 저장한다.
    /// 대시보드가 어느 시즌의 좌석 데이터를 보여줄지는 이 일정과 현재 날짜로 자동 판별한다.
    /// </summary>
    public class SeasonConfig
    {
        public List<YearSeasonSchedule> Schedules { get; set; } = new List<YearSeasonSchedule>();

        /// <summary>시즌 이름 4종. 순서 고정.</summary>
        public static readonly string[] SeasonNames = { "1학기", "여름방학", "2학기", "겨울방학" };

        public static string ConfigPath => Path.Combine(AppConfig.ConfigDirectory, "seasons.json");

        public static SeasonConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<SeasonConfig>(json);
                    if (cfg != null)
                    {
                        cfg.Schedules ??= new List<YearSeasonSchedule>();
                        cfg.Schedules.Sort((a, b) => a.Year.CompareTo(b.Year));
                        return cfg;
                    }
                }
            }
            catch
            {
                // 설정 파일이 깨졌으면 빈 일정으로 시작한다
            }
            return new SeasonConfig();
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
                // 저장 실패는 치명적이지 않다
            }
        }

        public YearSeasonSchedule? GetYear(int year) =>
            Schedules.FirstOrDefault(s => s.Year == year);

        public bool HasYear(int year) => GetYear(year) != null;

        [JsonIgnore]
        public IEnumerable<int> Years => Schedules.Select(s => s.Year).OrderBy(y => y);

        /// <summary>해당 연도 일정이 없으면 기본 일정으로 만들어 넣는다.</summary>
        public YearSeasonSchedule EnsureYear(int year)
        {
            var sched = GetYear(year);
            if (sched == null)
            {
                sched = new YearSeasonSchedule { Year = year, Seasons = DefaultSeasonsFor(year) };
                Schedules.Add(sched);
                Schedules.Sort((a, b) => a.Year.CompareTo(b.Year));
            }
            return sched;
        }

        public void RemoveYear(int year)
        {
            Schedules.RemoveAll(s => s.Year == year);
        }

        /// <summary>
        /// 예시 일정에 맞춘 기본값.
        /// 1학기 3/1~6/30, 여름방학 7/1~8/31, 2학기 9/1~12/31, 겨울방학 다음 해 1/1~2월 말일.
        /// </summary>
        public static List<SeasonPeriod> DefaultSeasonsFor(int year) => new List<SeasonPeriod>
        {
            new SeasonPeriod { Name = "1학기",   StartDate = new DateTime(year, 3, 1),  EndDate = new DateTime(year, 6, 30) },
            new SeasonPeriod { Name = "여름방학", StartDate = new DateTime(year, 7, 1),  EndDate = new DateTime(year, 8, 31) },
            new SeasonPeriod { Name = "2학기",   StartDate = new DateTime(year, 9, 1),  EndDate = new DateTime(year, 12, 31) },
            new SeasonPeriod { Name = "겨울방학", StartDate = new DateTime(year + 1, 1, 1), EndDate = new DateTime(year + 1, 2, DateTime.DaysInMonth(year + 1, 2)) },
        };

        /// <summary>
        /// 시즌 목록을 이름 순서(1학기→여름방학→2학기→겨울방학)로 맞추고 빠진 시즌은 기본값으로 채운다.
        /// </summary>
        public static List<SeasonPeriod> NormalizeSeasons(int year, IEnumerable<SeasonPeriod> input)
        {
            var defaults = DefaultSeasonsFor(year);
            var byName = input.Where(p => p != null)
                              .GroupBy(p => p.Name)
                              .ToDictionary(g => g.Key, g => g.First());
            var result = new List<SeasonPeriod>();
            foreach (var name in SeasonNames)
            {
                if (byName.TryGetValue(name, out var p))
                    result.Add(new SeasonPeriod { Name = name, StartDate = p.StartDate, EndDate = p.EndDate });
                else
                    result.Add(defaults.First(d => d.Name == name).Clone());
            }
            return result;
        }

        /// <summary>
        /// 현재 날짜가 포함되는 (연도, 시즌 이름). 어느 시즌에도 들지 않으면 null.
        /// </summary>
        public (int Year, string Season)? ResolveSeason(DateTime date)
        {
            DateTime d = date.Date;
            foreach (var sched in Schedules)
            {
                foreach (var p in sched.Seasons)
                {
                    if (d >= p.StartDate.Date && d <= p.EndDate.Date)
                        return (sched.Year, p.Name);
                }
            }
            return null;
        }
    }
}
