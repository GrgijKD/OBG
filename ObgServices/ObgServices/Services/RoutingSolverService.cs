using Google.OrTools.ConstraintSolver;
using ObgServices.Models;

namespace ObgServices.Services
{
    public class RoutingSolverService
    {
        public static List<OptimizedRoute> SolveRouting(RoutingDataModel data, List<Technician> techs, List<ServiceSite> sites)
        {
            RoutingIndexManager manager = new(data.DistanceMatrix.GetLength(0), data.VehicleCount, data.Depot);
            RoutingModel routing = new(manager);

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

            // Distance Matrix
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

            // Simulated Annealing
            RoutingSearchParameters searchParameters = operations_research_constraint_solver.DefaultRoutingSearchParameters();
            searchParameters.FirstSolutionStrategy = FirstSolutionStrategy.Types.Value.PathCheapestArc;
            searchParameters.LocalSearchMetaheuristic = LocalSearchMetaheuristic.Types.Value.SimulatedAnnealing;
            searchParameters.TimeLimit = new Google.Protobuf.WellKnownTypes.Duration { Seconds = 10 };

            Assignment solution = routing.SolveWithParameters(searchParameters);

            return solution != null ? GetRoutes(routing, manager, solution, techs, sites) : [];
        }

        public static List<OptimizedRoute> GetRoutes(RoutingModel routing, RoutingIndexManager manager, Assignment solution, List<Technician> techs, List<ServiceSite> sites)
        {
            var routes = new List<OptimizedRoute>();

            for (int i = 0; i < techs.Count; ++i)
            {
                var route = new OptimizedRoute { TechnicianId = techs[i].Id, TechnicianName = techs[i].Name };
                var index = routing.Start(i);
                int sequence = 0;

                while (!routing.IsEnd(index))
                {
                    var nodeIndex = manager.IndexToNode(index);
                    if (nodeIndex > 0 && nodeIndex <= sites.Count)
                    {
                        var site = sites[nodeIndex - 1];
                        route.Stops.Add(new RouteStop { SiteId = site.Id, SiteName = site.Name, Sequence = sequence++ });
                    }
                    index = solution.Value(routing.NextVar(index));
                }
                routes.Add(route);
            }
            return routes;
        }
    }
}
