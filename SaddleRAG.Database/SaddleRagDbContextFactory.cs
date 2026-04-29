// SaddleRagDbContextFactory.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.



#region Usings



using System.Collections.Concurrent;

using Microsoft.Extensions.Options;



#endregion



namespace SaddleRAG.Database;



/// <summary>

///     Creates SaddleRagDbContext instances for any configured MongoDB profile.

///     Caches contexts per profile to avoid reconnecting on every request.

///     Enables multi-user MCP scenarios where different sessions point at

///     different databases.

/// </summary>

public class SaddleRagDbContextFactory

{

    public SaddleRagDbContextFactory(IOptions<SaddleRagDbSettings> settings)

    {

        mSettings = settings.Value;

    }



    private readonly ConcurrentDictionary<string, SaddleRagDbContext> mContextCache =

        new ConcurrentDictionary<string, SaddleRagDbContext>();



    private readonly SaddleRagDbSettings mSettings;



    /// <summary>

    ///     Get the context for the default profile (from ActiveProfile config or env var).

    /// </summary>

    public SaddleRagDbContext GetDefault()

    {

        var cached = mContextCache.GetOrAdd(DefaultCacheKey, _ => CreateDefaultContext());

        return cached;

    }



    private SaddleRagDbContext CreateDefaultContext()

    {

        (var connectionString, var databaseName) = mSettings.Resolve();

        var defaultSettings = new SaddleRagDbSettings

                                  {

                                      ConnectionString = connectionString,

                                      DatabaseName = databaseName

                                  };

        return new SaddleRagDbContext(Options.Create(defaultSettings));

    }



    private SaddleRagDbContext CreateProfileContext(string name)

    {

        if (!mSettings.Profiles.TryGetValue(name, out var profile))

        {

            throw new InvalidOperationException($"MongoDB profile '{name}' is not configured. " +

                                                $"Available profiles: {string.Join(", ", mSettings.Profiles.Keys)}"

                                               );

        }



        var profileSettings = new SaddleRagDbSettings

                                  {

                                      ConnectionString = profile.ConnectionString,

                                      DatabaseName = profile.DatabaseName

                                  };



        return new SaddleRagDbContext(Options.Create(profileSettings));

    }



    /// <summary>

    ///     Get the context for a specific named profile.

    ///     Returns the default if profileName is null or empty.

    ///     Throws if the profile name is not configured.

    /// </summary>

    public SaddleRagDbContext GetForProfile(string? profileName)

    {

        SaddleRagDbContext result;



        if (string.IsNullOrEmpty(profileName))

            result = GetDefault();

        else

            result = mContextCache.GetOrAdd(profileName, name => CreateProfileContext(name));



        return result;

    }



    /// <summary>

    ///     List all configured profile names.

    /// </summary>

    public IReadOnlyList<string> GetProfileNames()

    {

        return mSettings.Profiles.Keys.ToList();

    }



    /// <summary>

    ///     Get profile metadata for display.

    /// </summary>

    public IReadOnlyDictionary<string, MongoDbProfile> GetProfiles()

    {

        return mSettings.Profiles;

    }



    /// <summary>

    ///     Get the name of the default (currently active) profile.

    /// </summary>

    public string? GetDefaultProfileName()

    {

        var name = ResolveDefaultProfileName();

        return name;

    }



    private string? ResolveDefaultProfileName()

    {

        var envOverride = Environment.GetEnvironmentVariable(SaddleRagDbSettings.ProfileEnvVar);

        var result = envOverride ?? mSettings.ActiveProfile;

        return result;

    }



    private const string DefaultCacheKey = "__default__";

}

