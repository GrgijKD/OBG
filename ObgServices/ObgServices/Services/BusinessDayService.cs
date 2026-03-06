namespace ObgServices.Services
{
    public static class BusinessDayHelper
    {
        public static int GetBusinessDayIndex(DateTime startDate, DateTime targetDate)
        {
            return (targetDate.Date - startDate.Date).Days;
        }

        // Планування тепер працює в календарних днях.
        public static int ToBusinessDays(int calendarDays)
        {
            return Math.Max(1, calendarDays);
        }
    }
}
