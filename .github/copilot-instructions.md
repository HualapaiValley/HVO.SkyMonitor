# GitHub Copilot Instructions for HVO.SkyMonitor

## Project Overview
HVO.SkyMonitor is a sky monitoring application built with modern .NET technologies, focusing on astronomy calculations and real-time data visualization.

## Core Technologies

### Runtime & Framework
- **.NET 10.0 (LTS)** - SDK version pinned via `global.json`
  - **Use .NET 10 features and updates where possible** (enhanced performance, improved APIs, etc.)
- **ASP.NET Core** - For building web applications and APIs
- **Blazor Server** - For interactive, real-time web UI with server-side rendering
- **Entity Framework Core** - For data access and ORM

### Testing
- **MSTest** - Primary framework for unit and integration testing
- **Moq (optional)** - For service mocking and test isolation

### Development Environment
- **GitHub** - Version control and collaboration
- **VS Code Dev Containers** - Consistent, containerized development environment

### UI Framework
- **Bootstrap 5.3** - Delivered via CDN for responsive UI components
- **Font Awesome** - Icon library for UI elements
- **Bootstrap Icons** - Additional icon set for UI primitives

## Coding Standards & Conventions

### Project Structure
- Use **explicit namespaces** matching folder structure
- Organize code into logical layers: Controllers, Services, Models, etc.
- Place shared code in the **HVO.Common** project
- Use separate test projects with `.Tests` suffix
- Test file naming: `{ClassUnderTest}Tests.cs`

### C# Guidelines
- **Use C# 14 features** appropriately (primary constructors, collection expressions, field keyword, etc.)
  - Leverage .NET 10-specific improvements in performance and APIs
- **NO top-level statements** - Always use explicit `Main` method with proper class structure:
  ```csharp
  namespace HVO.ProjectName
  {
      public class Program
      {
          public static void Main(string[] args)
          {
              // Application entry point
          }
      }
  }
  ```
- Use **var** for local variables when type is obvious
- Prefer **async/await** patterns over `.Result` or `.Wait()`
- Enable **nullable reference types** (`<Nullable>enable</Nullable>`)
- Enable **implicit usings** (`<ImplicitUsings>enable</ImplicitUsings>`)
- Use **file-scoped namespaces** for cleaner code
- Prefer **readonly structs** for immutable value types
- Apply `[MethodImpl(MethodImplOptions.AggressiveInlining)]` for hot-path methods

### Architecture Patterns
- **Functional programming patterns**: Use `Result<T>` and `Option<T>` from `HVO.Common` for error handling
- **Dependency Injection**: Use ASP.NET Core's built-in DI container
  - Use **constructor injection** for required dependencies
  - Register services in `Program.cs` using `builder.Services`
  - Use `IServiceCollection` extension methods for complex service registration
  - Prefer **interfaces** for testability
- **Repository pattern**: For data access abstraction with Entity Framework Core
- **CQRS (optional)**: Consider separating commands and queries for complex operations

### Memory & Performance
- Use **value types** (structs) for small, immutable data
- Use **Span<T>** and **Memory<T>** for efficient buffer operations and high-performance scenarios
- Use **ValueTask<T>** for potentially synchronous async operations
- Avoid boxing in hot paths
- Use **ArrayPool<T>** for temporary array allocations
- Implement proper caching strategies where appropriate
- Dispose of resources properly (implement **IDisposable** and **IAsyncDisposable** as needed)
- Profile memory usage for astronomy calculations

### Configuration Management
- Use **appsettings.json** for application configuration
- Support **environment-specific settings** (e.g., `appsettings.Development.json`, `appsettings.Production.json`)
- Use **strongly-typed configuration** with `IOptions<T>` pattern
- **Validate configuration at startup** using data annotations or `IValidateOptions<T>`
- Never commit sensitive configuration to source control

## Project-Specific Guidelines

### HVO.Common Library
- Contains shared utilities, functional types (`Result<T>`, `Option<T>`), and astronomy calculations
- All public APIs must have **XML documentation comments**
- Use **readonly struct** for immutable types
- Preserve stack traces with `ExceptionDispatchInfo` when rethrowing exceptions

### Astronomy Calculations
- Document all astronomical constants with sources (IERS, IAU standards)
- Use proper units (degrees, radians, hours) and convert explicitly
- Include references to astronomical standards and papers
- Consider numeric precision for celestial mechanics

### Blazor Component Development (Following Microsoft Best Practices)
- Use **Blazor Server** for interactive components with `@rendermode InteractiveServer`
- Create **code-behind files** (`.razor.cs`) for any component that contains logic, parameters, or lifecycle code to keep markup clean and testable
  - Purely static pages (e.g., simple landing pages) may remain markup-only
