# Local security boundaries (sessions, keys, management surfaces)

This is the operator reference for the boundaries a self-hosted QtsBilling instance enforces at start-up and on
every request (specification task 09, requirements R01/R03/R04/R13, finding F07). Everything below is enforced by
code in `QtsWebApp/QtsWebApp/Infrastructure/LocalSecurityPolicy.cs` and proven by
`TestQtsBilling/Integration/LocalSecurityPolicyTests.cs`; nothing here is a convention an installer has to remember.

## 1. What a deployed instance must have configured

Outside `Development` and the automated `Testing` environment the host refuses to start — before it serves any
business data — unless every item below is present. Each unmet item is written to the log by name (never by value)
and the refusal message lists them all.

| Configuration | Requirement | Why it is not optional |
|---|---|---|
| `QTS_INSTANCE_ORIGIN` | Absolute `https://` URL naming the server's real host name. | A loopback alias cannot be the identity other terminals and devices must trust, and cleartext is not an owned origin. |
| `QTS_DATAPROTECTION_PATH` | An existing private directory, **outside** the application/web root. | Session, anti-forgery and reset-ticket keys must survive a restart and a re-deploy. Keys inside the app directory are servable and get overwritten by the next deployment. |
| `QTS_OPENIDDICT_CERTIFICATE_PATH` (+ `..._PASSWORD`) | The reviewed token signing/encryption certificate, readable from disk. | A deployed host never signs access or refresh tokens with an ephemeral development key. |
| `QTS_OFFLINE_GRANT_PRIVATE_KEY_PATH` | The reviewed offline-grant signing key (PEM). | Offline sales stay verifiable across a host restart; see section 3. |
| `ConnectionStrings:BillingDb` or `QTS_POSTGRES_CONNECTION` | A real credential-bearing PostgreSQL connection. | There is no built-in development default to fall back on. |
| `QTS_TRUSTED_PROXY_HOSTS` (optional) | Comma-separated **literal IP addresses** of the local proxy, if any. | A forwarded `X-Forwarded-For` is believed only from a reviewed address, so a client cannot forge the address the rate limiter and the metrics boundary partition on. Wildcards, ranges and URLs are refused. |
| `QTS_ALLOWED_HOSTS` (optional) | Extra host names to answer for (a mDNS or LAN alias). | The Host header is filtered to the owned origin plus these names. With no owned origin configured there is nothing to filter, so development and the harness are unaffected. |

Verification:

```powershell
dotnet test TestQtsBilling/TestQtsBilling.csproj -c Release --filter "FullyQualifiedName~LocalSecurityPolicyTests"
```

## 2. Key inventory, purposes and separation

| Key | Where it lives | What it protects |
|---|---|---|
| Data Protection ring | XML key files under `QTS_DATAPROTECTION_PATH` | The authentication session cookie, the anti-forgery cookie/request-token pair, and the Data Protection token provider that issues password-reset tickets. Application discriminator: `QtsBilling`. |
| OIDC certificate | `QTS_OPENIDDICT_CERTIFICATE_PATH` (PFX) | Access, refresh and identity tokens for the native PKCE client. |
| Offline-grant key | `QTS_OFFLINE_GRANT_PRIVATE_KEY_PATH` (PEM) | The signed device grant (`POST /api/v1/devices/{id}/offline-grant`). Devices verify with the public half published at `GET /api/v1/devices/offline-grant-key`. |
| TLS listener certificate | The OS/web-server store | The locally trusted HTTPS origin terminals and devices connect to. |

Purposes are **separate even inside one ring**: a payload protected for `QtsBilling.Session` cannot be read by a
protector with another purpose. Setup tickets and password-reset verifiers are stored as SHA-256 hashes rather than
as ring-protected blobs, so a leaked ring does not hand out account access.

## 3. Restart survival and rotation overlap

