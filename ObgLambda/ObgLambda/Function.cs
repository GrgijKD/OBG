using Amazon.Lambda.Core;
using Amazon.LocationService;
using ObgLambda.Excel;
using ObgLambda.Json;
using ObgServices.Models;
using ObgServices.Services;
using System.Text;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ObgLambda;

public class Function
{
    private readonly AmazonLocationServiceClient _locationClient = new();

    public async Task<List<WeeklyRoute>> FunctionHandler(RoutingRequest request, ILambdaContext context)
    {
        try
        {
            context.Logger.LogLine("[BOOT] Function started");

            if (UseExcelInput())
            {
                var excel = ExcelInputLoader.TryLoadFirstExcel(context.Logger);

                if (excel is null)
                {
                    context.Logger.LogLine("Excel input не знайдено в папці 'input'.");
                    return [];
                }

                try
                {
                    context.Logger.LogLine($"Excel input знайдено: '{excel.FileName}' ({excel.Content.Length} bytes). Починаю парсинг...");
                    request = ExcelRoutingRequestParser.Parse(excel.Content, excel.FileName, context.Logger);
                    context.Logger.LogLine($"Excel парсинг завершено: Technicians={request.Technicians.Count}, Sites={request.Sites.Count}, TargetDates={request.TargetDates.Count}");
                }
                catch (Exception ex)
                {
                    context.Logger.LogLine($"Не вдалося розпарсити Excel '{excel.FileName}'. {ex}");
                    WriteCrashFile($"Excel parse error:{Environment.NewLine}{ex}");
                    return [];
                }
            }

            context.Logger.LogLine("Генерація Майстер-розкладу...");
            var geocodingService = new GeocodingService(_locationClient);

            var serviceIdMap = request.Sites
                .Select((site, index) => new { site.Id, DisplayId = $"srv-{index + 1}" })
                .ToDictionary(x => x.Id, x => x.DisplayId);

            var masterSchedule = MasterSchedulerService.GenerateMasterSchedule(request.Sites, request.Technicians, serviceIdMap);
            int horizon = masterSchedule.Count;
            DateTime cycleStartDate = GetNextMonday(DateTime.Today);

            OutputJsonWriter.TryWriteMasterCalendarJson(masterSchedule, cycleStartDate, horizon, request.Sites, context.Logger);

            int routePlanningDays = RoutingPlanningConfigLoader.LoadRoutePlanningDays(context.Logger);
            context.Logger.LogLine($"[CONFIG] RoutePlanningDays={routePlanningDays}");

            var allDates = request.TargetDates != null && request.TargetDates.Count != 0
                ? request.TargetDates.OrderBy(d => d).ToList()
                : Enumerable.Range(0, Math.Max(horizon, 7))
                    .Select(offset => cycleStartDate.AddDays(offset))
                    .ToList();

            var datesToProcess = allDates.Take(routePlanningDays).ToList();
            context.Logger.LogLine($"[PLAN] masterDays={allDates.Count}, routingDays={datesToProcess.Count}");

            var finalResult = new List<WeeklyRoute>();

            foreach (var t in request.Technicians)
                t.CurrentScheduledHours = 0;

            int? lastProcessedWeekNum = null;

            foreach (var targetDate in datesToProcess)
            {
                try
                {
                    int totalBusinessDays = BusinessDayHelper.GetBusinessDayIndex(cycleStartDate, targetDate);
                    int currentDayIndex = horizon > 0 ? totalBusinessDays % horizon : 0;
                    int currentWeekNum = totalBusinessDays / 7;

                    var sitesForDate = horizon > 0 && masterSchedule.ContainsKey(currentDayIndex)
                        ? masterSchedule[currentDayIndex]
                        : [];

                    context.Logger.LogLine($"[DAY] {targetDate:dd.MM.yyyy} dayIndex={currentDayIndex}, scheduledSites={sitesForDate.Count}");

                    if (sitesForDate.Count == 0)
                    {
                        finalResult.Add(new WeeklyRoute
                        {
                            Day = targetDate.DayOfWeek,
                            Date = targetDate.Date,
                            Routes = []
                        });
                        continue;
                    }

                    if (lastProcessedWeekNum != null && currentWeekNum > lastProcessedWeekNum)
                    {
                        foreach (var t in request.Technicians)
                            t.CurrentScheduledHours = 0;
                    }

                    lastProcessedWeekNum = currentWeekNum;

                    var availableTechs = request.Technicians
                        .Where(t => t.CurrentScheduledHours < t.MaxWeeklyHours)
                        .Where(t => sitesForDate.Any(s => TechnicianFilterService.ValidateHardConstraints(t, s)))
                        .ToList();

                    if (availableTechs.Count == 0)
                    {
                        context.Logger.LogLine($"[DAY] {targetDate:dd.MM.yyyy}: activeTechs=0, stops=0, totalHours=0.0");
                        finalResult.Add(new WeeklyRoute
                        {
                            Day = targetDate.DayOfWeek,
                            Date = targetDate.Date,
                            Routes = []
                        });
                        continue;
                    }

                    var routingData = await RoutingDataFactory.CreateModel(availableTechs, sitesForDate, geocodingService);
                    var dailyRoutes = RoutingSolverService.SolveRouting(routingData, availableTechs, targetDate.DayOfWeek);

                    if (dailyRoutes != null && dailyRoutes.Any(r => r.Stops.Count > 0))
                    {
                        int activeTechs = 0;
                        int totalStops = 0;
                        double totalHours = 0.0;

                        foreach (var route in dailyRoutes)
                        {
                            if (route.Stops.Count > 0)
                                activeTechs++;

                            totalStops += route.Stops.Count;

                            double addedHours = route.TotalDurationMinutes / 60.0;
                            totalHours += addedHours;

                            var tech = request.Technicians.FirstOrDefault(t => t.Name == route.TechnicianName);
                            if (tech != null)
                            {
                                tech.CurrentScheduledHours += addedHours;
                            }
                        }

                        context.Logger.LogLine($"[DAY] {targetDate:dd.MM.yyyy}: activeTechs={activeTechs}, stops={totalStops}, totalHours={totalHours:F1}");

                        finalResult.Add(new WeeklyRoute
                        {
                            Day = targetDate.DayOfWeek,
                            Date = targetDate.Date,
                            Routes = dailyRoutes
                        });
                    }
                    else
                    {
                        context.Logger.LogLine($"[DAY] {targetDate:dd.MM.yyyy}: activeTechs=0, stops=0, totalHours=0.0");
                        finalResult.Add(new WeeklyRoute
                        {
                            Day = targetDate.DayOfWeek,
                            Date = targetDate.Date,
                            Routes = []
                        });
                    }
                }
                catch (Exception exDay)
                {
                    context.Logger.LogLine($"[DAY-ERROR] {targetDate:dd.MM.yyyy}: {exDay.Message}");
                    WriteCrashFile($"Day error {targetDate:dd.MM.yyyy}:{Environment.NewLine}{exDay}");

                    finalResult.Add(new WeeklyRoute
                    {
                        Day = targetDate.DayOfWeek,
                        Date = targetDate.Date,
                        Routes = []
                    });
                }
            }

            var allRoutes = finalResult.SelectMany(w => w.Routes).ToList();

            OutputJsonWriter.TryWriteScheduleJson(allRoutes, request.Technicians, request.Sites, context.Logger);

            context.Logger.LogLine("[DONE] Function finished successfully");
            return finalResult;
        }
        catch (Exception ex)
        {
            context.Logger.LogLine($"[FATAL] {ex.Message}");
            WriteCrashFile($"Fatal error:{Environment.NewLine}{ex}");
            return [];
        }
    }

    private static DateTime GetNextMonday(DateTime fromDate)
    {
        var date = fromDate.Date;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0)
            daysUntilMonday = 7;
        return date.AddDays(daysUntilMonday);
    }

    private static bool UseExcelInput()
    {
        var raw = Environment.GetEnvironmentVariable("OBG_USE_EXCEL_INPUT");
        return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteCrashFile(string text)
    {
        try
        {
            var root = ResolveProjectRoot();
            var dir = Path.Combine(root, "output");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "runtime-error.txt");
            File.WriteAllText(path, text, Encoding.UTF8);
        }
        catch
        {
        }
    }

    public static string ResolveProjectRoot()
    {
        var cur = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (cur != null)
        {
            var input = Path.Combine(cur.FullName, "input");
            var lambda = Path.Combine(cur.FullName, "ObgLambda");
            var services = Path.Combine(cur.FullName, "ObgServices");

            if (Directory.Exists(input) && Directory.Exists(lambda) && Directory.Exists(services))
                return cur.FullName;

            cur = cur.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}

public class RoutingRequest
{
    public List<Technician> Technicians { get; set; } = [];
    public List<ServiceSite> Sites { get; set; } = [];
    public List<DateTime> TargetDates { get; set; } = [];
}
