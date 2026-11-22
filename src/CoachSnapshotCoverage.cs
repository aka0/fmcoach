using System.Collections.Generic;

internal sealed class CoachSnapshotCoverage
{
    internal bool Finished, FirstTeamRead, RegistrationRead, TeamStatsRead, TacticInventoryRead;
    internal int Players, AttributesComplete, SeasonStatsComplete, TacticsExpected, TacticsComplete, UnknownPlayers;
    internal bool SquadComplete => FirstTeamRead && RegistrationRead && UnknownPlayers == 0 &&
        AttributesComplete == Players && SeasonStatsComplete == Players;
    internal List<string> Missing()
    {
        var missing = new List<string>();
        if (!FirstTeamRead) missing.Add("first-team player list");
        if (!RegistrationRead) missing.Add("competition registration lists");
        if (UnknownPlayers > 0) missing.Add(UnknownPlayers + " players with unconfirmed identity/availability/registration");
        if (AttributesComplete != Players) missing.Add("some player attributes");
        if (SeasonStatsComplete != Players) missing.Add("some current-season club stats");
        if (!TacticInventoryRead) missing.Add("verified saved tactic inventory (all three slots)");
        if (TacticsExpected == 0) missing.Add("saved tactic list");
        else if (TacticsComplete != TacticsExpected) missing.Add("some tactic roles or team/player instructions");
        if (!TeamStatsRead) missing.Add("team report statistics");
        return missing;
    }
    internal bool Ready => Finished && Missing().Count == 0;
    internal string Summary(string stage)
    {
        if (!Finished) return "Coach loading · " + stage;
        return (Ready ? "Coach ready" : "Coach partially loaded") +
            $" · {Players} registered, available players · attributes {AttributesComplete}/{Players} · season stats {SeasonStatsComplete}/{Players}" +
            $" · tactics {TacticsComplete}/{(TacticsExpected > 0 ? TacticsExpected.ToString() : "?")} · team stats {(TeamStatsRead ? "loaded" : "unavailable")}";
    }
}
