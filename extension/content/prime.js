// New ERA Watch Companion - Prime Video content script. v1 SKELETON. MPL-2.0.
// (Prime itself is P3, shipping with the owned library - this skeleton rides along.)
//
// SELECTOR FACTS ONLY below (see README.md license rule): Amazon's atvwebplayersdk-
// prefixed classes are SDK-generated and stable (docs/movie-player-research.md
// section 2, observed in Netflix Marathon, unlicensed, reference-only - no code
// copied, 2024-2026).
const SEL = {
  // Post-play "next up" card -> our end-of-episode signal (picker owns advancement).
  NEXT_UP: ".atvwebplayersdk-nextupcard-button",
  SKIP: ".atvwebplayersdk-skipelement-button" // skip intro/recap
};

const HEARTBEAT_MS = 10000;
let endSent = false;

setInterval(() => {
  const video = document.querySelector("video");
  chrome.runtime.sendMessage({
    kind: "heartbeat",
    body: {
      service: "prime",
      title: document.title,
      t: video ? video.currentTime : null,
      playing: !!(video && !video.paused && !video.ended)
    }
  });
  if (!endSent && document.querySelector(SEL.NEXT_UP)) {
    endSent = true;
    chrome.runtime.sendMessage({ kind: "watch-end", body: { reason: "prime-next-up" } });
  }
}, HEARTBEAT_MS);
