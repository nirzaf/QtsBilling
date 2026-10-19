# Security Policy

## Reporting a Vulnerability

**Please do NOT report security vulnerabilities through public GitHub issues.**

If you believe you have found a security vulnerability in QtsBilling, please report it to us
responsibly so we can address it as quickly as possible.

### How to report

Use **one** of the following private channels:

1. GitHub's **private vulnerability reporting** — open a new draft security advisory via the
   repository's **Security** tab:
   `https://github.com/nirzaf/QtsBilling/security/advisories/new`
2. Contact the maintainer, **M.F.M Fazrin ([@nirzaf](https://github.com/nirzaf))**, using the
   contact details on their GitHub profile.

### What to include

- A description of the vulnerability and its potential impact
- Step-by-step instructions to reproduce it
- The affected version(s) or commit(s)
- Any suggested fix or mitigation, if you have one

### What to expect

- We will acknowledge receipt of your report as soon as we can.
- We will investigate and keep you informed of our progress.
- We will work on a fix and agree on a disclosure timeline with you.
- Please **do not** disclose the vulnerability publicly until we have addressed it.

We appreciate your help in keeping QtsBilling and its users safe. 🙏

## Supported Versions

QtsBilling is under active development. Security fixes are applied to the latest `master` branch.

| Version | Supported |
| --- | --- |
| Latest `master` | ✅ |
| Older commits | ⚠️ Best effort |

## Handling Secrets

QtsBilling reads all sensitive configuration (database connection strings, encryption keys) from
**environment variables** — never from source control. See the
[Configuration](README.md#configuration) section of the README and [`.env.example`](.env.example).

If a secret was ever committed to this repository, treat it as compromised and **rotate it
immediately**. Removing a value from the current source does not remove it from Git history.
