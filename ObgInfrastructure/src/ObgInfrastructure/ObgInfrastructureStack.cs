using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Location;
using Amazon.CDK.AWS.S3;
using Constructs;
using DynamoAttribute = Amazon.CDK.AWS.DynamoDB.Attribute;
using S3HttpMethods = Amazon.CDK.AWS.S3.HttpMethods;

namespace ObgInfrastructure
{
    public class ObgInfrastructureStack : Stack
    {
        public ObgInfrastructureStack(Construct scope, string id, IStackProps? props = null)
            : base(scope, id, props)
        {
            // GeoIndex (AWS Location Service)
            var geoIndex = new CfnPlaceIndex(this, "ObgGeoIndex", new CfnPlaceIndexProps
            {
                IndexName = "GeoIndex",
                DataSource = "Here",
                PricingPlan = "RequestBasedUsage",
                DataSourceConfiguration = new CfnPlaceIndex.DataSourceConfigurationProperty
                {
                    IntendedUse = "SingleUse"
                }
            });

            // Service Sites
            var sitesTable = new Table(this, "ServiceSitesTable", new TableProps
            {
                TableName = "ObgServiceSites",
                PartitionKey = new DynamoAttribute { Name = "id", Type = AttributeType.STRING },
                BillingMode = BillingMode.PAY_PER_REQUEST,
                RemovalPolicy = RemovalPolicy.DESTROY
            });

            // Services
            var servicesTable = new Table(this, "ServicesTable", new TableProps
            {
                TableName = "ObgServices",
                PartitionKey = new DynamoAttribute { Name = "id", Type = AttributeType.STRING },
                BillingMode = BillingMode.PAY_PER_REQUEST,
                RemovalPolicy = RemovalPolicy.DESTROY
            });

            // Пошук за SiteId
            servicesTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
            {
                IndexName = "SiteIdIndex",
                PartitionKey = new DynamoAttribute { Name = "siteId", Type = AttributeType.STRING },
                ProjectionType = ProjectionType.ALL
            });

            // S3 Bucket для зображень
            var assetsBucket = new Bucket(this, "ObgAssetsBucket", new BucketProps
            {
                BucketName = $"obg-assets-{this.Account}",
                RemovalPolicy = RemovalPolicy.DESTROY,
                AutoDeleteObjects = true,
                Cors =
                [
                    new CorsRule
                    {
                        AllowedMethods = [S3HttpMethods.GET, S3HttpMethods.PUT, S3HttpMethods.POST],
                        AllowedOrigins = ["*"],
                        AllowedHeaders = ["*"]
                    }
                ]
            });
        }
    }
}