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

        // Infrastructure containers - using Aspire managed containers
        // Using WithContainerRuntimeArgs to bind to 0.0.0.0 for dev container accessibility
        // Session lifetime: containers stop when AppHost stops
        var redis = builder.AddRedis("redis")
            .WithLifetime(ContainerLifetime.Session)
            .WithContainerRuntimeArgs("--publish", "0.0.0.0:6379:6379");

        var postgres = builder.AddPostgres("postgres")
            .WithLifetime(ContainerLifetime.Session)
            .WithContainerRuntimeArgs("--publish", "0.0.0.0:5432:5432")
            .AddDatabase("skymonitordb");

        // MinIO for object storage - using Docker volume (compatible with dev containers)
        var minio = builder.AddContainer("minio", "minio/minio", "latest")
            .WithHttpEndpoint(targetPort: 9000, name: "api")
            .WithHttpEndpoint(targetPort: 9001, name: "console")
            .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
            .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
            .WithVolume("minio-data", "/data")
            .WithArgs("server", "/data", "--console-address", ":9001")
            .WithLifetime(ContainerLifetime.Session)
            .WithContainerRuntimeArgs("--publish", "0.0.0.0:9000:9000", "--publish", "0.0.0.0:9001:9001");

        // Main application - run as project (dev) or container (deployment test)
        if (useContainers)
        {
            // Build and run containers from Dockerfiles
            // Context is repository root, dockerfile path is relative to context
            var skymonitorContainer = builder.AddDockerfile("skymonitor", "../../", "src/HVO.SkyMonitor/Dockerfile")
                .WithHttpEndpoint(targetPort: 8080, name: "http")
                .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
                .WithEnvironment("ConnectionStrings__skymonitordb", postgres.Resource.ConnectionStringExpression)
                .WithEnvironment("MinIO__Endpoint", minio.GetEndpoint("api"))
                .WithEnvironment("MinIO__AccessKey", "minioadmin")
                .WithEnvironment("MinIO__SecretKey", "minioadmin")
                .WithLifetime(ContainerLifetime.Session)
                .WaitFor(redis)
                .WaitFor(postgres)
                .WaitFor(minio)
                .WithContainerRuntimeArgs("--publish", "0.0.0.0:5174:8080");

            // Camera Agent containers - communicate with main application via HTTP
            var simulatorAgentContainer = builder.AddDockerfile("simulator-agent", "../../", "src/HVO.SkyMonitor.CameraAgent.Simulator/Dockerfile")
                .WithHttpEndpoint(targetPort: 8080, name: "http")
                .WithEnvironment("SkyMonitor__BaseUrl", skymonitorContainer.GetEndpoint("http"))
                .WithLifetime(ContainerLifetime.Session)
                .WaitFor(skymonitorContainer)
                .WithContainerRuntimeArgs("--publish", "0.0.0.0:5130:8080");

            var zwoAgentContainer = builder.AddDockerfile("zwo-agent", "../../", "src/HVO.SkyMonitor.CameraAgent.ZWO/Dockerfile")
                .WithHttpEndpoint(targetPort: 8080, name: "http")
                .WithEnvironment("SkyMonitor__BaseUrl", skymonitorContainer.GetEndpoint("http"))
                .WithLifetime(ContainerLifetime.Session)
                .WaitFor(skymonitorContainer)
                .WithContainerRuntimeArgs("--publish", "0.0.0.0:5232:8080");
        }
        else
        {
            var skymonitorProject = builder.AddProject<Projects.HVO_SkyMonitor>("skymonitor")
                .WithReference(redis)
                .WithReference(postgres)
                .WithEnvironment("MinIO__Endpoint", minio.GetEndpoint("api"))
                .WithEnvironment("MinIO__AccessKey", "minioadmin")
                .WithEnvironment("MinIO__SecretKey", "minioadmin")
                .WaitFor(redis)
                .WaitFor(postgres)
                .WaitFor(minio)
                .WithExternalHttpEndpoints();

            // Camera Agent projects - communicate with main application via HTTP
            var simulatorAgentProject = builder.AddProject<Projects.HVO_SkyMonitor_CameraAgent_Simulator>("simulator-agent")
                .WithEnvironment("SkyMonitor__BaseUrl", skymonitorProject.GetEndpoint("http"))
                .WaitFor(skymonitorProject)
                .WithExternalHttpEndpoints();

            var zwoAgentProject = builder.AddProject<Projects.HVO_SkyMonitor_CameraAgent_ZWO>("zwo-agent")
                .WithEnvironment("SkyMonitor__BaseUrl", skymonitorProject.GetEndpoint("http"))
                .WaitFor(skymonitorProject)
                .WithExternalHttpEndpoints();
        }

        builder.Build().Run();
    }
}
