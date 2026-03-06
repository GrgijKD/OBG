using Google.OrTools.ConstraintSolver;
using Google.Protobuf.WellKnownTypes;
using ObgServices.Models;

namespace ObgServices.Services
{
    public class RoutingSolverService
    {
        public static List<OptimizedRoute> SolveRouting(RoutingDataModel data, List<Technician> techs, DayOfWeek day)
        {
            if (techs.Count == 0)
                throw new ArgumentException("Список техніків порожній");

            RoutingIndexManager manager = new(data.DistanceMatrix.GetLength(0), data.VehicleCount, data.Starts, data.Ends);
            RoutingModel routing = new(manager);
            var solver = routing.solver();

            // Локації починаються після всіх стартів і фінішів техніків
            int siteOffset = data.VehicleCount * 2;

            int timeCallbackIndex = routing.RegisterTransitCallback((fromIndex, toIndex) =>
            {
                int fromNode = manager.IndexToNode(fromIndex);
                int toNode = manager.IndexToNode(toIndex);
                return Math.Max(0, data.TimeMatrix[fromNode, toNode])
                     + Math.Max(0, data.ServiceDurations[fromNode]);
            });

            int distCallbackIndex = routing.RegisterTransitCallback((fromIndex, toIndex) =>
                data.DistanceMatrix[manager.IndexToNode(fromIndex), manager.IndexToNode(toIndex)]);

            for (int t = 0; t < techs.Count; t++)
            {
                var tech = techs[t];
                int vehicleCostCallbackIndex = routing.RegisterTransitCallback((fromIndex, toIndex) =>
                {
                    int fromNode = manager.IndexToNode(fromIndex);
                    int toNode = manager.IndexToNode(toIndex);
                    long dist = data.DistanceMatrix[fromNode, toNode];

                    int siteIdx = toNode - siteOffset;
                    if (siteIdx >= 0 && siteIdx < data.ExpandedSites.Count)
                    {
                        var site = data.ExpandedSites[siteIdx];
                        if (site.ProhibitedTechIds.Contains(tech.Id)) return dist + 10_000; // штраф за небажаного техніка
                        if (site.PreferredTechIds.Contains(tech.Id)) return Math.Max(0, dist - 5_000); // знижка для бажаного техніка
                    }
                    return dist;
                });
                routing.SetArcCostEvaluatorOfVehicle(vehicleCostCallbackIndex, t);
            }

            // Виміри
            routing.AddDimension(timeCallbackIndex, 30, 1440, false, "Time");
            routing.AddDimension(distCallbackIndex, 0, 1_000_000, true, "Distance");
            var timeDimension = routing.GetMutableDimension("Time");

            for (int i = 0; i < data.ExpandedSites.Count; i++)
            {
                int nodeNumber = siteOffset + i;
                long nodeIndex = manager.NodeToIndex(nodeNumber);

                if (nodeIndex < 0)
                {
                    Console.WriteLine($"⚠️ NodeToIndex({nodeNumber}) = {nodeIndex}, пропускаємо.");
                    continue;
                }

                var site = data.ExpandedSites[i];

                // Часові вікна
                timeDimension.CumulVar(nodeIndex)
                    .SetRange(data.TimeWindows[nodeNumber][0], data.TimeWindows[nodeNumber][1]);

                // Жорстка фільтрація техніків
                var allowed = Enumerable.Range(0, techs.Count)
                    .Where(t => TechnicianFilterService.ValidateHardConstraints(techs[t], site))
                    .Select(t => (long)t)
                    .ToArray();

                if (allowed.Length == 0)
                {
                    // Жоден технік не підходить — пропускаємо точку
                    routing.AddDisjunction([nodeIndex], 10_000);
                    routing.ActiveVar(nodeIndex).SetValue(0);
                    continue;
                }

                if (allowed.Length < techs.Count)
                    routing.VehicleVar(nodeIndex).SetValues(allowed);

                routing.AddDisjunction([nodeIndex], 10_000);
            }

            // Синхронізація для TechsNeeded > 1
            for (int i = 0; i < data.ExpandedSites.Count; i++)
            {
                long idx1 = manager.NodeToIndex(siteOffset + i);
                if (idx1 < 0) continue;

                for (int j = i + 1; j < data.ExpandedSites.Count; j++)
                {
                    if (data.ExpandedSites[i].Id != data.ExpandedSites[j].Id) continue;

                    long idx2 = manager.NodeToIndex(siteOffset + j);
                    if (idx2 < 0) continue;

                    solver.Add(routing.ActiveVar(idx1) == routing.ActiveVar(idx2));
                    solver.Add(solver.MakeAbs(
                        timeDimension.CumulVar(idx1) - timeDimension.CumulVar(idx2)) <= 15);
                    solver.Add(routing.VehicleVar(idx1) != routing.VehicleVar(idx2));
                }
            }

            // Параметри та розв'язання
            var searchParameters = operations_research_constraint_solver.DefaultRoutingSearchParameters();
            searchParameters.FirstSolutionStrategy = FirstSolutionStrategy.Types.Value.PathCheapestArc;
            searchParameters.LocalSearchMetaheuristic = LocalSearchMetaheuristic.Types.Value.SimulatedAnnealing;
            searchParameters.TimeLimit = new Duration { Seconds = 30 };

            var solution = routing.SolveWithParameters(searchParameters);

            if (solution == null)
            {
                Console.WriteLine("Рішення не знайдено.");
                return [];
            }

            Console.WriteLine($"Знайдено рішення. Ціна: {solution.ObjectiveValue()}");
            return GetRoutes(routing, manager, solution, techs, data.ExpandedSites, siteOffset, day);
        }

        public static List<OptimizedRoute> GetRoutes(
            RoutingModel routing,
            RoutingIndexManager manager,
            Assignment solution,
            List<Technician> techs,
            List<ServiceSite> expandedSites,
            int siteOffset,
            DayOfWeek dayOfWeek)
        {
            var routes = new List<OptimizedRoute>();
            var timeDimension = routing.GetMutableDimension("Time");
            var distDimension = routing.GetMutableDimension("Distance");

            DateTime anchorDate = GetNextMonday(DateTime.Today);
            int daysOffset = ((int)dayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            DateTime baseDate = anchorDate.AddDays(daysOffset);

            for (int i = 0; i < techs.Count; i++)
            {
                var route = new OptimizedRoute
                {
                    TechnicianId = techs[i].Id,
                    TechnicianName = techs[i].Name
                };

                var index = routing.Start(i);
                while (!routing.IsEnd(index))
                {
                    int nodeNumber = manager.IndexToNode(index);
                    int siteIdx = nodeNumber - siteOffset;

                    if (siteIdx >= 0 && siteIdx < expandedSites.Count)
                    {
                        var site = expandedSites[siteIdx];
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

                long totalMeters = solution.Value(distDimension.CumulVar(routing.End(i)));
                route.TotalDistanceKm = totalMeters / 1000.0;

                long startMinutes = solution.Value(timeDimension.CumulVar(routing.Start(i)));
                long endMinutes = solution.Value(timeDimension.CumulVar(routing.End(i)));
                route.TotalDurationMinutes = endMinutes - startMinutes;

                routes.Add(route);
            }

            return routes;
        }

        private static DateTime GetNextMonday(DateTime fromDate)
        {
            var date = fromDate.Date;
            int daysUntilMonday = ((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7;
            if (daysUntilMonday == 0)
                daysUntilMonday = 7;
            return date.AddDays(daysUntilMonday);
        }
    }
}
