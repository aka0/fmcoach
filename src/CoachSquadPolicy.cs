using System;

internal static class CoachSquadPolicy
{
    internal static string Exclusion(bool? registered, bool? injured, string club, string loanClub, string ourClub,
        string team = null, string ourTeam = null)
    {
        if (loanClub == "0" || loanClub == "-1" || string.IsNullOrWhiteSpace(loanClub)) loanClub = null;
        if (injured == true) return "injured";
        if (ourClub == null) return "club identity unavailable";
        if (loanClub != null && loanClub != ourClub) return "loaned out";
        if (club != ourClub && loanClub != ourClub) return "not at this club";
        if (ourTeam != null && team != null && team != ourTeam) return "not in the first team";
        if (registered != true) return registered == false ? "not registered" : "registration not confirmed";
        if (injured == null) return "injury status unavailable";
        return null;
    }

    internal static bool? Boolean(string value) => bool.TryParse(value, out var parsed) ? parsed
        : value == "1" ? true : value == "0" ? false : (bool?)null;
}
