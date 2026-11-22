// Session boundaries for the persistent Unity behaviour. Only current-session data may reach the UI or disk.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SI.Core;
using SI.Bindable;
using UnityEngine;

public partial class CoachBehaviour
{
    IntPtr humanSourcePointer, gameSourcePointer;
    string sessionManagerId, sessionClubId, sessionCareer;
    List<Req> identityChecks;
    Req careerCheck;
    float nextIdentityCheck, lastTacticMergeAt = -999f;
    int dataSessionVersion;

    static IntPtr SourcePointer(TypedValue value)
    {
        try { return value?.Get()?.Pointer ?? IntPtr.Zero; } catch { return IntPtr.Zero; }
    }

    void AcceptDataSources(TypedValue human, TypedValue game, string humanPath, string gamePath)
    {
        var hp = SourcePointer(human); var gp = SourcePointer(game);
        if ((humanTv != null && human == null) || (gameTv != null && game == null) ||
            (humanSourcePointer != IntPtr.Zero && hp != humanSourcePointer) ||
            (gameSourcePointer != IntPtr.Zero && gp != gameSourcePointer))
            ResetDataSession("save or manager source changed");
        humanTv = human; gameTv = game;
        humanSourcePointer = hp; gameSourcePointer = gp;
        humanSrcPath = humanPath; gameSrcPath = gamePath;
    }

    static bool IdentityChanged(string previous, string current) => previous != null && current != null && previous != current;

    void PollSessionIdentity()
    {
        if (humanTv == null) return;
        if (identityChecks != null)
        {
            if (identityChecks.Any(r => !r.Done) || (careerCheck != null && !careerCheck.Done)) return;
            string V(int i) => identityChecks[i].Got ? ValueText(identityChecks[i].Str) : null;
            var managerId = V(0); var clubId = V(1); var manager = V(2); var club = V(3); var team = V(4);
            var career = careerCheck?.Got == true ? ValueText(careerCheck.Str) : null;
            bool clubRemoved = sessionClubId != null && identityChecks[1].Empty;
            if (clubRemoved || IdentityChanged(sessionManagerId, managerId) || IdentityChanged(sessionClubId, clubId) || IdentityChanged(sessionCareer, career))
                ResetDataSession("career, manager or club identity changed");
            sessionManagerId = managerId ?? sessionManagerId;
            sessionClubId = clubId ?? sessionClubId;
            sessionCareer = career ?? sessionCareer;
            if (manager != null) humanManager = manager;
            if (club != null) SetHumanClub(club, "session identity");
            if (clubId != null) humanClubId = clubId;
            if (team != null) humanTeamName = team;
            identityChecks = null; careerCheck = null;
            nextIdentityCheck = Time.unscaledTime + 3f;
        }
        if (Time.unscaledTime < nextIdentityCheck) return;
        identityChecks = new[] { "UniqueId", "Club.UniqueId", "Name", "Club.Name", "Team.Name" }
            .Select(p => Ask(humanTv, p, 5f, false, "session")).ToList();
        careerCheck = gameTv == null ? null : Ask(gameTv, "DateCreated", 5f, false, "session");
    }

    string CareerCacheKey()
    {
        // Do not reuse a different career's slot details for the same manager and club.
        if (sessionCareer == null) return "unidentified";
        return "career_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionCareer))).Substring(0, 24);
    }

    partial void ResetVersionSession();

    void ResetDataSession(string reason)
    {
        dataSessionVersion++;
        snapJob = null; snapForTest = snapTestRunning = false;
        foreach (var r in reqLive.Concat(reqQueue).Concat(reqToUnbind).Distinct().ToList())
        {
            r.Done = true;
            try { var key = r.Key; if (r.Cb != null) BindingUtils.UnbindIfValid(ref key, r.Cb); } catch { }
        }
        reqLive.Clear(); reqQueue.Clear(); reqToUnbind.Clear();
        identityChecks = null; careerCheck = dateReq = slotPollReq = null;
        foreach (var (key, cb) in humanBinds) { try { var k = key; BindingUtils.UnbindIfValid(ref k, cb); } catch { } }
        humanBinds.Clear(); humanScopesBound.Clear(); humanProbed.Clear();
        ClearTarget(); keepAlive.Clear();
        humanKey = 0; humanClub = humanClubId = humanTeamName = humanManager = humanClubSource = null;
        sessionManagerId = sessionClubId = sessionCareer = null;
        squad.Clear(); teamSnap.Clear(); tacticSnap.Clear(); profileSnap.Clear(); hintsSnap.Clear();
        squadContextText = null; squadContextTokens = 0; gameCurrencyName = null;
        slotStore.Clear(); slotStoreLoaded = false; tacticInventoryRead = false; primarySlot = activeSlot = null;
        primarySlotRaw = activeSlotRaw = slotMetaReadAt = lastMergedActiveSlotRaw = null;
        deadPlayerProps.Clear(); tacticScreensSeen.Clear();
        probe.Clear(); probeWritten = tacticsProbeWritten = false; probeFile = null;
        lastGameDate = gameDateKey = null; snapshotAt = default; snapshotStatus = "waiting for the current save";
        nextScheduledSnap = nextDateCheck = nextTacticScreenCheck = nextIdentityCheck = nextTreeHumanSearch = 0;
        lastDateSnap = lastTacticMergeAt = -999f;
        autoDumped = false; lastCandidatesLog = "";
        Chat?.ClearHistory(); chatLines.Clear(); chatChips.Clear(); chatDirty = true;
        ChatDeactivate("session changed"); inputText.Clear(); caret = 0; pendingChars.Clear();
        wasBusy = false; SetThinking(false); if (chatStatus != null) chatStatus.text = "";
        ResetFullSnapshot();
        ResetVersionSession();
        dirty = true;
        CoachPlugin.L.LogInfo("Coach session reset: " + reason);
    }
}

public partial class CoachBehaviour
{
    partial void ResetVersionSession()
    {
        snapTeamTv = null; oppSnap.Clear(); oppForName = null; oppComplete = false; nextOppRetry = 0;
        lastTacticScreenSeen = -999f; nextScreenCheck = 0;
        screenKey = "general"; screenVia = "default"; quickShownKey = null; lastTopRootName = null;
        rootNamesSample.Clear();
    }
}
