namespace ObgServices.Models
{
    public class WeeklyRoute
    {
        public DayOfWeek Day { get; set; }
        public DateTime Date { get; set; }
        public List<OptimizedRoute> Routes { get; set; } = [];
    }
}
