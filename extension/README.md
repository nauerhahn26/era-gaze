# New ERA Watch Companion (v1 skeleton)

A thin MV3 extension that rides inside the STREAMING Chrome profile ERAgaze's
watch mode launches (`POST /app/launch` on the gaze bus). ERAgaze works without
it: pause/volume are OS-side media keys and escape is the corner wake target.
This extension is the robustness layer on top:

- **watchdog heartbeat** - every ~10s each service script reads
  `video.currentTime` + `document.title` and POSTs to the bus
  (`/app/watch-heartbeat`, a logging stub in ERAgaze v2.5) so the system can
  tell "actually watching X" from "stuck on a spinner".
- **end-of-episode detection** - when a service's post-play surface appears, the
  script POSTs `/app/watch-end`; ERAgaze closes the streaming kiosk and the
  picker asks "what next?" (episode advancement is picker-owned - never
  autoplay, which also structurally defeats "are you still watching?").
- (planned, v2) profile pin and skip-intro clicks per service.

All bus traffic funnels through `background.js`: content scripts on https pages
cannot fetch `http://127.0.0.1` (mixed-content blocking), the service worker can
(`host_permissions`). Nothing here talks to any host except `127.0.0.1:49155`,
and nothing touches streams or DRM - UI-level assistance over a signed-in, paid
account.

## Install (registry policy - the only kiosk-safe path)

Chrome 137 (Apr 2025) removed `--load-extension` from branded Chrome. Install
via the `ExtensionInstallForcelist` enterprise policy instead (silent, survives
restarts, works under `--kiosk`):

1. Pack the extension (`chrome://extensions` > Pack extension, or `chrome
   --pack-extension=...`) and note its extension ID.
2. Host the `.crx` plus an update-manifest XML somewhere the device can reach
   (self-hosted update URL).
3. Registry (64-bit Chrome):
   `HKLM\SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist`
   - new string value, name `1`, data `<extension-id>;<update-manifest-url>`.
4. Relaunch Chrome; verify at `chrome://policy` and `chrome://extensions`.

The forcelist applies to every profile on the machine, but the content scripts
only match the streaming services, so in practice it is inert outside the
streaming kiosk.

## LICENSE RULE (read before touching selectors)

This code is **MPL-2.0**, like the whole repo (see the repo `LICENSE`).

- **Streaming Enhanced** (Dreamlinerm/Netflix-Prime-Auto-Skip, GPL-3.0) and
  **Netflix Marathon** (unlicensed) were used as **selector FACTS only** - which
  `data-uia` values, shadow-DOM element names, and `atvwebplayersdk-` classes
  exist. **No code was copied from either** and none may ever be: GPL code is
  license-incompatible with this repo and unlicensed code is unusable, full
  stop.
- **disney-plus-utility** (MIT) is the one code-reusable reference if real
  logic is needed later; MIT requires attribution when code is actually taken.
- Selector constants in the content scripts carry source comments; keep that
  practice when selectors change (they will - services redesign, and the
  device-verification checklist re-checks them on the i13).
