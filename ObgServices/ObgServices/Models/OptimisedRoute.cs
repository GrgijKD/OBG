namespace ObgServices.Models
{
    public class OptimizedRoute
    {
        public required string TechnicianId { get; set; }
        public required string TechnicianName { get; set; }
        public List<RouteStop> Stops { get; set; } = [];
        public double TotalDistanceKm { get; set; }
        public double TotalDurationMinutes { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    public class RouteStop
    {
        public required string SiteId { get; set; }
        public string? SiteName { get; set; }
        public DateTime ExpectedArrivalTime { get; set; }
        public int Sequence { get; set; }
    }
}