*Restart.* With `QTS_DATAPROTECTION_PATH` set, the ring is read from disk, so a restart keeps every issued session
cookie, anti-forgery cookie and pending reset ticket valid. Without it — in `Development`/`Testing` only — the ring
is ephemeral and those values die with the process. A deployed host never gets that fallback: it is refused at
start-up instead.

*Rotation is an overlap, not a swap.* Keep the previous key material readable for as long as anything it produced
can still be presented:

| Artifact | Validity to overlap | Practical rule |
|---|---|---|
| Data Protection key | Key ring default lifetime (90 days) and the 8-hour sliding session cookie | Add the new key to the ring and leave the old key files in place; the ring keeps them side by side. Delete only after the last ticket/session that any of them protected has expired. |
| Offline-grant key | The grant's own lifetime: 8 hours default, 24 hours maximum | Publish the replacement public half at `/api/v1/devices/offline-grant-key`, keep the retired private key file until every grant signed by it has expired or been revoked, then retire the device grant rather than the key first. |
| OIDC certificate | Access tokens 15 minutes, refresh tokens 8 hours | Install the new certificate, keep the old one available for verification for the full refresh-token lifetime before removing it. |

Do not delete old key files to "clean up": an early deletion invalidates live sessions, in-flight reset tickets and
offline grants that devices still hold. Nothing in the application rotates a key automatically — rotation is a
reviewed operator action, exactly so that it appears in the change record.

## 4. Backup

The ring directory, the OIDC certificate and the offline-grant key are part of the instance's **file** backup and
must be restorable together with the database; `scripts/backup-postgres.ps1` covers the database half only. A
restored database with a lost ring forces every operator to sign in again and invalidates outstanding reset tickets;
with a lost grant key, enrolled devices lose offline authority until each device is re-granted.

Key material is never committed: the secret scan (`pwsh scripts/secret-scan.ps1`) and the start-up refusal on a ring
inside the application directory are the two guards.

## 5. Anti-forgery boundary

* Cookie-authenticated mutations require the session proof in the `X-QTS-CSRF` header. The proof is issued by the
  same-origin `GET /api/v1/auth/csrf-token` (anonymous, because signing in is itself a mutation; the response is
  `no-store`) together with the separate `qts.csrf` cookie, which is `HttpOnly` and `SameSite=Strict`.
* Signing in is checked even though the caller has no session yet: login CSRF is a forgery, not a non-event.
* The proof never travels in a query string, and a foreign `Origin` (or `Referer`) on a mutation is refused exactly
  like a missing proof, so the rejection shape does not leak which check fired.
* Native bearer requests are never asked for a cookie proof and never receive one. OpenIddict's own
  `/connect/*` and `/.well-known/*` protocol endpoints are excluded so the PKCE flow stays token-free; their
  request integrity comes from the PKCE verifier, the `state` parameter and the registered redirect-address
  allowlist. The residual risk — a forged authorization POST could only deliver its code to a registered
  loopback redirect the attacker does not control — is accepted deliberately and is not a general exemption for
  business endpoints.
* The browser client caches one proof per session and, if the server rejects it (a session that changed after the
  proof was taken), re-fetches it and replays the identical request once. The replay is safe because a rejected
  mutation never reached the endpoint, and it keeps the command's `Idempotency-Key` and body unchanged.

## 6. Management surface

`/metrics` is off unless `QTS_METRICS_ENABLED=true`, and then answers only:

* a loopback caller, or the operator's own machine; or
* a caller whose **current** membership holds `audit.view` — the permission is re-read from the database, not
  trusted from the issued claim, so a withdrawn role stops scraping immediately.

Anybody else receives `404`, identical to the endpoint not existing, so the surface cannot be probed. The scraping
endpoint sits after exception, correlation, authentication and authorization handling, not before them.

## 7. What this does not claim

SameSite/Strict and the anti-forgery proof both apply; neither is described as sufficient on its own. Rate limiting
still normalizes the account identifier and does not accept an untrusted header as the partition key. No credential,
key or token value is ever written to a log, a problem detail or this document.
