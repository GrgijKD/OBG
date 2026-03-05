using Amazon.Lambda.Core;
using Amazon.LocationService;
using ObgLambda.Excel;
using ObgLambda.Json;
using ObgServices.Models;
using ObgServices.Services;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ObgLambda;

public class Function
{
    private readonly AmazonLocationServiceClient _locationClient = new();

    public async Task<List<OptimizedRoute>> FunctionHandler(RoutingRequest request, ILambdaContext context)
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

                // Ignore JSON payload; build RoutingRequest from Excel
                request = ExcelRoutingRequestParser.Parse(excel.Content, excel.FileName, context.Logger);

                context.Logger.LogLine($"Excel парсинг завершено: Technicians={request.Technicians.Count}, Sites={request.Sites.Count}");
            }
            catch (Exception ex)
            {
                // Do not fail and do not use JSON either
                context.Logger.LogLine($"Не вдалося розпарсити Excel '{excel.FileName}'. {ex}");
                return [];
            }
        }

        // Existing routing flow (works both for JSON input and for parsed Excel input)
        context.Logger.LogLine("Початок процесу оптимізації маршрутів");
        var geocodingService = new GeocodingService(_locationClient);

        // Фільтрація техніків за жорсткими обмеженнями
        var qualifiedTechs = request.Technicians
            .Where(t => request.Sites.Any(site => TechnicianFilterService.ValidateHardConstraints(t, site)))
            .ToList();

        if (qualifiedTechs.Count == 0)
        {
            context.Logger.LogLine("Не знайдено техніків для даних локацій");
            return [];
        }

        // Побудова моделі даних (матриці відстаней та часу)
        var routingData = await RoutingDataFactory.CreateModel(qualifiedTechs, request.Sites, geocodingService);

        // Google OR-Tools для пошуку рішення
        context.Logger.LogLine("Запуск Google OR-Tools...");
        var resultRoutes = RoutingSolverService.SolveRouting(routingData, qualifiedTechs, request.Sites);

        context.Logger.LogLine($"Оптимізація завершена. Сформовано маршрутів: {resultRoutes.Count}");

        // Export result schedule JSON into ./output (for local runs and Lambda /tmp mapping)
        OutputJsonWriter.TryWriteScheduleJson(resultRoutes, context.Logger);

        return resultRoutes;
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
