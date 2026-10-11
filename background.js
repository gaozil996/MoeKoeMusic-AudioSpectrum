// Fan messages out from the service worker to every page tab.
// The bridge page cannot reach content scripts directly.

// chrome.tabs.query enumerates every tab in the browser and is one of the
// heavier extension APIs. Running it once per spectrum frame put a full tab
// enumeration in the middle of the delivery path, and at 60 frames a second
// it cost more than the audio pipeline did. Cache the ids and refresh them
// off the hot path instead.
//
// The cache is refreshed at most every REFRESH_MS, and immediately whenever a
// tab event fires -- which also covers a service worker that was suspended
// and missed events while it slept.
var REFRESH_MS = 1000;

var tabIds = [];
var refreshedAt = 0;

function invalidate() { refreshedAt = 0; }

if (chrome.tabs) {
  if (chrome.tabs.onCreated) chrome.tabs.onCreated.addListener(invalidate);
  if (chrome.tabs.onRemoved) chrome.tabs.onRemoved.addListener(invalidate);
  if (chrome.tabs.onUpdated) chrome.tabs.onUpdated.addListener(invalidate);
}

function sendToCached(msg) {
  for (var i = 0; i < tabIds.length; i++) {
    try {
      chrome.tabs.sendMessage(tabIds[i], msg, function () { void chrome.runtime.lastError; });
    } catch (e) { /* tab went away between the query and here */ }
  }
}

function broadcast(msg) {
  var now = Date.now();
  if (now - refreshedAt < REFRESH_MS && tabIds.length) {
    sendToCached(msg);
    return;
  }
  refreshedAt = now;
  chrome.tabs.query({}, function (tabs) {
    if (chrome.runtime.lastError) { sendToCached(msg); return; }
    tabIds = (tabs || [])
      .map(function (t) { return t.id; })
      .filter(function (id) { return typeof id === 'number'; });
    sendToCached(msg);
  });
}

chrome.runtime.onMessage.addListener(function (request, sender, sendResponse) {
  if (!request) return false;

  if (request.type === 'viz-spectrum') {
    broadcast(request);
    sendResponse({ ok: true });
    return false;
  }

  if (request.type === 'viz-diag') {
    broadcast({ type: 'viz-diag', msg: request.msg });
    sendResponse({ ok: true });
    return false;
  }

  return false;
});