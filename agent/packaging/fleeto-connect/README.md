# Fleeto Connect packaging

Files of the Ubuntu package of Fleeto Connect (`agent/cmd/fleeto-connect`, MD-Files/PROXMOX-WEB.md). The `connect` stage of
`src/Fleeto.Web/Dockerfile` builds the package from them; `@VERSION@` in `control` becomes the Fleeto version with `-` written as `~`,
so a pre-release sorts before its release.
