using OpenIPC.Viewer.Core.Onvif;

namespace OpenIPC.Viewer.Core.Tests.Onvif;

// Preset names arrive mangled from cameras that store UTF-8 but label the
// response Latin-1. The repair has to be certain in both directions: recover a
// mangled name, and never touch one that was already right.
//
// The mangled forms are written as escapes rather than pasted: several of the
// bytes involved land in the C1 control range and would not survive a copy
// through a terminal or an editor.
public sealed class OnvifTextTests
{
    private const string Entrance = "\u0412\u0445\u043E\u0434";              // Вход
    private const string EntranceViaLatin1 = "\u00D0\u0092\u00D1\u0085\u00D0\u00BE\u00D0\u00B4";
    private const string Gate = "\u0412\u043E\u0440\u043E\u0442\u0430";                    // Ворота
    private const string GateViaCp1252 = "\u00D0\u2019\u00D0\u00BE\u00D1\u20AC\u00D0\u00BE\u00D1\u201A\u00D0\u00B0";
    private const string Cjk = "\u5165\u53E3";

    [Fact]
    public void AnAsciiNameIsUntouched() =>
        Assert.Equal("Gate 1", OnvifText.RepairMojibake("Gate 1"));

    [Fact]
    public void EmptyInputIsReturnedAsIs() =>
        Assert.Equal("", OnvifText.RepairMojibake(""));

    // UTF-8 bytes read back as Latin-1 — the common case.
    [Fact]
    public void ALatin1MisreadIsRecovered() =>
        Assert.Equal(Entrance, OnvifText.RepairMojibake(EntranceViaLatin1));

    // The same damage through Windows-1252, which differs from Latin-1 only in
    // 0x80-0x9F and is what a good number of firmwares actually use. This name
    // encodes to three bytes in that band, so handling Latin-1 alone leaves it
    // unrepaired.
    [Fact]
    public void ACp1252MisreadIsAlsoRecovered() =>
        Assert.Equal(Gate, OnvifText.RepairMojibake(GateViaCp1252));

    // A name that is already correct must survive. Read back as bytes it is not
    // valid UTF-8, which is how the repair knows to stand down.
    [Fact]
    public void AProperlyDecodedNameIsNotMangledFurther()
    {
        Assert.Equal(Entrance, OnvifText.RepairMojibake(Entrance));
        Assert.Equal(Gate, OnvifText.RepairMojibake(Gate));
    }

    // Characters outside what either decoder can emit cannot have come from
    // one, so the name is left alone rather than guessed at.
    [Fact]
    public void TextThatCouldNotHaveComeFromEitherDecoderIsLeftAlone() =>
        Assert.Equal(Cjk, OnvifText.RepairMojibake(Cjk));

    // The ambiguous class: a decode made purely of Latin-1 *symbols*.
    // "\u00C2\u00A9" would decode to "\u00A9" (©), and both readings are
    // plausible as intentional text, so the original stands.
    [Theory]
    [InlineData("\u00C2\u00A9")]
    [InlineData("\u00C2\u00B0C")]
    public void ASymbolOnlyDecode_IsAmbiguousAndLeftAlone(string name) =>
        Assert.Equal(name, OnvifText.RepairMojibake(name));

    // A decode containing a Latin-1 *letter* is not ambiguous — "Ã" chased by
    // a currency sign is a pair nobody types on purpose. This keeps French,
    // German and the â/ê/ô half of Vietnamese repairable even though those
    // letters live inside Latin-1.
    [Theory]
    [InlineData("Entr\u00C3\u00A9e", "Entr\u00E9e")]
    [InlineData("S\u00C3\u00A2n", "S\u00E2n")]
    public void ADecodeWithLatin1Letters_IsRepaired(string mangled, string expected) =>
        Assert.Equal(expected, OnvifText.RepairMojibake(mangled));
}
