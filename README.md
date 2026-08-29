# era-gaze

The gaze layer of the New ERA Communications family: **ERAgaze**, a single-file
C# engine (no SDK, no packages — compiles with the .NET Framework csc.exe that
ships in Windows) for Tobii Stream Engine trackers (TD I-13, PCEye, ...):

- stabilized OS cursor + soft dwell ring (median spike-reject → One-Euro filter,
  fixation lock, blink grace, track-loss park at a rest corner)
- the **gaze bus**: WebSocket + HTTP on `127.0.0.1:49155` — apps subscribe to
  gaze samples and call `/app/exit`, `/app/park`, `/status`, `/config`, plus the
  v2.5 watch-mode endpoints `/app/launch`, `/app/watch-end`, `/app/watch-heartbeat`
- dwell-click stays OFF by default: apps own activation (see era-core dwell.js)
- **watch mode** (v2.5): `/app/launch` spawns a streaming-service Chrome kiosk and
  makes the screen inert over it; a corner wake target (opposite the track-loss
  park corner, gated on fresh valid gaze samples only) dwell-reveals an options
  strip over the still-playing video (play/pause, volume, pick another, all done).
  `extension/` holds the thin MV3 companion (heartbeat + end-of-episode detection).

`volume-cap/` — an optional generic Windows utility that clamps master volume
to a configured cap (Core Audio event + poll; never touches mute/vol-down).

Requires the device's own Tobii runtime (the installer locates
`tobii_stream_engine.dll` in the existing Tobii installation — nothing
proprietary is bundled or redistributed). License: MPL-2.0; see NOTICE.
