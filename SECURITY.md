# Security policy

Fleeto is a remote monitoring and management platform. It runs with high privileges on the endpoints of IT teams and
their clients, so a vulnerability here can reach many machines at once. Reports are welcome and are handled with
priority.

## Reporting a vulnerability

Use **Report a vulnerability** under the Security tab of this repository (GitHub private vulnerability reporting). The
report stays private between you and Steaan until a fix is released.

Please do not open a public issue or a pull request for a security problem, and do not test against an instance you do
not run yourself: every Fleeto instance is operated by Steaan for a paying customer and carries their client data.

A useful report states what an attacker can do, which component is affected (server, agent, watchdog, gateway, signer,
`install.sh` or the public API), the version or commit you looked at, and the steps to reproduce it.

We aim to acknowledge a report within three working days and to keep you informed until it is fixed or explained.

## Supported versions

Fleeto is at `0.x`: the product is in beta and only the latest release is supported. Fixes go into the next release;
there are no patches for older versions.

## Scope

In scope: the server components (web, gateway, signer, workers), the agent and the watchdog, `install.sh` and the
release verification, the public REST API, enrollment and certificate handling, the signing and secret handling, remote
control and remote background, and the isolation between clients and between instances.

Out of scope: anything that needs an attacker to already hold the instance root key or the offline Steaan release key,
findings in third-party products Fleeto integrates with (report those to their vendor), and accepted risks that are
documented in `MD-Files/ARCHITECTURE.md` §5.
