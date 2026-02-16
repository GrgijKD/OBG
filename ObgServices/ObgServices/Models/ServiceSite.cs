namespace ObgServices.Models
{
    public class ServiceSite
    {
        public required string Id { get; set; }
        public string? Name { get; set; }
        public required string Address { get; set; }

        public List<TimeWindow> AccessWindows { get; set; } = [];

        public Skill RequiredSkill { get; set; }
        public SkillLevel RequiredSkillLevel { get; set; }

        public bool RequiresGreenWallSkills { get; set; }
        public bool RequiresPesticide { get; set; }
        public bool RequiresWorkAtHeights { get; set; }
        public bool RequiresPhysicallyDemandingJob { get; set; }
        public bool RequiresUsingLift { get; set; }
        public bool RequiresCitizenship { get; set; }

        public List<string> PreferredTechIds { get; set; } = [];
        public List<string> ProhibitedTechIds { get; set; } = [];
        public List<string> PermittedTechIds { get; set; } = []; // Якщо список порожній допуск мають усі

        public int VisitFreqency { get; set; } // Частота візитів, разів у тиждень
        public int VisitDuration { get; set; } // Орієнтовна тривалість візиту у хв
    }
}