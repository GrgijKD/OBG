using ObgServices.Models;

namespace ObgServices.Services
{
    public class WeeklySchedulerService
    {
        public static async Task<Dictionary<DayOfWeek, List<VisitTask>>> DistributeTasks(List<Technician> techs, List<ServiceSite> sites)
        {
            var weeklyPlan = new Dictionary<DayOfWeek, List<VisitTask>> {
                { DayOfWeek.Monday, new() }, { DayOfWeek.Tuesday, new() },
                { DayOfWeek.Wednesday, new() }, { DayOfWeek.Thursday, new() }, { DayOfWeek.Friday, new() }
            };

            for (int i = 0; i < sites.Count; i++)
            {
                var site = sites[i];

                // Визначаємо дні для цієї локації, передаючи її порядковий номер
                var days = GetDaysForFrequency(site.VisitsPerInterval, i);

                foreach (var day in days)
                {
                    var task = new VisitTask { Site = site };

                    // Підбір техніків
                    var qualified = techs.Where(t => TechnicianFilterService.ValidateHardConstraints(t, site)).ToList();
                    task.AssignedTechs = [.. qualified.Take(site.TechsNeeded)];

                    if (task.AssignedTechs.Count == site.TechsNeeded)
                        weeklyPlan[day].Add(task);
                }
            }
            return weeklyPlan;
        }

        private static List<DayOfWeek> GetDaysForFrequency(int frequency, int index)
        {
            return frequency switch
            {
                // 1x a week: Пн, Вт, Ср, Чт, Пт
                1 => [(DayOfWeek)((index % 5) + 1)],

                // 2x a week: Пн-Ср, Вт-Чт, Ср-Пт, Пн-Чт, Вт-Пт
                2 => (index % 5) switch
                {
                    0 => [DayOfWeek.Monday, DayOfWeek.Wednesday],
                    1 => [DayOfWeek.Tuesday, DayOfWeek.Thursday],
                    2 => [DayOfWeek.Wednesday, DayOfWeek.Friday],
                    3 => [DayOfWeek.Monday, DayOfWeek.Thursday],
                    _ => [DayOfWeek.Tuesday, DayOfWeek.Friday]
                },

                // 3x a week: Пн-Ср-Пт
                3 => [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],

                _ => [DayOfWeek.Monday]
            };
        }
    }
}
