using System;
using System.Collections.Generic;
using System.Linq;
using OpenIPC.Viewer.Core.Entities;

namespace OpenIPC.Viewer.Core.LinkHealth;

/// <summary>What the drop history says about where the fault sits.</summary>
public enum FailureDomainVerdict
{
    // Fewer than two drops on record — nothing to correlate yet.
    NotEnoughData = 0,
    // Cameras drop together: the fault is on the part of the path they share
    // (this computer's port, the PoE injector, the cables and adapters up to the
    // split point, the splitter/PoE switch itself, or the shared power budget).
    SharedSegment,
    // Cameras drop on their own: the fault is on each one's own branch (its cable
    // after the split, its plug, or the camera).
    IndividualBranches,
    // Both kinds of drop are common.
    Mixed,
}

/// <summary>Several drops close together in time.</summary>
public sealed record FailureIncident(DateTimeOffset At, IReadOnlyList<CameraId> Cameras);

/// <summary>How one camera's drops split into solo and shared ones.</summary>
public sealed record CameraDropProfile(CameraId CameraId, int Drops, int SoloDrops, int SharedDrops);

public sealed record FailureDomainReport(
    FailureDomainVerdict Verdict,
    int Incidents,
    int SharedIncidents,
    IReadOnlyList<CameraDropProfile> Cameras,
    IReadOnlyList<FailureIncident> RecentShared);

// "Topology search" that the data can support: which cameras fail TOGETHER.
// Drops of live streams within a short window of each other are one incident;
// an incident spanning several cameras points at the segment they share, a
// solo incident at one camera's own branch. That locates the faulty segment of
// the PoE chain without any per-pin measurement.
public static class FailureDomainAnalyzer
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(20);

    public static FailureDomainReport Analyze(IEnumerable<ConnectionEvent> events, TimeSpan? window = null)
    {
        var w = window ?? DefaultWindow;
        var drops = events
            .Where(e => e.Kind == ConnectionEventKind.Dropped)
            .OrderBy(e => e.At)
            .ToList();

        var incidents = new List<FailureIncident>();
        var i = 0;
        while (i < drops.Count)
        {
            var start = drops[i].At;
            var cams = new List<CameraId>();
            while (i < drops.Count && drops[i].At - start <= w)
            {
                if (!cams.Contains(drops[i].CameraId))
                    cams.Add(drops[i].CameraId);
                i++;
            }
            incidents.Add(new FailureIncident(start, cams));
        }

        var profiles = drops
            .Select(d => d.CameraId)
            .Distinct()
            .Select(id =>
            {
                var mine = incidents.Where(x => x.Cameras.Contains(id)).ToList();
                var shared = mine.Count(x => x.Cameras.Count > 1);
                return new CameraDropProfile(id, mine.Count, mine.Count - shared, shared);
            })
            .OrderByDescending(p => p.Drops)
            .ToList();

        var sharedIncidents = incidents.Count(x => x.Cameras.Count > 1);
        var soloIncidents = incidents.Count - sharedIncidents;

        FailureDomainVerdict verdict;
        if (drops.Count < 2)
            verdict = FailureDomainVerdict.NotEnoughData;
        else if (sharedIncidents >= 2 && sharedIncidents * 2 >= incidents.Count && soloIncidents * 3 < incidents.Count)
            verdict = FailureDomainVerdict.SharedSegment;
        else if (sharedIncidents >= 2)
            verdict = FailureDomainVerdict.Mixed;
        else if (soloIncidents >= 2)
            verdict = FailureDomainVerdict.IndividualBranches;
        else
            verdict = FailureDomainVerdict.NotEnoughData;

        var recentShared = incidents
            .Where(x => x.Cameras.Count > 1)
            .OrderByDescending(x => x.At)
            .Take(10)
            .ToList();

        return new FailureDomainReport(verdict, incidents.Count, sharedIncidents, profiles, recentShared);
    }
}
