using Google.OrTools.ConstraintSolver;
using ObgServices.Models;

namespace ObgServices.Services
{
    public class RoutingSolverService
    {
        public static List<OptimizedRoute> SolveRouting(RoutingDataModel data, List<Technician> techs, List<ServiceSite> sites)
        {
            RoutingIndexManager manager = new(data.DistanceMatrix.GetLength(0), data.VehicleCount, data.Office);
            RoutingModel routing = new(manager);

            // Жорстка фільтрація техніків
            for (int i = 1; i <= sites.Count; i++)
            {
                var site = sites[i - 1];
                long nodeIndex = manager.NodeToIndex(i);

                var allowedTechIndixes = new List<long>();
                for (int t = 0; t < techs.Count; t++)
                {
                    if (TechnicianFilterService.ValidateHardConstraints(techs[t], site))
                    {
                        allowedTechIndixes.Add(t);
                    }
                }
                routing.VehicleVar(nodeIndex).SetValues([.. allowedTechIndixes]);

                // Можливість пропуску якщо немає техніка або технік не встигає у часове вікно
                routing.AddDisjunction([nodeIndex], 10000);
            }

            // Часові вікна та тривалість
            int timeCallbackIndex = routing.RegisterTransitCallback((fromIndex, toIndex) =>
            {
                var fromNode = manager.IndexToNode(fromIndex);
                var toNode = manager.IndexToNode(toIndex);
                return data.TimeMatrix[fromNode, toNode] + data.ServiceDurations[fromNode];
            });

            routing.AddDimension(timeCallbackIndex, 30, 1440, false, "Time");
            var timeDimension = routing.GetMutableDimension("Time");

            for (int i = 0; i < data.TimeWindows.Length; ++i)
            {
                long index = manager.NodeToIndex(i);
                timeDimension.CumulVar(index).SetRange(data.TimeWindows[i][0], data.TimeWindows[i][1]);
            }

            // Матриця відстаней
            int transitCallbackIndex = routing.RegisterTransitCallback((fromIndex, toIndex) => {
                var fromNode = manager.IndexToNode(fromIndex);
                var toNode = manager.IndexToNode(toIndex);
                return data.DistanceMatrix[fromNode, toNode];
            });
            routing.SetArcCostEvaluatorOfAllVehicles(transitCallbackIndex);

            // Побажання до техніків
            for (int i = 1; i < sites.Count; i++)
            {
                var site = sites[i - 1];
                long nodeIndex = manager.NodeToIndex(i);

                if (site.ProhibitedTechIds.Count > 0)
                {
                    routing.AddDisjunction([nodeIndex], 2000);
                }
            }

            // Пошук рішення
            RoutingSearchParameters searchParameters = operations_research_constraint_solver.DefaultRoutingSearchParameters();
            searchParameters.FirstSolutionStrategy = FirstSolutionStrategy.Types.Value.PathCheapestArc;
            searchParameters.LocalSearchMetaheuristic = LocalSearchMetaheuristic.Types.Value.SimulatedAnnealing;
            searchParameters.TimeLimit = new Google.Protobuf.WellKnownTypes.Duration { Seconds = 10 };

            Assignment solution = routing.SolveWithParameters(searchParameters);

            if (solution == null)
            {
                Console.WriteLine("Рішення немає");
            }
            else
            {
                Console.WriteLine("Знайдено рішення з ціною: " + solution.ObjectiveValue());
            }

            return solution != null ? GetRoutes(routing, manager, solution, techs, sites) : [];
        }

        public static List<OptimizedRoute> GetRoutes(RoutingModel routing, RoutingIndexManager manager, Assignment solution, List<Technician> techs, List<ServiceSite> sites)
        {
            var routes = new List<OptimizedRoute>();
            var timeDimension = routing.GetMutableDimension("Time");

            for (int i = 0; i < techs.Count; ++i)
            {
                var route = new OptimizedRoute { TechnicianId = techs[i].Id, TechnicianName = techs[i].Name };
                var index = routing.Start(i);
                long routeDistance = 0;

                while (!routing.IsEnd(index))
                {
                    var nodeIndex = manager.IndexToNode(index);
                    if (nodeIndex > 0 && nodeIndex <= sites.Count)
                    {
                        var site = sites[nodeIndex - 1];
                        var timeVar = timeDimension.CumulVar(index);

                        route.Stops.Add(new RouteStop
                        {
                            SiteId = site.Id,
                            SiteName = site.Name,
                            // Конвертуємо хвилини з OR-Tools у реальний час
                            ExpectedArrivalTime = DateTime.Today.AddMinutes(solution.Value(timeVar)),
                            Sequence = route.Stops.Count
                        });
                    }

                    var previousIndex = index;
                    index = solution.Value(routing.NextVar(index));
                    // Додаємо відстань переїзду до загальної суми
                    routeDistance += routing.GetArcCostForVehicle(previousIndex, index, i);
                }

                route.TotalDistanceKm = routeDistance / 1000.0; // Конвертація в км
                var endTimeVar = timeDimension.CumulVar(index);
                route.TotalDurationMinutes = solution.Value(endTimeVar) - solution.Value(timeDimension.CumulVar(routing.Start(i)));

                routes.Add(route);
            }
            return routes;
        }
    }
}
