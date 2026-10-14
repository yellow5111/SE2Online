namespace Y51_SE2Online.Sync;

public static class FactionTag
{
    /// <summary>"[TAG] name", without doubling the tag when the name already carries it (roster names are decorated).</summary>
    public static string Prefix(string tag, string name)
    {
        name ??= "";
        if (string.IsNullOrEmpty(tag)) return name;
        string prefix = "[" + tag + "] ";
        return name.StartsWith(prefix, System.StringComparison.Ordinal) ? name : prefix + name;
    }

    /// <summary>The name without a leading "[TAG] ".</summary>
    public static string Strip(string name)
    {
        name ??= "";
        int end = name.StartsWith("[", System.StringComparison.Ordinal) ? name.IndexOf("] ", System.StringComparison.Ordinal) : -1;
        return end > 0 && end <= 12 ? name[(end + 2)..] : name;
    }
}
