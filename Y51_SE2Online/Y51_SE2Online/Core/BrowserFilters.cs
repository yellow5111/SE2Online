using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Y51_SE2Online.Core;

[JsonConverter(typeof(JsonStringEnumConverter<BrowserFilter>))]
public enum BrowserFilter { Any, Yes, No }

/// <summary>The server browser's password and whitelist filters for one list (servers or sessions).</summary>
public sealed class BrowserFilters
{
    public BrowserFilter Password { get; set; }
    public BrowserFilter Whitelist { get; set; }

    /// <summary>Yes and No only match listings that say (0.15.0 and later); Any shows every listing.</summary>
    public static bool Passes(BrowserFilter filter, bool? value) => filter switch
    {
        BrowserFilter.Yes => value == true,
        BrowserFilter.No => value == false,
        _ => true
    };

    public bool Matches(DirectoryListing listing) => Passes(Password, listing.PasswordRequired()) && Passes(Whitelist, listing.WhitelistOnly());

    public string Key => Password + "/" + Whitelist;

    internal void Clean()
    {
        if (!Enum.IsDefined(Password)) Password = BrowserFilter.Any;
        if (!Enum.IsDefined(Whitelist)) Whitelist = BrowserFilter.Any;
    }
}

/// <summary>Both lists' filters, kept in Y51_SE2Online.browser.json so they are the same the next time the game starts.</summary>
public sealed class BrowserFilterFile
{
    public BrowserFilters Servers { get; set; } = new();
    public BrowserFilters Sessions { get; set; } = new();

    public static BrowserFilterFile Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<BrowserFilterFile>(File.ReadAllText(path)) is { } file)
            {
                file.Servers ??= new(); file.Sessions ??= new();
                file.Servers.Clean(); file.Sessions.Clean();
                return file;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        return new BrowserFilterFile();
    }

    public void Save(string path)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
