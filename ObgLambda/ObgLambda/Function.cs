using Amazon.Lambda.Core;
using Amazon.LocationService;
using ObgLambda.Excel;
using ObgServices.Models;
using ObgServices.Services;

// Якщо в проєкті є OutputJsonWriter і ти хочеш експорт — залиш цей using.
// Якщо раптом буде помилка компіляції (нема namespace) — просто закоментуй 1 рядок нижче.
using ObgLambda.Json;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ObgLambda;

public class Function
{
    private readonly AmazonLocationServiceClient _locationClient = new();

    public async Task<List<WeeklyRoute>> FunctionHandler(RoutingRequest request, ILambdaContext context)
    {
        // Excel-input mode (default ON). If Excel is missing, we DO NOT fall back to JSON input.
        if (UseExcelInput())
        {
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
                return [];
            }
        }

        context.Logger.LogLine("Генерація Майстер-розкладу...");
        var geocodingService = new GeocodingService(_locationClient);

        // Master schedule (depends on your Services branch changes)
        var masterSchedule = MasterSchedulerService.GenerateMasterSchedule(request.Sites, request.Technicians);
        int horizon = masterSchedule.Count;
        DateTime cycleStartDate = new(2026, 1, 1);

        var finalResult = new List<WeeklyRoute>();

        var datesToProcess = request.TargetDates != null && request.TargetDates.Count != 0
            ? request.TargetDates
            : [DateTime.Today];

        foreach (var t in request.Technicians) t.CurrentScheduledHours = 0;

        int? lastProcessedWeekNum = null;

        foreach (var targetDate in datesToProcess)
        {
            int totalBusinessDays = BusinessDayHelper.GetBusinessDayIndex(cycleStartDate, targetDate);
            int currentDayIndex = totalBusinessDays % horizon;
            int currentWeekNum = totalBusinessDays / 5;

            var sitesForDate = masterSchedule[currentDayIndex];

            if (sitesForDate.Count == 0)
            {
                context.Logger.LogLine($"! Неможливо побудувати розклад на {targetDate:dd.MM.yyyy}: на цей день немає візитів");
                continue;
            }

            // Reset weekly hours on new business week
            if (lastProcessedWeekNum != null && currentWeekNum > lastProcessedWeekNum)
            {
                foreach (var t in request.Technicians)
                    t.CurrentScheduledHours = 0;
            }
            lastProcessedWeekNum = currentWeekNum;

            context.Logger.LogLine($"Обробка дати {targetDate:dd.MM.yyyy}");

            var availableTechs = request.Technicians
                .Where(t => t.CurrentScheduledHours < t.MaxWeeklyHours)
                .Where(t => sitesForDate.Any(s => TechnicianFilterService.ValidateHardConstraints(t, s)))
                .ToList();

            if (availableTechs.Count == 0)
            {
                context.Logger.LogLine($"! Немає доступних техніків на {targetDate:dd.MM.yyyy}");
                continue;
            }

            try
            {
                var routingData = await RoutingDataFactory.CreateModel(availableTechs, sitesForDate, geocodingService);

                // NOTE: ця версія SolveRouting у твоєму Services-бренчі приймає DayOfWeek
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

                            context.Logger.LogLine($"Для {tech.Name} - {route.Stops.Count} зупинок, +{addedHours:F1} годин, усього: {tech.CurrentScheduledHours:F1}/{tech.MaxWeeklyHours}");
                        }
                    }

                    finalResult.Add(new WeeklyRoute { Day = targetDate.DayOfWeek, Routes = dailyRoutes });
                }
                else
                {
                    context.Logger.LogLine($"! Неможливо побудувати розклад на {targetDate:dd.MM.yyyy}: рішення не знайдено");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"! Помилка при обробці {targetDate:dd.MM.yyyy}: {ex.Message}");
            }
        }

        // Optional export: якщо OutputJsonWriter є і він приймає List<OptimizedRoute> — то спрацює.
        // Якщо в тебе сигнатура інша — просто видали ці 3 рядки.
        try
        {
            var allRoutes = finalResult.SelectMany(w => w.Routes).ToList();
            OutputJsonWriter.TryWriteScheduleJson(allRoutes, request.Technicians, request.Sites, context.Logger);
        }
        catch
        {
            // intentionally ignore (export is optional)
        }

        return finalResult;
    }

    private static bool UseExcelInput()
    {
        var raw = Environment.GetEnvironmentVariable("OBG_USE_EXCEL_INPUT");
        return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase);
    }
}

public class RoutingRequest
{
    public List<Technician> Technicians { get; set; } = [];
    public List<ServiceSite> Sites { get; set; } = [];
    public List<DateTime> TargetDates { get; set; } = [];
}