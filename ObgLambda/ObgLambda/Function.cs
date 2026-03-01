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

        context.Logger.LogLine("Розподіл завдань на тиждень...");
        var geocodingService = new GeocodingService(_locationClient);
        var weeklyScheduler = new WeeklySchedulerService();

        // Розподіляємо локації по днях тижня (Пн-Пт)
        var weeklyPlan = await WeeklySchedulerService.DistributeTasks(request.Technicians, request.Sites);
        var finalResult = new List<WeeklyRoute>();

        foreach (var tech in request.Technicians)
        {
            tech.CurrentScheduledHours = 0; // Скидаємо перед розрахунком нового тижня
        }

        // Цикл по кожному робочому дню
        foreach (var dayEntry in weeklyPlan)
        {
            var currentDay = dayEntry.Key;
            var dayTasks = dayEntry.Value; // Список VisitTask

            if (dayTasks.Count == 0) continue;

            context.Logger.LogLine($"Оптимізація для {currentDay}");

            // Фільтрація кваліфікованих техніків, у яких не закінчилися години
            var availableTechs = request.Technicians
                .Where(t => t.CurrentScheduledHours < 40)
                .Where(t => dayTasks.Any(task => TechnicianFilterService.ValidateHardConstraints(t, task.Site)))
                .ToList();

            if (availableTechs.Count == 0)
            {
                context.Logger.LogLine($"Немає доступних техніків для {currentDay}.");
                finalResult.Add(new WeeklyRoute { Day = currentDay, Routes = [] });
                continue;
            }

            var daySites = dayTasks.Select(t => t.Site).ToList();

            // Побудова моделі та розрахунок маршрутів
            var routingData = await RoutingDataFactory.CreateModel(availableTechs, daySites, geocodingService);
            var dailyRoutes = RoutingSolverService.SolveRouting(routingData, availableTechs, currentDay);

            // Оновлення відпрацьованих годин на тиждень
            foreach (var route in dailyRoutes)
            {
                var tech = request.Technicians.FirstOrDefault(t => t.Id == route.TechnicianId);
                if (tech != null)
                {
                    double hoursSpent = route.TotalDurationMinutes / 60.0;
                    tech.CurrentScheduledHours += hoursSpent;

                    context.Logger.LogLine($"Технік {tech.Name}: +{hoursSpent:F1} год. Разом за тиждень: {tech.CurrentScheduledHours:F1} з 40 год.");
                }
            }

            finalResult.Add(new WeeklyRoute { Day = currentDay, Routes = dailyRoutes });
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
}
