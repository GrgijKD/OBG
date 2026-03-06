using ObgServices.Models;

namespace ObgServices.Services
{
    public class MasterSchedulerService
    {
        public static Dictionary<int, List<ServiceSite>> GenerateMasterSchedule(List<ServiceSite> sites, List<Technician> techs)
        {
            // Визначаємо максимальний інтервал для планування
            int maxInterval = sites.Max(s => s.VisitIntervalDays);
            int horizon = BusinessDayHelper.ToBusinessDays(maxInterval);

            // Розраховуємо загальні можливості команди по часу
            // Коефіцієнт 0.85 враховує час на переїзди між об'єктами
            double totalDailyCapacityPool = techs.Sum(t => t.MaxDailyServiceHours ?? 8) * 60 * 0.85;
            double totalWeeklyCapacityPool = techs.Sum(t => t.MaxWeeklyHours) * 60 * 0.85;

            var schedule = new Dictionary<int, List<ServiceSite>>();
            var dailyLoad = new double[horizon]; // Навантаження по днях
            var weeklyLoad = new double[(int)Math.Ceiling(horizon / 7.0)]; // Навантаження по календарних тижнях (7 днів)

            for (int i = 0; i < horizon; i++) schedule[i] = [];

            // Сортуємо спочатку найважчі об'єкти (багато техніків та/або часу)
            var sortedSites = sites.OrderByDescending(s => s.VisitDuration * s.TechsNeeded).ToList();

            foreach (var site in sortedSites)
            {
                int intervalWd = BusinessDayHelper.ToBusinessDays(site.VisitIntervalDays);
                double siteWeight = site.VisitDuration * site.TechsNeeded;

                int bestStartDay = -1; // Ініціалізуємо як -1, щоб знати, чи знайдено рішення
                double minLoadScore = double.MaxValue;

                for (int startDay = 0; startDay < intervalWd && startDay < horizon; startDay++)
                {
                    bool isDayOptionInvalid = false;
                    double currentOptionScore = 0;

                    // Перевіряємо всю послідовність візитів для цього варіанту старту
                    for (int d = startDay; d < horizon; d += intervalWd)
                    {
                        int weekIdx = d / 7;

                        // Якщо день або тиждень виходить за межі - даний старт більше не перевіряється
                        if (dailyLoad[d] + siteWeight > totalDailyCapacityPool ||
                            (weekIdx < weeklyLoad.Length && weeklyLoad[weekIdx] + siteWeight > totalWeeklyCapacityPool))
                        {
                            isDayOptionInvalid = true;
                            break;
                        }

                        // Якщо ліміти в нормі
                        currentOptionScore += dailyLoad[d];

                        // Бонус за близькість локацій
                        int geoBonus = schedule[d].Count(s => s.Address.Split(',')[0] == site.Address.Split(',')[0]);
                        currentOptionScore -= (geoBonus * 30);
                    }

                    // Якщо варіант валідний і кращий за попередні
                    if (!isDayOptionInvalid && currentOptionScore < minLoadScore)
                    {
                        minLoadScore = currentOptionScore;
                        bestStartDay = startDay;
                    }
                }

                // Якщо жоден день не підійшов
                if (bestStartDay != -1)
                {
                    // Фіксуємо візити
                    for (int d = bestStartDay; d < horizon; d += intervalWd)
                    {
                        schedule[d].Add(site);
                        dailyLoad[d] += siteWeight;
                        if (d / 7 < weeklyLoad.Length) weeklyLoad[d / 7] += siteWeight;
                    }
                }
                else
                {
                    Console.WriteLine($"[WARNING]: Об'єкт {site.Id} неможливо додати до розкладу без порушення лімітів часу");
                }
            }

            // Перебалансування з урахуванням нових лімітів
            RebalanceWithStrictLimits(schedule, dailyLoad, weeklyLoad, totalDailyCapacityPool, totalWeeklyCapacityPool, horizon);

            return schedule;
        }

        private static void RebalanceWithStrictLimits(
            Dictionary<int, List<ServiceSite>> schedule,
            double[] dailyLoad,
            double[] weeklyLoad,
            double dailyCap,
            double weeklyCap,
            int horizon)
        {
            for (int d = 0; d < horizon; d++)
            {
                int weekIdx = d / 7;

                // Якщо день або тиждень перевантажені
                while (dailyLoad[d] > dailyCap || (weekIdx < weeklyLoad.Length && weeklyLoad[weekIdx] > weeklyCap))
                {
                    var siteToMove = schedule[d]
                        .Where(s => s.VisitIntervalDays >= 14) // Тільки гнучкі об'єкти
                        .OrderBy(s => s.VisitDuration)
                        .FirstOrDefault();

                    if (siteToMove == null) break;

                    // Шукаємо вільніше місце в циклі
                    int targetDay = FindEmptyDayInRange(d, dailyLoad, dailyCap, horizon);

                    if (targetDay != d)
                    {
                        double weight = siteToMove.VisitDuration * siteToMove.TechsNeeded;

                        schedule[d].Remove(siteToMove);
                        schedule[targetDay].Add(siteToMove);

                        // Оновлюємо навантаження дня
                        dailyLoad[d] -= weight;
                        dailyLoad[targetDay] += weight;

                        // Оновлюємо навантаження тижня
                        weeklyLoad[d / 7] -= weight;
                        weeklyLoad[targetDay / 7] += weight;
                    }
                    else break;
                }
            }
        }

        private static int FindEmptyDayInRange(int currentDay, double[] dailyLoad, double capacity, int horizon)
        {
            // Перевіряємо сусідні дні в межах того ж календарного тижня (7 днів)
            int weekStart = (currentDay / 7) * 7;
            int weekEnd = Math.Min(weekStart + 6, horizon - 1);

            for (int i = weekStart; i <= weekEnd; i++)
            {
                if (dailyLoad[i] < dailyLoad[currentDay] && dailyLoad[i] < capacity)
                    return i;
            }
            return currentDay;
        }
    }
}
