namespace ObgServices.Services
{
    public class GeoDistanceService
    {
        // Радіус Землі у км
        private const double EarthRadiusKm = 6371.0;

        public static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
        {
            // Конвертація градусів у радіани
            double dLat = DegreesToRadians(lat2 - lat1);
            double dLon = DegreesToRadians(lon2 - lon1);

            lat1 = DegreesToRadians(lat1);
            lat2 = DegreesToRadians(lat2);

            // Формула гаверсинуса
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1) * Math.Cos(lat2) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            // Множемо на константний радіус у км
            return EarthRadiusKm * c;
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180;
        }

        // Перевірка входження в радіус
        public static bool IsPointInGeofence(double lat1, double lon1, double lat2, double lon2, double radiusKm, out double distance)
        {
            distance = CalculateDistance(lat1, lon1, lat2, lon2);
            return distance <= radiusKm;
        }
    }
}