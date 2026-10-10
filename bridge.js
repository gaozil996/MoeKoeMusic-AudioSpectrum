(function () {
  'use strict';

  // The hidden bridge window. The native host pushes one JSON line per
  // spectrum frame on stdout; MoeKoe routes those lines here through
  // electronAPI.nativeHost. We fan them out to every page tab.

  var HOST_ID = 'spectrum-sink';
  var api = window.electronAPI && window.electronAPI.nativeHost;

  if (!api) {
    console.error('[viz] nativeHost API not available');
    return;
  }

  function diag(msg) {
    try {
      chrome.runtime.sendMessage({ type: 'viz-diag', msg: msg }, function () {
        void chrome.runtime.lastError;
      });
    } catch (e) {}
  }

  var frames = 0;

  api.onMessage(function (payload) {
    if (!payload || payload.hostId !== HOST_ID) return;
    var m = payload.message;
    if (!m) return;

    if (m.type === 'hello') { diag('viz host hello'); return; }

    if (m.event === 'spectrum' && m.v) {
      frames++;
      chrome.runtime.sendMessage({
        type: 'viz-spectrum',
        v: m.v,
        p: m.p || null
      }, function () { void chrome.runtime.lastError; });
      return;
    }

    if (m.diag) { diag(m.diag + ': ' + m.msg); }
  });

  api.getStatus(HOST_ID).then(function (s) {
    diag('status ' + JSON.stringify(s));
  }, function (e) {
    diag('status failed ' + e);
  });

  diag('bridge ready');
})();