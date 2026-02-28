namespace ObgServices.Models
{
    public class Technician
    {
        public required string Id { get; set; }
        public required string Name { get; set; }


        public string? HomeAddressRaw { get; set; }
        public string? OfficeAddressRaw { get; set; }

        public StartFinishPoint StartsFrom { get; set; } = StartFinishPoint.Unknown;
        public StartFinishPoint FinishesAt { get; set; } = StartFinishPoint.Unknown;

        public int? MinBreakMinutes { get; set; }
        public TimeSpan? BreakNotEarlierThan { get; set; }
        public TimeSpan? BreakNotLaterThan { get; set; }

        public int? MaxDailyServiceHours { get; set; }

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