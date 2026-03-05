using Amazon.LocationService;
using Amazon.LocationService.Model;
using ObgServices.Models;

namespace ObgServices.Services
{
    public class GeocodingService(AmazonLocationServiceClient locationClient)
    {
        private readonly AmazonLocationServiceClient _locationClient = locationClient;
        private const string IndexName = "GeoIndex";

        public async Task<AddressInfo?> GetCoordinatesFromAddress(string address)
        {
            var request = new SearchPlaceIndexForTextRequest
            {
                IndexName = IndexName,
                Text = address,
                MaxResults = 1
            };

            var response = await _locationClient.SearchPlaceIndexForTextAsync(request);

            if (response.Results.Count > 0)
            {
                var place = response.Results[0].Place;
                return new AddressInfo
                {
                    Latitude = place.Geometry.Point[1],
                    Longitude = place.Geometry.Point[0],
                    FullAddress = address,
                    City = place.Municipality,
                    ZipCode = place.PostalCode
                };
            }

            return null; // Not found
        }
    }
}