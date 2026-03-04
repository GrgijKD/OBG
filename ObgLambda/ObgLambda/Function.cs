using Amazon.Lambda.Core;
using Amazon.LocationService;
using ObgLambda.Excel;
using ObgServices.Models;
using ObgServices.Services;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ObgLambda;

public class Function
{
    private readonly AmazonLocationServiceClient _locationClient = new();

    public async Task<List<WeeklyRoute>> FunctionHandler(RoutingRequest request, ILambdaContext context)
    {
        // Робота з Excel/JSON
        if (UseExcelInput())
        {
            var excel = ExcelInputLoader.TryLoadFirstExcel(context.Logger);
            if (excel is null) return [];
            try
            {
                request = ExcelRoutingRequestParser.Parse(excel.Content, excel.FileName, context.Logger);
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Помилка парсингу: {ex.Message}");
                return [];
            }
        }
        context.Logger.LogLine("Генерація Майстер-розкладу...");
        var geocodingService = new GeocodingService(_locationClient);
        var masterScheduler = new MasterSchedulerService();

        // Створюємо розклад на основі всіх локацій та техніків
        var masterSchedule = MasterSchedulerService.GenerateMasterSchedule(request.Sites, request.Technicians);
        int horizon = masterSchedule.Count;
        DateTime cycleStartDate = new(2026, 1, 1); // Точка відліку для робочих днів

        var finalResult = new List<WeeklyRoute>();

        // Визначаємо, які дати потрібно прорахувати
        var datesToProcess = request.TargetDates != null && request.TargetDates.Count != 0
            ? request.TargetDates
            : [DateTime.Today];

        // Цикл по цільових датах
        foreach (var targetDate in datesToProcess)
        {
            // Визначаємо індекс дня в циклі
            int totalBusinessDays = BusinessDayHelper.GetBusinessDayIndex(cycleStartDate, targetDate);
            int currentDayIndex = totalBusinessDays % horizon;

            var sitesForDate = masterSchedule[currentDayIndex];

            if (sitesForDate.Count == 0)
            {
                context.Logger.LogLine($"Неможливо побудувати розклад на {targetDate:dd.MM.yyyy}, на цей день циклу немає візитів).");
                continue;
            }

            if (currentDayIndex % 5 == 0)
            {
                foreach (var t in request.Technicians)
                    t.CurrentScheduledHours = 0;
            }

            context.Logger.LogLine($"Обробка дати {targetDate:dd.MM.yyyy} (День циклу: {currentDayIndex + 1})");

            // Фільтрація техніків для конкретного дня
            var availableTechs = request.Technicians
                .Where(t => sitesForDate.Any(s => TechnicianFilterService.ValidateHardConstraints(t, s)))
                .ToList();

            try
            {
                var routingData = await RoutingDataFactory.CreateModel(availableTechs, sitesForDate, geocodingService);
                var dailyRoutes = RoutingSolverService.SolveRouting(routingData, availableTechs, targetDate.DayOfWeek);

                if (dailyRoutes != null && dailyRoutes.Any(r => r.Stops.Count > 0))
                {
                    foreach (var route in dailyRoutes)
                    {
                        var tech = request.Technicians.FirstOrDefault(t => t.Id == route.TechnicianId);
                        tech?.CurrentScheduledHours += route.TotalDurationMinutes / 60.0;
                    }

                    context.Logger.LogLine($"Побудовано розклад техніків на {targetDate:dd.MM.yyyy}:");
                    foreach (var route in dailyRoutes)
                    {
                        context.Logger.LogLine($"- {route.TechnicianName}: {route.Stops.Count} зупинок.");
                    }

                    finalResult.Add(new WeeklyRoute
                    {
                        Day = targetDate.DayOfWeek,
                        Routes = dailyRoutes
                    });
                }
                else
                {
                    context.Logger.LogLine($"Неможливо побудувати розклад на {targetDate:dd.MM.yyyy}, рішення не знайдено).");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Неможливо побудувати розклад на {targetDate:dd.MM.yyyy} через помилку: {ex.Message}");
            }
        }

        return finalResult;
    }

    private static bool UseExcelInput()
    {
        var raw = Environment.GetEnvironmentVariable("OBG_USE_EXCEL_INPUT");

        // Default: true. Turn off with: false/0/no
        return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase);
    }
}

// Модель вхідного запиту (Excel -> RoutingRequest або JSON -> RoutingRequest)
public class RoutingRequest
{
    public List<Technician> Technicians { get; set; } = [];
    public List<ServiceSite> Sites { get; set; } = [];
    public List<DateTime> TargetDates { get; set; } = [];
}
