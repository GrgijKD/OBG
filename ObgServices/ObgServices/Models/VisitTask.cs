namespace ObgServices.Models
{
    public class VisitTask
    {
        public required ServiceSite Site { get; set; }
        public List<Technician> AssignedTechs { get; set; } = [];
        public int RequiredTechsCount => Site.TechsNeeded;
    }
}
