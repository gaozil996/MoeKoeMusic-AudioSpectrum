// Fan messages out from the service worker to every page tab.
// The bridge page cannot reach content scripts directly.

function broadcast(msg) {
  chrome.tabs.query({}, function (tabs) {
    if (chrome.runtime.lastError) return;
    tabs.forEach(function (t) {
      if (t.id === undefined) return;
      chrome.tabs.sendMessage(t.id, msg, function () { void chrome.runtime.lastError; });
    });
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