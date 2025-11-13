namespace HVO.SkyMonitor.AppHost;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Check if we should run applications as containers
        // Set USE_CONTAINERS=false to run projects directly (for debugging specific issues)
        // Default: true (runs containerized versions - Aspire builds automatically from Dockerfiles)
        var useContainersEnv = Environment.GetEnvironmentVariable("USE_CONTAINERS");
        var useContainers = useContainersEnv?.ToLowerInvariant() != "false";
        Console.WriteLine($"[AppHost] USE_CONTAINERS environment variable: '{useContainersEnv}' -> useContainers={useContainers}");

        // Load secrets from configuration (User Secrets in dev, Environment Variables in production)
        var minioUsername = builder.Configuration["MinIO:Username"] ?? Environment.GetEnvironmentVariable("MINIO_ROOT_USER") ?? "minioadmin";
        var minioPassword = builder.Configuration["MinIO:Password"] ?? Environment.GetEnvironmentVariable("MINIO_ROOT_PASSWORD") ?? "minioadmin";
        // var postgresUser = builder.Configuration["PostgreSQL:Username"] ?? Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
        // var postgresPassword = builder.Configuration["PostgreSQL:Password"] ?? Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";

        // Create parameter resources for PostgreSQL credentials
        // This allows Aspire to manage the credentials properly through its parameter system
        var postgresPasswordParam = builder.AddParameter("postgres-password", secret: true);

        // Infrastructure containers - using Aspire managed containers
        // Using WithContainerRuntimeArgs to bind to 0.0.0.0 for dev container accessibility
        // Session lifetime: containers stop when AppHost stops
        var redis = builder.AddRedis("redis")
            .WithLifetime(ContainerLifetime.Session)
            ;//.WithContainerRuntimeArgs("--publish", "0.0.0.0:6379:6379");

        var postgres = builder.AddPostgres("postgres")
            .WithPassword(postgresPasswordParam)
            .WithLifetime(ContainerLifetime.Session)
            .WithChildRelationship(postgresPasswordParam)
            ;//.WithContainerRuntimeArgs("--publish", "0.0.0.0:5432:5432")

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
            .WithLifetime(ContainerLifetime.Session)
            ;//.WithContainerRuntimeArgs("--publish", "0.0.0.0:9000:9000", "--publish", "0.0.0.0:9001:9001");

        // Main application - use container (default) or project (for debugging)
        if (useContainers)
        {
            // Build and run from Dockerfile (context is repo root, dockerfile path is relative)
            var skymonitor = builder.AddDockerfile("skymonitor", "../../", "src/HVO.SkyMonitor/Dockerfile")
                .WithHttpEndpoint(port: 5174, targetPort: 8080, name: "http")
                .WithReference(redis)
                .WithReference(postgressDatabase)
                .WithEnvironment("MinIO__Endpoint", minio.GetEndpoint("api"))
                .WithEnvironment("MinIO__AccessKey", minioUsername)
                .WithEnvironment("MinIO__SecretKey", minioPassword)
                .WithLifetime(ContainerLifetime.Session)
                .WaitFor(redis)
                .WaitFor(postgres)
                .WaitFor(minio)
                .WithContainerRuntimeArgs("--publish", "0.0.0.0:5174:8080");

            // Camera Agent: Simulator
            builder.AddDockerfile("simulator-agent", "../../", "src/HVO.SkyMonitor.CameraAgent.Simulator/Dockerfile")
                .WithHttpEndpoint(targetPort: 8080, name: "http")

                .WithEnvironment("SkyMonitor__BaseUrl", skymonitor.GetEndpoint("http"))
                .WithLifetime(ContainerLifetime.Session)
                .WaitFor(skymonitor)
                ;//.WithContainerRuntimeArgs("--publish", "0.0.0.0:5130:8080");

            // Camera Agent: ZWO
            builder.AddDockerfile("zwo-agent", "../../", "src/HVO.SkyMonitor.CameraAgent.ZWO/Dockerfile")
                .WithHttpEndpoint(targetPort: 8080, name: "http")
                .WithEnvironment("SkyMonitor__BaseUrl", skymonitor.GetEndpoint("http"))
                .WithLifetime(ContainerLifetime.Session)
                .WaitFor(skymonitor)
                ;//.WithContainerRuntimeArgs("--publish", "0.0.0.0:5232:8080");
        }
        else
        {
            // Run as projects for direct debugging
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
                .WaitFor(skymonitor)
                .WithExternalHttpEndpoints();

            // Camera Agent: ZWO
            builder.AddProject<Projects.HVO_SkyMonitor_CameraAgent_ZWO>("zwo-agent")
                .WithEnvironment("SkyMonitor__BaseUrl", skymonitor.GetEndpoint("http"))
                .WaitFor(skymonitor)
                .WithExternalHttpEndpoints();
        }

        builder.Build().Run();
    }
}
