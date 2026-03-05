namespace ObgServices.Services
{
    public static class BusinessDayHelper
    {
        public static int GetBusinessDayIndex(DateTime startDate, DateTime targetDate)
        {
            int dayCount = 0;
            for (DateTime date = startDate.Date; date < targetDate.Date; date = date.AddDays(1))
            {
                if (date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday)
                    dayCount++;
            }
            return dayCount; // 0-based index
        }

        // Перетворює інтервал у календарних днях у робочі дні
        public static int ToBusinessDays(int calendarDays)
        {
            if (calendarDays <= 7) return 5; // 1x a week
            return (int)Math.Round(calendarDays * (5.0 / 7.0));
        }
    }
}
