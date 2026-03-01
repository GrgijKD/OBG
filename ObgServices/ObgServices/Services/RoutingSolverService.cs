using Google.OrTools.ConstraintSolver;
using ObgServices.Models;

namespace ObgServices.Services
{
    public class RoutingSolverService
    {
        public static List<OptimizedRoute> SolveRouting(RoutingDataModel data, List<Technician> techs, DayOfWeek day)
        {
            int nodeCount = data.ExpandedSites.Count + 1; // +1 для офісу
            RoutingIndexManager manager = new(nodeCount, data.VehicleCount, data.Office);
            RoutingModel routing = new(manager);
            var solver = routing.solver();

            for (int i = 1; i <= data.ExpandedSites.Count; i++)
            {
                var site = data.ExpandedSites[i - 1];
                long nodeIndex = manager.NodeToIndex(i);

                // Жорстка фільтрація техніків
                var allowedTechIndices = new List<long>();
                for (int t = 0; t < techs.Count; t++)
                {
                    if (TechnicianFilterService.ValidateHardConstraints(techs[t], site))
                    {
                        allowedTechIndices.Add(t);
                    }
                }

                // Встановлюємо список дозволених техніків
                routing.VehicleVar(nodeIndex).SetValues([.. allowedTechIndices]);

                // Дозволяємо пропуск точки з великим штрафом
                routing.AddDisjunction([nodeIndex], 10000);
            }

            // Часовий вимір
            int timeCallbackIndex = routing.RegisterTransitCallback((fromIndex, toIndex) => {
                var fromNode = manager.IndexToNode(fromIndex);
                var toNode = manager.IndexToNode(toIndex);
                return data.TimeMatrix[fromNode, toNode] + data.ServiceDurations[fromNode];
            });

            routing.AddDimension(timeCallbackIndex, 30, 1440, false, "Time");
            var timeDimension = routing.GetMutableDimension("Time");

            // Синхронізація для TechsNeeded > 1 та часові вікна
            for (int i = 1; i <= data.ExpandedSites.Count; i++)
            {
                var site = data.ExpandedSites[i - 1];
                long idx1 = manager.NodeToIndex(i);

                // Встановлення часових вікон для кожної точки
                timeDimension.CumulVar(idx1).SetRange(data.TimeWindows[i][0], data.TimeWindows[i][1]);

                // Пошук дублікатів локація для синхронізації 2+ техніків
                for (int j = i + 1; j <= data.ExpandedSites.Count; j++)
                {
                    var otherSite = data.ExpandedSites[j - 1];
                    if (data.ExpandedSites[i - 1].Id == data.ExpandedSites[j - 1].Id)
                    {
                        long idx2 = manager.NodeToIndex(j);

                        solver.Add(routing.ActiveVar(idx1) == routing.ActiveVar(idx2));

                        // Допускається різниця прибуття у 15 хв
                        var time1 = timeDimension.CumulVar(idx1);
                        var time2 = timeDimension.CumulVar(idx2);
                        solver.Add(solver.MakeAbs(time1 - time2) <= 15);

                        // Техніки мають бути різними
                        solver.Add(routing.VehicleVar(idx1) != routing.VehicleVar(idx2));
                    }
                }
            }

            for (int i = 0; i < data.VehicleCount; ++i)
            {
                long startIdx = routing.Start(i);
                long endIdx = routing.End(i);

                // Час повернення в офіс не може бути більшим ніж час виїзду + 480 хв (8 год)
                routing.AddVariableMinimizedByFinalizer(timeDimension.CumulVar(endIdx));
                solver.Add(timeDimension.CumulVar(endIdx) - timeDimension.CumulVar(startIdx) <= 480);
            }

            // Матриця відстаней
            int transitCallbackIndex = routing.RegisterTransitCallback((fromIndex, toIndex) => {
                var fromNode = manager.IndexToNode(fromIndex);
                var toNode = manager.IndexToNode(toIndex);
                return data.DistanceMatrix[fromNode, toNode];
            });
            routing.SetArcCostEvaluatorOfAllVehicles(transitCallbackIndex);

            // М'які умови
            // Вимір для реальної відстані
            int distCallbackIndex = routing.RegisterTransitCallback((fromIndex, toIndex) => {
                return data.DistanceMatrix[manager.IndexToNode(fromIndex), manager.IndexToNode(toIndex)];
            });
            routing.AddDimension(distCallbackIndex, 0, 1000000, true, "Distance");

            // Вимір для відстані зі штрафами
            for (int t = 0; t < techs.Count; t++)
            {
                var currentTech = techs[t];
                int vehicleCostCallbackIndex = routing.RegisterTransitCallback((fromIndex, toIndex) => {
                    var toNode = manager.IndexToNode(toIndex);
                    long dist = data.DistanceMatrix[manager.IndexToNode(fromIndex), toNode];

                    if (toNode > 0 && toNode <= data.ExpandedSites.Count)
                    {
                        var targetSite = data.ExpandedSites[toNode - 1];

                        if (targetSite.ProhibitedTechIds.Contains(currentTech.Id))
                            return dist + 5000; // Штраф 50 км для небажаного техніка

                        if (targetSite.PreferredTechIds.Contains(currentTech.Id))
                            return dist > 5000 ? dist - 5000 : 0; // Знижка 5 км для бажаного техніка
                    }
                    return dist;
                });

                routing.SetArcCostEvaluatorOfVehicle(vehicleCostCallbackIndex, t);
            }

            // Налаштування пошуку
            RoutingSearchParameters searchParameters = operations_research_constraint_solver.DefaultRoutingSearchParameters();
            searchParameters.FirstSolutionStrategy = FirstSolutionStrategy.Types.Value.PathCheapestArc;
            searchParameters.LocalSearchMetaheuristic = LocalSearchMetaheuristic.Types.Value.SimulatedAnnealing;
            searchParameters.TimeLimit = new Google.Protobuf.WellKnownTypes.Duration { Seconds = 30 };

            // Вирішення
            Assignment solution = routing.SolveWithParameters(searchParameters);

            if (solution == null)
            {
                Console.WriteLine("Рішення не знайдено.");
                return [];
            }

            Console.WriteLine($"Знайдено рішення. Ціна: {solution.ObjectiveValue()}");
            return solution != null ? GetRoutes(routing, manager, solution, techs, data.ExpandedSites, day) : [];
        }

