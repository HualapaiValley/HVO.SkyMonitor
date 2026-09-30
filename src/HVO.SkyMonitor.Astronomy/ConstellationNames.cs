using System.Collections.Frozen;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// The 88 IAU constellations keyed by their three-letter abbreviation, so a figure identified by its abbreviation
/// can be shown by name.
/// </summary>
/// <remarks>
/// Lookup ignores case because topology sources differ: D3-Celestial writes <c>UMA</c> where the IAU writes
/// <c>UMa</c>. Serpens is one constellation with one abbreviation even though its figure is drawn in two parts.
/// </remarks>
public static class ConstellationNames
{
    private static readonly FrozenDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["And"] = "Andromeda",
        ["Ant"] = "Antlia",
        ["Aps"] = "Apus",
        ["Aql"] = "Aquila",
        ["Aqr"] = "Aquarius",
        ["Ara"] = "Ara",
        ["Ari"] = "Aries",
        ["Aur"] = "Auriga",
        ["Boo"] = "Boötes",
        ["Cae"] = "Caelum",
        ["Cam"] = "Camelopardalis",
        ["Cap"] = "Capricornus",
        ["Car"] = "Carina",
        ["Cas"] = "Cassiopeia",
        ["Cen"] = "Centaurus",
        ["Cep"] = "Cepheus",
        ["Cet"] = "Cetus",
        ["Cha"] = "Chamaeleon",
        ["Cir"] = "Circinus",
        ["CMa"] = "Canis Major",
        ["CMi"] = "Canis Minor",
        ["Cnc"] = "Cancer",
        ["Col"] = "Columba",
        ["Com"] = "Coma Berenices",
        ["CrA"] = "Corona Australis",
        ["CrB"] = "Corona Borealis",
        ["Crt"] = "Crater",
        ["Cru"] = "Crux",
        ["Crv"] = "Corvus",
        ["CVn"] = "Canes Venatici",
        ["Cyg"] = "Cygnus",
        ["Del"] = "Delphinus",
        ["Dor"] = "Dorado",
        ["Dra"] = "Draco",
        ["Equ"] = "Equuleus",
        ["Eri"] = "Eridanus",
        ["For"] = "Fornax",
        ["Gem"] = "Gemini",
        ["Gru"] = "Grus",
        ["Her"] = "Hercules",
        ["Hor"] = "Horologium",
        ["Hya"] = "Hydra",
        ["Hyi"] = "Hydrus",
        ["Ind"] = "Indus",
        ["Lac"] = "Lacerta",
        ["Leo"] = "Leo",
        ["Lep"] = "Lepus",
        ["Lib"] = "Libra",
        ["LMi"] = "Leo Minor",
        ["Lup"] = "Lupus",
        ["Lyn"] = "Lynx",
        ["Lyr"] = "Lyra",
        ["Men"] = "Mensa",
        ["Mic"] = "Microscopium",
        ["Mon"] = "Monoceros",
        ["Mus"] = "Musca",
        ["Nor"] = "Norma",
        ["Oct"] = "Octans",
        ["Oph"] = "Ophiuchus",
        ["Ori"] = "Orion",
        ["Pav"] = "Pavo",
        ["Peg"] = "Pegasus",
        ["Per"] = "Perseus",
        ["Phe"] = "Phoenix",
        ["Pic"] = "Pictor",
        ["PsA"] = "Piscis Austrinus",
        ["Psc"] = "Pisces",
        ["Pup"] = "Puppis",
        ["Pyx"] = "Pyxis",
        ["Ret"] = "Reticulum",
        ["Scl"] = "Sculptor",
        ["Sco"] = "Scorpius",
        ["Sct"] = "Scutum",
        ["Ser"] = "Serpens",
        ["Sex"] = "Sextans",
        ["Sge"] = "Sagitta",
        ["Sgr"] = "Sagittarius",
        ["Tau"] = "Taurus",
        ["Tel"] = "Telescopium",
        ["TrA"] = "Triangulum Australe",
        ["Tri"] = "Triangulum",
        ["Tuc"] = "Tucana",
        ["UMa"] = "Ursa Major",
        ["UMi"] = "Ursa Minor",
        ["Vel"] = "Vela",
        ["Vir"] = "Virgo",
        ["Vol"] = "Volans",
        ["Vul"] = "Vulpecula"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the number of constellations known by name.</summary>
    public static int Count => Names.Count;

    /// <summary>Returns the constellation's name, or <see langword="null"/> for an unrecognized abbreviation.</summary>
    public static string? Find(string? abbreviation)
        => abbreviation is not null && Names.TryGetValue(abbreviation, out var name) ? name : null;
}
