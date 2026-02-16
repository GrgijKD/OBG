using ObgServices.Models;

namespace ObgServices.Services
{
    public class TechnicianFilterService
    {
        public static bool ValidateHardConstraints(Technician tech, ServiceSite site)
        {
            // Рівень кваліфікації
            bool isQualified = site.RequiredSkill switch
            {
                Skill.Interior => tech.InteriorLevel >= site.RequiredSkillLevel,

                Skill.Exterior => tech.ExteriorLevel >= site.RequiredSkillLevel,

                Skill.Floral => tech.FloralLevel >= site.RequiredSkillLevel,

                _ => false // Невідомий тип сервісу - невалідний
            };

            // Допуск
            if (site.PermittedTechIds.Count != 0 && !site.PermittedTechIds.Contains(tech.Id))
                isQualified = false;

            // Громадянство
            if (site.RequiresCitizenship && !tech.HasCitizenship)
                isQualified = false;
            
            // Навички та сертифікати
            if (site.RequiresGreenWallSkills && !tech.HasLivingWallsSkills)
                isQualified = false;

            if (site.RequiresPesticide && !tech.PesticideCertificated)
                isQualified = false;

            if (site.RequiresWorkAtHeights && !tech.CanWorkAtHeights)
                isQualified = false;

            if (site.RequiresUsingLift && !tech.CertifiedUsingLift)
                isQualified = false;

            if (site.RequiresPhysicallyDemandingJob && !tech.CanPhysicallyDemandingJob)
                isQualified = false;

            // Ліміт робочих годин
            double estimatedTotalHours = tech.CurrentScheduledHours + (site.VisitDuration / 60.0);
            if (estimatedTotalHours > tech.MaxWeeklyHours)
                isQualified = false;

            return isQualified;
        }
    }
}
