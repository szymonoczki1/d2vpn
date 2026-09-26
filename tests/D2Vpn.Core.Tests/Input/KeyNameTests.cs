using D2Vpn.Core.Input;

namespace D2Vpn.Core.Tests.Input;

/// <summary>
/// The names that matter are the ones already in use: existing AHK scripts send CapsLock for a
/// 3074 uplink hold and trigger on backslash, and a reference limiter binds Mouse4, F9, F10, F11.
/// </summary>
public class KeyNameTests
{
    [Theory]
    [InlineData("capslock", 0x14)]
    [InlineData("CapsLock", 0x14)]
    [InlineData("  capslock  ", 0x14)]
    [InlineData("mouse4", 0x05)]
    [InlineData("f9", 0x78)]
    [InlineData("f1", 0x70)]
    [InlineData("f12", 0x7B)]
    [InlineData("backslash", 0xDC)]
    public void Known_names_parse_to_their_virtual_key(string name, int expected) =>
        Assert.Equal(expected, KeyName.Parse(name));

    [Theory]
    [InlineData("a", 'A')]
    [InlineData("Z", 'Z')]
    [InlineData("5", '5')]
    [InlineData("0", '0')]
    public void Single_characters_parse_to_their_code(string name, int expected) =>
        Assert.Equal(expected, KeyName.Parse(name));

    [Fact]
    public void A_raw_virtual_key_code_is_accepted()
    {
        // So a key with no name here is still bindable.
        Assert.Equal(20, KeyName.Parse("20"));
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("f99")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("999")]
    public void Unrecognised_names_are_rejected(string name) =>
        Assert.ThrowsAny<ArgumentException>(() => KeyName.Parse(name));

    [Theory]
    [InlineData(0x14, "capslock")]
    [InlineData(0x78, "f9")]
    [InlineData('A', "a")]
    public void Codes_render_back_to_a_name(int code, string expected) =>
        Assert.Equal(expected, KeyName.Describe(code));

    [Fact]
    public void Every_named_key_round_trips()
    {
        foreach (var name in new[] { "capslock", "mouse4", "mouse5", "f9", "f11", "backslash", "space" })
        {
            Assert.Equal(name, KeyName.Describe(KeyName.Parse(name)));
        }
    }
}