        public static List<OptimizedRoute> GetRoutes(
            RoutingModel routing,
            RoutingIndexManager manager,
            Assignment solution,
            List<Technician> techs,
            List<ServiceSite> expandedSites,
            DayOfWeek dayOfWeek)
        {
            var routes = new List<OptimizedRoute>();
            var timeDimension = routing.GetMutableDimension("Time");
            var distDimension = routing.GetMutableDimension("Distance"); // Реальна дистанція

            // Обчислюємо цільову дату
            int daysUntilTarget = (int)dayOfWeek - (int)DateTime.Today.DayOfWeek;
            DateTime baseDate = DateTime.Today.AddDays(daysUntilTarget);

            for (int i = 0; i < techs.Count; ++i)
            {
                var route = new OptimizedRoute { TechnicianId = techs[i].Id, TechnicianName = techs[i].Name };
                var index = routing.Start(i);

                while (!routing.IsEnd(index))
                {
                    var nodeIndex = manager.IndexToNode(index);
                    if (nodeIndex > 0 && nodeIndex <= expandedSites.Count)
                    {
                        var site = expandedSites[nodeIndex - 1];
                        long arrivalMinutes = solution.Value(timeDimension.CumulVar(index));

                        route.Stops.Add(new RouteStop
                        {
                            SiteId = site.Id,
                            SiteName = site.Name,
                            ExpectedArrivalTime = baseDate.AddMinutes(arrivalMinutes),
                            Sequence = route.Stops.Count
                        });
                    }
                    index = solution.Value(routing.NextVar(index));
                }

                long totalMeters = solution.Value(distDimension.CumulVar(index));
                route.TotalDistanceKm = totalMeters / 1000.0;

                long startMinutes = solution.Value(timeDimension.CumulVar(routing.Start(i)));
                long endMinutes = solution.Value(timeDimension.CumulVar(routing.End(i)));
                route.TotalDurationMinutes = endMinutes - startMinutes;

                routes.Add(route);
            }
            return routes;
        }
    }
}
