# ADR 0006: Signed short-lived session token with key rotation, instead of a static key

## Context

Integrators call the API on behalf of end users. A static API key per integrator identifies the integrator, but it
cannot be handed to a browser or an app without exposing it, and it says nothing about which conversation a request
belongs to. Passing the key with every chat request would also make a leaked key a long-term credential for every
session. A signing key that can never be replaced is a second problem: rotating it would log every user out.

## Decision

Two credentials with two jobs:

- `X-Client-Key` (an integrator key, compared in constant time against every configured key without early exit)
  authorises only `POST /chat/start` and `GET /tools`. It is rate limited per key.
- `POST /chat/start` mints a session token: `v2.<kid>.<payload>.<signature>`, where the payload carries the session id
  and an expiry and the signature is HMAC-SHA256 over everything before it, key id included. Every other chat endpoint
  requires `Authorization: Bearer <token>`. The token is verified without a lookup, is bound to exactly one session, and
  expires after a configurable lifetime (default 120 minutes) regardless of activity.

**Key rotation.** The current key has an id (`Auth:SessionTokenKeyId`, default `k1`) and signs every new token.
`Auth:PreviousSessionTokenKeys` lists earlier keys, each with an id, the key and a mandatory `RetiresAt`. A previous
key verifies tokens until that instant and never signs. The `kid` selects the key, so there is no trial-and-error
across keys: an unknown id, a retired key or a token in the earlier id-less `v1` format is rejected like any invalid
token. Startup validation refuses duplicate ids, keys shorter than 32 characters, a previous key equal to another key,
and a previous key without a retirement.

To rotate: add the old key under `PreviousSessionTokenKeys` with `RetiresAt` at least one token lifetime ahead, set a
new `SessionTokenKeyId` and `SessionTokenSigningKey`, deploy; remove the previous key once it has retired.

Separately, a session is considered idle-expired after `Session:IdleTtlMinutes` (default 30) and answers 410. Tokens
are truncated in logs and never logged in full.

## Consequences

- The token can be given to a client; the integrator key stays on the integrator's server.
- A stolen token is limited to one session and a bounded time.
- There is no token revocation list: deleting the session (`DELETE /chat/session`) is the revocation, because every
  request checks that the session still exists.
- The signing keys are secrets that must come from configuration or the environment. Rotation is planned and
  gradual; replacing a key that leaked is not, because a leaked previous key keeps verifying until its `RetiresAt`.
  For a compromise, drop the key at once and accept that its tokens stop working.
- Keys are read at startup, so a rotation takes effect with a restart; there is no key-management service behind it.
