# xterm.js (vendored)

The terminal emulator of the Remote background window (0.3.0). Served from the instance itself: the Content Security Policy allows
scripts from this origin only, and nothing is loaded from a CDN.

| File | Package | Source |
|---|---|---|
| `xterm.mjs`, `xterm.css` | `@xterm/xterm` 6.0.0 | `https://registry.npmjs.org/@xterm/xterm/-/xterm-6.0.0.tgz`, sha512 `TQwDdQGtwwDt+2cgKDLn0IRaSxYu1tSUjgKarSDkUM0ZNiSRXFpjxEsvc/Zgc5kq5omJ+V0a8/kIM2WD3sMOYg==` |
| `addon-fit.mjs` | `@xterm/addon-fit` 0.11.0 | `https://registry.npmjs.org/@xterm/addon-fit/-/addon-fit-0.11.0.tgz`, sha512 `jYcgT6xtVYhnhgxh3QgYDnnNMYTcf8ElbxxFzX0IZo+vabQqSPAjC3c1wJrKB5E19VwQei89QCiZZP86DCPF7g==` |

The files are the `lib/*.mjs` and `css/xterm.css` of those packages, verified against the registry integrity hash, with only the
`sourceMappingURL` comment removed (the source maps are not shipped). MIT licensed, see `LICENSE`. Neither uses `eval` or `new Function`.

To update: download the new tarballs, check their integrity against `https://registry.npmjs.org/<package>`, copy the same files, remove the
`sourceMappingURL` line, update this table and test the terminal on Windows and Linux.
