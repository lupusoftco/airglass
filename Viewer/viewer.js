"use strict";

(() => {
  // URL fragment: "#<token>" or "#<token>&debug". The fragment never reaches
  // the server in an HTTP request.
  const [rawToken = "", ...flags] = location.hash.slice(1).split("&");
  const token = decodeURIComponent(rawToken);
  const debug = flags.includes("debug");
  // Remove the token from the address bar and history right away so it
  // cannot be copied or reopened.
  history.replaceState(null, "", location.pathname);

  // --- Debug log (temporary) -------------------------------------------

  const debugEl = debug ? document.createElement("div") : null;
  if (debugEl) {
    debugEl.id = "debug";
    document.body.appendChild(debugEl);
  }

  const log = (text) => {
    const line = `${new Date().toISOString().slice(11, 23)} ${text}`;
    console.log(line);
    if (!debugEl) return;
    const row = document.createElement("div");
    row.textContent = line;
    debugEl.appendChild(row);
    while (debugEl.childElementCount > 300) debugEl.firstChild.remove();
    debugEl.scrollTop = debugEl.scrollHeight;
  };

  window.addEventListener("error", (event) => {
    log(`JS HATA: ${event.message} (${(event.filename || "").split("/").pop()}:${event.lineno}:${event.colno})`);
  });
  window.addEventListener("unhandledrejection", (event) => {
    const reason = event.reason;
    log(`YAKALANMAMIŞ PROMISE: ${reason && reason.name ? `${reason.name}: ${reason.message}` : reason}`);
  });

  /** "host 192.168.1.5:50000 udp" from an SDP candidate line. */
  const describeCandidate = (sdp) => {
    const fields = sdp.split(" ");
    const typ = fields.indexOf("typ");
    return `${typ >= 0 ? fields[typ + 1] : "?"} ${fields[4]}:${fields[5]} ${fields[2]}`;
  };

  log(`UA: ${navigator.userAgent}`);
  log(`secureContext=${window.isSecureContext} RTCPeerConnection=${typeof window.RTCPeerConnection}`);

  // --- UI ----------------------------------------------------------------

  const statusEl = document.getElementById("status");
  const video = document.getElementById("video");

  const setStatus = (text) => {
    statusEl.textContent = text || "";
    statusEl.hidden = !text;
  };

  if (!token) {
    log("Fragment'ta token yok");
    setStatus("Bağlantı geçersiz. QR kodu yeniden okutun.");
    return;
  }

  // --- Signaling -----------------------------------------------------------

  const socket = new WebSocket(`ws://${location.host}/ws`);
  let peer = null;
  let welcomed = false;
  let ended = false;

  const send = (message) => {
    if (socket.readyState !== WebSocket.OPEN) {
      log(`→ ${message.type} GÖNDERİLEMEDİ (WS readyState=${socket.readyState})`);
      return;
    }
    log(`→ ${message.type}${message.type === "candidate" ? ` ${describeCandidate(message.candidate)}` : ""}`);
    socket.send(JSON.stringify(message));
  };

  const end = (text) => {
    if (ended) return;
    ended = true;
    if (peer) peer.close();
    socket.close();
    video.srcObject = null;
    setStatus(text);
  };

  // Socket listeners are registered first so the handshake works even if
  // anything WebRTC-related fails later.
  socket.addEventListener("open", () => {
    log("WS open");
    log("→ hello");
    socket.send(JSON.stringify({ type: "hello", token }));
  });

  socket.addEventListener("error", () => log("WS error"));

  socket.addEventListener("close", (event) => {
    log(`WS close code=${event.code} reason="${event.reason}" clean=${event.wasClean}`);
    end(welcomed ? "Bağlantı kesildi" : "Bağlanılamadı. QR kodu yeniden okutun.");
  });

  // --- WebRTC --------------------------------------------------------------

  const createPeer = () => {
    // Local network only: no STUN/TURN servers, host candidates only.
    const pc = new RTCPeerConnection({ iceServers: [] });

    pc.addEventListener("track", (event) => {
      log(`track: ${event.track.kind}`);
      video.srcObject = event.streams[0] || new MediaStream([event.track]);
      video.play().catch((error) => log(`video.play() reddedildi: ${error.name}`));
    });
    pc.addEventListener("icecandidate", (event) => {
      if (!event.candidate || !event.candidate.candidate) {
        log("ICE toplama bitti");
        return;
      }
      send({
        type: "candidate",
        candidate: event.candidate.candidate,
        sdpMid: event.candidate.sdpMid,
        sdpMLineIndex: event.candidate.sdpMLineIndex,
      });
    });
    pc.addEventListener("icecandidateerror", (event) => {
      log(`ICE aday hatası: ${event.errorCode} ${event.errorText} ${event.address || ""}:${event.port || ""}`);
    });
    pc.addEventListener("signalingstatechange", () => log(`signalingState → ${pc.signalingState}`));
    pc.addEventListener("iceconnectionstatechange", () => log(`iceConnectionState → ${pc.iceConnectionState}`));
    pc.addEventListener("icegatheringstatechange", () => log(`iceGatheringState → ${pc.iceGatheringState}`));
    pc.addEventListener("connectionstatechange", () => {
      log(`connectionState → ${pc.connectionState}`);
      if (pc.connectionState === "failed") end("Bağlantı kesildi");
    });
    return pc;
  };

  video.addEventListener("playing", () => {
    log(`video oynuyor ${video.videoWidth}×${video.videoHeight}`);
    setStatus("");
  });

  const handle = async (message) => {
    switch (message.type) {
      case "welcome":
        welcomed = true;
        setStatus("Bağlandı. Görüntü bekleniyor…");
        break;
      case "offer":
        if (!peer) peer = createPeer();
        await peer.setRemoteDescription({ type: "offer", sdp: message.sdp });
        await peer.setLocalDescription(await peer.createAnswer());
        send({ type: "answer", sdp: peer.localDescription.sdp });
        break;
      case "candidate":
        if (!peer) return;
        await peer.addIceCandidate({
          candidate: message.candidate,
          sdpMid: message.sdpMid || null,
          sdpMLineIndex: message.sdpMLineIndex,
        });
        break;
    }
  };

  // Handle messages strictly in order: a candidate must not be applied
  // before the offer it belongs to.
  let queue = Promise.resolve();
  socket.addEventListener("message", (event) => {
    let message;
    try {
      message = JSON.parse(event.data);
    } catch {
      log(`← JSON olmayan mesaj (${String(event.data).length} bayt)`);
      return;
    }
    log(`← ${message.type}${message.type === "candidate" ? ` ${describeCandidate(message.candidate || "")}` : ""}`);
    queue = queue
      .then(() => handle(message))
      .catch((error) => {
        log(`${message.type} işlenemedi: ${error && error.name}: ${error && error.message}`);
        if (message.type === "offer") end("Bu tarayıcı görüntüyü açamadı.");
      });
  });
})();
