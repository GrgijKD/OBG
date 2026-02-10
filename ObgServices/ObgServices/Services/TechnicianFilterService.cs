using ObgServices.Models;

namespace ObgServices.Services
{
    public class TechnicianFilterService
    {
        public bool ValidateHardConstraints(Technician tech, ServiceSite site, Service service)
        {
            // Допуск та громадянство
            if (site.SecurityClearedTechIds.Count != 0 && !site.SecurityClearedTechIds.Contains(tech.Id))
                return false;
            if (site.RequiresCitizenship && !tech.HasCitizenship)
                return false;

            // Кваліфікація
            if (tech.Level < site.RequiredSkillLevel)
                return false;
            if (site.RequiresGreenWallSkills && !tech.HasGreenWallsSkills)
                return false;
            if (site.RequiresHighAltitudeWork && !tech.CanWorkHighAltitude)
                return false;

            // Фізичне навнтаження
            if (tech.MaxPhysicalStrain < site.PhysicalExertionLevel)
                return false;

            // Перевищення ліміту годин на тиждень
            if (tech.CurrentScheduledHours + (service.ServiceDurationMinutes / 60.0) > tech.MaxWeeklyHours)
                return false;

            return true;
        }
    }
}
