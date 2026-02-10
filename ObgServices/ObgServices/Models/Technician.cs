namespace ObgServices.Models
{
    public class Technician
    {
        public required string Id { get; set; }
        public required string Name { get; set; }

        public required AddressInfo StartLocation { get; set; }
        public required AddressInfo EndLocation { get; set; }

        public List<TimeWindow> WorkingHours { get; set; } = [];
        public int MaxWeeklyHours { get; set; }
        public double CurrentScheduledHours { get; set; }

        public SkillLevel Level { get; set; }
        public List<JobType> Skills { get; set; } = [];
        public bool HasGreenWallsSkills { get; set; }
        public bool CanWorkHighAltitude { get; set; }
        public PhysicalStrain MaxPhysicalStrain { get; set; }
        public bool HasCitizenship { get; set; }
    }
}
