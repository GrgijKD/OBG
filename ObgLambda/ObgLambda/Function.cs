using Amazon.Lambda.Core;
using ObgServices.Models;
using ObgServices.Services;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ObgLambda;

public class Function
{
    public static List<OptimizedRoute> FunctionHandler(RoutingRequest request, ILambdaContext context)
    {
        context.Logger.LogLine("Початок процесу оптимізації маршрутів");

        // Фільтрація техніків за жорсткими обмеженнями
        var qualifiedTechs = request.Technicians
        .Where(t => request.Sites.Any(site =>
            site.Services.Any(service =>
                TechnicianFilterService.ValidateHardConstraints(t, site, service)
            )
        ))
        .ToList();

        if (qualifiedTechs.Count == 0)
        {
            context.Logger.LogLine("Не знайдено техніків для даних локацій");
            return [];
        }

        // Побудова моделі даних (матриці відстаней та часу)
        var routingData = RoutingDataFactory.CreateModel(qualifiedTechs, request.Sites);

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