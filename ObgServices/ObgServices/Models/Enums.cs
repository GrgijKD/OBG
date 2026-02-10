namespace ObgServices.Models
{
    public enum SkillLevel { Junior, Medior, Senior }
    public enum JobType { Interior, Exterior, Floral }
    public enum PhysicalStrain { Light, Medium, Hard }

    public class TimeWindow
    {
        public DayOfWeek Day { get; set; }
        public TimeSpan OpenTime { get; set; }
        public TimeSpan CloseTime { get; set; }
    }
}
