// SaddleRagDbSettings.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.



namespace SaddleRAG.Database;



/// <summary>

///     Configuration settings for the MongoDB connection.

///     Supports named profiles for switching between local and shared databases.

/// </summary>

public class SaddleRagDbSettings

{

    /// <summary>

    ///     Name of the active profile. Selects from Profiles dictionary.

    ///     Overridden by SADDLERAG_MONGODB_PROFILE environment variable.

    ///     If null/empty and no profiles defined, uses ConnectionString/DatabaseName directly.

    /// </summary>

    public string? ActiveProfile { get; set; }



    /// <summary>

    ///     Named connection profiles.

    ///     Example: "local" â†’ localhost, "company" â†’ shared server.

    /// </summary>

    public Dictionary<string, MongoDbProfile> Profiles { get; set; } = new Dictionary<string, MongoDbProfile>();



    /// <summary>

    ///     When true, preload all configured profiles during MCP startup.

    ///     When false, preload only the default profile to keep local startup fast.

    /// </summary>

    public bool BootstrapAllProfilesAtStartup { get; set; } = true;



    /// <summary>

    ///     Direct connection string (used when no profiles are defined).

    /// </summary>

    public string ConnectionString { get; set; } = MongoDbProfile.DefaultConnectionString;



    /// <summary>

    ///     Direct database name (used when no profiles are defined).

    /// </summary>

    public string DatabaseName { get; set; } = MongoDbProfile.DefaultDatabaseName;



    /// <summary>

    ///     Resolve the effective connection string and database name

    ///     from the active profile or direct settings.

    /// </summary>

    public (string ConnectionString, string DatabaseName) Resolve()

    {

        var profileOverride = Environment.GetEnvironmentVariable(ProfileEnvVar);

        var profileName = profileOverride ?? ActiveProfile;



        var result = (ConnectionString, DatabaseName);



        if (!string.IsNullOrEmpty(profileName) && Profiles.TryGetValue(profileName, out var profile))

            result = (profile.ConnectionString, profile.DatabaseName);



        return result;

    }



    /// <summary>

    ///     Configuration section name in appsettings.

    /// </summary>

    internal const string ProfileEnvVar = "SADDLERAG_MONGODB_PROFILE";

    public const string SectionName = "MongoDB";

}

