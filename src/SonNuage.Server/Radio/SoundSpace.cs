using SonNuage.Server.Analysis;

namespace SonNuage.Server.Radio;

/// <summary>
/// Ressemblance sonore entre les morceaux de la bibliothèque : cosinus de leurs empreintes, une fois retiré ce que
/// toutes partagent (la moyenne de la bibliothèque). Sans ce centrage, les empreintes EffNet, toutes positives, se
/// ressemblent toutes.
/// </summary>
public sealed class SoundSpace
{
    public static readonly SoundSpace Empty = new(new Dictionary<Guid, float[]>());

    /// <summary>Empreintes centrées et normées : le cosinus n'est plus qu'un produit scalaire.</summary>
    private readonly IReadOnlyDictionary<Guid, float[]> _units;

    private SoundSpace(IReadOnlyDictionary<Guid, float[]> units) => _units = units;

    public int Count => _units.Count;

    public static SoundSpace Create(IReadOnlyDictionary<Guid, SoundPrint> prints)
    {
        // Une seule taille d'empreinte : la plus courante (une version d'analyse à la fois, en principe).
        var size = prints.Values.GroupBy(p => p.Values.Length).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? 0;
        if (size == 0) return Empty;
        var kept = prints.Where(p => p.Value.Values.Length == size).ToList();
        var center = new double[size];
        foreach (var (_, print) in kept)
            for (var i = 0; i < size; i++) center[i] += print.Values[i];
        for (var i = 0; i < size; i++) center[i] /= kept.Count;

        var units = new Dictionary<Guid, float[]>(kept.Count);
        foreach (var (id, print) in kept)
        {
            var unit = new float[size];
            double norm = 0;
            for (var i = 0; i < size; i++)
            {
                var v = print.Values[i] - center[i];
                unit[i] = (float)v;
                norm += v * v;
            }
            if (norm <= 1e-12) continue; // l'empreinte moyenne elle-même : ne ressemble à rien en particulier
            var scale = (float)(1 / Math.Sqrt(norm));
            for (var i = 0; i < size; i++) unit[i] *= scale;
            units[id] = unit;
        }
        return new SoundSpace(units);
    }

    public bool Knows(Guid track) => _units.ContainsKey(track);

    /// <summary>De -1 (opposés) à 1 (identiques) ; null si l'un des deux n'a pas d'empreinte.</summary>
    public double? Similarity(Guid a, Guid b)
    {
        if (!_units.TryGetValue(a, out var x) || !_units.TryGetValue(b, out var y)) return null;
        double dot = 0;
        for (var i = 0; i < x.Length; i++) dot += x[i] * y[i];
        return Math.Clamp(dot, -1, 1);
    }
}
