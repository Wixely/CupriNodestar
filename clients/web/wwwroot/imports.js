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

    // Input events queue here for the same reason inbound frames do: calling INTO wasm from a DOM handler re-enters
    // the runtime at an arbitrary point, and the renderer is mid-frame often enough for that to matter. The managed
    // side drains this once per frame, so a click is handled between frames rather than during one.
    input: [],
    inputAttached: false,
    cursor: '',

    // What the SCTP association negotiated as its largest message, or 0 before the channel opens.
    sctpMax: 0,

    // Whether the DOCUMENT wants keys, reported by the managed side from the engine's own focus state.
    //
    // This gates preventDefault, and getting it wrong is a real cost either way. Swallowing keys the document does
    // nothing with means Tab cannot move focus out of the canvas, space stops scrolling the page and the arrows go
    // nowhere — the visitor loses browser behaviour and gains nothing. Not swallowing them once a document DOES
    // have a focused field means every keystroke also scrolls the page underneath.
    //
    // Today a plain L2 document never consumes a key: CupriFace 0.3.0 has no focusable text in ordinary markup, so
    // DispatchKey answers false for everything. Keys are still forwarded, so the client is ready the moment that
    // changes, and until then the browser keeps its own behaviour.
    keyCapture: false,

    // Called once by $cupri__postset when the module initialises. Nothing to set up here — the state above is the
    // whole of it, and the input listeners attach lazily because the canvas may not exist yet.
    init: function () {},

    // Pointer moves COALESCE. A mouse dragged across the canvas produces a move per screen refresh and often more;
    // every one of them would otherwise cost a hit test and a full document repaint to reach the same hover state
    // the last one implies. Discrete events (down, up, click, wheel, key) never coalesce — each one means something.
    pushMove: function (x, y) {
      const last = cupri.input[cupri.input.length - 1];
      if (last && last.k === 1) { last.x = x; last.y = y; return; }
      cupri.input.push({ k: 1, x: x, y: y });
    },

    // Where the pointer is inside the canvas, in CSS pixels. The managed side converts to the document's own
    // coordinates, because only it knows the zoom the page was laid out at.
    at: function (e) {
      const c = cupri.canvasEl();
      if (!c) return null;
      const r = c.getBoundingClientRect();
      return { x: e.clientX - r.left, y: e.clientY - r.top };
    },

    canvasEl: function () {
      return (globalThis.__cupri && globalThis.__cupri.canvas) || null;
    },

    // CupriFace's EditKey. Anything not named here is either a printable character (carried as text) or something
    // the document has no meaning for, which is left to the browser.
    editKey: function (k) {
      switch (k) {
        case 'Backspace': return 1;
        case 'Delete': return 2;
        case 'ArrowLeft': return 3;
        case 'ArrowRight': return 4;
        case 'Home': return 5;
        case 'End': return 6;
        case 'Enter': return 7;
        case 'ArrowUp': return 8;
        case 'ArrowDown': return 9;
        case 'Tab': return 10;
        case ' ': return 12;
        case 'Escape': return 13;
        default: return 0;
      }
    },

    // Attached lazily rather than at init, because the module can finish loading before the page has built the
    // canvas. Called every frame from cupri_take_input and idempotent, so it simply starts working once the canvas
    // exists instead of depending on a load order neither side controls.
    ensureInput: function () {
      if (cupri.inputAttached) return;
      const c = cupri.canvasEl();
      if (!c) return;
      cupri.inputAttached = true;

      // A canvas is not focusable by default, so without this it never receives a key event. Set here rather than in
      // the page's markup to keep every input concern in one file.
      if (!c.hasAttribute('tabindex')) c.tabIndex = 0;
      c.style.outline = 'none';

      c.addEventListener('pointermove', function (e) {
        const p = cupri.at(e); if (p) cupri.pushMove(p.x, p.y);
      });

      c.addEventListener('pointerdown', function (e) {
        const p = cupri.at(e); if (!p) return;
        // Focus on press: a site with a text field is unusable if typing goes to the page instead of the canvas.
        try { c.focus({ preventScroll: true }); } catch (err) { c.focus(); }
        try { c.setPointerCapture(e.pointerId); } catch (err) { /* not fatal; drags just end at the edge */ }
        cupri.input.push({ k: 2, x: p.x, y: p.y });
      });

      c.addEventListener('pointerup', function (e) {
        const p = cupri.at(e); if (!p) return;
        try { c.releasePointerCapture(e.pointerId); } catch (err) { /* as above */ }
        cupri.input.push({ k: 3, x: p.x, y: p.y });
        // Up THEN click, in that order: the document settles its pressed state before anything activates.
        cupri.input.push({ k: 4, x: p.x, y: p.y, i0: e.detail || 1 });
      });

      c.addEventListener('pointercancel', function (e) {
        const p = cupri.at(e); if (p) cupri.input.push({ k: 3, x: p.x, y: p.y });
      });

      // passive:false so preventDefault is honoured. Without it the wheel scrolls the PAGE while the document sits
      // still — the canvas fills the viewport, so that reads as the site ignoring the wheel entirely.
      c.addEventListener('wheel', function (e) {
        const p = cupri.at(e); if (!p) return;
        // deltaMode 1 is lines and 2 is pages; the document wants pixels either way.
        var scale = e.deltaMode === 1 ? 16 : (e.deltaMode === 2 ? 800 : 1);
        cupri.input.push({ k: 5, x: p.x, y: p.y, a: e.deltaY * scale, b: e.deltaX * scale });
        e.preventDefault();
      }, { passive: false });

      c.addEventListener('keydown', function (e) {
        var mods = (e.shiftKey ? 1 : 0) | ((e.ctrlKey || e.metaKey) ? 2 : 0);
        var edit = cupri.editKey(e.key);
        var text = e.key && e.key.length === 1 && !e.ctrlKey && !e.metaKey ? e.key : '';

        // Ctrl+A is select-all inside the document rather than in the page around it.
        if ((e.ctrlKey || e.metaKey) && (e.key === 'a' || e.key === 'A')) { edit = 14; text = ''; }

        if (!edit && !text) return;   // a key the document has no meaning for: leave it to the browser
        cupri.input.push({ k: 6, x: 0, y: 0, i0: edit, i1: mods, t: text });

        // Only claimed while the document has somewhere to put it. See keyCapture above.
        if (cupri.keyCapture) e.preventDefault();
      });
    },

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

      ch.onopen = function () {
        cupri.state = 1;
        // The size THIS association actually negotiated, rather than the one the rite advertises. They are not the
        // same question: the rite's ceiling is a constant, and what a DataChannel will carry is whatever the two
        // ends agreed — our node offers a=max-message-size:262144, but a different peer, stack or middlebox may
        // agree far less. See CupriNodestar#4; a vessel that does not fragment makes this the number that matters.
        try { cupri.sctpMax = (pc.sctp && pc.sctp.maxMessageSize) | 0; } catch (e) { cupri.sctpMax = 0; }
      };
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

  // The negotiated SCTP maximum message size, in bytes, or 0 if not yet known.
  cupri_sctp_max__deps: ['$cupri'],
  cupri_sctp_max: function () { return cupri.sctpMax | 0; },

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

  // TAKE semantics again: one press is one step back, however often the module polls. Returns 1 when the visitor
  // pressed Back since the last call.
  cupri_take_back__deps: ['$cupri'],
  cupri_take_back: function () {
    const g = globalThis.__cupri;
    if (!g || !g.backPressed) return 0;
    g.backPressed = false;
    return 1;
  },

  // Whether there is anywhere to go back TO. Driven from the module because only it holds the history, and a Back
  // button that is enabled with an empty history is a button that lies.
  cupri_set_can_back__deps: ['$cupri'],
  cupri_set_can_back: function (can) {
    const g = globalThis.__cupri;
    if (g && typeof g.setCanGoBack === 'function') g.setCanGoBack(!!can);
  },

  // Offers a link back into the address bar. The page decides whether to take it — it will not overwrite typing.
  cupri_suggest_link__deps: ['$cupri'],
  cupri_suggest_link: function (ptr) {
    const g = globalThis.__cupri;
    if (g && typeof g.suggest === 'function') g.suggest(UTF8ToString(ptr));
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

  // --- input ---------------------------------------------------------------------------------------------------

  // Drains the queued input events into the supplied buffer and returns the bytes written, 0 when nothing happened,
  // or -1 if the buffer is too small (the queue is kept, so the next frame retries rather than losing a click).
  //
  // Packed rather than JSON: the shape is fixed and this is polled every frame, so there is nothing to gain from a
  // parser. Each record is eight 32-bit fields followed by its UTF-8 text, padded to a 4-byte boundary:
  //
  //   i32 kind   1 move, 2 down, 3 up, 4 click, 5 wheel, 6 key
  //   i32 i0     click count, or EditKey
  //   i32 i1     key modifiers
  //   f32 x, y   CSS pixels from the canvas's top-left
  //   f32 a, b   wheel delta Y and X, in pixels
  //   i32 len    text bytes that follow
  cupri_take_input__deps: ['$cupri'],
  cupri_take_input: function (ptr, cap) {
    cupri.ensureInput();
    if (cupri.input.length === 0) return 0;

    const view = new DataView(HEAPU8.buffer);
    var offset = 0;

    for (var i = 0; i < cupri.input.length; i++) {
      const e = cupri.input[i];
      const text = e.t || '';
      const textBytes = text ? lengthBytesUTF8(text) : 0;
      const record = 32 + ((textBytes + 1 + 3) & ~3);

      // Out of room: stop here and keep everything still unwritten, including this one.
      if (offset + record > cap) {
        if (offset === 0) return -1;
        cupri.input = cupri.input.slice(i);
        return offset;
      }

      const at = ptr + offset;
      view.setInt32(at, e.k | 0, true);
      view.setInt32(at + 4, e.i0 | 0, true);
      view.setInt32(at + 8, e.i1 | 0, true);
      view.setFloat32(at + 12, e.x || 0, true);
      view.setFloat32(at + 16, e.y || 0, true);
      view.setFloat32(at + 20, e.a || 0, true);
      view.setFloat32(at + 24, e.b || 0, true);
      view.setInt32(at + 28, textBytes, true);
      if (textBytes) stringToUTF8(text, at + 32, textBytes + 1);

      offset += record;
    }

    cupri.input = [];
    return offset;
  },

  // Whether the document has a focused field, so the keydown handler knows whether the key is ours to claim.
  cupri_set_key_capture__deps: ['$cupri'],
  cupri_set_key_capture: function (capture) {
    cupri.keyCapture = !!capture;
  },

  // The cursor the document says belongs under the pointer. Set on the canvas so a link looks like a link — the
  // page has no DOM for the site, so this is the only thing that can express it.
  cupri_set_cursor__deps: ['$cupri'],
  cupri_set_cursor: function (ptr) {
    const c = cupri.canvasEl();
    if (!c) return;
    const css = UTF8ToString(ptr) || 'default';
    if (css === cupri.cursor) return;     // assigning style on every move is a layout invalidation for nothing
    cupri.cursor = css;
    c.style.cursor = css;
  },

  // The accessibility tree CupriFace built from the layout it just painted, mirrored into a hidden element so the
  // browser's own accessibility machinery can read the site out.
  //
  // WITHOUT THIS A SITE IS AN EMPTY PAGE to anyone using a screen reader — a canvas announces itself and nothing
  // inside it, and this client had no answer to that at all. The renderer knows the roles and labels because it did
  // the layout; this is only the last hop.
  //
  // innerHTML, on a string this client's own renderer produced from the document it fetched. It is not arbitrary
  // remote markup: the tree is roles and labels, and the site never reaches the page's DOM (see index.html on why
  // the header is chrome). Skipped when unchanged, because rewriting a subtree resets a screen reader's cursor and
  // would fight anyone trying to read down the page.
  cupri_aria__deps: ['$cupri'],
  cupri_aria: function (ptr) {
    const host = document.getElementById('aria');
    if (!host) return;
    const html = UTF8ToString(ptr);
    if (html === cupri.aria) return;
    cupri.aria = html;
    host.innerHTML = html;
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
