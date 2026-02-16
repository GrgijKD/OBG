using ObgServices.Models;
namespace ObgServices.Services
{
    using ObgServices.Models;

    public class RoutingDataFactory
    {
        private const double AverageSpeed = 40.0; // Середня швидкість, км/год

        public static async Task<RoutingDataModel> CreateModel(
            List<Technician> techs,
            List<ServiceSite> sites,
            GeocodingService geocodingService)
        {
            // Отримуємо координати всіх локацій через GeocodingService
            var allLocations = new List<AddressInfo>
            {
                // Точка старту техніка
                techs[0].StartLocation
            };

            foreach (var site in sites)
            {
                var coords = await geocodingService.GetCoordinatesFromAddress(site.Address)
                    ?? throw new Exception($"Не вдалося визначити координати для адреси: {site.Address}");
                allLocations.Add(coords);
            }

            int n = allLocations.Count;

            var distanceMatrix = new long[n, n];
            var timeMatrix = new long[n, n];
            var timeWindows = new long[n][];
            var serviceDurations = new long[n];

            // Розрахунок матриць відстаней та часу
            for (int i = 0; i < n; i++)
            {
                timeWindows[i] = new long[2];

                for (int j = 0; j < n; j++)
                {
                    double distKm = GeoDistanceService.CalculateDistance(
                        allLocations[i].Latitude, allLocations[i].Longitude,
                        allLocations[j].Latitude, allLocations[j].Longitude);

                    distanceMatrix[i, j] = (long)(distKm * 1000); // у метрах

                    // Розрахунок часу переїзду у хвилинах
                    double travelTimeMinutes = (distKm / AverageSpeed) * 60.0;
                    timeMatrix[i, j] = (long)travelTimeMinutes;
                }
            }

            // Заповнення даних для кожної локації
            for (int i = 1; i < n; i++)
            {
                var site = sites[i - 1];

                // Тривалість сервісу
                serviceDurations[i] = site.VisitDuration;

                // Часові вікна (конвертація у хвилини)
                var window = site.AccessWindows.FirstOrDefault();
                if (window != null)
                {
                    timeWindows[i][0] = (long)window.OpenTime.TotalMinutes;
                    timeWindows[i][1] = (long)window.CloseTime.TotalMinutes;
                }
                else
                {
                    timeWindows[i][0] = 0; // 00:00
                    timeWindows[i][1] = 1440; // 24:00
                }
            }

            // Точка Офісу (доступна завжди, немає сервісу)
            timeWindows[0][0] = 0; // 00:00
            timeWindows[0][1] = 1440; // 24:00
            serviceDurations[0] = 0;  // В офісі ми не виконуємо сервіс

            return new RoutingDataModel
            {
                DistanceMatrix = distanceMatrix,
                TimeMatrix = timeMatrix,
                TimeWindows = timeWindows,
                ServiceDurations = serviceDurations,
                VehicleCount = techs.Count,
                Office = 0
            };
        }
    }
}
