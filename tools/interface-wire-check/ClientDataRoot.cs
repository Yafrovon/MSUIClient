using System;
using System.IO;
using MSUIClient;

/// <summary>
/// Where the WoW 1.12.1 Data folder lives, for the checks that open real MPQ archives.
///
/// The client resolves this from client-config.json's ClientDataPath, which may point
/// anywhere on disk; "GameData\Data" is documented as the self-contained layout, not the
/// only supported one. These checks used to hardcode &lt;repo&gt;/GameData/Data, so on a
/// machine whose config points at a real 1.12.1 install every asset-backed check failed for
/// want of data that was present the whole time — the harness only worked in one of the two
/// layouts the client itself supports.
///
/// Honour the configured path first, and fall back to the self-contained layout when there
/// is no usable config, so a checkout with neither still fails honestly rather than throwing
/// somewhere less obvious.
/// </summary>
internal static class ClientDataRoot
{
    private static readonly Lazy<string> Lazy = new(Resolve);

    /// <summary>Absolute path to the Data folder holding the .MPQ archives.</summary>
    public static string Path => Lazy.Value;

    private static string Resolve()
    {
        string repoRoot = ClientConfig.FindRepoRoot();
        string selfContained = System.IO.Path.Combine(repoRoot, "GameData", "Data");

        // ClientConfig.Load() WRITES a default config and throws when it finds none. That is
        // right for the client and wrong for a check run, so look before leaping rather than
        // catching the throw after the file has already been created.
        string[] candidates =
        [
            System.IO.Path.Combine(AppContext.BaseDirectory, "client-config.json"),
            System.IO.Path.Combine(repoRoot, "MSUIClient", "client-config.json"),
            System.IO.Path.Combine(repoRoot, "client-config.json"),
        ];
        bool haveConfig = false;
        foreach (string candidate in candidates)
            if (File.Exists(candidate)) { haveConfig = true; break; }
        if (!haveConfig) return selfContained;

        try
        {
            // Load() validates that the directory exists and holds archives, so a config
            // pointing somewhere stale falls back rather than reporting a phantom path.
            return ClientConfig.Load().ClientDataPath;
        }
        catch
        {
            return selfContained;
        }
    }
}
