using ObgServices.Models;

namespace ObgServices.Services
{
    public class RoutingDataFactory
    {
        public static async Task<RoutingDataModel> CreateModel(
            List<Technician> techs,
            List<ServiceSite> sites,
            GeocodingService geocodingService,
            DayOfWeek dayOfWeek)
        {
            using var semaphore = new SemaphoreSlim(20);

            var allGeocodeTasks = new List<Task<AddressInfo?>>();

            async Task<AddressInfo?> SafeGeocode(string address)
            {
                await semaphore.WaitAsync();
                try { return await geocodingService.GetCoordinatesFromAddress(address); }
                finally { semaphore.Release(); }
            }

            foreach (var t in techs) allGeocodeTasks.Add(SafeGeocode(t.StartLocation.FullAddress));
            foreach (var t in techs) allGeocodeTasks.Add(SafeGeocode(t.EndLocation.FullAddress));
            foreach (var s in sites) allGeocodeTasks.Add(SafeGeocode(s.Address));

            var results = await Task.WhenAll(allGeocodeTasks);

            var startCoords = results.Take(techs.Count).ToList();
            var endCoords = results.Skip(techs.Count).Take(techs.Count).ToList();
            var siteCoords = results.Skip(techs.Count * 2).ToList();

            var allLocations = new List<AddressInfo>();
            var starts = new int[techs.Count];
            var ends = new int[techs.Count];
            var breakNodes = Enumerable.Repeat(-1, techs.Count).ToArray();

            for (int i = 0; i < techs.Count; i++)
            {
                var start = startCoords[i] ?? throw new Exception($"Немає координат старту для {techs[i].Name}");
                techs[i].StartLocation = start;
                starts[i] = allLocations.Count;
                allLocations.Add(start);
            }

            for (int i = 0; i < techs.Count; i++)
            {
                var end = endCoords[i] ?? throw new Exception($"Немає координат фінішу для {techs[i].Name}");
                techs[i].EndLocation = end;
                ends[i] = allLocations.Count;
                allLocations.Add(end);
            }

            for (int i = 0; i < techs.Count; i++)
            {
                if (techs[i].MinBreakMinutes is > 0)
                {
                    breakNodes[i] = allLocations.Count;
                    allLocations.Add(new AddressInfo
                    {
                        FullAddress = "__break__",
                        Latitude = 0,
                        Longitude = 0
                    });
                }
            }

            var expandedSites = new List<ServiceSite>();
            int siteOffset = allLocations.Count;

            for (int i = 0; i < sites.Count; i++)
            {
                var coord = siteCoords[i] ?? throw new Exception($"Немає координат для сайту {sites[i].Address}");
                for (int k = 0; k < sites[i].TechsNeeded; k++)
                {
                    expandedSites.Add(sites[i]);
                    allLocations.Add(coord);
                }
            }

            int n = allLocations.Count;
            var distanceMatrix = new long[n, n];
            var timeMatrix = new long[n, n];
            var timeWindows = new long[n][];
            var serviceDurations = new long[n];

            bool IsBreakNode(int idx) => breakNodes.Contains(idx);

            for (int i = 0; i < n; i++)
            {
                timeWindows[i] = new long[2];
                for (int j = 0; j < n; j++)
                {
                    if (i == j)
                    {
                        distanceMatrix[i, j] = 0;
                        timeMatrix[i, j] = 0;
                        continue;
                    }

                    if (IsBreakNode(i) || IsBreakNode(j))
                    {
                        distanceMatrix[i, j] = 0;
                        timeMatrix[i, j] = 0;
                        continue;
                    }

                    double distKm = GeoDistanceService.CalculateDistance(
                        allLocations[i].Latitude, allLocations[i].Longitude,
                        allLocations[j].Latitude, allLocations[j].Longitude);

                    distanceMatrix[i, j] = (long)Math.Round(distKm * 1000.0);
                    double rawMinutes = (distKm / 40.0) * 60.0;
                    timeMatrix[i, j] = Math.Max(1L, (long)Math.Ceiling(rawMinutes));
                }
            }

            for (int i = 0; i < techs.Count; i++)
            {
                serviceDurations[starts[i]] = 0;
                serviceDurations[ends[i]] = 0;
                timeWindows[starts[i]][0] = 0;
                timeWindows[starts[i]][1] = 1440;
                timeWindows[ends[i]][0] = 0;
                timeWindows[ends[i]][1] = 1440;
            }

            for (int i = 0; i < techs.Count; i++)
            {
                int breakNode = breakNodes[i];
                if (breakNode < 0)
                    continue;

                int breakMinutes = Math.Max(1, techs[i].MinBreakMinutes ?? 0);
                serviceDurations[breakNode] = breakMinutes;

                var workingWindow = techs[i].WorkingHours.FirstOrDefault(w => w.Day == dayOfWeek);
                long earliest = workingWindow != null ? (long)workingWindow.OpenTime.TotalMinutes : 0;
                long latestEnd = workingWindow != null ? (long)workingWindow.CloseTime.TotalMinutes : 1440;

                if (techs[i].BreakNotEarlierThan is TimeSpan notEarlier)
                    earliest = Math.Max(earliest, (long)notEarlier.TotalMinutes);

                if (techs[i].BreakNotLaterThan is TimeSpan notLater)
                    latestEnd = Math.Min(latestEnd, (long)notLater.TotalMinutes);

                long latestStart = latestEnd - breakMinutes;
                if (latestStart < earliest)
                    latestStart = earliest;

                timeWindows[breakNode][0] = earliest;
                timeWindows[breakNode][1] = latestStart;
            }

            for (int i = siteOffset; i < n; i++)
            {
                var site = expandedSites[i - siteOffset];
                serviceDurations[i] = site.VisitDuration;
                var window = site.AccessWindows.FirstOrDefault(w => w.Day == dayOfWeek) ?? site.AccessWindows.FirstOrDefault();
                timeWindows[i][0] = window != null ? (long)window.OpenTime.TotalMinutes : 0;
                timeWindows[i][1] = window != null ? (long)window.CloseTime.TotalMinutes : 1440;
            }

            return new RoutingDataModel
            {
                DistanceMatrix = distanceMatrix,
                TimeMatrix = timeMatrix,
                TimeWindows = timeWindows,
                ServiceDurations = serviceDurations,
                VehicleCount = techs.Count,
                Starts = starts,
                Ends = ends,
                BreakNodes = breakNodes,
                SiteOffset = siteOffset,
                ExpandedSites = expandedSites
            };
        }
    }
}
