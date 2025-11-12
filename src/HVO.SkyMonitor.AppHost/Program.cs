namespace HVO.SkyMonitor.AppHost;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Infrastructure containers
        var redis = builder.AddRedis("redis")
            .WithDataVolume()
            .WithLifetime(ContainerLifetime.Persistent);

        var postgres = builder.AddPostgres("postgres")
            .WithDataVolume()
            .WithLifetime(ContainerLifetime.Persistent)
            .AddDatabase("skymonitordb");

        // MinIO for object storage - using Docker container directly
        var minio = builder.AddContainer("minio", "minio/minio")
            .WithArgs("server", "/data", "--console-address", ":9001")
            .WithEndpoint(9000, 9000, "api")
            .WithEndpoint(9001, 9001, "console")
            .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
            .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
            .WithBindMount("./minio-data", "/data")
            .WithLifetime(ContainerLifetime.Persistent);

        // Main application - references infrastructure
        var skymonitor = builder.AddProject<Projects.HVO_SkyMonitor>("skymonitor")
            .WithReference(redis)
            .WithReference(postgres)
            .WithEnvironment("MinIO__Endpoint", minio.GetEndpoint("api"))
            .WithEnvironment("MinIO__AccessKey", "minioadmin")
            .WithEnvironment("MinIO__SecretKey", "minioadmin")
            .WithExternalHttpEndpoints();

        // Camera Agent containers - communicate with main application via HTTP
        var simulatorAgent = builder.AddProject<Projects.HVO_SkyMonitor_CameraAgent_Simulator>("simulator-agent")
            .WithEnvironment("SkyMonitor__BaseUrl", skymonitor.GetEndpoint("http"))
            .WithExternalHttpEndpoints();

        var zwoAgent = builder.AddProject<Projects.HVO_SkyMonitor_CameraAgent_ZWO>("zwo-agent")
            .WithEnvironment("SkyMonitor__BaseUrl", skymonitor.GetEndpoint("http"))
            .WithExternalHttpEndpoints();

        builder.Build().Run();
    }
}
