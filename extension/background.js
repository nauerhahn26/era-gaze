// New ERA Watch Companion - background service worker (MV3). MPL-2.0 (repo LICENSE;
// see README.md for the OSS-reuse rule).
//
// Why this exists: content scripts on https pages cannot fetch http://127.0.0.1
// (mixed-content blocking applies to them); the service worker can, via
// host_permissions. All gaze-bus traffic funnels through here.

const BUS = "http://127.0.0.1:49155";

chrome.runtime.onMessage.addListener((msg) => {
  if (!msg || !msg.kind) return;
  if (msg.kind === "heartbeat") post("/app/watch-heartbeat", msg.body || {});
  else if (msg.kind === "watch-end") post("/app/watch-end", msg.body || {});
});

function post(path, body) {
  // ERAgaze parses the body regardless of Content-Type (contract); bus down = fine,
  // ERAgaze owns every failure mode, the extension is only the robustness layer.
  fetch(BUS + path, { method: "POST", body: JSON.stringify(body) }).catch(() => {});
}
