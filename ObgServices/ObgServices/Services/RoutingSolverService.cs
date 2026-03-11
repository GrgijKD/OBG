using Google.OrTools.ConstraintSolver;
using Google.Protobuf.WellKnownTypes;
using ObgServices.Models;

namespace ObgServices.Services
{
    public class RoutingSolverService
    {
        private const bool EnableSolverLogs = false;

        public static List<OptimizedRoute> SolveRouting(RoutingDataModel data, List<Technician> techs, DayOfWeek day)
        {
            if (techs.Count == 0)
                throw new ArgumentException("Список техніків порожній");

            RoutingIndexManager manager = new(data.DistanceMatrix.GetLength(0), data.VehicleCount, data.Starts, data.Ends);
            RoutingModel routing = new(manager);
            var solver = routing.solver();

            int siteOffset = data.SiteOffset;

            int timeCallbackIndex = routing.RegisterTransitCallback((fromIndex, toIndex) =>
            {
                int fromNode = manager.IndexToNode(fromIndex);
                int toNode = manager.IndexToNode(toIndex);
                return Math.Max(0, data.TimeMatrix[fromNode, toNode])
                     + Math.Max(0, data.ServiceDurations[fromNode]);
            });

            int distCallbackIndex = routing.RegisterTransitCallback((fromIndex, toIndex) =>
                data.DistanceMatrix[manager.IndexToNode(fromIndex), manager.IndexToNode(toIndex)]);

            int visitCallbackIndex = routing.RegisterTransitCallback((fromIndex, toIndex) =>
            {
                int toNode = manager.IndexToNode(toIndex);
                return toNode >= siteOffset ? 1 : 0;
            });

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
                        if (site.ProhibitedTechIds.Contains(tech.Id)) return dist + 10_000;
                        if (site.PreferredTechIds.Contains(tech.Id)) return Math.Max(0, dist - 5_000);
                    }
                    return dist;
                });
                routing.SetArcCostEvaluatorOfVehicle(vehicleCostCallbackIndex, t);
            }

            routing.AddDimension(timeCallbackIndex, 30, 1440, false, "Time");
            routing.AddDimension(distCallbackIndex, 0, 1_000_000, true, "Distance");
            routing.AddDimension(visitCallbackIndex, 0, Math.Max(1, data.ExpandedSites.Count), true, "Visits");
            var timeDimension = routing.GetMutableDimension("Time");
            var visitsDimension = routing.GetMutableDimension("Visits");

            var vehiclesWithEligibleSites = Enumerable.Range(0, techs.Count)
                .Where(v => data.ExpandedSites.Any(site => CanTechServeOnDay(techs[v], site, day)))
                .ToList();

            int targetActiveVehicles = Math.Min(vehiclesWithEligibleSites.Count, Math.Max(1, data.ExpandedSites.Count));
            var prioritizedVehicles = vehiclesWithEligibleSites
                .OrderBy(v => techs[v].CurrentScheduledHours)
                .ThenBy(v => techs[v].Name)
                .Take(targetActiveVehicles)
                .ToHashSet();

            int desiredMinStops = targetActiveVehicles == 0 ? 0 : Math.Max(1, data.ExpandedSites.Count / targetActiveVehicles);
            int desiredMaxStops = targetActiveVehicles == 0 ? data.ExpandedSites.Count : (int)Math.Ceiling(data.ExpandedSites.Count / (double)targetActiveVehicles);

            visitsDimension.SetGlobalSpanCostCoefficient(1_000);

            for (int v = 0; v < techs.Count; v++)
            {
                long endIndex = routing.End(v);

                if (!vehiclesWithEligibleSites.Contains(v))
                    continue;

                if (prioritizedVehicles.Contains(v))
                {
                    visitsDimension.SetCumulVarSoftLowerBound(endIndex, desiredMinStops, 30_000);
                    visitsDimension.SetCumulVarSoftUpperBound(endIndex, desiredMaxStops, 8_000);
                }
                else
                {
                    visitsDimension.SetCumulVarSoftUpperBound(endIndex, desiredMaxStops + 1, 4_000);
                }
            }

            for (int i = 0; i < data.ExpandedSites.Count; i++)
            {
                int nodeNumber = siteOffset + i;
                long nodeIndex = manager.NodeToIndex(nodeNumber);

                if (nodeIndex < 0)
                    continue;

                var site = data.ExpandedSites[i];
                timeDimension.CumulVar(nodeIndex).SetRange(data.TimeWindows[nodeNumber][0], data.TimeWindows[nodeNumber][1]);

                var allowedTechIndexes = Enumerable.Range(0, techs.Count)
                    .Where(t => CanTechServeOnDay(techs[t], site, day))
                    .ToList();

                var allowed = allowedTechIndexes.Select(t => (long)t).ToArray();

                if (allowed.Length == 0)
                {
                    routing.AddDisjunction([nodeIndex], 10_000);
                    routing.ActiveVar(nodeIndex).SetValue(0);
                    continue;
                }

                if (allowed.Length < techs.Count)
                    routing.VehicleVar(nodeIndex).SetValues(allowed);

                routing.AddDisjunction([nodeIndex], 10_000);
            }

            // mandatory/conditional break nodes per vehicle
            for (int v = 0; v < techs.Count; v++)
            {
                int breakNodeNumber = data.BreakNodes.Length > v ? data.BreakNodes[v] : -1;
                if (breakNodeNumber < 0)
                    continue;

                long breakIndex = manager.NodeToIndex(breakNodeNumber);
                if (breakIndex < 0)
                    continue;

                timeDimension.CumulVar(breakIndex).SetRange(data.TimeWindows[breakNodeNumber][0], data.TimeWindows[breakNodeNumber][1]);
                routing.VehicleVar(breakIndex).SetValue(v);
                routing.AddDisjunction([breakIndex], 0);

                var usedVar = solver.MakeIsDifferentCstVar(routing.NextVar(routing.Start(v)), routing.End(v));
                solver.Add(routing.ActiveVar(breakIndex) == usedVar);
                visitsDimension.CumulVar(breakIndex).SetMin(1);
            }

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
                    solver.Add(solver.MakeAbs(timeDimension.CumulVar(idx1) - timeDimension.CumulVar(idx2)) <= 15);
                    solver.Add(routing.VehicleVar(idx1) != routing.VehicleVar(idx2));
                }
            }

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
            return GetRoutes(routing, manager, solution, techs, data, day);
        }

        public static List<OptimizedRoute> GetRoutes(
            RoutingModel routing,
            RoutingIndexManager manager,
            Assignment solution,
            List<Technician> techs,
            RoutingDataModel data,
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
                long startMinutes = solution.Value(timeDimension.CumulVar(routing.Start(i)));
                long endMinutes = solution.Value(timeDimension.CumulVar(routing.End(i)));

                var route = new OptimizedRoute
                {
                    TechnicianId = techs[i].Id,
                    TechnicianName = techs[i].Name,
                    RouteStartTime = baseDate.AddMinutes(startMinutes),
                    RouteEndTime = baseDate.AddMinutes(endMinutes)
                };

                var index = routing.Start(i);
                while (!routing.IsEnd(index))
                {
                    index = solution.Value(routing.NextVar(index));
                    if (routing.IsEnd(index))
                        break;

                    int nodeNumber = manager.IndexToNode(index);
                    long arrivalMinutes = solution.Value(timeDimension.CumulVar(index));

                    if (data.BreakNodes.Contains(nodeNumber))
                    {
                        int breakMinutes = techs[i].MinBreakMinutes ?? 0;
                        route.Stops.Add(new RouteStop
                        {
                            SiteId = $"break-{i + 1}",
                            SiteName = "Break",
                            ExpectedArrivalTime = baseDate.AddMinutes(arrivalMinutes),
                            Sequence = route.Stops.Count,
                            Type = "break",
                            DurationMinutes = breakMinutes
                        });
                        continue;
                    }

                    int siteIdx = nodeNumber - data.SiteOffset;
                    if (siteIdx >= 0 && siteIdx < data.ExpandedSites.Count)
                    {
                        var site = data.ExpandedSites[siteIdx];
                        route.Stops.Add(new RouteStop
                        {
                            SiteId = site.Id,
                            SiteName = site.Name,
                            ExpectedArrivalTime = baseDate.AddMinutes(arrivalMinutes),
                            Sequence = route.Stops.Count,
                            Type = "service",
                            DurationMinutes = site.VisitDuration,
                            Address = site.Address
                        });
                    }
                }

                long totalMeters = solution.Value(distDimension.CumulVar(routing.End(i)));
                route.TotalDistanceKm = totalMeters / 1000.0;
                route.TotalDurationMinutes = endMinutes - startMinutes;
                routes.Add(route);
            }

            return routes;
        }

        private static bool CanTechServeOnDay(Technician tech, ServiceSite site, DayOfWeek dayOfWeek)
        {
            if (!TechnicianFilterService.ValidateHardConstraints(tech, site))
                return false;

            bool techAvailable = tech.WorkingHours.Count == 0 || tech.WorkingHours.Any(w => w.Day == dayOfWeek);
            if (!techAvailable)
                return false;

            bool siteAvailable = site.AccessWindows.Count == 0 || site.AccessWindows.Any(w => w.Day == dayOfWeek);
            return siteAvailable;
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
