using SatTrack.Core.Orbit;

namespace SatTrack.Core.Catalog;

/// <summary>Web pages with information about a satellite.</summary>
public enum InfoSite
{
    /// <summary>N2YO: description, live position, upcoming passes.</summary>
    N2yo,

    /// <summary>CelesTrak SATCAT: the official catalog record (launch, owner, orbit, status).</summary>
    CelestrakCatalog,

    /// <summary>The orbital element data this app is using, from its original source.</summary>
    OrbitalElements,

    /// <summary>AMSAT's live status page for amateur satellites (which ones are working).</summary>
    AmsatStatus,
}

public static class SatelliteLinks
{
    public static string Title(InfoSite site) => site switch
    {
        InfoSite.N2yo => "N2YO: description and live position",
        InfoSite.CelestrakCatalog => "CelesTrak: satellite catalog record",
        InfoSite.OrbitalElements => "Orbital elements this app is using",
        InfoSite.AmsatStatus => "AMSAT: amateur satellite status",
        _ => site.ToString(),
    };

    public static string ShortName(InfoSite site) => site switch
    {
        InfoSite.N2yo => "N2YO",
        InfoSite.CelestrakCatalog => "CelesTrak",
        InfoSite.OrbitalElements => "elements",
        InfoSite.AmsatStatus => "AMSAT status",
        _ => site.ToString(),
    };

    /// <summary>The page for this satellite, or null when there isn't one (e.g. no catalog number).</summary>
    public static string? Url(InfoSite site, SatelliteEntry entry, ElementSet? elements)
    {
        int id = entry.NoradId > 0 ? entry.NoradId : elements?.NoradId ?? 0;
        return site switch
        {
            InfoSite.N2yo when id > 0 => $"https://www.n2yo.com/satellite/?s={id}",
            InfoSite.CelestrakCatalog when id > 0 => $"https://celestrak.org/satcat/table-satcat.php?CATNR={id}",
            InfoSite.OrbitalElements when elements?.Source == "AMSAT" => OrbitalElementsService.AmsatUrl,
            InfoSite.OrbitalElements when entry.HasInlineElements => null, // they're in the user's own list file
            InfoSite.OrbitalElements when id > 0 => $"https://celestrak.org/NORAD/elements/gp.php?CATNR={id}&FORMAT=TLE",
            InfoSite.AmsatStatus => "https://www.amsat.org/status/",
            _ => null,
        };
    }
}
