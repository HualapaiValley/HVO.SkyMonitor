using System.Linq;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.LogicHost.Services;
using static OpenIddict.Abstractions.OpenIddictConstants;

#pragma warning disable CA1848 // Database seeding logs run rarely; LoggerMessage delegates add noise
#pragma warning disable CA2007 // ConfigureAwait(false) not required in startup-only seeding helpers

namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>
/// Seeds the database with initial data for development and production.
/// </summary>
internal static class DatabaseSeeder
{
    /// <summary>The current canonical central graph revision; see <see cref="CanonicalCentralGraphSeedChain"/>.</summary>
    internal static Guid BasicCentralProcessingGraphRevisionId => CanonicalCentralGraphSeedChain.Current.RevisionId;

    internal static Guid BasicCentralProcessingGraphAssignmentId => CanonicalCentralGraphSeedChain.Current.AssignmentId;

    /// <summary>
    /// Seeds the database with default accounts, scopes, and OAuth2 clients.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider serviceProvider, ILogger logger)
    {
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var apiKeyHasher = serviceProvider.GetRequiredService<IApiKeyHasher>();
        var dbContext = serviceProvider.GetRequiredService<ApplicationDbContext>();
        var options = serviceProvider.GetRequiredService<IOptions<DatabaseSeedOptions>>().Value;
        var bootstrapOptions = serviceProvider.GetRequiredService<IOptions<DeviceBootstrapSecretsOptions>>().Value;
        var centralIdentityOptions = serviceProvider.GetRequiredService<IOptions<CentralIdentityOptions>>().Value;
        var bootstrapClient = (bootstrapOptions.CentralIdentity ?? centralIdentityOptions).ClientCredentials;

        // Interactive credentials and integration keys are opt-in configuration.
        await EnsurePlatformEditorRoleAsync(roleManager);
        await SeedDefaultUsersAsync(userManager, options.Users, logger);

        // Seed system service account
        var systemAccount = await SeedSystemAccountAsync(userManager, logger);

        // Seed API keys for system integrations
        await SeedApiKeysAsync(dbContext, userManager, apiKeyHasher, systemAccount, options.ApiKeys, logger);

        // Seed OpenIddict scopes and clients
        await SeedOpenIddictDataAsync(serviceProvider, options, bootstrapClient, logger);

        _ = await ConvergeCanonicalCentralGraphChainAsync(
            dbContext,
            serviceProvider.GetRequiredService<ICentralProcessingGraphNodeRegistry>(),
            CanonicalCentralGraphSeedChain.Revisions,
            CreateBasicCentralProcessingGraph(),
            (serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow(),
            logger);
    }

    /// <summary>
    /// Stages the canonical chain, persists it together with every other pending seed row in one
    /// <c>SaveChangesAsync</c>, and only then reports what the persisted catalog leaves behind, so the report resolves
    /// against the seed entry this startup just made current.
    /// </summary>
    internal static async Task<CanonicalCentralGraphSeedReport> ConvergeCanonicalCentralGraphChainAsync(
        ApplicationDbContext dbContext,
        ICentralProcessingGraphNodeRegistry nodeRegistry,
        IReadOnlyList<CanonicalCentralGraphSeedRevision> chain,
        ProcessingGraphDefinition currentDefinition,
        DateTimeOffset now,
        ILogger logger)
    {
        await SeedCanonicalCentralGraphChainAsync(dbContext, nodeRegistry, chain, currentDefinition);
        await dbContext.SaveChangesAsync();
        return await CanonicalCentralGraphSeedDiagnostics.ReportAsync(dbContext, chain, nodeRegistry, now, logger);
    }

    /// <summary>
    /// Converges the canonical central graph rows on <paramref name="chain"/>. Historical entries are validated
    /// against their recorded constants and never inserted; the current entry is inserted when absent and otherwise
    /// compared exactly. Every pending row is staged on <paramref name="dbContext"/> and persisted by the caller's
    /// single <c>SaveChangesAsync</c>, inside the exclusive initialization application lock held by
    /// <see cref="DatabaseInitializer"/>, so concurrent startups cannot interleave.
    /// </summary>
    private static async Task SeedCanonicalCentralGraphChainAsync(
        ApplicationDbContext dbContext,
        ICentralProcessingGraphNodeRegistry nodeRegistry,
        IReadOnlyList<CanonicalCentralGraphSeedRevision> chain,
        ProcessingGraphDefinition currentDefinition)
    {
        ArgumentNullException.ThrowIfNull(nodeRegistry);
        ArgumentNullException.ThrowIfNull(currentDefinition);
        CanonicalCentralGraphSeedChain.Validate(chain);
        var current = chain[^1];
        var (definitionJson, definitionJsonSha256, portable, central) =
            CanonicalCentralGraphSeedChain.Compile(currentDefinition);
        if (!portable.IsValid || !central.IsValid || !nodeRegistry.Validate(central.Plan!))
        {
            throw new InvalidOperationException("The canonical basic central processing graph is invalid.");
        }
        if (currentDefinition.Name != CanonicalCentralGraphSeedChain.GraphName ||
            currentDefinition.Revision != current.Revision ||
            definitionJsonSha256 != current.DefinitionJsonSha256 ||
            portable.Plan!.DefinitionIdentitySha256 != current.DefinitionIdentitySha256 ||
            portable.Plan.PlanIdentitySha256 != current.PortablePlanIdentitySha256 ||
            central.Plan!.PlanIdentitySha256 != current.CentralPlanIdentitySha256)
        {
            throw new InvalidOperationException(
                "The canonical central graph generated from code does not match the current seed chain entry. " +
                "Append a new CanonicalCentralGraphSeedChain entry instead of changing a recorded one.");
        }

        for (var index = 0; index < chain.Count - 1; index++)
        {
            await ValidateHistoricalSeedRevisionAsync(dbContext, chain[index]);
        }

        var revision = await dbContext.CentralProcessingGraphRevisions.SingleOrDefaultAsync(
            item => item.Id == current.RevisionId);
        if (revision is null)
        {
            if (await dbContext.CentralProcessingGraphRevisions.AnyAsync(item =>
                    item.Name == currentDefinition.Name && item.Revision == currentDefinition.Revision ||
                    item.DefinitionIdentitySha256 == current.DefinitionIdentitySha256))
            {
                throw new InvalidOperationException("The canonical central graph seed identity conflicts with another row.");
            }
            revision = new CentralProcessingGraphRevision
            {
                Id = current.RevisionId,
                Name = currentDefinition.Name,
                Revision = currentDefinition.Revision,
                DefinitionJson = definitionJson,
                DefinitionIdentitySha256 = current.DefinitionIdentitySha256,
                PortablePlanIdentitySha256 = current.PortablePlanIdentitySha256,
                CentralPlanIdentitySha256 = current.CentralPlanIdentitySha256,
                CreatedAtUtc = current.SeededAtUtc,
                CreatedByUserId = CanonicalCentralGraphSeedChain.SeedActorUserId,
                PublishedAtUtc = current.SeededAtUtc,
                PublishedByUserId = CanonicalCentralGraphSeedChain.SeedActorUserId
            };
            dbContext.CentralProcessingGraphRevisions.Add(revision);
        }
        else if (!MatchesSeedRevision(revision, current) || revision.DefinitionJson != definitionJson)
        {
            throw new InvalidOperationException("The canonical central graph seed row has conflicting content.");
        }

        var assignment = await dbContext.CentralProcessingGraphAssignments.SingleOrDefaultAsync(
            item => item.Id == current.AssignmentId);
        if (assignment is null)
        {
            dbContext.CentralProcessingGraphAssignments.Add(new CentralProcessingGraphAssignment
            {
                Id = current.AssignmentId,
                RevisionId = revision.Id,
                TargetHost = CentralProcessingGraphTargetHost.Central,
                Scope = CentralProcessingGraphAssignmentScope.GlobalDefault,
                EffectiveFromUtc = current.SeededAtUtc,
                CreatedAtUtc = current.SeededAtUtc,
                ActorUserId = CanonicalCentralGraphSeedChain.SeedActorUserId,
                ReasonCode = CanonicalCentralGraphSeedChain.AssignmentReasonCode
            });
        }
        else if (!MatchesSeedAssignment(assignment, current))
        {
            throw new InvalidOperationException("The canonical central graph assignment seed row has conflicting content.");
        }
    }

    /// <summary>
    /// A historical revision is optional (a database initialized after it was superseded never receives it), but a
    /// retained row and its assignment must both still match the constants recorded when it shipped.
    /// </summary>
    private static async Task ValidateHistoricalSeedRevisionAsync(
        ApplicationDbContext dbContext,
        CanonicalCentralGraphSeedRevision entry)
    {
        var revision = await dbContext.CentralProcessingGraphRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == entry.RevisionId);
        var assignment = await dbContext.CentralProcessingGraphAssignments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == entry.AssignmentId);
        if (revision is null && assignment is null)
        {
            return;
        }
        if (revision is null || assignment is null || !MatchesSeedRevision(revision, entry) ||
            CanonicalCentralGraphSeedChain.ComputeDefinitionJsonSha256(revision.DefinitionJson) !=
                entry.DefinitionJsonSha256 ||
            !MatchesSeedAssignment(assignment, entry))
        {
            throw new InvalidOperationException(
                $"The historical canonical central graph seed revision {entry.Revision} has conflicting content.");
        }
    }

    private static bool MatchesSeedRevision(
        CentralProcessingGraphRevision revision,
        CanonicalCentralGraphSeedRevision entry)
        => revision.Name == CanonicalCentralGraphSeedChain.GraphName && revision.Revision == entry.Revision &&
           revision.DefinitionIdentitySha256 == entry.DefinitionIdentitySha256 &&
           revision.PortablePlanIdentitySha256 == entry.PortablePlanIdentitySha256 &&
           revision.EdgePlanIdentitySha256 is null &&
           revision.CentralPlanIdentitySha256 == entry.CentralPlanIdentitySha256 &&
           revision.CreatedAtUtc == entry.SeededAtUtc &&
           revision.CreatedByUserId == CanonicalCentralGraphSeedChain.SeedActorUserId &&
           revision.PublishedAtUtc == entry.SeededAtUtc &&
           revision.PublishedByUserId == CanonicalCentralGraphSeedChain.SeedActorUserId &&
           revision.RetiredAtUtc is null && revision.RetiredByUserId is null &&
           revision.RetirementReasonCode is null;

    private static bool MatchesSeedAssignment(
        CentralProcessingGraphAssignment assignment,
        CanonicalCentralGraphSeedRevision entry)
        => assignment.RevisionId == entry.RevisionId &&
           assignment.TargetHost == CentralProcessingGraphTargetHost.Central &&
           assignment.Scope == CentralProcessingGraphAssignmentScope.GlobalDefault &&
           assignment.ObservatoryId is null && assignment.LogicalCameraId is null &&
           assignment.EffectiveFromUtc == entry.SeededAtUtc && assignment.EffectiveUntilUtc is null &&
           assignment.CreatedAtUtc == entry.SeededAtUtc &&
           assignment.ActorUserId == CanonicalCentralGraphSeedChain.SeedActorUserId &&
           assignment.ReasonCode == CanonicalCentralGraphSeedChain.AssignmentReasonCode;

    internal static ProcessingGraphDefinition CreateBasicCentralProcessingGraph()
    {
        // The fixed seed revision must not change when deployment-specific transient settings change.
        return CreateBasicCentralProcessingGraph(new CentralDerivativeRecipeCatalog());
    }

    internal static ProcessingGraphDefinition CreateBasicCentralProcessingGraph(
        ICentralDerivativeRecipeCatalog recipeCatalog)
    {
        ArgumentNullException.ThrowIfNull(recipeCatalog);
        var recipes = recipeCatalog.GetRequiredRecipes(FrameArtifactRole.Raw)
            .Concat(recipeCatalog.GetRequiredRecipes(FrameArtifactRole.Calibrated))
            .DistinctBy(static recipe => recipe.RecipeName, StringComparer.Ordinal)
            .ToArray();
        var associations = CentralDerivativeRecipeCatalog.MeasuredStellarAssociationsRecipe;
        var nodes = recipes.Select((recipe, index) => CreateCentralGraphNode(
            recipe,
            index * 10,
            [
                new ProcessingGraphDependencyDefinition(SourceId(recipe.SourceRole)),
                // Annotation labels only stars the frame itself measured (#526). The association product is optional:
                // when it is omitted the annotation still renders, with every star label suppressed.
                .. recipe.RecipeName == BuiltInProcessingRecipes.Annotation
                    ? [new ProcessingGraphDependencyDefinition("MeasuredStellarAssociations", Required: false)]
                    : Array.Empty<ProcessingGraphDependencyDefinition>()
            ],
            [
                new ProcessingGraphInputContract(
                    [recipe.SourceRole],
                    [ProcessingProductKind.PixelData],
                    [],
                    [],
                    []),
                .. recipe.RecipeName == BuiltInProcessingRecipes.Annotation
                    ? [new ProcessingGraphInputContract(
                        [FrameArtifactRole.Metadata],
                        [ProcessingProductKind.Metadata],
                        [CentralDerivativeRecipeCatalog.MeasuredStellarAssociationsVariant],
                        [BuiltInProcessingRecipes.MeasuredStellarAssociations],
                        [MeasuredStellarAssociationsV1.CurrentSchemaVersion],
                        Required: false,
                        BindingName: BuiltInProcessingRecipes.MeasuredStellarAssociationsInputName,
                        BindingKind: ProcessingGraphInputBindingKind.AuxiliaryArtifact)]
                    : Array.Empty<ProcessingGraphInputContract>()
            ]))
            .ToList();
        var weather = CentralDerivativeRecipeCatalog.WeatherCloudOverlayRecipe;
        nodes.Add(CreateCentralGraphNode(
            weather,
            nodes.Count * 10,
            [
                new ProcessingGraphDependencyDefinition("Preview"),
                new ProcessingGraphDependencyDefinition("CloudAssessment")
            ],
            [
                new ProcessingGraphInputContract(
                    [FrameArtifactRole.Preview],
                    [ProcessingProductKind.PixelData],
                    [CentralDerivativeRecipeCatalog.PreviewVariant],
                    [BuiltInProcessingRecipes.EncodedPreview],
                    [],
                    BindingName: "input"),
                new ProcessingGraphInputContract(
                    [FrameArtifactRole.Metadata],
                    [ProcessingProductKind.Metadata],
                    [CentralDerivativeRecipeCatalog.CloudAssessmentVariant],
                    [BuiltInProcessingRecipes.CloudAssessment],
                    [],
                    BindingName: "assessment",
                    BindingKind: ProcessingGraphInputBindingKind.AuxiliaryArtifact)
            ]));
        // The frame's projected scene is attached by the host at expansion, never declared as a graph binding.
        nodes.Add(CreateCentralGraphNode(
            associations,
            nodes.Count * 10,
            [new ProcessingGraphDependencyDefinition(SourceId(associations.SourceRole))],
            [new ProcessingGraphInputContract(
                [associations.SourceRole],
                [ProcessingProductKind.PixelData],
                [],
                [],
                [])]));
        var sourceRoles = recipes.Select(static recipe => recipe.SourceRole)
            .Append(FrameArtifactRole.Raw)
            .Distinct()
            .Order()
            .ToArray();
        return new(
            ProcessingGraphSchemaVersions.Current,
            CanonicalCentralGraphSeedChain.GraphName,
            CanonicalCentralGraphSeedChain.Current.Revision,
            sourceRoles.Select(role => new ProcessingGraphSourceDefinition(
                SourceId(role),
                [new ProcessingGraphProductContract(role, "source", ProcessingProductKind.PixelData)]))
                .ToImmutableArray(),
            nodes.ToImmutableArray());
    }

    private static string SourceId(FrameArtifactRole role) => role switch
    {
        FrameArtifactRole.Raw => "$raw",
        FrameArtifactRole.Calibrated => "$calibrated",
        _ => throw new InvalidOperationException("The central graph source role is unsupported.")
    };

    private static ProcessingGraphNodeDefinition CreateCentralGraphNode(
        CentralDerivativeRecipe recipe,
        int order,
        ImmutableArray<ProcessingGraphDependencyDefinition> dependencies,
        ImmutableArray<ProcessingGraphInputContract> inputs)
    {
        var isTransient = recipe.Transient is not null;
        if (!isTransient && (!BuiltInProcessingRecipes.TryGetDefinition(recipe.RecipeName, out var recipeDefinition) ||
            recipeDefinition is null))
        {
            throw new InvalidOperationException("A canonical central built-in recipe is unavailable.");
        }
        _ = BuiltInProcessingRecipes.TryGetDefinition(recipe.RecipeName, out var builtInDefinition);
        var id = recipe.RecipeName switch
        {
            BuiltInProcessingRecipes.EncodedPreview => "Preview",
            BuiltInProcessingRecipes.Annotation => "Annotation",
            BuiltInProcessingRecipes.ImageQuality => "ImageQuality",
            BuiltInProcessingRecipes.CloudAssessment => "CloudAssessment",
            BuiltInProcessingRecipes.RollingMean => "RollingMean",
            BuiltInProcessingRecipes.WeatherCloudOverlay => "WeatherCloudOverlay",
            BuiltInProcessingRecipes.MeasuredStellarAssociations => "MeasuredStellarAssociations",
            CentralTransientRuntime.RecipeName => "TransientDetection",
            _ => throw new InvalidOperationException("The central recipe cannot be represented in the basic graph.")
        };
        using var transientOptions = isTransient
            ? JsonDocument.Parse(recipe.Transient!.ExecutionOptionsJson)
            : null;
        var normalizedOptions = isTransient
            ? transientOptions!.RootElement.Clone()
            : BuiltInProcessingRecipes.NormalizeOptions(recipe.RecipeName, recipe.Options);
        var window = recipe.Window is null
            ? null
            : new ProcessingGraphWindowRequirement(
                ProcessingGraphWindowKind.Centered,
                recipe.Window.Positions.Count(static position => position.IsRequired),
                recipe.Window.Positions.Count,
                 recipe.Window.Positions.Where(static position => position.IsRequired)
                     .Select(static position => position.SequenceOffset).ToImmutableArray(),
                 ["layout", "role", "variant", "source-recipe", "rig", "orientation", "calibration", "mask",
                     "sensor", "setpoint", "processing-profile", "location"],
                recipe.Window.Timeout.Ticks,
                recipe.Window.MissingInputOutcome switch
                {
                    CentralDerivativeWindowOutcome.Run => ProcessingGraphMissingInputOutcome.Run,
                    CentralDerivativeWindowOutcome.Skip => ProcessingGraphMissingInputOutcome.Skip,
                    CentralDerivativeWindowOutcome.Fail => ProcessingGraphMissingInputOutcome.Fail,
                    CentralDerivativeWindowOutcome.Quarantine => ProcessingGraphMissingInputOutcome.Quarantine,
                    _ => throw new InvalidOperationException("The central window missing-input policy is invalid.")
                });
        return new(
            id,
            recipe.RecipeName,
            recipe.RecipeVersion,
            isTransient ? ProcessingOperationKind.Window : builtInDefinition!.OperationKind,
            true,
            recipe.RecipeName is BuiltInProcessingRecipes.CloudAssessment or
                BuiltInProcessingRecipes.WeatherCloudOverlay or BuiltInProcessingRecipes.MeasuredStellarAssociations
                ? ProcessingGraphNodeFailurePolicy.Optional
                : ProcessingGraphNodeFailurePolicy.Required,
            order,
            normalizedOptions,
            dependencies,
            inputs,
            isTransient
                ? []
                : [new ProcessingGraphProductContract(
                    recipe.TargetRole,
                    recipe.TargetVariant,
                    recipe.TargetRole == FrameArtifactRole.Metadata
                        ? ProcessingProductKind.Metadata
                        : ProcessingProductKind.PixelData,
                    builtInDefinition,
                    SchemaVersion: recipe.RecipeName switch
                    {
                        BuiltInProcessingRecipes.CloudAssessment => CloudAssessmentV1.CurrentSchemaVersion,
                        BuiltInProcessingRecipes.MeasuredStellarAssociations =>
                            MeasuredStellarAssociationsV1.CurrentSchemaVersion,
                        _ => null
                    },
                    MediaType: recipe.RecipeName switch
                    {
                        BuiltInProcessingRecipes.RollingMean => "application/x-hvo-linear-frame",
                        BuiltInProcessingRecipes.ImageQuality => "application/json",
                        BuiltInProcessingRecipes.CloudAssessment =>
                            StructuredProcessingProductContracts.CloudAssessmentMediaType,
                        BuiltInProcessingRecipes.WeatherCloudOverlay => "application/x-hvo-packed-image",
                        BuiltInProcessingRecipes.MeasuredStellarAssociations =>
                            StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType,
                        _ => "image/jpeg"
                    })],
            window,
            [],
            [ProcessingGraphHosts.LogicHost]);
    }

    private static async Task SeedDefaultUsersAsync(
        UserManager<ApplicationUser> userManager,
        IEnumerable<SeedUserOptions> users,
        ILogger logger)
    {
        foreach (var descriptor in users)
        {
            var user = await userManager.FindByEmailAsync(descriptor.Email);

            if (user == null)
            {
                user = new ApplicationUser
                {
                    UserName = descriptor.Username,
                    Email = descriptor.Email,
                    EmailConfirmed = true,
                    AccountType = AccountType.User
                };

                var result = await userManager.CreateAsync(user, descriptor.Password);

                if (result.Succeeded)
                {
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("Seeded user {Email}", descriptor.Email);
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Failed to create user {descriptor.Email}: " +
                        string.Join(", ", result.Errors.Select(error => error.Description)));
                }
            }

            var needsUpdate = false;
            if (user.AccountType != AccountType.User)
            {
                user.AccountType = AccountType.User;
                needsUpdate = true;
            }

            if (needsUpdate)
            {
                var updateResult = await userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to reconcile user {descriptor.Email}: " +
                        string.Join(", ", updateResult.Errors.Select(error => error.Description)));
                }
            }

            if (!await userManager.CheckPasswordAsync(user, descriptor.Password))
            {
                if (await userManager.HasPasswordAsync(user))
                {
                    var removePasswordResult = await userManager.RemovePasswordAsync(user);
                    if (!removePasswordResult.Succeeded)
                    {
                        throw new InvalidOperationException(
                            $"Failed to remove the previous password for {descriptor.Email}: " +
                            string.Join(", ", removePasswordResult.Errors.Select(error => error.Description)));
                    }
                }

                var passwordResult = await userManager.AddPasswordAsync(user, descriptor.Password);
                if (!passwordResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to update the password for {descriptor.Email}: " +
                        string.Join(", ", passwordResult.Errors.Select(error => error.Description)));
                }
            }

            var isEditor = await userManager.IsInRoleAsync(user, AuthorizationRoleNames.PlatformEditor);
            if (descriptor.IsPlatformEditor.HasValue && isEditor != descriptor.IsPlatformEditor.Value)
            {
                var roleResult = descriptor.IsPlatformEditor.Value
                    ? await userManager.AddToRoleAsync(user, AuthorizationRoleNames.PlatformEditor)
                    : await userManager.RemoveFromRoleAsync(user, AuthorizationRoleNames.PlatformEditor);
                if (!roleResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to reconcile Platform Editor role for {descriptor.Email}: " +
                        string.Join(", ", roleResult.Errors.Select(error => error.Description)));
                }
                var stampResult = await userManager.UpdateSecurityStampAsync(user);
                if (!stampResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to rotate the security stamp for {descriptor.Email} after role reconciliation.");
                }
            }
        }
    }

    private static async Task EnsurePlatformEditorRoleAsync(RoleManager<IdentityRole> roleManager)
    {
        if (await roleManager.RoleExistsAsync(AuthorizationRoleNames.PlatformEditor))
        {
            return;
        }
        var result = await roleManager.CreateAsync(new IdentityRole(AuthorizationRoleNames.PlatformEditor));
        if (!result.Succeeded && !await roleManager.RoleExistsAsync(AuthorizationRoleNames.PlatformEditor))
        {
            throw new InvalidOperationException(
                "Failed to seed the Platform Editor role: " +
                string.Join(", ", result.Errors.Select(error => error.Description)));
        }
    }

    private static async Task<ApplicationUser> SeedSystemAccountAsync(UserManager<ApplicationUser> userManager, ILogger logger)
    {
        const string systemEmail = "system@skymonitor.local";
        const string systemUsername = "system-service";

        var existingAccount = await userManager.FindByEmailAsync(systemEmail);
        if (existingAccount != null)
        {
            if (await userManager.HasPasswordAsync(existingAccount))
            {
                var passwordResult = await userManager.RemovePasswordAsync(existingAccount);
                if (!passwordResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        "Failed to remove password authentication from the system account: " +
                        string.Join(", ", passwordResult.Errors.Select(error => error.Description)));
                }
            }
            var needsUpdate = false;
            if (!string.Equals(existingAccount.UserName, systemUsername, StringComparison.Ordinal))
            {
                existingAccount.UserName = systemUsername;
                needsUpdate = true;
            }
            if (!existingAccount.EmailConfirmed)
            {
                existingAccount.EmailConfirmed = true;
                needsUpdate = true;
            }
            if (existingAccount.AccountType != AccountType.System)
            {
                existingAccount.AccountType = AccountType.System;
                needsUpdate = true;
            }
            if (needsUpdate)
            {
                var updateResult = await userManager.UpdateAsync(existingAccount);
                if (!updateResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        "Failed to reconcile the system account: " +
                        string.Join(", ", updateResult.Errors.Select(error => error.Description)));
                }
            }
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("System service account already exists: {Email}", systemEmail);
            }
            return existingAccount;
        }

        var systemAccount = new ApplicationUser
        {
            UserName = systemUsername,
            Email = systemEmail,
            EmailConfirmed = true,
            AccountType = AccountType.System,
            // System accounts should not have passwords - they use API keys or client credentials
            PasswordHash = null
        };

        // Create without password since system accounts don't use password authentication
        var result = await userManager.CreateAsync(systemAccount);

        if (result.Succeeded)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("System service account created successfully: {Email}", systemEmail);
            }
            logger.LogInformation("System account uses API key authentication only - no password authentication");
            return systemAccount;
        }

        throw new InvalidOperationException(
            "Failed to create the system account: " +
            string.Join(", ", result.Errors.Select(error => error.Description)));
    }

    private static async Task SeedOpenIddictDataAsync(
        IServiceProvider serviceProvider,
        DatabaseSeedOptions options,
        ClientCredentialsOptions? bootstrapClient,
        ILogger logger)
    {
        var scopeManager = serviceProvider.GetRequiredService<IOpenIddictScopeManager>();
        var applicationManager = serviceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        // Seed scopes
        await SeedScopesAsync(scopeManager, logger);

        // Seed OAuth2 applications/clients
        await SeedApplicationsAsync(applicationManager, options, bootstrapClient, logger);
    }

    private static async Task SeedScopesAsync(IOpenIddictScopeManager scopeManager, ILogger logger)
    {
        var scopes = new[]
        {
            new { Name = "api.camera", DisplayName = "Camera Control", Description = "Access to camera control endpoints" },
            new { Name = "api.artifacts.read", DisplayName = "Artifact Retrieval", Description = "Job-bound access to central artifact content" },
            new { Name = "api.runner", DisplayName = "Processing Runner", Description = "Self-hosted processing runner registration, claims, and job-scoped completion" },
            new { Name = "api.frames", DisplayName = "Frame APIs", Description = "Access to frame ingestion endpoints" },
            new { Name = "api.images", DisplayName = "Image APIs", Description = "Access to image processing endpoints" },
            new { Name = "api.admin", DisplayName = "Administrative Access", Description = "Full administrative access" },
            new { Name = "api.viewer", DisplayName = "Viewer Access", Description = "Read-only API access" },
            new { Name = "api.owner.write", DisplayName = "Owner Mutation Access", Description = "Modify resources owned by the authenticated user" },
            new { Name = "api.webhooks", DisplayName = "Webhook Access", Description = "Webhook publishing scopes" }
        };

        foreach (var scope in scopes)
        {
            if (await scopeManager.FindByNameAsync(scope.Name) == null)
            {
                await scopeManager.CreateAsync(new OpenIddictScopeDescriptor
                {
                    Name = scope.Name,
                    DisplayName = scope.DisplayName,
                    Description = scope.Description,
                    Resources = { "skymonitor_api" }
                });

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Created scope: {ScopeName}", scope.Name);
                }
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Scope already exists: {ScopeName}", scope.Name);
                }
            }
        }
    }

    private static async Task SeedApplicationsAsync(
        IOpenIddictApplicationManager applicationManager,
        DatabaseSeedOptions options,
        ClientCredentialsOptions? bootstrapClient,
        ILogger logger)
    {
        if (bootstrapClient is not null &&
            !string.IsNullOrWhiteSpace(bootstrapClient.ClientId) &&
            !string.IsNullOrWhiteSpace(bootstrapClient.ClientSecret))
        {
            await EnsureConfidentialClientAsync(
                applicationManager,
                logger,
                bootstrapClient.ClientId,
                bootstrapClient.ClientSecret,
                "Camera Agent Bootstrap Client",
                bootstrapClient.Scopes.Count > 0 ? bootstrapClient.Scopes : ClientCredentialsOptions.DefaultScopes);
        }

        foreach (var client in options.ConfidentialClients)
        {
            await EnsureConfidentialClientAsync(
                applicationManager,
                logger,
                client.ClientId,
                client.ClientSecret,
                client.DisplayName,
                client.Scopes);
        }

        foreach (var client in options.PublicClients)
        {
            await EnsurePublicClientAsync(
                applicationManager,
                logger,
                client.ClientId,
                client.DisplayName,
                client.Scopes,
                client.RedirectUris.Select(static value => new Uri(value, UriKind.Absolute)).ToArray(),
                client.PostLogoutRedirectUris.Select(static value => new Uri(value, UriKind.Absolute)).ToArray());
        }
    }

    private static async Task EnsureConfidentialClientAsync(
        IOpenIddictApplicationManager applicationManager,
        ILogger logger,
        string clientId,
        string clientSecret,
        string displayName,
        IEnumerable<string> scopes)
    {
        var managedDescriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            DisplayName = displayName,
            ConsentType = ConsentTypes.Implicit,
            ClientType = ClientTypes.Confidential
        };

        managedDescriptor.Permissions.Add(Permissions.Endpoints.Token);
        managedDescriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
        foreach (var scope in scopes)
        {
            managedDescriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        }

        var application = await applicationManager.FindByClientIdAsync(clientId);
        if (application is null)
        {
            await applicationManager.CreateAsync(managedDescriptor);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Created OAuth2 client: {ClientId}", clientId);
            }
            return;
        }

        var currentPermissions = await applicationManager.GetPermissionsAsync(application);
        var metadataChanged = !string.Equals(
                await applicationManager.GetDisplayNameAsync(application), displayName, StringComparison.Ordinal) ||
            !string.Equals(
                await applicationManager.GetConsentTypeAsync(application), ConsentTypes.Implicit, StringComparison.Ordinal) ||
            !string.Equals(
                await applicationManager.GetClientTypeAsync(application), ClientTypes.Confidential, StringComparison.Ordinal) ||
            !currentPermissions.ToHashSet(StringComparer.Ordinal).SetEquals(managedDescriptor.Permissions);
        var secretChanged = !await applicationManager.ValidateClientSecretAsync(application, clientSecret);

        if (metadataChanged)
        {
            var persistedDescriptor = new OpenIddictApplicationDescriptor();
            await applicationManager.PopulateAsync(persistedDescriptor, application);
            persistedDescriptor.ClientId = managedDescriptor.ClientId;
            persistedDescriptor.DisplayName = managedDescriptor.DisplayName;
            persistedDescriptor.ConsentType = managedDescriptor.ConsentType;
            persistedDescriptor.ClientType = managedDescriptor.ClientType;
            persistedDescriptor.Permissions.Clear();
            persistedDescriptor.Permissions.UnionWith(managedDescriptor.Permissions);
            if (secretChanged)
            {
                persistedDescriptor.ClientSecret = clientSecret;
            }

            await applicationManager.UpdateAsync(application, persistedDescriptor);
        }
        else if (secretChanged)
        {
            await applicationManager.UpdateAsync(application, clientSecret);
        }
        else
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("OAuth2 client already matches configuration: {ClientId}", clientId);
            }
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Updated OAuth2 client from configuration: {ClientId}", clientId);
        }
    }

    private static async Task EnsurePublicClientAsync(
        IOpenIddictApplicationManager applicationManager,
        ILogger logger,
        string clientId,
        string displayName,
        IEnumerable<string> scopes,
        IReadOnlyCollection<Uri> redirectUris,
        IReadOnlyCollection<Uri> postLogoutUris)
    {
        if (await applicationManager.FindByClientIdAsync(clientId) != null)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("OAuth2 client already exists: {ClientId}", clientId);
            }
            return;
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = displayName,
            ConsentType = ConsentTypes.Explicit,
            ClientType = ClientTypes.Public
        };

        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);

        descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
        descriptor.Permissions.Add(Permissions.Endpoints.Token);
        descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
        descriptor.Permissions.Add(Permissions.GrantTypes.Password);
        descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
        descriptor.Permissions.Add(Permissions.ResponseTypes.Code);

        var requestedScopes = new[] { Scopes.OpenId, Scopes.Email, Scopes.Profile, Scopes.OfflineAccess }
            .Concat(scopes);

        foreach (var scope in requestedScopes)
        {
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        }

        foreach (var uri in redirectUris)
        {
            descriptor.RedirectUris.Add(uri);
        }

        foreach (var uri in postLogoutUris)
        {
            descriptor.PostLogoutRedirectUris.Add(uri);
        }

        await applicationManager.CreateAsync(descriptor);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Created OAuth2 public client: {ClientId}", clientId);
        }
    }

    private static async Task SeedApiKeysAsync(
        ApplicationDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IApiKeyHasher hasher,
        ApplicationUser systemAccount,
        IEnumerable<SeedApiKeyOptions> apiKeys,
        ILogger logger)
    {
        foreach (var descriptor in apiKeys)
        {
            var owner = systemAccount;
            if (!string.IsNullOrWhiteSpace(descriptor.UserEmail))
            {
                owner = await userManager.FindByEmailAsync(descriptor.UserEmail);
                if (owner is null)
                {
                    owner = new ApplicationUser
                    {
                        UserName = descriptor.UserEmail,
                        Email = descriptor.UserEmail,
                        EmailConfirmed = true,
                        AccountType = AccountType.User
                    };
                    var result = await userManager.CreateAsync(owner);
                    if (!result.Succeeded)
                    {
                        throw new InvalidOperationException(
                            $"Failed to create API-key owner '{descriptor.UserEmail}': "
                            + string.Join(", ", result.Errors.Select(error => error.Description)));
                    }
                }
            }

            var hashed = hasher.Hash(descriptor.RawKey);
            var existing = await dbContext.ApiKeys.SingleOrDefaultAsync(key => key.HashedKey == hashed);
            if (existing is not null)
            {
                existing.UserId = owner.Id;
                existing.DisplayName = descriptor.DisplayName;
                existing.AccessLevel = descriptor.AccessLevel;
                existing.ObservatoryId = null;
                existing.IsActive = true;
                existing.ExpiresUtc = null;
                existing.CreatedBy = "DatabaseSeeder";
                continue;
            }

            dbContext.ApiKeys.Add(new ApiKey
            {
                UserId = owner.Id,
                DisplayName = descriptor.DisplayName,
                AccessLevel = descriptor.AccessLevel,
                HashedKey = hashed,
                CreatedUtc = DateTimeOffset.UtcNow,
                CreatedBy = "DatabaseSeeder"
            });

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Seeded API key {Name}", descriptor.DisplayName);
            }
        }
    }

}

#pragma warning restore CA2007
#pragma warning restore CA1848
