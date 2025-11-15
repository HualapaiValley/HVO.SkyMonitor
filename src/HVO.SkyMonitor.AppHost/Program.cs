namespace HVO.SkyMonitor.AppHost;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Load secrets from configuration (User Secrets in dev, Environment Variables in production)
        var minioUsername = builder.Configuration["MinIO:Username"] ?? Environment.GetEnvironmentVariable("MINIO_ROOT_USER") ?? "minioadmin";
        var minioPassword = builder.Configuration["MinIO:Password"] ?? Environment.GetEnvironmentVariable("MINIO_ROOT_PASSWORD") ?? "minioadmin";

        // Create parameter resources for PostgreSQL credentials
        // This allows Aspire to manage the credentials properly through its parameter system
        var postgresPasswordParam = builder.AddParameter("postgres-password", secret: true);

        // Infrastructure containers - using Aspire managed containers
        // Session lifetime: containers stop when AppHost stops
        var redis = builder.AddRedis("redis")
            .WithLifetime(ContainerLifetime.Session);

        var postgres = builder.AddPostgres("postgres")
            .WithPassword(postgresPasswordParam)
            .WithDataVolume()  // Add persistent volume for database data
            .WithLifetime(ContainerLifetime.Session)
            .WithChildRelationship(postgresPasswordParam);

        var postgressDatabase = postgres.AddDatabase("skymonitordb");

        // MinIO for object storage - using Docker volume (compatible with dev containers)
        // Credentials loaded from configuration (User Secrets or environment variables)
        var minio = builder.AddContainer("minio", "minio/minio", "latest")
            .WithHttpEndpoint(targetPort: 9000, name: "api")
            .WithHttpEndpoint(targetPort: 9001, name: "console")
            .WithEnvironment("MINIO_ROOT_USER", minioUsername)
            .WithEnvironment("MINIO_ROOT_PASSWORD", minioPassword)
            .WithVolume("minio-data", "/data")
            .WithArgs("server", "/data", "--console-address", ":9001")
            .WithLifetime(ContainerLifetime.Session);

        // Applications run as projects for full Aspire integration (telemetry, debugging, hot reload)
        // For Docker container support, see the README.md in each project directory
        var skymonitor = builder.AddProject<Projects.HVO_SkyMonitor>("skymonitor")
            .WithReference(redis)
            .WithReference(postgressDatabase)
            .WithEnvironment("MinIO__Endpoint", minio.GetEndpoint("api"))
            .WithEnvironment("MinIO__AccessKey", minioUsername)
            .WithEnvironment("MinIO__SecretKey", minioPassword)
            .WaitFor(redis)
            .WaitFor(postgres)
            .WaitFor(minio)
            .WithExternalHttpEndpoints();

        // Camera Agent: Simulator
        builder.AddProject<Projects.HVO_SkyMonitor_CameraAgent_Simulator>("simulator-agent")
            .WithEnvironment("SkyMonitor__BaseUrl", skymonitor.GetEndpoint("http"))
            .WithEnvironment("CentralIdentity__ServiceUrl", skymonitor.GetEndpoint("http"))
            .WaitFor(skymonitor)
            .WithExternalHttpEndpoints();

        // Camera Agent: ZWO
        builder.AddProject<Projects.HVO_SkyMonitor_CameraAgent_ZWO>("zwo-agent")
            .WithEnvironment("SkyMonitor__BaseUrl", skymonitor.GetEndpoint("http"))
            .WithEnvironment("CentralIdentity__ServiceUrl", skymonitor.GetEndpoint("http"))
            .WaitFor(skymonitor)
            .WithExternalHttpEndpoints();

        builder.Build().Run();
    }
}
