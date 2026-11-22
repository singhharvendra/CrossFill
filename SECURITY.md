# Security policy

## Supported versions

The latest released minor version receives security fixes.

## Reporting a vulnerability

Please **do not open a public issue.** Use GitHub's
[private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
on this repository (Security → Report a vulnerability).

Include the version, a description of the impact and steps to reproduce. You
should get an acknowledgement within 5 working days. Once a fix is released, the
advisory is published and you are credited unless you'd rather not be.

## Security model

* CrossFill runs a real browser on the user's machine with the user's
  privileges. Mapping files decide which URLs it opens, so **only use mappings
  you trust**, as you would with any script.
* Mapping URLs are limited to `http`, `https` and `file`.
* The demo web app (`crossfill serve`) is a single-user local tool. It binds to
  `127.0.0.1`, rejects requests whose `Host` header is not a loopback name (to
  block DNS rebinding), accepts transfers only as JSON (so plain cross-site
  form posts are rejected), and sends `X-Frame-Options: DENY`. Do not expose it
  on a network.
* Field values are never written to logs.
* CrossFill never submits a form; a person always does.
