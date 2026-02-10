using Amazon.Lambda.Core;
using ObgServices.Models;
using ObgServices.Services;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ObgLambda;

public class Function
{
    private readonly TechnicianFilterService _filterService;
    private readonly RoutingDataFactory _dataFactory;

    public Function()
    {
        _filterService = new TechnicianFilterService();
        _dataFactory = new RoutingDataFactory();
    }

    public List<OptimizedRoute> FunctionHandler(RoutingRequest request, ILambdaContext context)
    {
        context.Logger.LogLine("Початок процесу оптимізації маршрутів...");

        // Фільтрація техніків за жорсткими обмеженнями
        var qualifiedTechs = request.Technicians
        .Where(t => request.Sites.Any(site =>
            site.Services.Any(service =>
                _filterService.ValidateHardConstraints(t, site, service)
            )
        ))
        .ToList();

        if (qualifiedTechs.Count == 0)
        {
            context.Logger.LogLine("Не знайдено техніків для даних локацій.");
            return [];
        }

        // Побудова моделі даних (матриці відстаней та часу)
        var routingData = _dataFactory.CreateModel(qualifiedTechs, request.Sites);

        // Використання Google OR-Tools для пошуку рішення
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