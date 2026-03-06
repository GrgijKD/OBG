using ObgServices.Models;

namespace ObgServices.Services
{
    public class TechnicianFilterService
    {
        public static bool ValidateHardConstraints(Technician tech, ServiceSite site)
        {
            return ExplainHardConstraintFailure(tech, site) is null;
        }

        public static string? ExplainHardConstraintFailure(Technician tech, ServiceSite site)
        {
            var reasons = new List<string>();

            bool hasBaseSkill = site.RequiredSkill switch
            {
                Skill.Interior => tech.InteriorLevel >= site.RequiredSkillLevel,
                Skill.Exterior => tech.ExteriorLevel >= site.RequiredSkillLevel,
                Skill.Floral => tech.FloralLevel >= site.RequiredSkillLevel,
                _ => false
            };

            if (!hasBaseSkill)
            {
                reasons.Add($"base-skill:{site.RequiredSkill}/{site.RequiredSkillLevel}");
            }

            if (site.PermittedTechIds.Count != 0 && !site.PermittedTechIds.Contains(tech.Id))
            {
                reasons.Add("not-in-permitted-techs");
            }

            if (site.RequiresCitizenship && !tech.HasCitizenship)
            {
                reasons.Add("citizenship");
            }

            if (site.RequiresGreenWallSkills && !tech.HasLivingWallsSkills)
            {
                reasons.Add("green-wall");
            }

            if (site.RequiresPesticide && !tech.PesticideCertificated)
            {
                reasons.Add("pesticide");
            }

            if (site.RequiresWorkAtHeights && !tech.CanWorkAtHeights)
            {
                reasons.Add("heights");
            }

            if (site.RequiresUsingLift && !tech.CertifiedUsingLift)
            {
                reasons.Add("lift");
            }

            if (site.RequiresPhysicallyDemandingJob && !tech.CanPhysicallyDemandingJob)
            {
                reasons.Add("physical");
            }

            return reasons.Count == 0 ? null : string.Join(", ", reasons);
        }
    }
}
