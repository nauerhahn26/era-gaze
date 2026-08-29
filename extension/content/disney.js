// New ERA Watch Companion - Disney+ content script. v1 SKELETON. MPL-2.0.
//
// Disney+'s web player is built from web components: selectors must PIERCE SHADOW
// ROOTS (a plain querySelector will miss them).
//
// ELEMENT-NAME FACTS ONLY below (see README.md license rule): observed in
// Dreamlinerm/Netflix-Prime-Auto-Skip "Streaming Enhanced" src/content-script/
// disney.ts (GPL-3.0, reference-only - no code copied) and docs/movie-player-
// research.md section 2b. disney-plus-utility (MIT) is the one code-reusable
// reference if real logic is needed later (attribution required).
const EL = {
  // Post-play "up next" web component -> our end-of-episode signal. (Kids-profile
  // autoplay-next is OFF by default and cloud-synced, which already matches
  // picker-owned advancement.)
  UP_NEXT: "up-next-lite-v1",
  SKIP_OVERLAY: "skip-overlay", // skip intro/recap overlay component
  AD_BADGE: "ad-badge" // lives inside a shadowRoot
};
// Keyboard fact: 'S' is a dedicated skip-intro key on Disney+ (research 2b).

// Minimal shadow-piercing lookup (ours). Skeleton-grade: full-tree walk, called at
// heartbeat cadence only - replace with a scoped MutationObserver in v2.
function deepQuery(sel, root) {
  root = root || document;
  const hit = root.querySelector(sel);
  if (hit) return hit;
  for (const host of root.querySelectorAll("*")) {
    if (host.shadowRoot) {
      const inner = deepQuery(sel, host.shadowRoot);
      if (inner) return inner;
    }
  }
  return null;
}

const HEARTBEAT_MS = 10000;
let endSent = false;

setInterval(() => {
  const video = document.querySelector("video");
  chrome.runtime.sendMessage({
    kind: "heartbeat",
    body: {
      service: "disney",
      title: document.title,
      t: video ? video.currentTime : null,
      playing: !!(video && !video.paused && !video.ended)
    }
  });
  if (!endSent && deepQuery(EL.UP_NEXT)) {
    endSent = true;
    chrome.runtime.sendMessage({ kind: "watch-end", body: { reason: "disney-up-next" } });
  }
}, HEARTBEAT_MS);
