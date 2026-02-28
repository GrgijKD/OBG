namespace ObgServices.Models
{
    public class RoutingDataModel
    {
        public required long[,] DistanceMatrix { get; set; }
        public required long[,] TimeMatrix { get; set; }
        public required long[][] TimeWindows { get; set; }
        public required long[] ServiceDurations { get; set; }
        public int VehicleCount { get; set; }
        public int Office { get; set; } = 0;
    }
}
