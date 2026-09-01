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

    // The offscreen field an IME composes into.
    //
    // A canvas cannot host composition. An input method needs a real editable element to attach to — it reads the
    // caret from it to place its candidate window, and delivers partial text to it as the visitor picks characters.
    // Without one, everything that is not direct Latin typing is unreachable: Japanese, Chinese, Korean, and every
    // phone keyboard that offers autocorrect or suggestions, all of which arrive as composition rather than as
    // keystrokes.
    ime: null,
    composing: false,

    // Makes the hidden field once, on demand.
    imeField: function () {
      if (cupri.ime) return cupri.ime;

      const f = document.createElement('textarea');
      f.setAttribute('autocapitalize', 'off');
      f.setAttribute('autocomplete', 'off');
      f.setAttribute('autocorrect', 'off');
      f.setAttribute('spellcheck', 'false');
      f.setAttribute('aria-hidden', 'true');
      f.tabIndex = -1;

      // Positioned, not hidden. `display:none` or `visibility:hidden` would take it out of the focus order and
      // most IMEs refuse to attach to it — so it is one transparent pixel, moved to wherever the document says
      // the caret is, which is where a candidate window belongs.
      f.style.cssText =
        'position:fixed; width:1px; height:1px; padding:0; border:0; outline:none; resize:none; overflow:hidden;'
        + ' opacity:0; z-index:-1; left:0; top:0;';

      document.body.appendChild(f);
      cupri.ime = f;

      f.addEventListener('compositionstart', function () { cupri.composing = true; });

      f.addEventListener('compositionupdate', function (e) {
        cupri.input.push({ k: 11, x: 0, y: 0, t: e.data || '' });
      });

      f.addEventListener('compositionend', function (e) {
        cupri.composing = false;
        cupri.input.push({ k: 12, x: 0, y: 0, t: e.data || '' });
        f.value = '';
      });

      // Text that arrived without composition: ordinary typing, a paste, or a phone keyboard inserting a word.
      // Skipped while composing, because those characters are already going through the two handlers above and
      // sending them twice would type everything twice over.
      f.addEventListener('input', function (e) {
        if (cupri.composing || e.isComposing) return;
        const text = f.value;
        f.value = '';
        if (text) cupri.input.push({ k: 13, x: 0, y: 0, t: text });
      });

      // COPY AND CUT come from the document's selection, not from this field.
      //
      // The field is emptied after every insertion, so a native copy would put nothing on the clipboard. The
      // default is prevented and the request is queued instead; the renderer answers next frame with whatever the
      // document has selected, and writes that.
      //
      // PASTE is deliberately NOT handled here. This field has focus, so the browser pastes into it natively, that
      // raises `input`, and the text reaches the document by the path everything else already takes. Intercepting
      // it would mean asking for clipboard-read permission to reimplement what the browser does for free.
      f.addEventListener('copy', function (e) { e.preventDefault(); cupri.input.push({ k: 14, x: 0, y: 0 }); });
      f.addEventListener('cut', function (e) { e.preventDefault(); cupri.input.push({ k: 15, x: 0, y: 0 }); });

      // Editing keys still have to reach the document, and they arrive here rather than at the canvas because
      // this is what has focus while a field is being typed into.
      f.addEventListener('keydown', function (e) {
        if (e.isComposing) return;   // the IME owns the key; committing is what compositionend is for

        // Select-all is a CHORD, not a key, so it cannot come out of the map below — that switches on `e.key`
        // alone. It is here because copy has nothing to answer with until the document has a selection: measured,
        // CopySelection() returns an empty string until a SelectAll is dispatched, and this field's own selection
        // is no substitute because the field is emptied after every insertion.
        if ((e.ctrlKey || e.metaKey) && !e.altKey && (e.key === 'a' || e.key === 'A')) {
          cupri.input.push({ k: 6, x: 0, y: 0, i0: 14, i1: 2, t: '' });
          e.preventDefault();
          return;
        }

        // Undo and redo reach the document from here too. This field has focus whenever a field in the site does,
        // so the canvas handler never sees these — and typing is exactly when they are wanted.
        if ((e.ctrlKey || e.metaKey) && !e.altKey) {
          const k = e.key.toLowerCase();
          if (k === 'z' && !e.shiftKey) { cupri.input.push({ k: 17 }); e.preventDefault(); return; }
          if (k === 'y' || (k === 'z' && e.shiftKey)) { cupri.input.push({ k: 18 }); e.preventDefault(); return; }
        }

        const edit = cupri.editKey(e.key);
        if (!edit) return;           // printable text comes through `input` instead, already composed
        cupri.input.push({ k: 6, x: 0, y: 0, i0: edit, i1: (e.shiftKey ? 1 : 0) | ((e.ctrlKey || e.metaKey) ? 2 : 0), t: '' });
        e.preventDefault();
      });

      return f;
    },

    // ---- The video underlay ------------------------------------------------------------------------------
    //
    // The BROWSER decodes; the document does not. The engine lays a video out, punches a TRANSPARENT HOLE in the
    // frame where the element shows, and paints its own controls on top; a real <video> sits behind the canvas and
    // shows through the hole. That is the whole trick, and it is why the canvas loses its CSS background the
    // moment a video exists — an opaque background under the bitmap would fill the hole in with grey.
    //
    // Nothing here decodes, seeks or scales: those are the browser's, driven by the engine's own controls through
    // the imports below. What this side owns is one element per video and the events that come back.
    //
    // BUILT IN `init`, NOT HERE. An Emscripten library object is stringified into the generated JS, so a member
    // can only be something that survives being written out as source — a literal. `new Map()` is a constructed
    // value and arrives on the other side as an empty object, which fails at the first `.set` with "not a
    // function" and takes the whole document build down with it.
    videos: null,

    videoOpen: function (id, src) {
      const canvas = cupri.canvasEl();
      if (!canvas) return null;

      // Once, and only once there is something to show: a canvas that never carries a video keeps the background
      // the page gave it, which is what covers the gap before the first frame is painted.
      canvas.style.position = 'relative';
      canvas.style.zIndex = '1';
      canvas.style.background = 'transparent';

      const v = document.createElement('video');
      v.src = src;
      v.playsInline = true;      // iOS otherwise hijacks playback into its own fullscreen player
      v.preload = 'auto';
      // pointer-events:none because the CANVAS is the input surface. The controls the visitor sees are painted by
      // the document; letting the element take clicks would put the browser's own controls in front of them.
      v.style.cssText = 'position:absolute; z-index:0; pointer-events:none; display:none;';

      // The browser's truth about playback, not ours. An autoplay rejection pauses without anyone asking, and a
      // document whose play button disagrees with the video is worse than one with no button at all.
      v.addEventListener('loadedmetadata', function () {
        cupri.input.push({ k: 20, i0: id, x: v.duration || 0, a: v.videoWidth || 0, b: v.videoHeight || 0 });
      });
      v.addEventListener('loadeddata', function () { cupri.input.push({ k: 21, i0: id }); });
      v.addEventListener('play', function () { cupri.input.push({ k: 22, i0: id, i1: 1 }); });
      v.addEventListener('pause', function () { cupri.input.push({ k: 22, i0: id, i1: 0 }); });
      v.addEventListener('timeupdate', function () { cupri.input.push({ k: 23, i0: id, x: v.currentTime || 0 }); });
      v.addEventListener('ended', function () { cupri.input.push({ k: 24, i0: id }); });

      canvas.parentNode.insertBefore(v, canvas);
      cupri.videos.set(id, v);
      return v;
    },

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

    // Called once by $cupri__postset when the module initialises. The input listeners still attach lazily, because
    // the canvas may not exist yet; what belongs here is anything that cannot be written as a literal above.
    init: function () {
      cupri.videos = new Map();
    },

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
        // 14 is SelectAll, which is Ctrl+A rather than a key of its own — see the keydown handler.
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

      // A FINGER IS NOT A MOUSE, and the browser sends it as both.
      //
      // Every touch also arrives as a synthesised pointer event, so forwarding both would deliver each gesture
      // twice — once to the pointer path and once to the touch recogniser, which is how a single tap activates a
      // link and then activates whatever the recogniser decides came next. The touch path is the better one on a
      // touch device: it carries identifiers, so it can follow more than one finger, and it feeds the recogniser
      // that produces flings and long-presses. So touch-derived pointer events are dropped here and the real touch
      // events below are used instead.
      c.addEventListener('pointermove', function (e) {
        if (e.pointerType === 'touch') return;
        const p = cupri.at(e); if (p) cupri.pushMove(p.x, p.y);
      });

      c.addEventListener('pointerdown', function (e) {
        if (e.pointerType === 'touch') return;
        const p = cupri.at(e); if (!p) return;
        // Focus on press: a site with a text field is unusable if typing goes to the page instead of the canvas.
        try { c.focus({ preventScroll: true }); } catch (err) { c.focus(); }
        try { c.setPointerCapture(e.pointerId); } catch (err) { /* not fatal; drags just end at the edge */ }

        // LEFT BUTTON ONLY. A press is what activates whatever is under it, so forwarding a right- or middle-click
        // as one means a right-click presses the thing it was meant to open a menu about. CupriFace hit exactly
        // this in both of its own web hosts and fixed it in 0.9.0 (their #85); this client hand-wrote its input
        // layer rather than using theirs, so it had the same bug independently and does not inherit the fix.
        if (e.button !== 0) return;

        // The click count rides on the press. The renderer's host raises a click from the press and the release
        // by itself, so there is no separate click event to send — sending one would activate every link twice.
        cupri.input.push({ k: 2, x: p.x, y: p.y, i0: e.detail || 1 });
      });

      c.addEventListener('pointerup', function (e) {
        if (e.pointerType === 'touch') return;
        const p = cupri.at(e); if (!p) return;
        try { c.releasePointerCapture(e.pointerId); } catch (err) { /* as above */ }

        // Matched to the press above. Releasing a button whose press was never sent would leave the document
        // holding a press it never received, which is how a stuck :active state happens.
        if (e.button !== 0) return;

        cupri.input.push({ k: 3, x: p.x, y: p.y });
      });

      c.addEventListener('pointercancel', function (e) {
        if (e.pointerType === 'touch') return;
        const p = cupri.at(e); if (p) cupri.input.push({ k: 3, x: p.x, y: p.y });
      });

      // TOUCH, as touch. Each changed finger becomes one record carrying its identifier, so the renderer's
      // recogniser can follow several at once and tell a fling from a drag from a long press.
      //
      // passive:false because these MUST be preventable. Left alone, a swipe scrolls the page while the document
      // sits still, and a pinch zooms the whole browser — on a phone the canvas fills the screen, so both read as
      // the site being broken rather than as the browser doing its job.
      const touches = function (kind) {
        return function (e) {
          const rect = c.getBoundingClientRect();
          for (var i = 0; i < e.changedTouches.length; i++) {
            const t = e.changedTouches[i];
            cupri.input.push({
              k: kind,
              i0: t.identifier | 0,
              x: t.clientX - rect.left,
              y: t.clientY - rect.top,
              // The recogniser needs a clock to tell a flick from a slow drag. e.timeStamp is the event's own,
              // which is closer to when the finger actually moved than reading the clock in this handler.
              a: e.timeStamp,
            });
          }
          e.preventDefault();
        };
      };

      c.addEventListener('touchstart', touches(7), { passive: false });
      c.addEventListener('touchmove', touches(8), { passive: false });
      c.addEventListener('touchend', touches(9), { passive: false });
      c.addEventListener('touchcancel', touches(10), { passive: false });

      // passive:false so preventDefault is honoured. Without it the wheel scrolls the PAGE while the document sits
      // still — the canvas fills the viewport, so that reads as the site ignoring the wheel entirely.
      c.addEventListener('wheel', function (e) {
        const p = cupri.at(e); if (!p) return;
        // deltaMode 1 is lines and 2 is pages; the document wants pixels either way.
        var scale = e.deltaMode === 1 ? 16 : (e.deltaMode === 2 ? 800 : 1);
        cupri.input.push({ k: 5, x: p.x, y: p.y, a: e.deltaY * scale, b: e.deltaX * scale });
        e.preventDefault();
      }, { passive: false });

      // A RIGHT-CLICK IS THE DOCUMENT'S, and the browser's own menu is always wrong here. Over a canvas it offers
      // "Save image as" for a picture of somebody's site, and nothing else useful; over a field in that site it
      // offers none of the editing the document itself can do. The engine paints its own — measured, a right-click
      // on a text field puts Paste and Select All into the ARIA mirror — so this one is swallowed.
      // The browser can leave fullscreen on its own — its Escape is handled before any page sees it — so the
      // engine is told rather than left believing a video is still filling the screen.
      document.addEventListener('fullscreenchange', function () {
        cupri.input.push({ k: 19, i0: document.fullscreenElement ? 1 : 0 });
      });

      c.addEventListener('contextmenu', function (e) {
        const p = cupri.at(e); if (!p) return;
        cupri.input.push({ k: 16, x: p.x, y: p.y });
        e.preventDefault();
      });

      c.addEventListener('keydown', function (e) {
        var mods = (e.shiftKey ? 1 : 0) | ((e.ctrlKey || e.metaKey) ? 2 : 0);
        var edit = cupri.editKey(e.key);
        var text = e.key && e.key.length === 1 && !e.ctrlKey && !e.metaKey ? e.key : '';

        // Ctrl+A is select-all inside the document rather than in the page around it.
        if ((e.ctrlKey || e.metaKey) && (e.key === 'a' || e.key === 'A')) { edit = 14; text = ''; }

        // Undo and redo, which the engine keeps a history for and the browser knows nothing about. Both spellings
        // of redo are accepted because both are in use — Ctrl+Y on Windows, Ctrl+Shift+Z everywhere else.
        if ((e.ctrlKey || e.metaKey) && !e.altKey) {
          const k = e.key.toLowerCase();
          if (k === 'z' && !e.shiftKey) { cupri.input.push({ k: 17 }); e.preventDefault(); return; }
          if (k === 'y' || (k === 'z' && e.shiftKey)) { cupri.input.push({ k: 18 }); e.preventDefault(); return; }
        }

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
  cupri_present: function (rgba, w, h, dx, dy, dw, dh) {
    const canvasHost = globalThis.__cupri;
    const canvas = canvasHost && canvasHost.canvas;
    if (!canvas) return;

    // A view over the wasm heap, not a copy: putImageData reads it synchronously, so there is nothing to outlive.
    //
    // THE BUFFER IS ALWAYS THE WHOLE SURFACE, whatever changed — measured, by ticking a document with a small hover
    // change and comparing the byte count against both products: an 82x34 damage region still arrived as 1,920,000
    // bytes for an 800x600 surface. So the rectangle says which part CHANGED; it does not describe the buffer.
    const view = new Uint8ClampedArray(HEAPU8.buffer, rgba, w * h * 4);
    const image = new ImageData(view, w, h);
    const ctx = canvas.getContext('2d');

    // putImageData's seven-argument form uploads only the dirty rectangle, which is the entire point: a hover on one
    // link repaints a few thousand pixels rather than the whole canvas, every frame, on every device.
    //
    // The full-surface form is kept for the case where the damage IS the surface — a first paint, a resize, a
    // navigation — because the dirty-rect path has its own per-call cost and there is nothing to save there.
    const partial = dw > 0 && dh > 0 && (dw < w || dh < h);
    if (partial) ctx.putImageData(image, 0, 0, dx, dy, dw, dh);
    else ctx.putImageData(image, 0, 0);

    // Counted so the browser gate can assert the optimisation is actually engaged. Without this, a regression that
    // silently blitted the whole surface every frame would look identical from the outside — correct pixels, and
    // nothing to notice until someone profiled a phone.
    //
    // On `globalThis.__cupri`, the PAGE's object, not the `$cupri` this library keeps for itself. They are two
    // different objects that both answer to "cupri" in this file, which is exactly how the first version of this
    // wrote the counters somewhere the test could not read them.
    const s = canvasHost.blits || (canvasHost.blits = { full: 0, partial: 0, pixels: 0, surface: 0 });
    if (partial) s.partial++; else s.full++;
    s.pixels += partial ? dw * dh : w * h;
    s.surface += w * h;
    s.last = dx + ',' + dy + ' ' + dw + 'x' + dh + ' of ' + w + 'x' + h;
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

  // Puts text the document copied onto the system clipboard.
  //
  // Asynchronous, and nothing waits for it: the visitor pressed a key, the answer is a clipboard they will use
  // later, and there is nothing useful to do if it fails beyond saying so. A failure here is a browser refusing
  // permission, not the document being wrong.
  cupri_clipboard_write__deps: ['$cupri'],
  cupri_clipboard_write: function (ptr) {
    const text = UTF8ToString(ptr);
    if (!text) return;
    try {
      navigator.clipboard.writeText(text).catch(function (err) {
        console.log('[cupri] the clipboard refused a write: ' + err);
      });
    } catch (err) {
      console.log('[cupri] no clipboard to write to: ' + err);
    }
  },

  // The document asking for the clipboard — a context-menu Paste rather than Ctrl+V, which the hidden field
  // already handles natively. Reading needs permission, so this can be refused; the text arrives as an ordinary
  // insertion when it does not.
  cupri_clipboard_paste__deps: ['$cupri'],
  cupri_clipboard_paste: function () {
    try {
      navigator.clipboard.readText().then(function (text) {
        if (text) cupri.input.push({ k: 13, x: 0, y: 0, t: text });
      }).catch(function (err) {
        console.log('[cupri] the clipboard refused a read: ' + err);
      });
    } catch (err) {
      console.log('[cupri] no clipboard to read from: ' + err);
    }
  },

  // Where the document's caret is, and whether it has one at all.
  //
  // Called by the renderer whenever the focused field changes. Focus follows the DOCUMENT: when it has a field,
  // the hidden textarea takes the browser's focus so an IME attaches to it; when it does not, focus returns to the
  // canvas so the page keeps its own key behaviour. Moving the element to the caret is not cosmetic — an IME reads
  // its position to place the candidate window, and a field parked at the origin puts the candidate
  // list in the corner of the screen instead of under what is being typed.
  cupri_set_text_input__deps: ['$cupri'],
  cupri_set_text_input: function (focused, numeric, multiline, x, y) {
    const canvas = cupri.canvasEl();
    if (!canvas) return;

    const f = cupri.imeField();

    if (!focused) {
      if (document.activeElement === f) { try { canvas.focus({ preventScroll: true }); } catch (err) { canvas.focus(); } }
      return;
    }

    // The document reports the caret in CANVAS pixels; the field is positioned in the viewport.
    const r = canvas.getBoundingClientRect();
    f.style.left = Math.round(r.left + x) + 'px';
    f.style.top = Math.round(r.top + y) + 'px';

    // inputmode drives which keyboard a phone offers. Getting this wrong is not cosmetic either: a numeric field
    // that raises a full alphabet keyboard is a worse experience than no hint at all.
    f.setAttribute('inputmode', numeric ? 'numeric' : 'text');

    if (document.activeElement !== f) { try { f.focus({ preventScroll: true }); } catch (err) { f.focus(); } }
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
  // ---- The video underlay: transport only ------------------------------------------------------------------
  //
  // Everything below moves a call from the engine to one <video> element. The engine owns what plays, when and
  // where; this side owns nothing but the element, which is the arrangement that lets the BROWSER decode. A site
  // with a video is therefore not paying for a software decoder in wasm.

  cupri_video_open__deps: ['$cupri'],
  cupri_video_open: function (id, ptr) {
    cupri.videoOpen(id, UTF8ToString(ptr));
  },

  // A source the engine already holds the bytes of — an inline data: URI, or a file it read. Handed over as a
  // Blob rather than re-encoded into a URL, and the URL is revoked on close so a long visit does not leak one
  // per video.
  cupri_video_open_bytes__deps: ['$cupri'],
  cupri_video_open_bytes: function (id, ptr, len) {
    const bytes = HEAPU8.slice(ptr, ptr + len);
    const url = URL.createObjectURL(new Blob([bytes], { type: 'video/webm' }));
    const v = cupri.videoOpen(id, url);
    if (v) v.dataset.blobUrl = url; else URL.revokeObjectURL(url);
  },

  cupri_video_close__deps: ['$cupri'],
  cupri_video_close: function (id) {
    const v = cupri.videos.get(id);
    if (!v) return;
    v.pause();
    v.remove();
    cupri.videos.delete(id);
    if (v.dataset.blobUrl) URL.revokeObjectURL(v.dataset.blobUrl);
  },

  // A rejected play() is expected rather than exceptional: a browser refuses to start unmuted audio without a
  // gesture. Nothing is done with it because the `pause` event that follows already tells the engine, and its
  // controls follow the browser rather than our optimism.
  cupri_video_play__deps: ['$cupri'],
  cupri_video_play: function (id) {
    const v = cupri.videos.get(id);
    if (v) { const p = v.play(); if (p && p.catch) p.catch(function () { }); }
  },

  cupri_video_pause__deps: ['$cupri'],
  cupri_video_pause: function (id) { const v = cupri.videos.get(id); if (v) v.pause(); },

  cupri_video_muted__deps: ['$cupri'],
  cupri_video_muted: function (id, muted) { const v = cupri.videos.get(id); if (v) v.muted = !!muted; },

  cupri_video_volume__deps: ['$cupri'],
  cupri_video_volume: function (id, volume) { const v = cupri.videos.get(id); if (v) v.volume = volume; },

  cupri_video_loop__deps: ['$cupri'],
  cupri_video_loop: function (id, loop) { const v = cupri.videos.get(id); if (v) v.loop = !!loop; },

  cupri_video_seek__deps: ['$cupri'],
  cupri_video_seek: function (id, seconds) { const v = cupri.videos.get(id); if (v) v.currentTime = seconds; },

  // Where the hole is, in the page's own coordinates.
  //
  // THE DIVISION BY DENSITY IS LOAD-BEARING. Everything the engine says is in canvas pixels, and this canvas is
  // sized in DEVICE pixels so the site renders at the display's real resolution — so on any screen with a scale
  // factor the rect is larger than the box it belongs in, by exactly that factor. It is the same conversion the
  // input path does in the other direction.
  //
  // clip-path rather than overflow, because the element is not inside whatever the engine scrolled: a video half
  // out of a scrolling panel would otherwise hang over the rest of the page. A transform on the chain moved the
  // painted hole, so it is mirrored here — the alternative is a video that slides out from under its own frame.
  cupri_video_rect__deps: ['$cupri'],
  cupri_video_rect: function (id, x, y, w, h, cT, cR, cB, cL, visible, fitPtr, a, b, c, d, e, f) {
    const v = cupri.videos.get(id);
    if (!v) return;
    if (!visible) { v.style.display = 'none'; return; }

    const canvas = cupri.canvasEl();
    if (!canvas) return;
    const box = canvas.getBoundingClientRect();
    const density = (canvas.clientWidth ? canvas.width / canvas.clientWidth : 1) || 1;

    // What the ENGINE asked for, in its own pixels, kept where a test can read it — the same arrangement the
    // blit counters use and for the same reason. Without it the conversion below can only be checked against a
    // guess at the layout, and a client that never divided by the density passes that as long as the doubled
    // element still fits on the canvas, which at most zooms it does.
    const host = globalThis.__cupri;
    if (host) host.lastVideoRect = { id: id, x: x, y: y, w: w, h: h, density: density };

    v.style.display = '';
    v.style.left = (box.left + window.scrollX + x / density) + 'px';
    v.style.top = (box.top + window.scrollY + y / density) + 'px';
    v.style.width = (w / density) + 'px';
    v.style.height = (h / density) + 'px';
    v.style.objectFit = UTF8ToString(fitPtr) || 'contain';
    v.style.clipPath = (cT || cR || cB || cL)
      ? 'inset(' + (cT / density) + 'px ' + (cR / density) + 'px ' + (cB / density) + 'px ' + (cL / density) + 'px)'
      : '';

    const identity = a === 1 && b === 0 && c === 0 && d === 1 && e === 0 && f === 0;
    v.style.transformOrigin = '0 0';
    v.style.transform = identity ? '' : 'matrix(' + a + ',' + b + ',' + c + ',' + d + ','
                                      + (e / density) + ',' + (f / density) + ')';
  },

  // Fullscreen, on the canvas's CONTAINER rather than the canvas: the videos are siblings of the canvas, so
  // fullscreening the canvas alone would take the document's frame and leave every video behind it on the page.
  // 0 toggles, 1 enters, 2 leaves.
  cupri_window_command__deps: ['$cupri'],
  cupri_window_command: function (command) {
    const canvas = cupri.canvasEl();
    if (!canvas) return;
    const target = canvas.parentElement || document.documentElement;
    const inside = !!document.fullscreenElement;

    if (command === 2 || (command === 0 && inside)) {
      if (document.exitFullscreen) document.exitFullscreen();
      return;
    }
    if ((command === 1 || command === 0) && target.requestFullscreen) {
      const p = target.requestFullscreen();
      if (p && p.catch) p.catch(function (err) { console.log('[cupri] fullscreen refused: ' + err); });
    }
  },

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
