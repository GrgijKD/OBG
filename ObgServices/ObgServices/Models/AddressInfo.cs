namespace ObgServices.Models
{
    public class AddressInfo
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }

        // Raw address text (useful when coordinates are not yet resolved)
        public string? FullAddress { get; set; }

        public string? City { get; set; }
        public string? ZipCode { get; set; }
    }
}
