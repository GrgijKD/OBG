using Amazon.Lambda.Core;
using Amazon.LocationService;
using ObgLambda.Excel;
using ObgLambda.Json;
using ObgServices.Models;
using ObgServices.Services;
using System.Text;

//[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

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
                context.Logger.LogLine("[BOOT] Excel input mode = ON");
                var excel = ExcelInputLoader.TryLoadFirstExcel(context.Logger);

                if (excel is null)
                {
                    context.Logger.LogLine("Excel input не знайдено в папці 'input'. JSON з поля ігнорується (поки що).");
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
            else
            {
                context.Logger.LogLine("[BOOT] Excel input mode = OFF");
            }

            context.Logger.LogLine("Генерація Майстер-розкладу...");
            var geocodingService = new GeocodingService(_locationClient);

            var masterSchedule = MasterSchedulerService.GenerateMasterSchedule(request.Sites, request.Technicians);
            int horizon = masterSchedule.Count;
            DateTime cycleStartDate = GetNextMonday(DateTime.Today);

            context.Logger.LogLine($"[MASTER-EXPORT] start. cycleStartDate={cycleStartDate:yyyy-MM-dd}, horizon={horizon}");

            OutputJsonWriter.TryWriteMasterCalendarJson(masterSchedule, cycleStartDate, horizon, context.Logger);

            context.Logger.LogLine("[MASTER-EXPORT] done");

            var finalResult = new List<WeeklyRoute>();

            var datesToProcess = request.TargetDates != null && request.TargetDates.Count != 0
                ? request.TargetDates.OrderBy(d => d).ToList()
                : Enumerable.Range(0, Math.Max(horizon, 7))
                    .Select(offset => cycleStartDate.AddDays(offset))
                    .ToList();

            context.Logger.LogLine($"[PLAN] datesToProcess={datesToProcess.Count}");

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
                        context.Logger.LogLine($"! Неможливо побудувати розклад на {targetDate:dd.MM.yyyy}: на цей день немає візитів");
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

                    context.Logger.LogLine($"Обробка дати {targetDate:dd.MM.yyyy}");

                    foreach (var tech in request.Technicians)
                    {
                        var weeklyOk = tech.CurrentScheduledHours < tech.MaxWeeklyHours;
                        var hardEligibleSites = sitesForDate
                            .Where(s => TechnicianFilterService.ValidateHardConstraints(tech, s))
                            .Select(s => s.Id)
                            .ToList();

                        context.Logger.LogLine(
                            $"[DAY][TECH] {tech.Name}: weekly={tech.CurrentScheduledHours:F1}/{tech.MaxWeeklyHours}, weeklyOk={weeklyOk}, hardEligibleToday={hardEligibleSites.Count}, sites=[{string.Join(", ", hardEligibleSites)}]");
                    }

                    var availableTechs = request.Technicians
                        .Where(t => t.CurrentScheduledHours < t.MaxWeeklyHours)
                        .Where(t => sitesForDate.Any(s => TechnicianFilterService.ValidateHardConstraints(t, s)))
                        .ToList();

                    context.Logger.LogLine($"[DAY] availableTechs=[{string.Join(", ", availableTechs.Select(t => t.Name))}]");

                    if (availableTechs.Count == 0)
                    {
                        context.Logger.LogLine($"! Немає доступних техніків на {targetDate:dd.MM.yyyy}");
                        finalResult.Add(new WeeklyRoute
                        {
                            Day = targetDate.DayOfWeek,
                            Date = targetDate.Date,
                            Routes = []
                        });
                        continue;
                    }

                    context.Logger.LogLine($"[ROUTING] create model for {targetDate:dd.MM.yyyy}");
                    var routingData = await RoutingDataFactory.CreateModel(availableTechs, sitesForDate, geocodingService);

                    context.Logger.LogLine($"[ROUTING] solve for {targetDate:dd.MM.yyyy}");
                    var dailyRoutes = RoutingSolverService.SolveRouting(routingData, availableTechs, targetDate.DayOfWeek);

                    if (dailyRoutes != null && dailyRoutes.Any(r => r.Stops.Count > 0))
                    {
                        context.Logger.LogLine($"Побудовано розклад техніків на {targetDate:dd.MM.yyyy}:");

                        foreach (var route in dailyRoutes)
                        {
                            var tech = request.Technicians.FirstOrDefault(t => t.Name == route.TechnicianName);
                            if (tech != null)
                            {
                                double addedHours = route.TotalDurationMinutes / 60.0;
                                tech.CurrentScheduledHours += addedHours;

                                context.Logger.LogLine(
                                    $"Для {tech.Name} - {route.Stops.Count} зупинок, +{addedHours:F1} годин, усього: {tech.CurrentScheduledHours:F1}/{tech.MaxWeeklyHours}");
                            }
                        }

                        finalResult.Add(new WeeklyRoute
                        {
                            Day = targetDate.DayOfWeek,
                            Date = targetDate.Date,
                            Routes = dailyRoutes
                        });
                    }
                    else
                    {
                        context.Logger.LogLine($"! Неможливо побудувати розклад на {targetDate:dd.MM.yyyy}: рішення не знайдено");
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
                    context.Logger.LogLine($"[DAY-ERROR] {targetDate:dd.MM.yyyy}: {exDay}");
                    WriteCrashFile($"Day error {targetDate:dd.MM.yyyy}:{Environment.NewLine}{exDay}");

                    finalResult.Add(new WeeklyRoute
                    {
                        Day = targetDate.DayOfWeek,
                        Date = targetDate.Date,
                        Routes = []
                    });
                }
            }

            context.Logger.LogLine($"[EXPORT] finalResult days={finalResult.Count}");

            var allRoutes = finalResult.SelectMany(w => w.Routes).ToList();

            context.Logger.LogLine($"[EXPORT] allRoutes count={allRoutes.Count}");
            OutputJsonWriter.TryWriteScheduleJson(allRoutes, request.Technicians, request.Sites, context.Logger);

            context.Logger.LogLine("[EXPORT] output.json done");
            OutputJsonWriter.TryWriteTimetableJson(finalResult, context.Logger);

            context.Logger.LogLine("[EXPORT] timetable.json done");
            context.Logger.LogLine("[DONE] Function finished successfully");

            return finalResult;
        }
        catch (Exception ex)
        {
            context.Logger.LogLine($"[FATAL] {ex}");
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

    private static string ResolveProjectRoot()
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
