using System.Text.Json;

using D2Vpn.Core.Api;

namespace D2Vpn.Core.Tests.Api;

/// <summary>
/// Reading Bungie's JSON, against recorded shapes.
/// </summary>
/// <remarks>
/// No network and no key. The cases worth covering - cross save, a withheld component, three
/// characters with different last-played times - are exactly the ones that cannot be reproduced on
/// demand against the live service, so a recorded body is the only way to have them at all.
/// </remarks>
public class BungieResponseTests
{
    private static JsonElement Json(string text) =>
        JsonDocument.Parse(text).RootElement;

    // ---- the envelope ----

    [Fact]
    public void Success_is_error_code_one()
    {
        Assert.Equal(
            BungieFailure.None,
            BungieResponses.ReadEnvelope(Json("""{ "ErrorCode": 1, "Response": [] }""")));
    }

    [Fact]
    public void A_rejected_key_is_told_apart_from_everything_else()
    {
        // So the window can say "the key was not accepted" rather than "something went wrong",
        // which is the difference between a fixable problem and a mysterious one.
        Assert.Equal(
            BungieFailure.Key,
            BungieResponses.ReadEnvelope(Json("""{ "ErrorCode": 2101 }""")));

        Assert.Equal(
            BungieFailure.Throttled,
            BungieResponses.ReadEnvelope(Json("""{ "ErrorCode": 51 }""")));

        Assert.Equal(
            BungieFailure.Unavailable,
            BungieResponses.ReadEnvelope(Json("""{ "ErrorCode": 5 }""")));
    }

    [Fact]
    public void A_body_that_is_not_an_envelope_is_unavailable()
    {
        Assert.Equal(
            BungieFailure.Unavailable,
            BungieResponses.ReadEnvelope(Json("""{ "nonsense": true }""")));
    }

    // ---- resolving a name ----

    [Fact]
    public void One_platform_resolves_to_that_platform()
    {
        var player = BungieResponses.ReadPlayers(Json("""
        {
          "ErrorCode": 1,
          "Response": [
            {
              "membershipType": 3,
              "membershipId": "4611686018400000001",
              "bungieGlobalDisplayName": "Guardian",
              "bungieGlobalDisplayNameCode": 1234,
              "crossSaveOverride": 0
            }
          ]
        }
        """));

        Assert.Equal(3, player?.MembershipType);
        Assert.Equal("4611686018400000001", player?.MembershipId);
        Assert.Equal("Guardian#1234", player?.DisplayName);
    }

    [Fact]
    public void Cross_save_resolves_to_the_membership_that_holds_the_data()
    {
        // The failure this prevents is silent: the other membership resolves, returns 200, and
        // hands back an empty profile - indistinguishable from a private one. Listed first here
        // on purpose, because picking by position is the thing that happens to work until someone
        // has played on two platforms.
        var player = BungieResponses.ReadPlayers(Json("""
        {
          "ErrorCode": 1,
          "Response": [
            {
              "membershipType": 1,
              "membershipId": "4611686018400000009",
              "bungieGlobalDisplayName": "Guardian",
              "bungieGlobalDisplayNameCode": 1234,
              "crossSaveOverride": 3
            },
            {
              "membershipType": 3,
              "membershipId": "4611686018400000001",
              "bungieGlobalDisplayName": "Guardian",
              "bungieGlobalDisplayNameCode": 1234,
              "crossSaveOverride": 3
            }
          ]
        }
        """));

        Assert.Equal(3, player?.MembershipType);
        Assert.Equal("4611686018400000001", player?.MembershipId);
    }

    [Fact]
    public void No_match_is_no_player()
    {
        Assert.Null(BungieResponses.ReadPlayers(Json("""
        { "ErrorCode": 1, "Response": [] }
        """)));
    }

    // ---- the activity ----

    [Fact]
    public void The_character_with_the_most_recent_start_is_the_one_being_played()
    {
        // All three come back, each with whatever it was last doing. There is no "active
        // character" field, so the most recent start is the only thing that distinguishes the one
        // at the keyboard - and it is listed in the middle here so order cannot be what decides.
        var (snapshot, failure) = BungieResponses.ReadActivity(Json("""
        {
          "ErrorCode": 1,
          "Response": {
            "characterActivities": {
              "data": {
                "2305843009000000001": {
                  "dateActivityStarted": "2026-10-01T09:00:00Z",
                  "currentActivityHash": 0
                },
                "2305843009000000002": {
                  "dateActivityStarted": "2026-10-04T14:31:07Z",
                  "currentActivityHash": 1234567890
                },
                "2305843009000000003": {
                  "dateActivityStarted": "2026-10-03T22:15:00Z",
                  "currentActivityHash": 55
                }
              },
              "privacy": 1
            }
          }
        }
        """));

        Assert.Equal(BungieFailure.None, failure);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 4, 14, 31, 7, TimeSpan.Zero),
            snapshot?.StartedAt);
        Assert.True(snapshot?.InActivity);
    }

    [Fact]
    public void A_withheld_component_is_private_not_an_error()
    {
        // The request succeeded; the component was not given. This is the single most likely
        // reason the API half does nothing for somebody, so it has to be sayable.
        var (snapshot, failure) = BungieResponses.ReadActivity(Json("""
        {
          "ErrorCode": 1,
          "Response": { "characterActivities": { "privacy": 2 } }
        }
        """));

        Assert.Null(snapshot);
        Assert.Equal(BungieFailure.Private, failure);
    }

    [Fact]
    public void Orbit_is_a_reading_rather_than_nothing()
    {
        // Hash 0 is not in an activity. The start time is still the last one, which is why the
        // hash has to be read as well - the time alone cannot say you have left.
        var (snapshot, failure) = BungieResponses.ReadActivity(Json("""
        {
          "ErrorCode": 1,
          "Response": {
            "characterActivities": {
              "data": {
                "2305843009000000001": {
                  "dateActivityStarted": "2026-10-04T14:31:07Z",
                  "currentActivityHash": 0
                }
              },
              "privacy": 1
            }
          }
        }
        """));

        Assert.Equal(BungieFailure.None, failure);
        Assert.NotNull(snapshot);
        Assert.False(snapshot?.InActivity);
    }

    // ---- the name ----

    [Theory]
    [InlineData("Guardian#1234", "Guardian", 1234)]
    [InlineData("  Guardian#0007  ", "Guardian", 7)]
    [InlineData("Two Words#99", "Two Words", 99)]
    [InlineData("has#hash#4321", "has#hash", 4321)]
    public void A_bungie_name_splits_on_the_last_hash(string input, string name, int code)
    {
        var parsed = BungieResponses.ParseBungieName(input);

        Assert.Equal(name, parsed?.Name);
        Assert.Equal(code, parsed?.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Guardian")]
    [InlineData("Guardian#")]
    [InlineData("#1234")]
    [InlineData("Guardian#12345")]
    [InlineData("Guardian#abcd")]
    public void A_name_that_is_not_one_is_refused(string? input)
    {
        Assert.Null(BungieResponses.ParseBungieName(input));
    }

    // ---- the key ----

    [Fact]
    public void A_masked_key_shows_its_ends_only()
    {
        // So the window can say which key it is using without a screenshot carrying it.
        Assert.Equal("none", BungieKey.Mask(null));
        Assert.Equal("****", BungieKey.Mask("abcd"));
        Assert.Equal("abcd********wxyz", BungieKey.Mask("abcd12345678wxyz"));
    }
}
