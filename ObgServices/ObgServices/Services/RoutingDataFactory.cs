using ObgServices.Models;

namespace ObgServices.Services
{
    public class RoutingDataFactory
    {
        private const double AverageSpeed = 40.0; // Середня швидкість, км/год

        public static async Task<RoutingDataModel> CreateModel(
            List<Technician> techs,
            List<ServiceSite> sites,
            GeocodingService geocodingService)
        {
            // Отримуємо координати всіх локацій через GeocodingService
            // Точка старту техніка (офіс) у поточній реалізації береться як StartLocation першого техніка
            // Якщо координати не задані (0,0), пробуємо визначити їх через геокодування за FullAddress / City+Zip
            using var semaphore = new SemaphoreSlim(20);
            var office = techs[0].StartLocation;
            var tasks = new List<Task<AddressInfo?>>();

            async Task<AddressInfo?> GeocodeWithLimit(string address)
            {
                await semaphore.WaitAsync();
                try
                {
                    return await geocodingService.GetCoordinatesFromAddress(address);
                }
                finally
                {
                    semaphore.Release();
                }
            }

            if ((office.Latitude == 0 && office.Longitude == 0))
            {
                string? officeQuery = !string.IsNullOrWhiteSpace(office.FullAddress)
                    ? office.FullAddress
                    : string.Join(" ", new[] { office.City, office.ZipCode }.Where(s => !string.IsNullOrWhiteSpace(s)));

                if (!string.IsNullOrWhiteSpace(officeQuery))
                {
                    var officeCoords = await geocodingService.GetCoordinatesFromAddress(officeQuery)
                        ?? throw new Exception($"Не вдалося визначити координати для офісу/старту техніка: {officeQuery}");

                    office.Latitude = officeCoords.Latitude;
                    office.Longitude = officeCoords.Longitude;
                    office.City = officeCoords.City;
                    office.ZipCode = officeCoords.ZipCode;
                    office.FullAddress ??= officeQuery;
                }
            }

            foreach (var site in sites)
            {
                tasks.Add(Task.Run<AddressInfo?>(async () => {
                    var coords = await GeocodeWithLimit(site.Address) ?? throw new Exception($"Не вдалося визначити координати для адреси: {site.Address}");
                    return coords;
                }));
            }
            var results = await Task.WhenAll(tasks);

            var allLocations = new List<AddressInfo> { office };

            int skipCount = (office.Latitude != 0 && office.Longitude != 0 && tasks.Count > sites.Count) ? 1 : 0;
            var siteCoordinates = results.Skip(skipCount).ToList();

            var expandedSites = new List<ServiceSite>();
            var expandedLocations = new List<AddressInfo> { office };

            for (int i = 0; i < sites.Count; i++)
            {
                var site = sites[i];
                var coords = siteCoordinates[i] ?? throw new Exception($"Не вдалося отримати координати для {site.Id}");

                // Якщо треба 2+ техніка, додаємо копії локації
                for (int k = 0; k < site.TechsNeeded; k++)
                {
                    expandedSites.Add(site);
                    expandedLocations.Add(coords);
                }
            }

            // Кількість окремих візитів (не адрес)
            int n = expandedLocations.Count;

            var distanceMatrix = new long[n, n];
            var timeMatrix = new long[n, n];
            var timeWindows = new long[n][];
            var serviceDurations = new long[n];

            // Розрахунок матриць
            for (int i = 0; i < n; i++)
            {
                timeWindows[i] = new long[2];

                for (int j = 0; j < n; j++)
                {
                    double distKm = GeoDistanceService.CalculateDistance(
                        expandedLocations[i].Latitude, expandedLocations[i].Longitude,
                        expandedLocations[j].Latitude, expandedLocations[j].Longitude);

                    distanceMatrix[i, j] = (long)(distKm * 1000); // у метрах

                    double travelTimeMinutes = (distKm / AverageSpeed) * 60.0;
                    timeMatrix[i, j] = (long)travelTimeMinutes;
                }
            }

            // Заповнення даних
            for (int i = 1; i < n; i++)
            {
                var site = expandedSites[i - 1];

                serviceDurations[i] = site.VisitDuration;

                var window = site.AccessWindows.FirstOrDefault();
                if (window != null)
                {
                    timeWindows[i][0] = (long)window.OpenTime.TotalMinutes;
                    timeWindows[i][1] = (long)window.CloseTime.TotalMinutes;
                }
                else
                {
                    timeWindows[i][0] = 0;
                    timeWindows[i][1] = 1440;
                }
            }

            // Точка Офісу
            timeWindows[0][0] = 0;
            timeWindows[0][1] = 1440;
            serviceDurations[0] = 0;

            return new RoutingDataModel
            {
                DistanceMatrix = distanceMatrix,
                TimeMatrix = timeMatrix,
                TimeWindows = timeWindows,
                ServiceDurations = serviceDurations,
                VehicleCount = techs.Count,
                Office = 0,
                // Рекомендую додати цю властивість у вашу модель RoutingDataModel, 
                // щоб передати розширений список у RoutingSolverService.SolveRouting
                ExpandedSites = expandedSites
            };
        }
    }
}
