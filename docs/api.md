# The Bungie API half of the instance timer

The traffic timer (`InstanceWatcher`, docs/protocol.md finding 50 onward) knows *that* the instance
changed, the moment it changes. It cannot know when the activity began on the server, because it
only sees the connection - which is a second or two later, and after a loading screen, more.

That one number is the only thing the API is for here. It is a **correction, not a source**: every
failure below leaves the traffic figure standing, and the timer works with no key, no account and
no network.

## What was verified, and how

Unlike most of `docs/protocol.md`, none of this is measured from captures. It is read from Bungie's
own published OpenAPI document and from the schema pages, which is a stronger source than
measurement for this kind of question - it is the contract rather than one observation of it.

Fetched from `https://raw.githubusercontent.com/Bungie-net/api/master/openapi.json`, 2026-10-04.

| Question | Answer | How |
|---|---|---|
| Does `GetProfile` need OAuth? | No. `security` is absent on the operation. | OpenAPI document |
| Does `SearchDestinyPlayerByBungieName` need OAuth? | No, same. | OpenAPI document |
| Is there a start time at all? | `dateActivityStarted`, "The last date that the user started playing an activity." | `DestinyCharacterActivitiesComponent` schema |
| How do you tell orbit from an activity? | `currentActivityHash` is 0 outside one. | same schema |

So: **an API key in the `X-API-Key` header is enough.** No OAuth, no token refresh, no redirect
URL, no browser.

What the key does *not* get past is the account's own privacy. Component 204 is returned only if
"you are the currently authenticated user or the user has elected to allow anonymous viewing of its
progression info". Anonymous viewing of advisor data is public by default, so this works for most
people without changing anything - but when it does not, the response is a success with the
component simply missing, which is why `BungieResponses.ReadActivity` reports that case as
`Private` rather than as an error.

## The two calls

```
POST /Destiny2/SearchDestinyPlayerByBungieName/-1/     {displayName, displayNameCode}
GET  /Destiny2/{membershipType}/Profile/{id}/?components=204
```

`-1` is "search every platform", which makes one call enough whoever is asking.

### Cross save is the trap

With cross save on, several platform memberships resolve for one person and **only one of them
carries the data**. The others return HTTP 200 and an empty profile - indistinguishable from a
private one, and with nothing in the response saying why.

`crossSaveOverride` names the platform that holds it, and is 0 when cross save is off. Picking the
first entry in the list happens to work until somebody has played on two platforms, which is
exactly the kind of bug that appears only for other people. `BungieResponses.ReadPlayers` chooses
by the override, and there is a test with the wrong membership listed first.

### There is no "active character"

All three characters come back, each with whatever it was last doing. The one being played is the
one with the most recent `dateActivityStarted`, and that is the only thing distinguishing it.

## Polling

A change starts a burst of attempts; the first that lands ends it until the next change. An
activity twenty minutes long costs a handful of calls rather than eighty.

The retry is not politeness, it is necessary: the API's current activity **lags**. For the first
seconds after entering somewhere it still describes the last place. A correction taken from that
would drag the timer backwards into an activity already over, so `InstanceWatcher.Correct` refuses
anything more than two minutes from the traffic start and the burst simply asks again.

Default interval is 15 s, which is what the prior art settled on for the same call.

## The key is not in the config file

It lives in `%APPDATA%\d2vpn\bungie.key`, beside `d2vpn.json` but not inside it. The config is the
thing people paste into a thread when asking why a module will not start, and the thing a
distributed zip would most plausibly ship with sensible defaults already set. One file to hold back
is easier to remember than one field to redact.

**No key is compiled in.** The prior art ships one in its source; a shipped key is one revocation
away from breaking for every user at once, and until then they all share its rate limit. A key is
free and instant from `bungie.net/developer`.

## Sources

- <https://bungie-net.github.io/> - the generated API documentation
- <https://github.com/Bungie-net/api> - the OpenAPI document itself
- <https://bungie-net.github.io/multi/schema_Destiny-DestinyComponentType.html> - component 204 and
  its privacy wording
