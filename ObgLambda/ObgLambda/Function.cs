using Amazon.Lambda.Core;
using Amazon.LocationService;
using ObgServices.Models;
using ObgServices.Services;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ObgLambda;

public class Function
{
    private readonly AmazonLocationServiceClient _locationClient = new();

    public async Task<List<OptimizedRoute>> FunctionHandler(RoutingRequest request, ILambdaContext context)
    {
        context.Logger.LogLine("Початок процесу оптимізації маршрутів");
        var geocodingService = new GeocodingService(_locationClient);

        // Фільтрація техніків за жорсткими обмеженнями
        var qualifiedTechs = request.Technicians
        .Where(t => request.Sites.Any(site => TechnicianFilterService.ValidateHardConstraints(t, site)
            )
        ).ToList();

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

        return resultRoutes;
    }
}

// Модель вхідного запиту
public class RoutingRequest
{
    public List<Technician> Technicians { get; set; } = [];
    public List<ServiceSite> Sites { get; set; } = [];
}