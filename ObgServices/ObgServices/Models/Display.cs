namespace ObgServices.Models
{
    public class Display
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string? NickName { get; set; }
        public string? ServiceId { get; set; }
        public string? SiteId { get; set; }

        public int? WalkingRouteSequenceNumber { get; set; }
        public double? CareUnits { get; set; }
        public bool HasSubirrigation { get; set; }
    }
}
