namespace SonNuage.Server.Analysis;

/// <summary>
/// Empreinte sonore d'un morceau : la moyenne des représentations Discogs-EffNet de l'extrait analysé
/// (essentia_analyzer.py), en demi-précision. Deux morceaux qui sonnent pareil ont des empreintes proches
/// (<see cref="Radio.SoundSpace"/>).
/// </summary>
public sealed class SoundPrint
{
    /// <summary>Au-delà, ce n'est pas une empreinte de nos modèles (EffNet : 1280 dimensions).</summary>
    public const int MaxDimensions = 4096;

    private SoundPrint(float[] values) => Values = values;

    public float[] Values { get; }

    public static SoundPrint? From(float[] values) =>
        values.Length is > 0 and <= MaxDimensions && values.All(float.IsFinite) ? new SoundPrint(values) : null;

    public static SoundPrint? FromBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length % 2 != 0) return null;
        var values = new float[bytes.Length / 2];
        for (var i = 0; i < values.Length; i++) values[i] = (float)BitConverter.ToHalf(bytes, 2 * i);
        return From(values);
    }

    public static SoundPrint? FromBase64(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var buffer = new byte[text.Length];
        return Convert.TryFromBase64String(text, buffer, out var written) ? FromBytes(buffer[..written]) : null;
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[Values.Length * 2];
        for (var i = 0; i < Values.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(2 * i), (Half)Values[i]);
        return bytes;
    }
}