- Use **Blazor Scoped CSS** (`.razor.css`) for component-specific styling - Automatically scoped to prevent style conflicts
- Use **Blazor Scoped JavaScript** (`.razor.js`) for component-specific client-side behavior - Isolated JavaScript modules
- Prefer **scoped CSS** over inline styling. Reserve inline style attributes for cases where values are dynamically generated at runtime
- **Avoid inline JavaScript** in Razor markup. Place client-side code in scoped JS files or the component code-behind; tiny `onclick` or `data-*` attributes are acceptable only when no alternative exists
- **Component File Structure Pattern**:
  ```
  ComponentName.razor      # Markup only - no <style> or <script> blocks
  ComponentName.razor.cs   # C# logic and event handlers
  ComponentName.razor.css  # Scoped styles (automatically scoped by Blazor)
  ComponentName.razor.js   # Scoped JavaScript (optional, for client interop)
  ```
- All **layout and root components** must ensure the `<html>` element renders with `data-theme="hvo-dark"` so theme variables apply correctly
- Implement **IDisposable** for components with subscriptions or timers
- Use **StateHasChanged()** judiciously to minimize re-renders
- Leverage **Blazor's built-in validation** with EditForm and DataAnnotations

### Entity Framework Core
- Use **async/await** for all database operations
- Apply **AsNoTracking()** for read-only queries
- Use **migrations** for schema changes
- Configure entities using **Fluent API** in `OnModelCreating`
- Use **value converters** for custom type mappings

### API Development
- Implement **API versioning** with URL segments (`/api/v1.0/endpoint`)
- Use **IHttpClientFactory** for HTTP client management
- Follow **REST conventions** for API endpoints

### Testing Best Practices
- Use **MSTest** as the primary testing framework
- Follow **Arrange-Act-Assert (AAA)** pattern
- Use **descriptive test names** that explain the scenario
- Test both success and failure paths
- Use **[DataTestMethod]** with **[DataRow]** for parameterized tests
- Mock external dependencies using **Moq**
- Use **WebApplicationFactory<T>** for integration tests
- Use **service mocking** instead of database seeding for integration tests
- Create enhanced `TestWebApplicationFactory` with proper service replacement
- **FluentAssertions** is optional
- Suppress **CS1030 warnings** in test projects for clean builds

### Error Handling and Logging

#### Structured Logging
- Use **structured logging** with `ILogger<T>` throughout the workspace
- Follow consistent logging patterns across all components
- **Dependency Injection for Logging**: Use constructor injection with optional `ILogger<T>?` parameters in all hardware device classes
- Use **named parameters** in log messages for better searchability and monitoring
- **Hardware Device Logging**: All GPIO and IoT device classes must support `ILogger` with fallback creation when not provided
- **Replace Debug.WriteLine with structured logging** - No console debugging in production code

#### Log Level Guidelines
- **Trace**: High-frequency operations like timer events and GPIO state toggles
- **Debug**: Operational state changes, method entry/exit, configuration changes
- **Information**: Important business events, startup/shutdown, major state transitions
- **Warning**: Recoverable errors, configuration issues, performance concerns
- **Error**: Exceptions and unrecoverable errors with full context
- **Critical**: System-level failures requiring immediate attention

#### Exception Handling
- Implement proper exception handling with **specific exception types**
- Use `Result<T>` for expected failures (business logic)
- Use `Result<T, TEnum>` for typed error codes in APIs
- Use exceptions for unexpected failures (system errors)
- Always provide descriptive error messages
- Log errors with appropriate context

#### Validation
- Ensure **API request DTOs encapsulate validation** using data annotations or `IValidatableObject` so controllers rely on automatic model validation

## UI/UX Guidelines

### Bootstrap 5.3
- Use **Bootstrap 5** for responsive UI design with component-specific customizations in scoped CSS
- Use **utility classes** for spacing, colors, and typography
- Leverage **responsive grid system** (container, row, col-*)
- Use **Bootstrap components** (cards, modals, navbars) for consistency
- Load Bootstrap via CDN in `App.razor` or `_Layout.cshtml`

### Icons
- Use **Bootstrap Icons** for UI actions and navigation
- Use **Font Awesome** for specialized icons
- Ensure icons have **aria-labels** for accessibility

### Accessibility
- Ensure proper **semantic HTML**
- Use **ARIA attributes** where needed
- Test with **keyboard navigation**
- Maintain **sufficient color contrast**

## Development Workflow

