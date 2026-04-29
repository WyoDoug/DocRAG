// ProfileTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using SaddleRAG.Database;
using ModelContextProtocol.Server;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools for managing MongoDB database profiles.
///     Enables multi-user scenarios where different sessions
///     can target different databases on the same MCP server.
/// </summary>
[McpServerToolType]
public static class ProfileTools
{
    [McpServerTool(Name = "list_profiles")]
    [Description("List all configured MongoDB database profiles. " +
                 "Each profile points at a different MongoDB instance/database. " +
                 "Use this to discover which databases are available, then pass " +
                 "the profile name as the 'profile' parameter on other tools."
                )]
    public static string ListProfiles(SaddleRagDbContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        var profiles = contextFactory.GetProfiles();
        var defaultName = contextFactory.GetDefaultProfileName();

        var response = new
                           {
                               DefaultProfile = defaultName,
                               Profiles = profiles.Select(p => new
                                                                   {
                                                                       Name = p.Key,
                                                                       p.Value.Description,
                                                                       IsDefault = p.Key.Equals(defaultName,
                                                                                StringComparison.OrdinalIgnoreCase
                                                                           )
                                                                   }
                                                         )
                           };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        return json;
    }
}
