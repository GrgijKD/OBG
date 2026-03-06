using ObgServices.Models;

namespace ObgServices.Services
{
    public class RoutingDataFactory
    {
        public static async Task<RoutingDataModel> CreateModel(
            List<Technician> techs,
            List<ServiceSite> sites,
            GeocodingService geocodingService)
        {
            using var semaphore = new SemaphoreSlim(20);

            // Збираємо всі унікальні адреси для геокодування
            var techStarts = techs.Select(t => t.StartLocation).ToList();
            var techEnds = techs.Select(t => t.EndLocation).ToList();

            var allGeocodeTasks = new List<Task<AddressInfo?>>();

            async Task<AddressInfo?> SafeGeocode(string address)
            {
                await semaphore.WaitAsync();
                try { return await geocodingService.GetCoordinatesFromAddress(address); }
                finally { semaphore.Release(); }
            }

            // Додаємо старти
            foreach (var t in techs) allGeocodeTasks.Add(SafeGeocode(t.StartLocation.FullAddress));
            // Додаємо фініші
            foreach (var t in techs) allGeocodeTasks.Add(SafeGeocode(t.EndLocation.FullAddress));
            // Додаємо локації
            foreach (var s in sites) allGeocodeTasks.Add(SafeGeocode(s.Address));

            var results = await Task.WhenAll(allGeocodeTasks);

            var startCoords = results.Take(techs.Count).ToList();
            var endCoords = results.Skip(techs.Count).Take(techs.Count).ToList();
            var siteCoords = results.Skip(techs.Count * 2).ToList();

            // Формуємо розширений список локацій для матриці
            var allLocations = new List<AddressInfo>();
            var starts = new int[techs.Count];
            var ends = new int[techs.Count];

            // Додаємо точки старту
            for (int i = 0; i < techs.Count; i++)
            {
                starts[i] = allLocations.Count;
                allLocations.Add(startCoords[i] ?? throw new Exception($"Немає координат старту для {techs[i].Name}"));
            }

            // Додаємо точки фінішу
            for (int i = 0; i < techs.Count; i++)
            {
                ends[i] = allLocations.Count;
                allLocations.Add(endCoords[i] ?? throw new Exception($"Немає координат фінішу для {techs[i].Name}"));
            }

            // Додаємо локації з урахуванням TechsNeeded > 1
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

            // Розрахунок матриць на основі всіх локацій
            for (int i = 0; i < n; i++)
            {
                timeWindows[i] = new long[2];
                for (int j = 0; j < n; j++)
                {
                    double distKm = GeoDistanceService.CalculateDistance(
                        allLocations[i].Latitude, allLocations[i].Longitude,
                        allLocations[j].Latitude, allLocations[j].Longitude);

                    distanceMatrix[i, j] = (long)(distKm * 1000);
                    timeMatrix[i, j] = (long)((distKm / 40.0) * 60.0);
                }
            }

            // Заповнення вікон та тривалості
            // Старти та фініші техніків мають 0 тривалості роботи
            for (int i = 0; i < siteOffset; i++)
            {
                serviceDurations[i] = 0;
                timeWindows[i][0] = 0;
                timeWindows[i][1] = 1440;
            }

            // Локації
            for (int i = siteOffset; i < n; i++)
            {
                var site = expandedSites[i - siteOffset];
                serviceDurations[i] = site.VisitDuration;
                var window = site.AccessWindows.FirstOrDefault();
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
                ExpandedSites = expandedSites
            };
        }
    }
}
