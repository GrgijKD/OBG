namespace ObgServices.Models
{
    public class ServiceSite
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string? Name { get; set; }
        public string? Address { get; set; }
        public AddressInfo? Coordinates { get; set; }

        public List<TimeWindow> AccessWindows { get; set; } = [];
        public string? AccessInstructions { get; set; }

        public SkillLevel RequiredSkillLevel { get; set; }
        public bool RequiresGreenWallSkills { get; set; }
        public bool RequiresHighAltitudeWork { get; set; }
        public PhysicalStrain PhysicalExertionLevel { get; set; }
        public bool RequiresCitizenship { get; set; }

        public List<string> PreferredTechIds { get; set; } = [];
        public List<string> ProhibitedTechIds { get; set; } = [];
        public List<string> SecurityClearedTechIds { get; set; } = [];

        public List<Service> Services { get; set; } = [];
    }  
}
