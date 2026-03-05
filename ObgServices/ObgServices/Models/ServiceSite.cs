namespace ObgServices.Models
{
    public class ServiceSite
    {
        public required string Id { get; set; }
        public string? Name { get; set; }
        public required string Address { get; set; }

        public List<string> CurrentTechIds { get; set; } = [];

        public BestAccessMode BestAccessedBy { get; set; } = BestAccessMode.Unknown;
        public string? BestAccessedByRaw { get; set; }

        public int TechsNeeded { get; set; } = 1;

        public bool PermitRequired { get; set; }
        public PermitDifficulty PermitDifficulty { get; set; } = PermitDifficulty.Unknown;

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

        public string? VisitFrequencyRaw { get; set; }
        public byte VisitsPerInterval { get; set; } // Напр. 2 у "2x a week"
        public int VisitIntervalDays { get; set; } = 7; // 7 = weekly, 14 = every 2 weeks, etc.
        public bool LockWeekdayAfterFirst { get; set; } // для "(weekly)" - закріпити день після першого плану
        public DayOfWeek? AnchorDayOfWeek { get; set; } // заповниться пізніше планувальником

        public string? VisitDurationRaw { get; set; }

        public int VisitFreqency { get; set; } // Частота візитів, разів у тиждень (для сумісності)
        public int VisitDuration { get; set; } // Орієнтовна тривалість візиту у хв

        public int AnchorDayNumber { get; set; } // Номер робочого дня в циклі
    }
}