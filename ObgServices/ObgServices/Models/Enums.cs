namespace ObgServices.Models
{
    public enum Skill { Interior, Exterior, Floral }
    public enum SkillLevel { None, Junior, Medior, Senior }

    public enum StartFinishPoint { Unknown, Home, Office, EitherWorks }
    public enum BestAccessMode { Unknown, CarVan, HubThenWalk, EitherWorks }
    public enum PermitDifficulty { Unknown, Easy, Medium, Hard }

    public class TimeWindow
    {
        public DayOfWeek Day { get; set; }
        public TimeSpan OpenTime { get; set; }
        public TimeSpan CloseTime { get; set; }
    }
}