### Git Conventions
- Use **conventional commits** format: `type(scope): description`
  - Types: `feat`, `fix`, `docs`, `refactor`, `test`, `chore`
- Create **feature branches** from `main`
- Write **descriptive PR descriptions**
- Squash commits when merging

### Code Review Focus
- Memory safety and null reference handling
- Proper async/await usage
- Test coverage for new features
- Documentation completeness
- Performance implications for astronomy calculations

### Tooling Availability Policy
- When a command-line tool is missing (for example `rg` or `python`), either install it via the devcontainer provisioning scripts or immediately document the supported alternative in `README.md`/these instructions.
- Avoid repeatedly invoking known-missing commands—switch to the confirmed binary (for example `python3`) until the alias is installed.
- After installing a new tool, update `.devcontainer/post-create.sh` (or equivalent) so future containers match the current environment.

## Common Patterns & Examples

### Strongly-Typed Configuration
```csharp
// Configuration class with validation
public class ObservatorySettings
{
    [Required]
    [Range(-90, 90)]
    public double Latitude { get; set; }
    
    [Required]
    [Range(-180, 180)]
    public double Longitude { get; set; }
    
    [Required]
    public string TimeZone { get; set; } = string.Empty;
}

// Registration in Program.cs
builder.Services.Configure<ObservatorySettings>(
    builder.Configuration.GetSection("Observatory"));

builder.Services.AddOptions<ObservatorySettings>()
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Usage in a service
public class AstronomyService
{
    private readonly ObservatorySettings _settings;
    
    public AstronomyService(IOptions<ObservatorySettings> settings)
    {
        _settings = settings.Value;
    }
}
```

### Using Result<T> for Error Handling
```csharp
public async Task<Result<UserProfile>> GetUserProfileAsync(int userId)
{
    try
    {
        var user = await _context.Users.FindAsync(userId);
        if (user is null)
            return Result<UserProfile>.Failure(new NotFoundException($"User {userId} not found"));
        
        return Result<UserProfile>.Success(user.ToProfile());
    }
    catch (Exception ex)
    {
        return Result<UserProfile>.Failure(ex);
    }
}
```

### Blazor Component with Dependency Injection
```csharp
@page "/sky-view"
@using HVO.Astronomy
@inject IAstronomyService AstronomyService
@implements IDisposable

<div class="container">
    <h3>Sky Position</h3>
    <p>Altitude: @_altitude°</p>
    <p>Azimuth: @_azimuth°</p>
</div>

@code {
    private double _altitude;
    private double _azimuth;
    private Timer? _timer;

    protected override void OnInitialized()
    {
        _timer = new Timer(UpdatePosition, null, 0, 1000);
    }

    private void UpdatePosition(object? state)
    {
        var position = AstronomyService.GetCurrentPosition();
        _altitude = position.AltitudeDeg;
        _azimuth = position.AzimuthDeg;
        InvokeAsync(StateHasChanged);
    }

    public void Dispose() => _timer?.Dispose();
}
```

### Entity Framework Configuration
```csharp
public class ApplicationDbContext : DbContext
{
    public DbSet<Observation> Observations => Set<Observation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Observation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Timestamp).IsRequired();
            entity.HasIndex(e => e.Timestamp);
            entity.Property(e => e.RightAscension).HasPrecision(18, 10);
        });
    }
}
```

### MSTest with Moq
```csharp
[TestClass]
public class AstronomyServiceTests
{
    [TestMethod]
    public async Task GetCurrentPosition_WithValidCoordinates_ReturnsPosition()
    {
        // Arrange
        var mockRepo = new Mock<IObservationRepository>();
        var service = new AstronomyService(mockRepo.Object);

        // Act
        var result = await service.GetCurrentPositionAsync(35.0, -120.0);

        // Assert
        Assert.IsTrue(result.IsSuccessful);
        Assert.IsNotNull(result.Value);
    }
}
```

## Performance Considerations

- Use **server-side Blazor** for reduced JavaScript bundle size
- Implement **pagination** for large data sets
- Use **SignalR** efficiently - batch updates when possible
- Cache astronomy calculations when appropriate
- Use **compiled queries** in Entity Framework for frequently-executed queries
- Profile with **dotnet-counters** and **dotnet-trace** for production issues
- Leverage **.NET 10 performance improvements** in core libraries and runtime

## Security Guidelines

- **Never commit secrets** to version control (use User Secrets or Azure Key Vault)
- Validate all user inputs
- Use **parameterized queries** (Entity Framework handles this)
- Implement **CSRF protection** (enabled by default in ASP.NET Core)
- Use **HTTPS** in production
- Follow **OWASP** guidelines for web application security
