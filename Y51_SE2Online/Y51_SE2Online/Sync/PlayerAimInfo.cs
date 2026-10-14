namespace Y51_SE2Online.Sync;

/// <summary>The lines of the aim panel for another player, from this client's social snapshot.</summary>
public sealed record PlayerAimInfo(string Title, string Faction, string Relation, string Ping, string Hint = "")
{
    public static PlayerAimInfo For(string name, long identity, SocialSnapshot social, int pingMs)
    {
        var faction = PlayerOptionRules.FactionOf(social.Factions, identity);
        string title = FactionTag.Prefix(faction?.Tag, FactionTag.Strip(name));
        string factionLine = faction == null ? "No faction" : "Faction: " + faction.Name + " (" + faction.RoleOf(identity) + ")";
        string relation = RelationTable.Build(social.Factions, social.Pacts).Of(social.Actor, identity) switch
        {
            PlayerRelation.Own => "You",
            PlayerRelation.Faction => "Your faction",
            PlayerRelation.Allied => "Allied faction",
            PlayerRelation.Peace => "At peace",
            PlayerRelation.War => "At war",
            _ => "Neutral"
        };
        return new PlayerAimInfo(title, factionLine, relation, pingMs >= 0 ? "Ping: " + pingMs + " ms" : "");
    }
}
