// The JS half of the on-ramp: it owns the RTCPeerConnection and exposes four functions to WASM.
//
// This is an Emscripten JS library, linked into the module at build time (--js-library). It is NOT [JSImport]:
// NativeAOT-LLVM has no Mono runtime, so interop is DllImport("js") on the managed side and mergeInto here.
//
// The whole reason there is no signalling server: the node publishes its ICE credentials and DTLS fingerprint inside
// its own SIGNED Intonation. A browser holding that link already has the remote description, so it can synthesise the
// answer locally and dial straight out. Nothing is exchanged with a third party, and nobody else learns of the visit.

mergeInto(LibraryManager.library, {
  // --- state -------------------------------------------------------------------------------------------------

  $cupri__postset: 'cupri.init();',
  $cupri: {
    pc: null,
    channel: null,
    // Inbound messages queue here until the managed side polls for them. A queue rather than a callback because
    // calling INTO wasm from a JS event handler re-enters the runtime at an arbitrary point; polling keeps the
    // managed side in control of when it runs.
    inbox: [],
    state: 0, // 0 connecting, 1 open, 2 failed
    error: '',

    init: function () {},

    // Tears the current connection down completely.
    //
    // Load-bearing for reconnecting, and the absence of it was a real bug: a new dial used to leave the previous
    // RTCPeerConnection alive with its onmessage handler still attached, so late frames from the DEAD session kept
    // arriving in the shared inbox. The next Pilgrimage then met an Auspice frame in the middle of its handshake
    // and died with "Unexpected frame on stream 7 during the Toll exchange" — a confusing failure a long way from
    // its cause. Detaching the handlers matters as much as closing: close() is not instantaneous, and a message
    // already in flight will still be delivered.
    teardown: function () {
      try {
        if (cupri.channel) {
          cupri.channel.onmessage = null;
          cupri.channel.onopen = null;
          cupri.channel.onclose = null;
          cupri.channel.onerror = null;
          cupri.channel.close();
        }
      } catch (e) { /* already gone; nothing to salvage */ }

      try {
        if (cupri.pc) {
          cupri.pc.onconnectionstatechange = null;
          cupri.pc.oniceconnectionstatechange = null;
          cupri.pc.close();
        }
      } catch (e) { /* as above */ }

      cupri.channel = null;
      cupri.pc = null;
      cupri.inbox = [];   // cleared AFTER detaching, or a racing delivery lands in the next session's queue
    },

    fail: function (why) {
      cupri.error = String(why);
      cupri.state = 2;
      console.error('[cupri]', why);
    },

    // Builds the SDP answer from the node's published parameters. Normally this arrives over a signalling channel;
    // here it is reconstructed locally from the signed link, which is the entire trick.
    answerFrom: function (p) {
      const fp = p.fingerprint.toUpperCase().match(/../g).join(':');
      return [
        'v=0',
        'o=- 0 0 IN IP4 ' + p.host,
        's=-',
        't=0 0',
        'a=group:BUNDLE 0',
        'm=application ' + p.port + ' UDP/DTLS/SCTP webrtc-datachannel',
        'c=IN IP4 ' + p.host,
        'a=mid:0',
        'a=sctp-port:5000',
        'a=max-message-size:262144',
        'a=ice-ufrag:' + p.ufrag,
        'a=ice-pwd:' + p.password,
        'a=ice-lite',
        'a=fingerprint:' + p.fingerprintAlgorithm + ' ' + fp,
        // The node is the DTLS server and never initiates checks; the browser is the client and the controller.
        'a=setup:passive',
        'a=candidate:1 1 udp 2130706431 ' + p.host + ' ' + p.port + ' typ host',
        'a=end-of-candidates',
        '',
      ].join('\r\n');
    },
  },

  // --- exported to WASM --------------------------------------------------------------------------------------

  // Starts the connection. Takes the endpoint parameters as JSON so the managed side owns Intonation parsing and
  // this file stays ignorant of the wire format.
  cupri_connect__deps: ['$cupri'],
  cupri_connect: function (jsonPtr) {
    try {
      const p = JSON.parse(UTF8ToString(jsonPtr));

      // Whatever came before goes first. Belt and braces alongside the close on dispose: this is the one place that
      // is guaranteed to run before a new session exists, so a leak anywhere else still cannot corrupt this one.
      cupri.teardown();

      cupri.state = 0;
      cupri.inbox = [];

      const pc = new RTCPeerConnection({ iceServers: [] });
      cupri.pc = pc;

      // The browser opens the channel. `negotiated:false` with id 0 matches what the node's DCEP responder expects.
      const ch = pc.createDataChannel('cupri', { ordered: true });
      ch.binaryType = 'arraybuffer';
      cupri.channel = ch;

      ch.onopen = function () { cupri.state = 1; };
      ch.onclose = function () { console.log('[cupri] datachannel closed'); if (cupri.state !== 2) cupri.state = 3; };
      ch.onerror = function (e) { cupri.fail('datachannel: ' + (e && e.message ? e.message : 'error')); };
      ch.onmessage = function (e) { cupri.inbox.push(new Uint8Array(e.data)); };

      pc.oniceconnectionstatechange = function () {
        console.log('[cupri] ice ' + pc.iceConnectionState);
        if (pc.iceConnectionState === 'failed') cupri.fail('ice failed');
      };

      // Noticing that the far end has GONE is the slow part of WebRTC, and getting it wrong is why a restarted
      // server used to leave this client sitting on a dead connection indefinitely.
      //
      // A peer that dies without closing anything leaves the DataChannel readyState 'open' — there is no FIN to
      // observe, because there is no TCP. Chrome only gives up when ICE consent freshness expires (RFC 7675), about
      // THIRTY SECONDS later, which is far too long to look like anything but a hang.
      //
      // 'disconnected' arrives within a few seconds, but it is legitimately transient: a burst of packet loss on a
      // mobile link produces it and then recovers. So it starts a grace timer rather than failing outright, and only
      // a disconnect that is still there when the timer fires is treated as the far end being gone.
      var lapse = null;
      var cancelLapse = function () { if (lapse !== null) { clearTimeout(lapse); lapse = null; } };

      pc.onconnectionstatechange = function () {
        var s = pc.connectionState;
        // Logged, not just acted on: when a visit stops working the FIRST question is what the transport thought
        // was happening, and without this the answer is invisible from outside the tab.
        console.log('[cupri] connection ' + s);

        if (s === 'failed' || s === 'closed') { cancelLapse(); cupri.fail('connection ' + s); return; }

        if (s === 'disconnected') {
          cancelLapse();
          lapse = setTimeout(function () {
            if (pc.connectionState === 'disconnected') cupri.fail('connection lost');
          }, 5000);
          return;
        }

        if (s === 'connected') cancelLapse();   // it came back on its own; nothing to report
      };

      pc.createOffer()
        .then(function (offer) { return pc.setLocalDescription(offer); })
        .then(function () {
          return pc.setRemoteDescription({ type: 'answer', sdp: cupri.answerFrom(p) });
        })
        .catch(function (e) { cupri.fail(e); });
    } catch (e) {
      cupri.fail(e);
    }
  },

  // 0 connecting, 1 open, 2 failed, 3 closed.
  cupri_state__deps: ['$cupri'],
  cupri_state: function () { return cupri.state; },

  // Closes the connection and drops everything associated with it. Called when a visit ends, so a peer connection
  // does not outlive the session that owns it.
  cupri_close__deps: ['$cupri'],
  cupri_close: function () {
    cupri.teardown();
    cupri.state = 3;
  },

  // The seeded link, fetched by the host page before the module loaded. Deliberately NOT HttpClient on the managed
  // side: without Mono there is no browser HTTP handler behind it, so it would compile and then fail at runtime.
  // Handing the page's own fetch result across is both simpler and one less thing to go wrong.
  cupri_seed__deps: ['$cupri'],
  cupri_seed: function (ptr, cap) {
    // globalThis, not Module: under the dotnet.js loader the page never owns the Module object, so the bridge
    // between page and module scope is a global � the same pattern CupriFace's imports.js uses.
    const seed = (globalThis.__cupri && globalThis.__cupri.seed) ? globalThis.__cupri.seed : '';
    const bytes = lengthBytesUTF8(seed) + 1;
    if (bytes > cap) return -1;
    stringToUTF8(seed, ptr, cap);
    return bytes - 1;
  },

  // Asks the page to re-fetch the seed. Fire and forget: the fetch is async and this boundary is not, so the module
  // starts it here and watches cupri_seed_serial to learn whether it landed.
  cupri_refresh_seed__deps: ['$cupri'],
  cupri_refresh_seed: function () {
    const bridge = globalThis.__cupri;
    if (bridge && typeof bridge.refreshSeed === 'function') bridge.refreshSeed();
  },

  // Advances only on a SUCCESSFUL re-fetch, which is what makes it usable as "is the node back?". A counter rather
  // than a flag so a refresh that lands while the module is between polls cannot be missed.
  cupri_seed_serial__deps: ['$cupri'],
  cupri_seed_serial: function () {
    const bridge = globalThis.__cupri;
    return bridge && bridge.seedSerial ? bridge.seedSerial | 0 : 0;
  },

  // --- rendering -----------------------------------------------------------------------------------------------

  // Blits a rendered frame onto the page's canvas. The bytes are STRAIGHT (unpremultiplied) RGBA because that is
  // what ImageData means; the managed side converts out of Skia's premultiplied surface before calling.
  cupri_present__deps: ['$cupri'],
  cupri_present: function (rgba, w, h) {
    const canvas = globalThis.__cupri && globalThis.__cupri.canvas;
    if (!canvas) return;
    // A view over the wasm heap, not a copy: putImageData reads it synchronously, so there is nothing to outlive.
    const view = new Uint8ClampedArray(HEAPU8.buffer, rgba, w * h * 4);
    canvas.getContext('2d').putImageData(new ImageData(view, w, h), 0, 0);
  },

  // The device-pixel ratio actually in force, derived from the canvas rather than read from the window: it is the
  // ratio the buffer was BUILT with, so it stays correct even between a monitor change and the next resize pass.
  // Returned as the real number of device pixels per CSS pixel, which is what the renderer needs to lay a document
  // out in CSS pixels and then draw it at native resolution.
  cupri_canvas_scale__deps: ['$cupri'],
  cupri_canvas_scale: function () {
    const c = globalThis.__cupri && globalThis.__cupri.canvas;
    if (!c || !c.clientWidth) return 1;
    return c.width / c.clientWidth;
  },

  cupri_canvas_width__deps: ['$cupri'],
  cupri_canvas_width: function () {
    const c = globalThis.__cupri && globalThis.__cupri.canvas;
    return c ? c.width : 0;
  },

  cupri_canvas_height__deps: ['$cupri'],
  cupri_canvas_height: function () {
    const c = globalThis.__cupri && globalThis.__cupri.canvas;
    return c ? c.height : 0;
  },

  // --- navigation ----------------------------------------------------------------------------------------------

  // TAKE semantics, not peek: returns a pending link once and clears it, so one submit produces exactly one visit
  // however often the managed side polls. Returns 0 when nothing is pending.
  cupri_take_link__deps: ['$cupri'],
  cupri_take_link: function (ptr, cap) {
    const g = globalThis.__cupri;
    const link = g && g.pending ? g.pending : '';
    if (!link) return 0;
    const bytes = lengthBytesUTF8(link) + 1;
    if (bytes > cap) { g.pending = ''; return -1; }
    stringToUTF8(link, ptr, cap);
    g.pending = '';
    return bytes - 1;
  },

  // Chrome status, written by the client rather than by any site — see index.html for why that separation matters.
  cupri_status__deps: ['$cupri'],
  cupri_status: function (ptr) {
    const g = globalThis.__cupri;
    if (g && g.status) g.status(UTF8ToString(ptr));
  },

  cupri_send__deps: ['$cupri'],
  cupri_send: function (ptr, len) {
    if (!cupri.channel || cupri.channel.readyState !== 'open') return -1;
    try {
      // Copy: the wasm heap can move under us, and send() is asynchronous.
      cupri.channel.send(HEAPU8.slice(ptr, ptr + len));
      return 0;
    } catch (e) {
      cupri.fail(e);
      return -1;
    }
  },

  // Copies the next inbound message into the supplied buffer. Returns its length, 0 when the inbox is empty, or -1
  // if the buffer is too small (the message is kept, so a bigger buffer can retry rather than lose it).
  cupri_recv__deps: ['$cupri'],
  cupri_recv: function (ptr, cap) {
    if (cupri.inbox.length === 0) return 0;
    const msg = cupri.inbox[0];
    if (msg.length > cap) return -1;
    HEAPU8.set(msg, ptr);
    cupri.inbox.shift();
    return msg.length;
  },
});
