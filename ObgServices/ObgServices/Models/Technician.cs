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

        public SkillLevel InteriorLevel { get; set; }
        public SkillLevel ExteriorLevel { get; set; }
        public SkillLevel FloralLevel { get; set; }

        public bool HasLivingWallsSkills { get; set; }
        public bool PesticideCertificated { get; set; }
        public bool CanWorkAtHeights { get; set; }
        public bool CanPhysicallyDemandingJob { get; set; }
        public bool CertifiedUsingLift { get; set; }
        public bool HasCitizenship { get; set; }
    }
}