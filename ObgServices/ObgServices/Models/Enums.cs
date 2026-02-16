namespace ObgServices.Models
{
    public enum Skill { Interior, Exterior, Floral }
    public enum SkillLevel { None, Junior, Medior, Senior }

    public class TimeWindow
    {
        public DayOfWeek Day { get; set; }
        public TimeSpan OpenTime { get; set; }
        public TimeSpan CloseTime { get; set; }
    }
}