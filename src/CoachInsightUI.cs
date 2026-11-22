using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

public partial class CoachBehaviour
{
    static readonly Color InsightAmber = new(0.95f, 0.72f, 0.38f, 1f);

    VisualElement InsightCard(string title, string description = null, Color? marker = null)
    {
        var card = new VisualElement();
        card.style.backgroundColor = Col(new Color(1f, 1f, 1f, 0.035f));
        BorderAll(card.style, 1, Divider); Radius(card.style, 7); Pad(card.style, 10, 12);
        card.style.marginBottom = Px(8); card.style.minWidth = Px(0);
        if (marker.HasValue) { card.style.borderLeftWidth = new StyleFloat(3f); card.style.borderLeftColor = Col(marker.Value); }
        if (!string.IsNullOrWhiteSpace(title)) card.Add(Wrap(title, 13, TextMain, true));
        if (!string.IsNullOrWhiteSpace(description))
        {
            var detail = Wrap(description, 12, TextDim); detail.style.marginTop = Px(5); card.Add(detail);
        }
        return card;
    }

    Label InsightBadge(string label, bool positive)
    {
        var tint = positive ? Accent : InsightAmber;
        var badge = Text(label, 10, tint, true); Pad(badge.style, 3, 7); Radius(badge.style, 4);
        badge.style.backgroundColor = Col(new Color(tint.r, tint.g, tint.b, 0.11f));
        badge.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
        badge.style.alignSelf = new StyleEnum<Align>(Align.FlexStart); badge.style.marginTop = Px(5);
        return badge;
    }

    VisualElement InsightMeter(string title, double value, double maximum, string detail, bool primary)
    {
        var card = InsightCard(null, null, primary ? Accent : InsightAmber);
        var heading = Row(); heading.style.alignItems = new StyleEnum<Align>(Align.Center);
        var label = Wrap(title, 12, TextMain, true); label.style.flexGrow = new StyleFloat(1); label.style.flexShrink = new StyleFloat(1); label.style.minWidth = Px(0);
        heading.Add(label);
        heading.Add(Text(value.ToString("0.0", CultureInfo.InvariantCulture), 20, primary ? Accent : InsightAmber, true));
        var scale = Text(" / " + maximum.ToString("0", CultureInfo.InvariantCulture), 10, TextDim); scale.style.marginLeft = Px(3); heading.Add(scale);
        card.Add(heading);
        var track = new VisualElement(); track.style.height = Px(5); track.style.marginTop = Px(7);
        track.style.backgroundColor = Col(new Color(1f, 1f, 1f, 0.08f)); Radius(track.style, 3);
        var fill = new VisualElement(); fill.style.height = Px(5); Radius(fill.style, 3);
        fill.style.width = new StyleLength(new Length((float)Math.Max(0, Math.Min(100, value / maximum * 100)), LengthUnit.Percent));
        fill.style.backgroundColor = Col(primary ? Accent : InsightAmber); track.Add(fill); card.Add(track);
        card.tooltip = detail;
        return card;
    }

    string QualityTitle(string name) => name switch
    {
        "pace" => "Pace", "physical" => "Physical endurance", "technical" => "Passing & ball skills",
        "mental" => "Reading the game", "aerial" => "Aerial ability", "defensive" => "Defending", _ => name,
    };

    VisualElement FitInsight(string note)
    {
        string title = Regex.IsMatch(note, "tackling|marking|rest.defence", RegexOptions.IgnoreCase) ? "Defensive cover" :
            Regex.IsMatch(note, "passing|technical|creative", RegexOptions.IgnoreCase) ? "Passing & decisions" :
            Regex.IsMatch(note, "pace|quick|slow", RegexOptions.IgnoreCase) ? "Using our pace" :
            Regex.IsMatch(note, "press|stamina|work.rate", RegexOptions.IgnoreCase) ? "Energy & pressing" : "Role fit";
        return InsightCard(title, note, InsightAmber);
    }

    void BuildPlayerInsightCards()
    {
        var identity = InsightCard(targetName ?? "Player", string.Join(" · ", new[] { V("Age") != null ? V("Age") + " years" : null,
            V("PositionCombinedString") ?? V("NaturalPositionShortString") }.Where(value => value != null)));
        var ownership = Ownership(); if (ownership != null) identity.Add(InsightBadge(ownership, ownership == "YOUR SQUAD"));
        body.Add(identity);
        bool goalkeeper = squad.FirstOrDefault(player => player.name == targetName)?.IsGK == true ||
            Regex.IsMatch(V("PositionCombinedString") ?? V("NaturalPositionShortString") ?? "", "Goalkeeper|^GK", RegexOptions.IgnoreCase);
        double? Average(string[] attributes)
        {
            var numbers = attributes.Select(name => V("Attribute" + name)).Select(value => int.TryParse(value, out var n) && n >= 1 && n <= 20 ? (double?)n : null).Where(n => n.HasValue).Select(n => n.Value).ToList();
            return numbers.Count == 0 ? null : Math.Round(numbers.Average(), 1);
        }
        var groups = ProfileGroups.Where(group => !goalkeeper || group.group is "mental" or "physical")
            .Select(group => (name: QualityTitle(group.group), score: Average(group.attrs))).ToList();
        if (goalkeeper) groups.Add(("Goalkeeping skills", CoachQualityProfile.GoalkeepingAverage(attribute => int.TryParse(V("Attribute" + attribute), out var n) ? (double?)n : null)));
        var ranked = groups.Where(group => group.score.HasValue).OrderByDescending(group => group.score.Value).ToList();
        if (ranked.Count == 0) body.Add(InsightCard("Reading player attributes", "A quality profile will appear here once the attributes are available."));
        else
        {
            SectionHeader("QUALITY PROFILE");
            foreach (var group in ranked.Take(3)) body.Add(InsightMeter(group.name, group.score.Value, 20, "Average of this player's relevant attributes", true));
            if (ranked.Count > 3)
            {
                SectionHeader("LOWER-RATED QUALITIES");
                foreach (var group in ranked.AsEnumerable().Reverse().Take(2)) body.Add(InsightMeter(group.name, group.score.Value, 20, "Relative to this player's other qualities", false));
            }
        }
        var fit = hintsSnap.Where(note => targetName != null && note.Contains(targetName)).Take(3).ToList();
        if (fit.Count > 0) { SectionHeader("FIT CHECKS"); foreach (var note in fit) body.Add(FitInsight(note)); }
        Foot(goalkeeper ? "Skill averages exclude eccentricity, rushing-out and punching tendencies. Ask in Chat for role advice." : "Derived from player attributes. Ask in Chat for role and selection advice.");
    }
}
