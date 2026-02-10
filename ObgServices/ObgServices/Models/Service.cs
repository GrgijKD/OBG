namespace ObgServices.Models
{
    public class Service
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public required string SiteId { get; set; }
        public JobType Type { get; set; }

        public int VisitFrequencyWeeks { get; set; }
        public int ServiceDurationMinutes { get; set; }

        public string? AssignedTechId { get; set; }

        public List<Display> Displays { get; set; } = [];
    }
}
