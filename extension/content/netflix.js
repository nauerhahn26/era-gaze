// New ERA Watch Companion - Netflix content script. v1 SKELETON. MPL-2.0.
//
// SELECTOR FACTS ONLY below (see README.md license rule): sources were read as
// documentation of which selectors exist, no code was copied from them.
//   - data-uia selectors: docs/movie-player-research.md section 2 (board-builder
//     workspace), observed in Netflix Marathon (unlicensed, reference-only) and
//     Streaming Enhanced (GPL-3.0, reference-only), 2024-2026.
const SEL = {
  SKIP_INTRO: "[data-uia='player-skip-intro']",
  // Post-play "next episode" surface. Presence doubles as our end-of-episode signal:
  // the picker owns advancement (never autoplay), so we END the session instead of
  // clicking it.
  NEXT_EPISODE: "[data-uia='next-episode-seamless-button']",
  POST_PLAY: "[data-uia='next-episode-seamless-button']"
};
// NOTE: the in-page player API (netflix.appContext ... videoPlayer pause/play/seek)
// needs world:"MAIN" injection; isolated-world scripts like this one cannot see it.
// v2 concern - this skeleton stays DOM-only.

const HEARTBEAT_MS = 10000; // human-scale; never hammer the services
let endSent = false;

setInterval(() => {
  const video = document.querySelector("video");
  // Watchdog heartbeat -> ERAgaze /app/watch-heartbeat (via the background worker).
  chrome.runtime.sendMessage({
    kind: "heartbeat",
    body: {
      service: "netflix",
      title: document.title,
      t: video ? video.currentTime : null,
      playing: !!(video && !video.paused && !video.ended)
    }
  });
  // End detection (v1 stub): post-play DOM appeared -> ask ERAgaze to close the
  // kiosk; the picker asks "what next?".
  if (!endSent && document.querySelector(SEL.POST_PLAY)) {
    endSent = true;
    chrome.runtime.sendMessage({ kind: "watch-end", body: { reason: "netflix-post-play" } });
  }
}, HEARTBEAT_MS);
