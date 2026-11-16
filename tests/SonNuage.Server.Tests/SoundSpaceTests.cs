using SonNuage.Server.Analysis;
using SonNuage.Server.Radio;

namespace SonNuage.Server.Tests;

/// <summary>Empreintes sonores (analyse Essentia) : à quel point deux morceaux sonnent pareil.</summary>
public sealed class SoundSpaceTests
{
    private static SoundPrint Print(params float[] values) => SoundPrint.From(values)!;

    [Fact]
    public void A_print_survives_the_trip_through_the_database_and_the_analyzer()
    {
        var print = Print(0.5f, -0.25f, 1f);
        var bytes = print.ToBytes();

        Assert.Equal(print.Values, SoundPrint.FromBytes(bytes)!.Values);
        Assert.Equal(print.Values, SoundPrint.FromBase64(Convert.ToBase64String(bytes))!.Values);
        Assert.Null(SoundPrint.FromBytes([1, 2, 3]));
        Assert.Null(SoundPrint.FromBase64("pas du base64"));
        Assert.Null(SoundPrint.FromBytes(null));
    }

    [Fact]
    public void What_every_track_shares_does_not_make_two_tracks_alike()
    {
        // Toutes les empreintes partagent une grosse composante commune : sans la retirer, tout se ressemble.
        Guid a = Guid.NewGuid(), nearA = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var space = SoundSpace.Create(new Dictionary<Guid, SoundPrint>
        {
            [a] = Print(10.3f, 10f, 10f, 10f),
            [nearA] = Print(10.3f, 10.05f, 10f, 10f),
            [b] = Print(10f, 10.3f, 10f, 10f),
            [c] = Print(10f, 10f, 10.3f, 10f),
        });

        Assert.Equal(1, space.Similarity(a, a)!.Value, 3);
        Assert.True(space.Similarity(a, nearA) > 0.8);
        Assert.True(space.Similarity(a, b) < 0.2);
        Assert.Null(space.Similarity(a, Guid.NewGuid()));
    }
}
