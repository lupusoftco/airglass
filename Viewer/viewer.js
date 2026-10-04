"use strict";

(() => {
  // URL fragment: "#<token>". It never reaches the server in an HTTP
  // request. Remove it from the address bar and history right away so it
  // cannot be copied or reopened.
  const token = decodeURIComponent(location.hash.slice(1).split("&")[0]);
  history.replaceState(null, "", location.pathname);

  // --- Diagnostics log ---------------------------------------------------
  //
  // Always collected; the panel starts hidden and shows everything since the
  // page loaded when opened with the button in the corner.

  const debugEl = document.getElementById("debug");
  const debugToggle = document.getElementById("debug-toggle");
  const maxLogLines = 1000;

  const log = (text) => {
    const line = `${new Date().toISOString().slice(11, 23)} ${text}`;
    console.log(line);
    const row = document.createElement("div");
    row.textContent = line;
    debugEl.appendChild(row);
    while (debugEl.childElementCount > maxLogLines) debugEl.firstChild.remove();
    if (!debugEl.hidden) debugEl.scrollTop = debugEl.scrollHeight;
  };

  debugToggle.addEventListener("click", (event) => {
    event.stopPropagation();
    debugEl.hidden = !debugEl.hidden;
    debugToggle.setAttribute("aria-pressed", String(!debugEl.hidden));
    if (!debugEl.hidden) debugEl.scrollTop = debugEl.scrollHeight;
  });
  // Scrolling or selecting in the log must not count as a tap on the video.
  debugEl.addEventListener("click", (event) => event.stopPropagation());

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

  const describe = (message) =>
    `${message.type}${message.type === "candidate" ? ` ${describeCandidate(message.candidate || "")}` : ""}`;

  log(`UA: ${navigator.userAgent}`);
  log(`secureContext=${window.isSecureContext} RTCPeerConnection=${typeof window.RTCPeerConnection}`);
  document.addEventListener("visibilitychange", () => log(`visibility → ${document.visibilityState}`));

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

  // --- Signaling over plain HTTP -----------------------------------------
  //
  // POST /signal/hello {token} → {session}
  // POST /signal/send  {message}           (our messages, strictly in order)
  // GET  /signal/poll  → [messages]        (long poll for the Mac's messages)
  // POST /signal/bye                       (page closed)

  let session = null;
  let peer = null;
  let ended = false;

  const request = (method, path, body, options = {}) =>
    fetch(path, {
      method,
      cache: "no-store",
      headers: {
        ...(body ? { "Content-Type": "application/json" } : {}),
        ...(session ? { "X-AirGlass-Session": session } : {}),
      },
      body: body ? JSON.stringify(body) : undefined,
      ...options,
    });

  const end = (text) => {
    if (ended) return;
    ended = true;
    log(`Bitti: ${text}`);
    if (peer) peer.close();
    exitFullscreen();
    video.srcObject = null;
    setStatus(text);
  };

  // Sends are chained so the Mac receives them in order (an answer must
  // arrive before the candidates that follow it).
  let sendChain = Promise.resolve();
  const send = (message) => {
    sendChain = sendChain.then(async () => {
      if (ended) return;
      log(`→ ${describe(message)}`);
      const response = await request("POST", "/signal/send", message);
      if (!response.ok) {
        log(`→ ${message.type} reddedildi: HTTP ${response.status}`);
        end("Bağlantı kesildi");
      }
    }).catch((error) => {
      log(`→ ${message.type} gönderilemedi: ${error.name}: ${error.message}`);
      end("Bağlantı kesildi");
    });
  };

  const poll = async () => {
    let failures = 0;
    while (!ended) {
      let response;
      try {
        response = await request("GET", "/signal/poll");
      } catch (error) {
        // A dropped long poll is not fatal; give up after a few in a row.
        failures += 1;
        log(`poll ağ hatası (${failures}): ${error.name}: ${error.message}`);
        if (failures >= 3) return end("Bağlantı kesildi");
        await new Promise((resolve) => setTimeout(resolve, 500));
        continue;
      }
      failures = 0;
      if (!response.ok) {
        log(`poll: HTTP ${response.status}`);
        return end("Bağlantı kesildi");
      }
      const messages = await response.json();
      for (const message of messages) {
        log(`← ${describe(message)}`);
        enqueue(message);
      }
    }
  };

  // --- Fullscreen ----------------------------------------------------------
  //
  // iPhone Safari only allows real fullscreen for a <video> element, and only
  // from a user gesture (webkitEnterFullscreen). The first tap goes
  // fullscreen; afterwards a tap shows a fullscreen button for 3 seconds.

  const fullscreenButton = document.getElementById("fullscreen");
  let firstTapHandled = false;
  let hideButtonTimer = null;

  const isFullscreen = () => Boolean(video.webkitDisplayingFullscreen || document.fullscreenElement);

  const enterFullscreen = () => {
    if (ended || isFullscreen()) return;
    try {
      if (video.webkitEnterFullscreen && video.readyState >= 1) {
        video.webkitEnterFullscreen();
      } else if (document.documentElement.requestFullscreen) {
        document.documentElement.requestFullscreen().catch((error) => log(`Tam ekran reddedildi: ${error.name}`));
      } else {
        log("Tam ekran bu tarayıcıda yok veya görüntü henüz hazır değil");
      }
    } catch (error) {
      log(`Tam ekran açılamadı: ${error.name}: ${error.message}`);
    }
  };

  function exitFullscreen() {
    try {
      if (video.webkitDisplayingFullscreen && video.webkitExitFullscreen) video.webkitExitFullscreen();
      else if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    } catch {
      // Already out of fullscreen.
    }
  }

  const flashFullscreenButton = () => {
    if (ended || !video.srcObject || isFullscreen()) return;
    fullscreenButton.hidden = false;
    clearTimeout(hideButtonTimer);
    hideButtonTimer = setTimeout(() => {
      fullscreenButton.hidden = true;
    }, 3000);
  };

  document.addEventListener("click", () => {
    if (!firstTapHandled && video.srcObject) {
      firstTapHandled = true;
      enterFullscreen();
    } else {
      flashFullscreenButton();
    }
  });

  fullscreenButton.addEventListener("click", (event) => {
    event.stopPropagation();
    fullscreenButton.hidden = true;
    enterFullscreen();
  });

  video.addEventListener("webkitbeginfullscreen", () => log("Tam ekran açıldı"));
  video.addEventListener("webkitendfullscreen", () => {
    log("Tam ekrandan çıkıldı");
    // iOS pauses the video when leaving its fullscreen player.
    if (!ended) video.play().catch(() => {});
  });
  // A live mirror never stays paused (e.g. paused from the fullscreen player).
  video.addEventListener("pause", () => {
    if (!ended && video.srcObject) video.play().catch(() => {});
  });
  window.addEventListener("resize", () => log(`Ekran ${innerWidth}×${innerHeight}`));

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

  // Handle the Mac's messages strictly in order: a candidate must not be
  // applied before the offer it belongs to.
  let handleChain = Promise.resolve();
  const enqueue = (message) => {
    handleChain = handleChain
      .then(() => handle(message))
      .catch((error) => {
        log(`${message.type} işlenemedi: ${error && error.name}: ${error && error.message}`);
        if (message.type === "offer") end("Bu tarayıcı görüntüyü açamadı.");
      });
  };

  // Tell the Mac right away when the page goes away; keepalive lets the
  // request outlive the page.
  window.addEventListener("pagehide", () => {
    if (!session || ended) return;
    request("POST", "/signal/bye", null, { keepalive: true }).catch(() => {});
  });

  // --- Start ---------------------------------------------------------------

  (async () => {
    log("→ hello");
    let response;
    try {
      response = await request("POST", "/signal/hello", { token });
    } catch (error) {
      log(`hello ağ hatası: ${error.name}: ${error.message}`);
      return end("Bağlanılamadı. QR kodu yeniden okutun.");
    }
    if (!response.ok) {
      log(`hello reddedildi: HTTP ${response.status}`);
      return end("Bağlanılamadı. QR kodu yeniden okutun.");
    }
    session = (await response.json()).session;
    log("← hello kabul edildi");
    setStatus("Bağlandı. Görüntü bekleniyor…");
    poll();
  })();
})();
