namespace ObgServices.Services
{
    public static class BusinessDayHelper
    {
        public static int GetBusinessDayIndex(DateTime startDate, DateTime targetDate)
        {
            // NOTE: despite the legacy name, we now operate on FULL calendar days (7-day weeks)
            // to make weekly hours and scheduling align with real calendar weeks.
            // 0-based day index from startDate to targetDate.
            return (int)(targetDate.Date - startDate.Date).TotalDays;
        }

        // Converts a calendar-day interval into "planning days".
        // NOTE: With 7-day weeks enabled, we keep calendar-day semantics (identity).
        public static int ToBusinessDays(int calendarDays)
        {
            return Math.Max(1, calendarDays);
        }
    }
}
