using ObgServices.Models;
namespace ObgServices.Services
{
    public class RoutingDataFactory
    {
        private const double AverageSpeed = 40.0; // Середня швидкість для розрахунку часу, км/год

        public static RoutingDataModel CreateModel(List<Technician> techs, List<ServiceSite> sites)
        {
            var allLocations = new List<AddressInfo> { techs[0].StartLocation };
            allLocations.AddRange(sites.Select(s => s.Coordinates!));

            int n = allLocations.Count;

            var distanceMatrix = new long[n, n];
            var timeMatrix = new long[n, n];
            var timeWindows = new long[n][];
            var serviceDurations = new long[n];

            for (int i = 0; i < n; i++)
            {
                timeWindows[i] = new long[2];

                for (int j = 0; j < n; j++)
                {
                    double distKm = GeoDistanceService.CalculateDistance(
                        allLocations[i].Latitude, allLocations[i].Longitude,
                        allLocations[j].Latitude, allLocations[j].Longitude);

                    distanceMatrix[i, j] = (long)(distKm * 1000); // у метрах

                    // Розрахунок часу: Відстань/Швидкість*60 хвилин
                    double travelTimeMinutes = (distKm / AverageSpeed) * 60.0;
                    timeMatrix[i, j] = (long)travelTimeMinutes;
                }
            }

            // Заповнюємо дані для кожної локації
            for (int i = 1; i < n; i++)
            {
                var site = sites[i - 1];

                // Тривалість сервісу 
                serviceDurations[i] = site.Services.FirstOrDefault()?.ServiceDurationMinutes ?? 30;

                // Часові вікна (конвертуємо години роботи у хвилини від початку дня)
                var window = site.AccessWindows.FirstOrDefault(); // Беремо перше доступне вікно
                if (window != null)
                {
                    timeWindows[i][0] = (long)window.OpenTime.TotalMinutes;
                    timeWindows[i][1] = (long)window.CloseTime.TotalMinutes;
                }
                else
                {
                    timeWindows[i][0] = 0; // Відкрито з 00:00
                    timeWindows[i][1] = 1440; // До кінця доби (24 * 60)
                }
            }

            // Години роботи Депо (Точка 0)
            timeWindows[0][0] = (long)(techs[0].WorkingHours.FirstOrDefault()?.OpenTime.TotalMinutes ?? 480.0); // 08:00
            timeWindows[0][1] = (long)(techs[0].WorkingHours.FirstOrDefault()?.CloseTime.TotalMinutes ?? 1080.0); // 18:00

            return new RoutingDataModel
            {
                DistanceMatrix = distanceMatrix,
                TimeMatrix = timeMatrix,
                TimeWindows = timeWindows,
                ServiceDurations = serviceDurations,
                VehicleCount = techs.Count,
                Depot = 0
            };
        }
    }
}
