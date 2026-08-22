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
      cupri.state = 0;
      cupri.inbox = [];

      const pc = new RTCPeerConnection({ iceServers: [] });
      cupri.pc = pc;

      // The browser opens the channel. `negotiated:false` with id 0 matches what the node's DCEP responder expects.
      const ch = pc.createDataChannel('cupri', { ordered: true });
      ch.binaryType = 'arraybuffer';
      cupri.channel = ch;

      ch.onopen = function () { cupri.state = 1; };
      ch.onclose = function () { if (cupri.state !== 2) cupri.state = 3; };
      ch.onerror = function (e) { cupri.fail('datachannel: ' + (e && e.message ? e.message : 'error')); };
      ch.onmessage = function (e) { cupri.inbox.push(new Uint8Array(e.data)); };

      pc.oniceconnectionstatechange = function () {
        if (pc.iceConnectionState === 'failed') cupri.fail('ice failed');
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

  // The seeded link, fetched by the host page before the module loaded. Deliberately NOT HttpClient on the managed
  // side: without Mono there is no browser HTTP handler behind it, so it would compile and then fail at runtime.
  // Handing the page's own fetch result across is both simpler and one less thing to go wrong.
  cupri_seed__deps: ['$cupri'],
  cupri_seed: function (ptr, cap) {
    const seed = (typeof Module !== 'undefined' && Module.cupriSeed) ? Module.cupriSeed : '';
    const bytes = lengthBytesUTF8(seed) + 1;
    if (bytes > cap) return -1;
    stringToUTF8(seed, ptr, cap);
    return bytes - 1;
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
